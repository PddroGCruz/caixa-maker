using System.IO.Compression;
using System.Text;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests.ViewModels;

public sealed class WorkspaceViewModelsTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public WorkspaceViewModelsTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_ViewModelTests_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_sandboxRoot);

        _paths = AppPaths.Resolve(new AppPathsOptions
        {
            DesktopMode = false,
            ProjectsDir = Path.Combine(_sandboxRoot, "projects"),
            OutputsDir = Path.Combine(_sandboxRoot, "outputs"),
            ConfigDir = Path.Combine(_sandboxRoot, "config"),
            LogsDir = Path.Combine(_sandboxRoot, "logs"),
            TempDir = Path.Combine(_sandboxRoot, "temp"),
        });
        _paths.EnsureDirs();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandboxRoot))
            {
                Directory.Delete(_sandboxRoot, recursive: true);
            }
        }
        catch
        {
            // Melhor esforço ao limpar diretório temporário
        }
    }

    private MainWindowViewModel CreateMainVm(
        NullNativeFileDialogService? dialogs = null,
        ResolvedAppTheme systemTheme = ResolvedAppTheme.Light)
        => new(_paths, dialogs ?? new NullNativeFileDialogService(), BoxModelRegistry.Default, systemTheme);

    private static byte[] CreateSamplePngBytes(int width = 24, int height = 16)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var dark = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            canvas.DrawRect(4, 3, width - 8, height - 6, dark);
        }

        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void CatalogAndTwoModels_ShouldComputeExactSummaryAndDynamicWindowTitle()
    {
        MainWindowViewModel vm = CreateMainVm();

        vm.InDesigner.Should().BeFalse();
        vm.WindowTitle.Should().Be("Caixa Maker — Catálogo de Modelos");
        vm.CanSaveProject.Should().BeFalse();
        vm.Box.CatalogModels.Should().HaveCount(2);

        // Modelo 1: Caixa com Tampa e Alças (200x150x100 mm, MDF 3 mm, folga 0)
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.InDesigner.Should().BeTrue();
        vm.IsDirty.Should().BeFalse();
        vm.WindowTitle.Should().Be("Caixa Maker — Caixa com Tampa e Alças (200×150×100 mm)");
        vm.CanSaveProject.Should().BeTrue();
        vm.CanToggleLidAngle.Should().BeTrue();

        vm.Box.Summary.Should().NotBeNull();
        vm.Box.Summary!.ComputedDimensions.InnerWidth.Should().BeApproximately(194.0, 1e-6);
        vm.Box.Summary.ComputedDimensions.InnerDepth.Should().BeApproximately(144.0, 1e-6);
        vm.Box.Summary.ComputedDimensions.InnerHeight.Should().BeApproximately(97.0, 1e-6);
        vm.Box.EstimatedCuttingAreaCm2.Should().BeApproximately(1419.4, 0.05);
        vm.Box.TotalPartUnits.Should().Be(6);

        // Alterar largura marca projeto como Dirty (*)
        vm.Box.BoxWidth = 220.0;
        vm.IsDirty.Should().BeTrue();
        vm.WindowTitle.Should().EndWith("*");

        // Modelo 2: Caixa Aberta Sem Tampa (5 faces, sem articulação de tampa)
        vm.Box.LoadModel(OpenBoxModel.Id, applyDefaults: true, markAsEdit: false);
        vm.Box.HasLid.Should().BeFalse();
        vm.CanToggleLidAngle.Should().BeFalse();
        vm.Personalization.Surfaces.Should().HaveCount(5);
        vm.Personalization.Surfaces.Select(s => s.PanelId).Should().NotContain(SurfaceId.Lid);
    }

    [Fact]
    public void Personalization_TextSvgRaster_And_FiveQuickAlignments_ShouldMaintainSatValidity()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        // 1. Texto vetorial com modo Hatch
        vm.Personalization.DraftTextContent = "IFAC";
        vm.Personalization.DraftFontSizeMm = 12.0;
        vm.Personalization.DraftRenderMode = TextRenderMode.Hatch;
        TextPersonalizationElement textEl = vm.Personalization.AddTextElement();
        textEl.VectorPaths.Should().NotBeEmpty();
        textEl.Operation.Should().Be(CncOperationType.EngraveFill);

        // Testar os 5 alinhamentos rápidos respeitando área segura
        Rect2D safe = vm.Personalization.ActiveSurface!.SafeAreaForMargin(vm.Personalization.SafetyMarginMm);
        foreach (QuickAlignmentPreset preset in Enum.GetValues<QuickAlignmentPreset>())
        {
            vm.Personalization.ApplyQuickAlignment(preset);
            vm.Personalization.IsValidationValid.Should().BeTrue($"alinhamento {preset} deve respeitar margem segura SAT");
        }

        // Centro exato após CenterSurface
        vm.Personalization.ApplyQuickAlignment(QuickAlignmentPreset.CenterSurface);
        textEl.CenterMm().X.Should().BeApproximately(safe.Center.X, 1e-2);
        textEl.CenterMm().Y.Should().BeApproximately(safe.Center.Y, 1e-2);

        // 2. Importar SVG na face Trás
        vm.Personalization.SelectSurface(SurfaceId.Back);
        const string simpleSvg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="20mm" height="12mm" viewBox="0 0 20 12">
              <rect x="1" y="1" width="18" height="10" fill="none" stroke="#000000" stroke-width="0.2" />
            </svg>
            """;
        VectorSvgPersonalizationElement svgEl = vm.Personalization.ImportSvgFromString(simpleSvg, "retangulo.svg");
        svgEl.Paths.Should().NotBeEmpty();
        svgEl.WidthMm.Should().BeApproximately(18.0, 0.2);

        // 3. Importar imagem raster PNG + vetorização opcional
        vm.Personalization.SelectSurface(SurfaceId.Lid);
        byte[] png = CreateSamplePngBytes(32, 20);
        RasterImagePersonalizationElement rasterEl = vm.Personalization.ImportRasterFromBytes(
            png,
            "logo_teste.png",
            vectorize: true);
        rasterEl.ProcessedImageB64.Should().NotBeNullOrEmpty();
        rasterEl.Vectorize.Should().BeTrue();
        rasterEl.VectorPaths.Should().NotBeEmpty();

        vm.Personalization.TotalElementCount.Should().Be(3);
        vm.Personalization.IsValidationValid.Should().BeTrue();
    }

    [Fact]
    public void AnchorModes_AllSevenModes_ShouldRepositionCorrectlyWhenBoxResizes()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.Personalization.SelectSurface(SurfaceId.Front);

        SurfaceDefinition initialFront = vm.Personalization.ActiveSurface!;
        double margin = 10.0;
        var anchorsToTest = Enum.GetValues<AnchorMode>();
        var createdIds = new Dictionary<AnchorMode, string>();

        const double elementWidth = 14.0;
        const double elementHeight = 8.0;
        foreach (AnchorMode anchor in anchorsToTest)
        {
            TextPersonalizationElement el = vm.Personalization.AddTextElement(anchor.ToString());
            vm.Personalization.SelectElement(el.Id);
            vm.Personalization.UpdateSelectedElementTransform(
                widthMm: elementWidth,
                heightMm: elementHeight,
                preserveAspectRatio: false,
                recordUndo: false);
            double xMm = anchor == AnchorMode.TopRight || anchor == AnchorMode.BottomRight
                ? initialFront.WidthMm - elementWidth - margin
                : margin;
            double yMm = anchor == AnchorMode.TopLeft || anchor == AnchorMode.TopRight
                ? margin
                : initialFront.HeightMm - elementHeight - margin;
            vm.Personalization.UpdateSelectedElementTransform(
                xMm: xMm,
                yMm: yMm,
                preserveAspectRatio: false,
                recordUndo: false);

            vm.Personalization.SetSelectedAnchor(anchor);
            createdIds[anchor] = el.Id;
        }

        // Redimensionar caixa de 200x100 para 280x160 mm
        vm.Box.ApplyParameters(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 280.0,
            BoxDepth = 150.0,
            BoxHeight = 160.0,
            Folga = 0.0,
        }, markAsEdit: true);

        SurfaceDefinition resizedFront = vm.Personalization.ActiveSurface!;
        resizedFront.WidthMm.Should().BeApproximately(280.0, 0.5);
        resizedFront.HeightMm.Should().BeApproximately(160.0, 0.5);

        // Verificar comportamento de cada âncora
        var absEl = vm.Personalization.Manager.GetElement(createdIds[AnchorMode.Absolute]);
        absEl.XMm.Should().BeApproximately(margin, 1e-2);

        var centerEl = vm.Personalization.Manager.GetElement(createdIds[AnchorMode.Center]);
        centerEl.CenterMm().X.Should().BeApproximately(resizedFront.SafeRectMm.Center.X, 0.5);
        centerEl.CenterMm().Y.Should().BeApproximately(resizedFront.SafeRectMm.Center.Y, 0.5);

        var topRightEl = vm.Personalization.Manager.GetElement(createdIds[AnchorMode.TopRight]);
        double distFromRight = resizedFront.WidthMm - (topRightEl.XMm + topRightEl.WidthMm);
        distFromRight.Should().BeApproximately(margin, 1e-2);
        topRightEl.YMm.Should().BeApproximately(margin, 1e-2);

        var bottomLeftEl = vm.Personalization.Manager.GetElement(createdIds[AnchorMode.BottomLeft]);
        double distFromBottom = resizedFront.HeightMm - (bottomLeftEl.YMm + bottomLeftEl.HeightMm);
        distFromBottom.Should().BeApproximately(margin, 1e-2);
        bottomLeftEl.XMm.Should().BeApproximately(margin, 1e-2);

        var bottomRightEl = vm.Personalization.Manager.GetElement(createdIds[AnchorMode.BottomRight]);
        (resizedFront.WidthMm - (bottomRightEl.XMm + bottomRightEl.WidthMm)).Should().BeApproximately(margin, 1e-2);
        (resizedFront.HeightMm - (bottomRightEl.YMm + bottomRightEl.HeightMm)).Should().BeApproximately(margin, 1e-2);

        var relEl = vm.Personalization.Manager.GetElement(createdIds[AnchorMode.Relative]);
        relEl.RelX.Should().BeGreaterThan(0.0).And.BeLessThan(1.0);
        relEl.CenterMm().X.Should().BeApproximately(relEl.RelX * resizedFront.WidthMm, 0.2);
        relEl.CenterMm().Y.Should().BeApproximately(relEl.RelY * resizedFront.HeightMm, 0.2);
    }

    [Fact]
    public async Task ClipboardDuplicateDelete_And_EditableKeyboardGuard_ShouldProtectTextInputs()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        TextPersonalizationElement el = vm.Personalization.AddTextElement("ORIG");
        vm.Personalization.TotalElementCount.Should().Be(1);

        // Quando foco está em TextBox ou NumberBox editável, Delete / Ctrl+C / Ctrl+V / Ctrl+Z NÃO excluem elemento CNC!
        var textBoxContext = new KeyboardFocusContext(FocusControlKind.TextBox, IsReadOnly: false);
        var numberBoxContext = new KeyboardFocusContext(FocusControlKind.NumberBox, IsReadOnly: false);

        KeyboardShortcutGuard.IsEditableKeyboardTarget(textBoxContext).Should().BeTrue();
        KeyboardShortcutGuard.IsEditableKeyboardTarget(numberBoxContext).Should().BeTrue();

        await vm.HandleAcceleratorAsync(DesktopCommandIds.EditDelete, textBoxContext);
        await vm.HandleAcceleratorAsync(DesktopCommandIds.EditDelete, numberBoxContext);
        vm.Personalization.TotalElementCount.Should().Be(1);

        // Fora de campos editáveis, Copy / Paste / Duplicate / Delete atuam normalmente
        var canvasContext = new KeyboardFocusContext(FocusControlKind.NonEditable);
        await vm.HandleAcceleratorAsync(DesktopCommandIds.EditCopy, canvasContext);
        vm.CanPaste.Should().BeTrue();

        await vm.HandleAcceleratorAsync(DesktopCommandIds.EditDuplicate, canvasContext);
        vm.Personalization.TotalElementCount.Should().Be(2);

        // Colar em outra face
        vm.Personalization.SelectSurface(SurfaceId.Right);
        await vm.HandleAcceleratorAsync(DesktopCommandIds.EditPaste, canvasContext);
        vm.Personalization.TotalElementCount.Should().Be(3);
        vm.Personalization.ElementsOnActiveSurface.Should().ContainSingle();

        // Excluir elemento selecionado na face ativa via atalho Delete
        await vm.HandleAcceleratorAsync(DesktopCommandIds.EditDelete, canvasContext);
        vm.Personalization.TotalElementCount.Should().Be(2);
    }

    [Fact]
    public void HistoryUndoRedo_ShouldCapAt40Steps_AndRestoreSequentially()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        // 45 adições sequenciais -> histórico limitado a 40 passos (PersonalizationHistory.DefaultMaxSteps)
        for (int i = 1; i <= 45; i++)
        {
            vm.Personalization.AddTextElement($"E{i}");
        }

        vm.Personalization.TotalElementCount.Should().Be(45);
        vm.Personalization.CanUndo.Should().BeTrue();
        vm.Personalization.UndoCount.Should().Be(PersonalizationHistory.DefaultMaxSteps);

        // Desfaz 5 passos
        for (int u = 0; u < 5; u++)
        {
            vm.Personalization.Undo();
        }

        vm.Personalization.TotalElementCount.Should().Be(40);
        vm.Personalization.CanRedo.Should().BeTrue();
        vm.Personalization.RedoCount.Should().Be(5);

        // Refaz 3 passos
        for (int r = 0; r < 3; r++)
        {
            vm.Personalization.Redo();
        }

        vm.Personalization.TotalElementCount.Should().Be(43);
        vm.Personalization.RedoCount.Should().Be(2);
    }

    [Fact]
    public void AutosaveDraft_And_UiSettings_ShouldPersistDetectRestoreAndDiscard()
    {
        MainWindowViewModel vm1 = CreateMainVm();

        // Persistência de tema e layout em ui_settings.json
        vm1.Theme.SetPreference(AppThemePreference.Dark);
        vm1.LeftSidebarWidth = 380.0;
        vm1.RightSidebarWidth = 410.0;
        vm1.ToolbarVisible = false;
        vm1.LeftSidebarOpen = false;

        string settingsPath = Path.Combine(_paths.ConfigDir, UiSettingsService.SettingsFileName);
        File.Exists(settingsPath).Should().BeTrue();

        // Edição não salva gera autosave_draft.json em TempDir
        vm1.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm1.Box.BoxWidth = 245.0;
        vm1.Personalization.AddTextElement("RASCUNHO");

        string draftPath = Path.Combine(_paths.TempDir, AutosaveDraftService.DraftFileName);
        File.Exists(draftPath).Should().BeTrue();

        // Nova instância detecta UI settings + rascunho pendente no Catálogo
        MainWindowViewModel vm2 = CreateMainVm();
        vm2.Theme.Preference.Should().Be(AppThemePreference.Dark);
        vm2.Theme.IsDarkTheme.Should().BeTrue();
        vm2.LeftSidebarWidth.Should().BeApproximately(380.0, 0.1);
        vm2.RightSidebarWidth.Should().BeApproximately(410.0, 0.1);
        vm2.ToolbarVisible.Should().BeFalse();
        vm2.LeftSidebarOpen.Should().BeFalse();

        vm2.HasPendingDraft.Should().BeTrue();
        vm2.DraftSummaryText.Should().Contain("245×150×100");

        // Restaurar rascunho recompõe caixa + elementos + estado dirty
        vm2.RestoreDraft().Should().BeTrue();
        vm2.InDesigner.Should().BeTrue();
        vm2.IsDirty.Should().BeTrue();
        vm2.Box.BoxWidth.Should().BeApproximately(245.0, 0.01);
        vm2.Personalization.TotalElementCount.Should().Be(1);

        // Salvar projeto limpa draft automaticamente
        vm2.SaveProjectAs("Projeto Recuperado").Should().BeTrue();
        vm2.IsDirty.Should().BeFalse();
        File.Exists(draftPath).Should().BeFalse();

        // Criar novo draft e descartar explicitamente
        vm2.Box.BoxDepth = 180.0;
        File.Exists(draftPath).Should().BeTrue();
        vm2.DiscardDraft();
        File.Exists(draftPath).Should().BeFalse();
        vm2.HasPendingDraft.Should().BeFalse();
    }

    [Fact]
    public async Task ExportCamAndPortableBundle_ShouldRespectSatBlocking_AndGenerateValidFiles()
    {
        var dialogs = new NullNativeFileDialogService();
        MainWindowViewModel vm = CreateMainVm(dialogs);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        TextPersonalizationElement el = vm.Personalization.AddTextElement("CAM1:1");
        vm.Personalization.SelectElement(el.Id);

        // Posicionar em cima da zona proibida para verificar bloqueio SAT
        vm.Personalization.UpdateSelectedElementTransform(xMm: 0.2, yMm: 0.2, recordUndo: false);
        vm.Personalization.IsValidationValid.Should().BeFalse();
        vm.CanExportCam.Should().BeFalse();

        string blockedSvgPath = Path.Combine(_sandboxRoot, "blocked.svg");
        (await vm.ExportVectorFileAsync(VectorExportFormat.Svg, blockedSvgPath)).Should().BeNull();
        File.Exists(blockedSvgPath).Should().BeFalse();

        // Corrigir posição para centro seguro -> desbloqueia exportação
        vm.Personalization.ApplyQuickAlignment(QuickAlignmentPreset.CenterSurface);
        vm.Personalization.IsValidationValid.Should().BeTrue();
        vm.CanExportCam.Should().BeTrue();

        string outSvg = Path.Combine(_sandboxRoot, "caixa.svg");
        string outDxf = Path.Combine(_sandboxRoot, "caixa.dxf");
        string outPdf = Path.Combine(_sandboxRoot, "caixa.pdf");
        string outZip = Path.Combine(_sandboxRoot, "pacote.zip");
        string outBundle = Path.Combine(_sandboxRoot, "projeto.caixamaker.json");

        (await vm.ExportVectorFileAsync(VectorExportFormat.Svg, outSvg)).Should().Be(Path.GetFullPath(outSvg));
        (await vm.ExportVectorFileAsync(VectorExportFormat.Dxf, outDxf)).Should().Be(Path.GetFullPath(outDxf));
        (await vm.ExportVectorFileAsync(VectorExportFormat.Pdf, outPdf)).Should().Be(Path.GetFullPath(outPdf));
        (await vm.ExportBundleZipAsync(outZip)).Should().Be(Path.GetFullPath(outZip));
        (await vm.ExportPortablePackageAsync(outBundle)).Should().BeTrue();

        File.ReadAllText(outSvg, Encoding.UTF8).Should().Contain("layer_cut_external");
        File.ReadAllText(outDxf, Encoding.ASCII).Should().Contain("AC1015");
        Encoding.ASCII.GetString(File.ReadAllBytes(outPdf)[..8]).Should().StartWith("%PDF-1.4");

        using (ZipArchive zip = ZipFile.OpenRead(outZip))
        {
            zip.Entries.Select(e => e.FullName).Should().Contain("manifest.json");
        }

        // Reimportar pacote portátil para verificar roundtrip
        vm.Box.BoxWidth = 310.0;
        dialogs.OnPromptUnsavedChanges = _ => UnsavedChangesDecision.DiscardAndContinue;
        (await vm.ImportPortablePackageAsync(outBundle)).Should().BeTrue();
        vm.Box.BoxWidth.Should().BeApproximately(200.0, 0.1);
        vm.Personalization.TotalElementCount.Should().Be(1);
    }

    [Fact]
    public async Task MainWindowViewModel_FormatsEngineeringTelemetryRotate90AndSavedProjectExport()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        vm.WorkspaceHeaderProjectText.Should().Be("Caixa com Tampa e Alças [200×150×100 mm • MDF 3 mm]");
        vm.StatusBarMaterialText.Should().Be("MATERIAL: MDF 3.0 mm (Folga 0.00 mm)");
        vm.StatusBarDimensionsText.Should().Be("DIM: 200 × 150 × 100 mm");
        vm.StatusBarPiecesText.Should().Be("PEÇAS: 6 (1419.4 cm²)");

        vm.Box.BoxThickness = 6.0;
        vm.Box.Folga = -0.15;
        vm.WorkspaceHeaderProjectText.Should().Contain("MDF 6 mm");
        vm.StatusBarMaterialText.Should().Be("MATERIAL: MDF 6.0 mm (Folga -0.15 mm)");

        // Salvar e testar exportação direta por projectId (usada na linha da tabela do Catálogo)
        vm.SaveProjectAs("Caixa Engenharia Fase 2").Should().BeTrue();
        vm.SavedProjectId.Should().NotBeNullOrWhiteSpace();
        string exportedSavedPkg = Path.Combine(_sandboxRoot, "exported_from_catalog.caixamaker.json");
        (await vm.ExportSavedProjectPackageAsync(vm.SavedProjectId!, exportedSavedPkg)).Should().BeTrue();
        File.Exists(exportedSavedPkg).Should().BeTrue();

        // Rotação rápida de +90° e CanEditSelection
        vm.Personalization.CanEditSelection.Should().BeFalse();
        TextPersonalizationElement el = vm.Personalization.AddTextElement("TESTE");
        vm.Personalization.SelectElement(el.Id);
        vm.Personalization.CanEditSelection.Should().BeTrue();
        vm.Personalization.RotateSelected90();
        vm.Personalization.SelectedElement!.RotationDeg.Should().BeApproximately(90.0, 1e-6);

        vm.Personalization.ToggleSelectedLock();
        vm.Personalization.CanEditSelection.Should().BeFalse();
        vm.Personalization.RotateSelected90();
        vm.Personalization.SelectedElement!.RotationDeg.Should().BeApproximately(90.0, 1e-6);
    }

    [Fact]
    public void Phase3Helpers_CustomDimensionPresets_QuickFolga_And_MoveElementBetweenSurfaces()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        vm.Box.ApplyCustomPresetDimensions(120, 90, 60);
        vm.Box.BoxWidth.Should().Be(120);
        vm.Box.BoxDepth.Should().Be(90);
        vm.Box.BoxHeight.Should().Be(60);

        vm.Box.SetQuickFolga(-0.10);
        vm.Box.Folga.Should().BeApproximately(-0.10, 1e-6);
        vm.Box.SetQuickFolga(5.0);
        vm.Box.Folga.Should().Be(BoxParameters.MaxFolga);

        vm.Personalization.SelectSurface(SurfaceId.Front);
        TextPersonalizationElement textEl = vm.Personalization.AddTextElement("MOVER FACE");
        vm.Personalization.SelectElement(textEl.Id);

        vm.Personalization.MoveSelectedElementToSurface(SurfaceId.Lid);
        vm.Personalization.ActiveSurfaceId.Should().Be(SurfaceId.Lid);
        vm.Personalization.SelectedElementId.Should().Be(textEl.Id);
        vm.Personalization.SelectedElement!.PanelId.Should().Be(SurfaceId.Lid);

        // Elemento travado não pode ser movido entre superfícies
        vm.Personalization.ToggleSelectedLock();
        vm.Personalization.MoveSelectedElementToSurface(SurfaceId.Back);
        vm.Personalization.SelectedElement!.PanelId.Should().Be(SurfaceId.Lid);

        // Primeiro Undo desfaz o bloqueio; segundo Undo retorna elemento para Front
        vm.Personalization.Undo();
        vm.Personalization.SelectedElement!.Locked.Should().BeFalse();
        vm.Personalization.Undo();
        vm.Personalization.Manager.GetElement(textEl.Id).PanelId.Should().Be(SurfaceId.Front);
    }
}
