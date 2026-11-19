using System.Diagnostics;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Cam;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;
using SkiaSharp;

namespace CaixaMaker.App.Services;

/// <summary>Snapshot imutável de progresso emitido durante o pré-aquecimento da Splash Screen.</summary>
public sealed record SplashWarmupProgress(
    int StageIndex,
    int TotalStages,
    string StageId,
    double Percent,
    string StatusText);

/// <summary>Resultado consolidado do pipeline assíncrono de pré-aquecimento CAD/CAM na inicialização.</summary>
public sealed record StartupWarmupResult(
    bool CamBackendReady,
    int PrewarmedPreviewCount,
    int IndexedProjectCount,
    bool HasPendingAutosaveDraft,
    double ElapsedMs,
    IReadOnlyList<SplashWarmupProgress> EmittedStages);

/// <summary>
/// Pipeline real em 5 estágios executado durante a Splash Screen nativa WinUI 3:
/// <list type="number">
/// <item>Carregamento das preferências e perfil da mesa CNC (<see cref="UiSettingsService"/>).</item>
/// <item>Aquecimento JIT do núcleo paramétrico (<see cref="BoxModelRegistry"/>, <see cref="BoxScene3DBuilder"/>, <see cref="GeometryValidator"/>).</item>
/// <item>Pré-renderização offscreen em cache das miniaturas 3D (<see cref="CatalogPreview3DService"/>).</item>
/// <item>Indexação da biblioteca local de projetos <c>.cmaker</c> (<see cref="ProjectLibraryService"/>).</item>
/// <item>Instanciação final e ativação da <c>MainWindow</c>.</item>
/// </list>
/// </summary>
public static class StartupWarmupPipeline
{
    public const int TotalStageCount = 5;

    public static IReadOnlyList<SplashWarmupProgress> CanonicalMilestones { get; } =
    [
        new(1, TotalStageCount, "settings", 20.0, "Carregando preferências e perfis CNC..."),
        new(2, TotalStageCount, "geometry-jit", 45.0, "Inicializando núcleo paramétrico e solver SAT..."),
        new(3, TotalStageCount, "skia-thumbnails", 75.0, "Aquecendo pipeline SkiaSharp e miniaturas 3D..."),
        new(4, TotalStageCount, "project-library", 92.0, "Indexando biblioteca de projetos (.cmaker)..."),
        new(5, TotalStageCount, "workspace-ready", 100.0, "Preparando área de trabalho CAD/CAM..."),
    ];

