using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Interop;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Cam;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Logging;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace CaixaMaker.App;

public partial class App : Application
{
    private static int _pendingExternalActivation;
    private static string? _pendingExternalFilePath;
    private static App? _instance;

    private AppPaths? _appPaths;
    private RotatingFileLogger? _logger;
    private SplashWindow? _splashWindow;
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _viewModel;
    private readonly List<string> _sessionOutputFiles = [];

    public App()
    {
        InitializeComponent();
        _instance = this;
        UnhandledException += OnAppUnhandledException;
    }

    public static void NotifyExternalActivation(string? projectFilePath = null)
    {
        App? current = _instance;
        if (current is null)
        {
            if (!string.IsNullOrWhiteSpace(projectFilePath))
            {
                Interlocked.Exchange(ref _pendingExternalFilePath, projectFilePath);
            }

            Interlocked.Exchange(ref _pendingExternalActivation, 1);
            return;
        }

        current.HandleExternalActivation(projectFilePath);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Program.IsCaptureScreenshotsMode || Program.IsSmokeTestMode)
        {
            InitializeAutomationSession();
            return;
        }

        _splashWindow = new SplashWindow();
        _splashWindow.Closed += (_, _) => _splashWindow = null;
        _splashWindow.Activate();

        _ = RunDesktopStartupWithSplashAsync(_splashWindow);
    }

    private void InitializeAutomationSession()
    {
        (AppPaths paths, RotatingFileLogger logger) = InitializePathsAndLogging();
        var (viewModel, mainWindow) = CreateAndActivateMainWindow(paths, logger, initialProjectPath: null);

        if (Program.IsCaptureScreenshotsMode && !string.IsNullOrWhiteSpace(Program.ScreenshotOutputDirectory))
        {
            _ = Diagnostics.UiScreenshotCaptureService.RunAsync(
                mainWindow,
                viewModel,
                paths,
                logger,
                Program.ScreenshotOutputDirectory);
        }
        else if (Program.IsSmokeTestMode)
        {
            _ = RunSmokeTestAsync(mainWindow, viewModel, paths, logger);
        }
    }

    private async Task RunDesktopStartupWithSplashAsync(SplashWindow splash)
    {
        AppPaths? paths = null;
        RotatingFileLogger? logger = null;

        try
        {
            (paths, logger) = InitializePathsAndLogging();
            await Services.StartupWarmupPipeline.RunAsync(
                paths,
                progress => _ = splash.DispatcherQueue.TryEnqueue(() =>
                    _splashWindow?.UpdateProgress(progress.Percent, progress.StatusText)),
                activateMainWindowAsync: () => RunOnDispatcherAsync(splash.DispatcherQueue, () =>
                {
                    string? initialProjectPath = Interlocked.Exchange(ref _pendingExternalFilePath, null)
                        ?? Program.StartupProjectFilePath;

                    (MainWindowViewModel viewModel, MainWindow mainWindow) =
                        CreateAndActivateMainWindow(paths, logger, initialProjectPath);

                    if (Interlocked.Exchange(ref _pendingExternalActivation, 0) == 1)
                    {
                        BringMainWindowToFocus();
                    }

                    if (!string.IsNullOrWhiteSpace(initialProjectPath) && File.Exists(initialProjectPath))
                    {
                        string targetFile = initialProjectPath;
                        _ = mainWindow.DispatcherQueue.TryEnqueue(async () =>
                        {
                            await viewModel.OpenExternalProjectFileAsync(targetFile, registerInLibrary: true);
                        });
                    }
                }),
                stageMinimumDuration: TimeSpan.FromMilliseconds(165));

            await Task.Delay(120);
        }
        catch (Exception ex)
        {
            logger?.Error("Lifecycle", $"Falha durante o boot com Splash Screen: {ex.Message}", ex);

            if (_mainWindow is null)
            {
                await RunOnDispatcherAsync(splash.DispatcherQueue, () =>
                {
                    (AppPaths effectivePaths, RotatingFileLogger effectiveLogger) =
                        paths is not null && logger is not null
                            ? (paths, logger)
                            : InitializePathsAndLogging();
                    _ = CreateAndActivateMainWindow(effectivePaths, effectiveLogger, Program.StartupProjectFilePath);
                });
            }
        }
        finally
        {
            _ = splash.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    splash.Close();
                }
                catch
                {
                    // Splash já pode ter sido fechada manualmente.
                }

                if (ReferenceEquals(_splashWindow, splash))
                {
                    _splashWindow = null;
                }
            });
        }
    }

    private (AppPaths Paths, RotatingFileLogger Logger) InitializePathsAndLogging()
    {
        AppPaths paths = AppPaths.Resolve(new AppPathsOptions { DesktopMode = true });
        paths.EnsureDirs();
        _appPaths = paths;

        var logger = new RotatingFileLogger(paths.NativeLogFilePath);
        _logger = logger;
        logger.Info(
            "Lifecycle",
            $"Iniciando {AppVersion.ProductName} v{AppVersion.Version} (WinUI 3 Unpackaged x64).");
        logger.Info("Storage", $"ProjectsDir={paths.ProjectsDir}");
        logger.Info("Storage", $"ConfigDir={paths.ConfigDir}");
        logger.Info("Storage", $"OutputsDir={paths.OutputsDir}");

        MigrationReport migrationReport = LegacyProjectMigrator.MigrateLegacyProjects(
            paths.ProjectsDir,
            paths.LegacyProjectsDirs,
            paths.LegacyMigrationMarkerPath);

        logger.Info(
            "Migration",
            $"Performed={migrationReport.Performed}, Migrated={migrationReport.MigratedCount}, " +
            $"Skipped={migrationReport.SkippedExistingCount}, Errors={migrationReport.Errors.Count}");

        foreach (string err in migrationReport.Errors)
        {
            logger.Warn("Migration", err);
        }

        int staleRemoved = OutputCleanupService.CleanupStaleOutputs(paths.OutputsDir);
        logger.Info("Cleanup", $"Artefatos expirados (>24h) removidos no boot: {staleRemoved}");

        bool camReady = CamModuleInfo.VerifyGraphicsBackend();
        logger.Info("CAM", $"{CamModuleInfo.EngineName} pronto={camReady} CoreVersion={CamModuleInfo.CoreVersion}");

        return (paths, logger);
    }

    private (MainWindowViewModel ViewModel, MainWindow Window) CreateAndActivateMainWindow(
        AppPaths paths,
        RotatingFileLogger logger,
        string? initialProjectPath)
    {
        _ = initialProjectPath;
        var dialogService = new Services.WinUiNativeFileDialogService();
        MainWindowViewModel viewModel = new(
            paths: paths,
            dialogService: dialogService,
            initialSystemTheme: Current.RequestedTheme == ApplicationTheme.Dark
                ? Models.ResolvedAppTheme.Dark
                : Models.ResolvedAppTheme.Light);

        _viewModel = viewModel;
        _mainWindow = new MainWindow(viewModel, paths, logger);
        dialogService.AttachWindow(_mainWindow);
        _mainWindow.Closed += OnMainWindowClosed;
        _mainWindow.Activate();

        return (viewModel, _mainWindow);
    }

    private static Task RunOnDispatcherAsync(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    private static async Task RunSmokeTestAsync(
        MainWindow window,
        MainWindowViewModel vm,
        AppPaths paths,
        RotatingFileLogger logger)
    {
        try
        {
            await Task.Delay(350);
            logger.Info("SmokeTest", $"Inicializado: InDesigner={vm.InDesigner}, WindowTitle='{vm.WindowTitle}'");

            vm.StartNewProjectFromCatalog("box_with_lid_and_handles");
            await Task.Delay(150);
            logger.Info("SmokeTest", $"Designer aberto: InDesigner={vm.InDesigner}, WindowTitle='{vm.WindowTitle}', AreaCm2={vm.Box.EstimatedCuttingAreaCm2:F1}");

            vm.Theme.SetPreference(Models.AppThemePreference.Dark);
            await Task.Delay(100);
            vm.Theme.SetPreference(Models.AppThemePreference.Light);
            await Task.Delay(100);
            logger.Info("SmokeTest", $"Temas alternados com sucesso (Dark -> Light).");

            vm.SetWorkspaceArea(Models.WorkspaceArea.Personalization);
            vm.Personalization.AddTextElement("IFMaker Smoke");
            vm.Box.SetCameraPreset(Models.CameraPreset.Front);
            vm.Box.SetCameraPreset(Models.CameraPreset.Top);
            vm.Box.SetCameraPreset(Models.CameraPreset.Isometric);
            vm.Box.ExplodedView = true;
            vm.Box.LidAngle = 45.0;
            await Task.Delay(120);

            int scene3DPngBytes = 0;
            int editor2DPngBytes = 0;
            int panelCount = 0;
            int triangleCount = 0;

            if (vm.Box.CurrentCalculator is { } calc && vm.Box.SheetGeometry is { } sheet)
            {
                BoxScene3D scene3D = BoxScene3DBuilder.BuildScene(
                    calc,
                    vm.Box.SelectedModel,
                    sheet,
                    vm.Personalization.Manager,
                    vm.Box.LidAngle,
                    vm.Box.ExplodedView);
                panelCount = scene3D.Panels.Count;
                triangleCount = scene3D.Panels.Sum(p => p.Triangles.Count);

                var camera = new Camera3DController();
                camera.SyncFromViewModel(vm.Box);
                byte[] png3D = OffscreenViewportRenderer.RenderScene3DPng(
                    scene3D,
                    camera,
                    vm.Personalization.Manager,
                    new RenderScene3DOptions
                    {
                        WidthPx = 480,
                        HeightPx = 360,
                        DarkTheme = true,
                        ShowDimensions = vm.Box.ShowDimensions,
                        ShowSurfaceGuides = true,
                        ActiveSurfaceId = vm.Personalization.ActiveSurfaceId,
                        SelectedElementId = vm.Personalization.SelectedElementId,
                    });
                scene3DPngBytes = png3D.Length;

                if (vm.Personalization.ActiveSurface is { } activeSurface)
                {
                    var editor2D = new SurfaceCanvas2DController();
                    string panelKey = activeSurface.PanelId.ToString().ToLowerInvariant();
                    SurfaceContour2D? contour = sheet.PanelsById.TryGetValue(panelKey, out PanelGeometry2D? p2d)
                        ? SurfaceCanvas2DController.MapPanelContourToSurface(p2d, activeSurface)
                        : null;
                    byte[] png2D = OffscreenViewportRenderer.RenderEditor2DPng(
                        editor2D,
                        activeSurface,
                        vm.Personalization.Manager.ListElements(),
                        contour,
                        new RenderEditor2DOptions
                        {
                            WidthPx = 480,
                            HeightPx = 320,
                            DarkTheme = false,
                            SelectedElementId = vm.Personalization.SelectedElementId,
                            SafetyMarginMm = vm.Personalization.SafetyMarginMm,
                        });
                    editor2DPngBytes = png2D.Length;
                }
            }

            vm.SetWorkspaceArea(Models.WorkspaceArea.Fabrication);
            string svg = vm.Manufacturing.BuildSvgForScope(vm.Personalization.Manager, Cam.Exporters.PersonalizationExportMode.FullBox);

            // Verifica reutilização in-place do CadMetricTable em runtime WinUI 3 e Z-buffer paralelo com Zoom 5x na thread STA
            var metricTable = new Controls.CadPrimitives.CadMetricTable();
            metricTable.SetRows([
                new Controls.CadPrimitives.CadMetricItem("Largura", "200 mm", IsHighlighted: false),
                new Controls.CadPrimitives.CadMetricItem("Slot", "3.00 mm", IsHighlighted: true),
            ]);
            if (metricTable.VisualRowCount != 2)
            {
                throw new InvalidOperationException("CadMetricTable deveria conter 2 linhas após SetRows inicial.");
            }
            object reusedRowInstance = metricTable.GetVisualRowAt(0);
            metricTable.SetRows([
                new Controls.CadPrimitives.CadMetricItem("Largura", "240 mm", IsHighlighted: true),
                new Controls.CadPrimitives.CadMetricItem("Slot", "3.05 mm", IsHighlighted: false),
            ]);
            if (!ReferenceEquals(reusedRowInstance, metricTable.GetVisualRowAt(0)))
            {
                throw new InvalidOperationException("CadMetricTable.SetRows recriou elementos XAML em vez de reutilizar linhas in-place.");
            }

            // Verifica SplashScreenControl + progressão de estágios do pipeline real de pré-aquecimento
            var splashControl = new Controls.SplashScreenControl();
            splashControl.EnsureHeroArtworkRendered();
            foreach (Services.SplashWarmupProgress milestone in Services.StartupWarmupPipeline.CanonicalMilestones)
            {
                splashControl.SetProgress(milestone.Percent, milestone.StatusText);
            }
            if (Math.Abs(splashControl.CurrentPercent - 100.0) > 0.01)
            {
                throw new InvalidOperationException("SplashScreenControl deveria atingir 100% após os estágios canônicos.");
            }
            Services.StartupWarmupResult warmupResult = await Services.StartupWarmupPipeline.RunAsync(
                paths,
                _ => { },
                stageMinimumDuration: TimeSpan.Zero);
            if (!warmupResult.CamBackendReady || warmupResult.PrewarmedPreviewCount <= 0)
            {
                throw new InvalidOperationException("StartupWarmupPipeline deveria aquecer backend CAM e miniaturas 3D.");
            }

            logger.Info(
                "SmokeTest",
                $"Viewport3D/2D e Fabricação verificados: Paineis3D={panelCount}, Triangulos={triangleCount}, Png3D={scene3DPngBytes}B, Png2D={editor2DPngBytes}B, Elementos={vm.Personalization.TotalElementCount}, SvgLen={svg.Length}, Valid={vm.Personalization.IsValidationValid}, ReuseXAML=OK, SplashPrewarmed={warmupResult.PrewarmedPreviewCount}");
        }
        catch (Exception ex)
        {
            logger.Error("SmokeTest", $"Falha no smoke test UI: {ex.Message}", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            window.DispatcherQueue.TryEnqueue(() => window.ForceCloseForSmokeTest());
        }
    }

    private void HandleExternalActivation(string? projectFilePath)
    {
        MainWindow? window = _mainWindow;
        MainWindowViewModel? vm = _viewModel;
        if (window is null)
        {
            if (!string.IsNullOrWhiteSpace(projectFilePath))
            {
                Interlocked.Exchange(ref _pendingExternalFilePath, projectFilePath);
            }

            Interlocked.Exchange(ref _pendingExternalActivation, 1);
            return;
        }

        _ = window.DispatcherQueue.TryEnqueue(async () =>
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            NativeWindowHelper.BringWindowToFront(hwnd);
            window.Activate();
            _logger?.Info("SingleInstance", "Ativação redirecionada; janela principal trazida para o foco.");

            if (!string.IsNullOrWhiteSpace(projectFilePath)
                && File.Exists(projectFilePath)
                && vm is not null
                && await vm.ConfirmUnsavedIfNeededAsync())
            {
                await vm.OpenExternalProjectFileAsync(projectFilePath, registerInLibrary: true);
            }
        });
    }

    private void BringMainWindowToFocus()
    {
        MainWindow? window = _mainWindow;
        if (window is null)
        {
            Interlocked.Exchange(ref _pendingExternalActivation, 1);
            return;
        }

        _ = window.DispatcherQueue.TryEnqueue(() =>
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            NativeWindowHelper.BringWindowToFront(hwnd);
            window.Activate();
            _logger?.Info("SingleInstance", "Ativação redirecionada; janela principal trazida para o foco.");
        });
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            if (_appPaths is not null)
            {
                int cleaned = OutputCleanupService.CleanupStaleOutputs(
                    _appPaths.OutputsDir,
                    sessionFiles: _sessionOutputFiles);
                _logger?.Info("Cleanup", $"Limpeza de encerramento em outputs/: {cleaned} arquivo(s) removido(s).");
            }

            _logger?.Info("Lifecycle", "Caixa Maker encerrado de forma controlada.");
        }
        finally
        {
            _logger?.Dispose();
            _logger = null;
        }
    }

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.Error("UnhandledException", e.Message, e.Exception);
    }
}