    public static async Task<StartupWarmupResult> RunAsync(
        AppPaths paths,
        Action<SplashWarmupProgress> reportProgress,
        Func<Task>? activateMainWindowAsync = null,
        TimeSpan? stageMinimumDuration = null,
        BoxModelRegistry? registry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(reportProgress);

        BoxModelRegistry effectiveRegistry = registry ?? BoxModelRegistry.Default;
        TimeSpan minStageWait = stageMinimumDuration ?? TimeSpan.FromMilliseconds(165);
        var sw = Stopwatch.StartNew();
        var emitted = new List<SplashWarmupProgress>(TotalStageCount + 1);

        void Emit(int stageIdx, string stageId, double pct, string status)
        {
            var entry = new SplashWarmupProgress(stageIdx, TotalStageCount, stageId, pct, status);
            emitted.Add(entry);
            reportProgress(entry);
        }

        Emit(0, "boot", 6.0, "Iniciando Caixa Maker v" + AppVersion.Version + "...");

        // Estágio 1 (0 -> 20%): Preferências + Mesa CNC
        Emit(1, CanonicalMilestones[0].StageId, CanonicalMilestones[0].Percent, CanonicalMilestones[0].StatusText);
        Task stage1Work = Task.Run(() =>
        {
            var settingsService = new UiSettingsService(paths);
            _ = settingsService.Load();
        }, cancellationToken);
        await AwaitWithMinDelayAsync(stage1Work, minStageWait, cancellationToken);

        // Estágio 2 (20 -> 45%): JIT geométrico + Scene3D + Validador SAT + CAM
        Emit(2, CanonicalMilestones[1].StageId, CanonicalMilestones[1].Percent, CanonicalMilestones[1].StatusText);
        bool camReady = false;
        Task stage2Work = Task.Run(() =>
        {
            camReady = CamModuleInfo.VerifyGraphicsBackend();
            var parameters = BoxParameters.Default;
            foreach (IBoxModelDefinition model in effectiveRegistry.ListModels())
            {
                BoxCalculator calc = model.CreateCalculator(parameters);
                BoxDrawer drawer = model.CreateDrawer(calc);
                BoxSheetGeometry2D sheet = model.BuildSheetGeometry(calc, drawer);
                var surfaces = new SurfaceManager(calc, model, drawer: drawer);
                _ = BoxScene3DBuilder.BuildScene(calc, model, sheet, surfaces, model.SupportsLidArticulation ? 35.0 : 0.0, false);
                _ = surfaces.Validate(GeometryValidator.DefaultSafetyMarginMm);
            }
        }, cancellationToken);
        await AwaitWithMinDelayAsync(stage2Work, minStageWait, cancellationToken);

        // Estágio 3 (45 -> 75%): Pré-renderização de miniaturas 3D em cache MTA
        Emit(3, CanonicalMilestones[2].StageId, CanonicalMilestones[2].Percent, CanonicalMilestones[2].StatusText);
        int prewarmedCount = 0;
        Task stage3Work = Task.Run(() =>
        {
            foreach (IBoxModelDefinition model in effectiveRegistry.ListModels())
            {
                using SKBitmap assembled = CatalogPreview3DService.RenderModelPreviewBitmap(
                    model.ModelId,
                    exploded: false,
                    darkTheme: true,
                    widthPx: 380,
                    heightPx: 270,
                    registry: effectiveRegistry);
                prewarmedCount++;

                using SKBitmap exploded = CatalogPreview3DService.RenderModelPreviewBitmap(
                    model.ModelId,
                    exploded: true,
                    darkTheme: true,
                    widthPx: 380,
                    heightPx: 270,
                    registry: effectiveRegistry);
                prewarmedCount++;
            }
        }, cancellationToken);
        await AwaitWithMinDelayAsync(stage3Work, TimeSpan.FromMilliseconds(minStageWait.TotalMilliseconds * 1.25), cancellationToken);

        // Estágio 4 (75 -> 92%): Indexação da biblioteca de projetos (.cmaker)
        Emit(4, CanonicalMilestones[3].StageId, CanonicalMilestones[3].Percent, CanonicalMilestones[3].StatusText);
        int indexedCount = 0;
        bool hasPendingDraft = false;
        Task stage4Work = Task.Run(() =>
        {
            var library = new ProjectLibraryService(paths, effectiveRegistry);
            IReadOnlyList<ProjectSummary> projects = library.ListProjects();
            indexedCount = projects.Count;
            _ = library.CountPendingLegacyJsonProjects();

            var draftService = new AutosaveDraftService(paths);
            hasPendingDraft = draftService.HasDraft();
        }, cancellationToken);
        await AwaitWithMinDelayAsync(stage4Work, minStageWait, cancellationToken);

        // Estágio 5 (92 -> 100%): Instanciação e abertura da MainWindow
        Emit(5, CanonicalMilestones[4].StageId, 95.0, CanonicalMilestones[4].StatusText);
        if (activateMainWindowAsync is not null)
        {
            await activateMainWindowAsync();
        }
        else if (minStageWait > TimeSpan.Zero)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(120, minStageWait.TotalMilliseconds)), cancellationToken);
        }

        Emit(5, CanonicalMilestones[4].StageId, 100.0, "Ambiente CAD/CAM pronto.");
        sw.Stop();

        return new StartupWarmupResult(
            CamBackendReady: camReady,
            PrewarmedPreviewCount: prewarmedCount,
            IndexedProjectCount: indexedCount,
            HasPendingAutosaveDraft: hasPendingDraft,
            ElapsedMs: sw.Elapsed.TotalMilliseconds,
            EmittedStages: emitted);
    }

    private static async Task AwaitWithMinDelayAsync(
        Task workTask,
        TimeSpan minDelay,
        CancellationToken cancellationToken)
    {
        if (minDelay <= TimeSpan.Zero)
        {
            await workTask;
            return;
        }

        await Task.WhenAll(workTask, Task.Delay(minDelay, cancellationToken));
    }
}
