using System.Text;
using System.Xml.Linq;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Nesting;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage28NestingWorkspaceAndUiTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public Stage28NestingWorkspaceAndUiTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Stage28_" + Guid.NewGuid().ToString("N")[..10]);
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
            // Melhor esforço ao limpar sandbox temporário
        }
    }

    private MainWindowViewModel CreateMainVm()
        => new(_paths, new NullNativeFileDialogService(), BoxModelRegistry.Default, ResolvedAppTheme.Dark);

    private string WriteSampleSvg(string fileName, double w = 80.0, double h = 50.0)
    {
        string path = Path.Combine(_sandboxRoot, fileName);
        string svg = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" width="120mm" height="90mm" viewBox="0 0 120 90">
              <g id="layer_cut_external">
                <rect x="5" y="5" width="{w}" height="{h}" fill="none" stroke="#000000" stroke-width="0.03mm" />
              </g>
              <g id="layer_cut_internal">
                <circle cx="{5 + (w * 0.5)}" cy="{5 + (h * 0.5)}" r="8" fill="none" stroke="#B71C1C" stroke-width="0.03mm" />
              </g>
            </svg>
            """;
        File.WriteAllText(path, svg, Encoding.UTF8);
        return path;
    }

    private string WriteSampleDxf(string fileName, double w = 70.0, double h = 45.0)
    {
        string path = Path.Combine(_sandboxRoot, fileName);
        string dxf = $"""
            0
            SECTION
            2
            HEADER
            9
            $INSUNITS
            70
            4
            0
            ENDSEC
            0
            SECTION
            2
            ENTITIES
            0
            LWPOLYLINE
            8
            CUT_EXTERNAL
            90
            4
            70
            1
            10
            0
            20
            0
            10
            {w}
            20
            0
            10
            {w}
            20
            {h}
            10
            0
            20
            {h}
            0
            ENDSEC
            0
            EOF
            """;
        File.WriteAllText(path, dxf, Encoding.ASCII);
        return path;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
                Directory.Exists(Path.Combine(dir.FullName, "native-winui")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Não foi possível localizar a raiz do repositório.");
    }

    [Fact]
    public async Task IndependentWorkflow_ImportsExternalSvgAndDxf_AdjustsQuantity_OptimizesAndExports()
    {
        MainWindowViewModel mainVm = CreateMainVm();
        mainVm.OpenNestingFromCatalogCommand.Execute(null);

        mainVm.IsInNestingWorkspace.Should().BeTrue();
        mainVm.NestingOrigin.Should().Be(NestingEntryOrigin.Catalog);
        mainVm.NestingBackButtonLabel.Should().Be("← Voltar ao Catálogo");
        mainVm.Nesting.HasActiveBoxProject.Should().BeFalse();

        string svgPath = WriteSampleSvg("suporte.svg", 80.0, 50.0);
        string dxfPath = WriteSampleDxf("placa.dxf", 70.0, 45.0);

        int count = await mainVm.Nesting.ImportExternalFilesAsync([svgPath, dxfPath]);
        count.Should().Be(2);
        mainVm.Nesting.Parts.Should().HaveCount(2);

        // Multiplica a 1ª peça para 3 unidades
        mainVm.Nesting.Parts[0].IncrementQuantityCommand.Execute(null);
        mainVm.Nesting.Parts[0].IncrementQuantityCommand.Execute(null);
        mainVm.Nesting.Parts[0].Quantity.Should().Be(3);
        mainVm.Nesting.TotalRequestedInstances.Should().Be(4);

        // Configura retalho personalizado 400 x 300 mm
        mainVm.Nesting.ApplySheetPresetCommand.Execute("Scrap400x300");
        mainVm.Nesting.SheetWidthMm.Should().Be(400.0);
        mainVm.Nesting.SheetHeightMm.Should().Be(300.0);

        await mainVm.Nesting.RunOptimizationAsync();

        mainVm.Nesting.CurrentResult.Should().NotBeNull();
        mainVm.Nesting.CurrentResult!.TotalPlacedInstances.Should().Be(4);
        mainVm.Nesting.CurrentResult.UnplacedParts.Should().BeEmpty();
        mainVm.Nesting.CurrentResult.OverallUtilizationPercent.Should().BeGreaterThan(0.0);
        mainVm.Nesting.CanExport.Should().BeTrue();

        // Exporta SVG e ZIP 1:1
        string outSvg = Path.Combine(_sandboxRoot, "out_chapa_01.svg");
        string outZip = Path.Combine(_sandboxRoot, "out_lote.zip");
        await mainVm.Nesting.ExportCurrentSheetSvgAsync(outSvg);
        await mainVm.Nesting.ExportFullZipAsync(outZip);
        File.Exists(outSvg).Should().BeTrue();
        File.Exists(outZip).Should().BeTrue();

        mainVm.ReturnFromNestingCommand.Execute(null);
        mainVm.IsInNestingWorkspace.Should().BeFalse();
        mainVm.InDesigner.Should().BeFalse();
    }

    [Fact]
    public async Task IntegratedBoxAndMixedWorkflow_Loads6PanelsInMemory_ReplicatesCopies_MixesDxf_AndPreservesProjectOnReturn()
    {
        MainWindowViewModel mainVm = CreateMainVm();
        mainVm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        mainVm.Box.BoxWidth = 180.0;
        mainVm.Box.BoxDepth = 130.0;
        mainVm.Box.BoxHeight = 90.0;
        mainVm.SetWorkspaceArea(WorkspaceArea.Personalization);
        mainVm.Personalization.DraftTextContent = "LOTE28";
        mainVm.Personalization.AddTextElement();
        mainVm.SetWorkspaceArea(WorkspaceArea.Fabrication);

        int persCountBefore = mainVm.Personalization.TotalElementCount;
        persCountBefore.Should().Be(1);

        // Abre pela Etapa 3 carregando instantaneamente em memória e rodando 1ª otimização
        await mainVm.OpenNestingFromManufacturingAsync();

        mainVm.IsInNestingWorkspace.Should().BeTrue();
        mainVm.NestingOrigin.Should().Be(NestingEntryOrigin.Manufacturing);
        mainVm.NestingBackButtonLabel.Should().Be("← Voltar para Fabricação");
        mainVm.Nesting.HasActiveBoxProject.Should().BeTrue();
        mainVm.Nesting.Parts.Should().HaveCount(6);
        mainVm.Nesting.CurrentResult.Should().NotBeNull();
        mainVm.Nesting.CurrentResult!.TotalPlacedInstances.Should().Be(6);

        // Multiplica para 2 caixas (12 chapas) e mistura um DXF externo na mesma fila
        mainVm.Nesting.BoxCopiesCount = 2;
        mainVm.Nesting.TotalRequestedInstances.Should().Be(12);

        string dxfPath = WriteSampleDxf("extra_tag.dxf", 60.0, 35.0);
        await mainVm.Nesting.ImportExternalFilesAsync([dxfPath]);
        mainVm.Nesting.Parts.Should().HaveCount(7);
        mainVm.Nesting.TotalRequestedInstances.Should().Be(13);

        await mainVm.Nesting.RunOptimizationAsync();
        mainVm.Nesting.CurrentResult!.TotalPlacedInstances.Should().Be(13);
        mainVm.Nesting.CurrentResult.UnplacedParts.Should().BeEmpty();

        // Retorna para Etapa 3 preservando intacto o estado da caixa e da personalização
        mainVm.ReturnFromNestingCommand.Execute(null);
        mainVm.IsInNestingWorkspace.Should().BeFalse();
        mainVm.InDesigner.Should().BeTrue();
        mainVm.ActiveArea.Should().Be(WorkspaceArea.Fabrication);
        mainVm.Box.BoxWidth.Should().Be(180.0);
        mainVm.Box.BoxDepth.Should().Be(130.0);
        mainVm.Box.BoxHeight.Should().Be(90.0);
        mainVm.Personalization.TotalElementCount.Should().Be(persCountBefore);
    }

    [Fact]
    public async Task OversizedPartBanner_AndMultiSheetNavigation_WorkCorrectly()
    {
        MainWindowViewModel mainVm = CreateMainVm();
        mainVm.OpenNestingFromCatalog();

        // 1) Peça maior que o retalho -> HasUnplacedParts = true
        string hugeDxf = WriteSampleDxf("gigante.dxf", 500.0, 260.0);
        string mediumDxf = WriteSampleDxf("media.dxf", 140.0, 120.0);
        await mainVm.Nesting.ImportExternalFilesAsync([hugeDxf, mediumDxf]);
        mainVm.Nesting.Parts[1].Quantity = 4; // 4 peças médias em retalho 300x200 -> 2+ chapas

        mainVm.Nesting.SheetWidthMm = 300.0;
        mainVm.Nesting.SheetHeightMm = 200.0;
        await mainVm.Nesting.RunOptimizationAsync();

        mainVm.Nesting.HasUnplacedParts.Should().BeTrue();
        mainVm.Nesting.UnplacedPartsSummary.Should().Contain("gigante");

        mainVm.Nesting.CurrentResult!.Sheets.Count.Should().BeGreaterThanOrEqualTo(2);
        mainVm.Nesting.SelectedSheetIndex.Should().Be(0);
        mainVm.Nesting.CanGoPreviousSheet.Should().BeFalse();
        mainVm.Nesting.CanGoNextSheet.Should().BeTrue();
        mainVm.Nesting.SheetCounterLabel.Should().StartWith("Chapa 1 de ");

        mainVm.Nesting.NextSheetCommand.Execute(null);
        mainVm.Nesting.SelectedSheetIndex.Should().Be(1);
        mainVm.Nesting.CanGoPreviousSheet.Should().BeTrue();
        mainVm.Nesting.SheetCounterLabel.Should().StartWith("Chapa 2 de ");

        mainVm.Nesting.PreviousSheetCommand.Execute(null);
        mainVm.Nesting.SelectedSheetIndex.Should().Be(0);
    }

    [Fact]
    public async Task NestingViewportRenderer_RendersValidPngInBothThemes_AndReusesStaticCacheOnPanZoom()
    {
        MainWindowViewModel mainVm = CreateMainVm();
        mainVm.StartNewProjectFromCatalog(OpenBoxModel.Id);
        await mainVm.OpenNestingFromManufacturingAsync();

        NestingSheetLayout? sheet = mainVm.Nesting.CurrentSheet;
        sheet.Should().NotBeNull();
        NestingSheetConfig cfg = mainVm.Nesting.BuildCurrentConfig();

        using var renderer = new NestingViewportRenderer();
        byte[] darkPng = renderer.RenderToPngBytes(sheet, cfg, 960, 600, isDarkTheme: true);
        darkPng.Length.Should().BeGreaterThan(1000);
        darkPng.Take(4).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47);
        renderer.StaticCache.RecordCount.Should().Be(1);

        // Pan e Zoom reutilizam o SKPicture cacheado sem regravar
        byte[] zoomedDarkPng = renderer.RenderToPngBytes(sheet, cfg, 960, 600, isDarkTheme: true, zoom: 2.2, panX: 35.0, panY: -20.0);
        zoomedDarkPng.Length.Should().BeGreaterThan(1000);
        renderer.StaticCache.RecordCount.Should().Be(1);
        renderer.StaticCache.CacheHitCount.Should().BeGreaterThanOrEqualTo(1);

        // Troca de tema gera novo cache limpo e PNG válido
        byte[] lightPng = renderer.RenderToPngBytes(sheet, cfg, 960, 600, isDarkTheme: false);
        lightPng.Length.Should().BeGreaterThan(1000);
        lightPng.Take(4).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47);
        renderer.StaticCache.RecordCount.Should().Be(2);
    }

    [Fact]
    public void NestingViewportRenderer_EngraveFillCompoundPath_PunchesCentralHoleWithEvenOdd()
    {
        // Peça 120x120 mm centrada na chapa 120x120 mm (margem 0) com moldura EngraveFill (20..100) e furo central (40..80)
        (double X, double Y)[] outerPart = [(0, 0), (120, 0), (120, 120), (0, 120)];
        (double X, double Y)[] fillOuter = [(20, 20), (100, 20), (100, 100), (20, 100)];
        (double X, double Y)[] fillHole = [(40, 40), (80, 40), (80, 80), (40, 80)];

        var part = new NestingPart(
            id: "plate_evenodd",
            name: "Placa EvenOdd",
            outerContour: outerPart,
            internalFeatures:
            [
                new NestingPolyline2D(fillOuter, IsClosed: true, NestingSemanticLayer.EngraveFill, CompoundGroupId: "grp_logo", IsFilled: true),
                new NestingPolyline2D(fillHole, IsClosed: true, NestingSemanticLayer.EngraveFill, CompoundGroupId: "grp_logo", IsFilled: true),
            ],
            sourceTag: "Externo",
            rotationPolicy: NestingRotationPolicy.Locked0);

        var config = new NestingSheetConfig(SheetWidthMm: 120.0, SheetHeightMm: 120.0, MarginMm: 0.0, PartSpacingMm: 0.0);
        NestingResult result = new TrueShapeNestingEngine().Optimize([part], config);
        result.TotalPlacedInstances.Should().Be(1);
        NestingSheetLayout sheet = result.Sheets[0];

        const int widthPx = 400;
        const int heightPx = 424; // heightPx - 24 = 400 -> chapa quadrada perfeitamente centralizada
        using var renderer = new NestingViewportRenderer();
        byte[] png = renderer.RenderToPngBytes(
            sheet,
            config,
            widthPx,
            heightPx,
            isDarkTheme: false,
            showSheetBoundary: true,
            showUsableMargin: false,
            showPartLabels: false);

        using SKBitmap bmp = SKBitmap.Decode(png);
        bmp.Should().NotBeNull();

        // Converte coordenadas da chapa (mm) para pixel (espelhando fórmulas do NestingViewportRenderer)
        const double padPx = 36.0;
        double scale = Math.Min((widthPx - (2.0 * padPx)) / 120.0, (heightPx - (2.0 * padPx) - 24.0) / 120.0);
        double originX = (widthPx - (120.0 * scale)) * 0.5;
        double originY = (heightPx - 24.0 - (120.0 * scale)) * 0.5;

        SKColor SampleMm(double mmX, double mmY)
            => bmp.GetPixel((int)Math.Round(originX + (mmX * scale)), (int)Math.Round(originY + (mmY * scale)));

        SKColor bgPartPixel = SampleMm(10.0, 60.0);   // fora da moldura EngraveFill (apenas fundo da peça)
        SKColor solidRingPixel = SampleMm(30.0, 60.0); // dentro da borda sólida da moldura EngraveFill (roxo)
        SKColor holeCenterPixel = SampleMm(60.0, 60.0); // centro do furo (deve preservar fundo da peça, SEM roxo!)

        // Centro do furo deve coincidir com o fundo da peça (comprovando EvenOdd ativo)
        Math.Abs(holeCenterPixel.Red - bgPartPixel.Red).Should().BeLessThanOrEqualTo(3);
        Math.Abs(holeCenterPixel.Green - bgPartPixel.Green).Should().BeLessThanOrEqualTo(3);
        Math.Abs(holeCenterPixel.Blue - bgPartPixel.Blue).Should().BeLessThanOrEqualTo(3);

        // Anel sólido da moldura deve ter preenchimento roxo (#6A1B9A com alfa 140), reduzindo fortemente o canal verde
        (holeCenterPixel.Green - solidRingPixel.Green).Should().BeGreaterThan(50);
    }

    [Fact]
    public void Stage28XamlAudit_ContainsNestingWorkspaceControls_FixedExportFooter_AndDualEntryPoints()
    {
        string repoRoot = FindRepoRoot();
        string appRoot = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App");
        XNamespace xNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        // 1. NestingWorkspaceView.xaml
        XDocument nestingDoc = XDocument.Load(Path.Combine(appRoot, "Views", "Nesting", "NestingWorkspaceView.xaml"));
        string nestingXml = nestingDoc.ToString();
        nestingXml.Should().Contain("BackFromNestingBtn");
        nestingXml.Should().Contain("ImportExternalFilesBtn");
        nestingXml.Should().Contain("PartsQueueItemsControl");
        nestingXml.Should().Contain("ActiveBoxBatchCard");
        nestingXml.Should().Contain("ViewportHost");
        nestingXml.Should().Contain("NestingCanvasImage");
        nestingXml.Should().Contain("UnplacedWarningBanner");
        nestingXml.Should().Contain("SheetWidthInput");
        nestingXml.Should().Contain("SheetHeightInput");
        nestingXml.Should().Contain("MarginInput");
        nestingXml.Should().Contain("SpacingInput");
        nestingXml.Should().Contain("RunOptimizationBtn");
        nestingXml.Should().Contain("NestingExportFooter");
        nestingXml.Should().Contain("ExportSvgBtn");
        nestingXml.Should().Contain("ExportDxfBtn");
        nestingXml.Should().Contain("ExportPdfBtn");
        nestingXml.Should().Contain("ExportZipBtn");

        // Rodapé de exportação deve permanecer fixo fora do ScrollViewer
        XElement exportFooter = nestingDoc.Descendants()
            .First(e => (string?)e.Attribute(xNs + "Name") == "NestingExportFooter");
        exportFooter.Ancestors().Any(a => a.Name.LocalName == "ScrollViewer").Should().BeFalse();

        // 2. CatalogHomeView.xaml (portas de entrada independentes: sidebar + cartão de topo)
        XDocument catalogDoc = XDocument.Load(Path.Combine(appRoot, "Views", "CatalogHomeView.xaml"));
        string catalogXml = catalogDoc.ToString();
        catalogXml.Should().Contain("NavNestingBtn");
        catalogXml.Should().Contain("Otimizador de Corte");
        catalogXml.Should().Contain("SVG/DXF");
        catalogXml.Should().Contain("OpenNestingFromCatalogBtn");
        catalogXml.Should().Contain("Otimizador de Corte (SVG / DXF)");

        // 3. ManufacturingAreaRightPanel.xaml (porta de entrada da Etapa 3)
        XDocument mfgDoc = XDocument.Load(Path.Combine(appRoot, "Views", "Areas", "ManufacturingAreaRightPanel.xaml"));
        string mfgXml = mfgDoc.ToString();
        mfgXml.Should().Contain("OpenNestingFromManufacturingBtn");
        mfgXml.Should().Contain("Otimizar Chapas em Retalho / Lote (Nesting)");
        mfgXml.Should().Contain("BackToPersonalizationBtn");

        // 4. MainWindow.xaml (camada paralela + entradas de 1º nível nos menus Arquivo, Projeto e Ferramentas com Alt+4)
        XDocument mainDoc = XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        string mainXml = mainDoc.ToString();
        mainXml.Should().Contain("WorkspaceShell");
        mainXml.Should().Contain("NestingView");
        mainXml.Should().Contain("MenuFileNesting");
        mainXml.Should().Contain("MenuProjNesting");
        mainXml.Should().Contain("MenuToolsNesting");
        mainXml.Should().Contain("Alt+4");
        mainXml.Should().Contain("tools.nesting");

        // 5. WorkspaceSplitShell.xaml (acesso direto no cabeçalho ao lado do stepper 1→2→3)
        XDocument shellDoc = XDocument.Load(Path.Combine(appRoot, "Views", "WorkspaceSplitShell.xaml"));
        string shellXml = shellDoc.ToString();
        shellXml.Should().Contain("HeaderOpenNestingBtn");
        shellXml.Should().Contain("Otimizador");
    }

    [Fact]
    public async Task OpenNestingIndependent_PreservesAllFourOrigins_AndDoesNotDiscardQueuedExternalParts()
    {
        MainWindowViewModel mainVm = CreateMainVm();

        // 1. Origem: Catálogo via comando unificado (Alt+4 / Menu)
        await mainVm.DispatchCommandAsync(DesktopCommandIds.ToolsNesting);
        mainVm.IsInNestingWorkspace.Should().BeTrue();
        mainVm.NestingOrigin.Should().Be(NestingEntryOrigin.Catalog);
        mainVm.NestingBackButtonLabel.Should().Be("← Voltar ao Catálogo");
        mainVm.Nesting.HasActiveBoxProject.Should().BeFalse();

        // Importa peça SVG externa na fila
        string svgPath = WriteSampleSvg("externa_persistente.svg", 80.0, 50.0);
        int imported = await mainVm.Nesting.ImportExternalFilesAsync([svgPath]);
        imported.Should().Be(1);
        mainVm.Nesting.Parts.Should().HaveCount(1);

        mainVm.ReturnFromNestingCommand.Execute(null);
        mainVm.IsInNestingWorkspace.Should().BeFalse();
        mainVm.InDesigner.Should().BeFalse();

        // 2. Origem: Modelagem (Etapa 1)
        mainVm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        mainVm.ActiveArea.Should().Be(WorkspaceArea.Modeling);
        mainVm.OpenNestingIndependentCommand.Execute(null);
        mainVm.IsInNestingWorkspace.Should().BeTrue();
        mainVm.NestingOrigin.Should().Be(NestingEntryOrigin.Modeling);
        mainVm.NestingBackButtonLabel.Should().Be("← Voltar para Modelagem");
        mainVm.Nesting.HasActiveBoxProject.Should().BeTrue();
        mainVm.Nesting.Parts.Should().HaveCount(1); // peça externa preservada intacta

        mainVm.ReturnFromNestingCommand.Execute(null);
        mainVm.IsInNestingWorkspace.Should().BeFalse();
        mainVm.InDesigner.Should().BeTrue();
        mainVm.ActiveArea.Should().Be(WorkspaceArea.Modeling);

        // 3. Origem: Personalização (Etapa 2)
        mainVm.SetWorkspaceArea(WorkspaceArea.Personalization);
        await mainVm.DispatchCommandAsync(DesktopCommandIds.ToolsNesting);
        mainVm.IsInNestingWorkspace.Should().BeTrue();
        mainVm.NestingOrigin.Should().Be(NestingEntryOrigin.Personalization);
        mainVm.NestingBackButtonLabel.Should().Be("← Voltar para Personalização");
        mainVm.Nesting.Parts.Should().HaveCount(1);

        mainVm.ReturnFromNestingCommand.Execute(null);
        mainVm.IsInNestingWorkspace.Should().BeFalse();
        mainVm.InDesigner.Should().BeTrue();
        mainVm.ActiveArea.Should().Be(WorkspaceArea.Personalization);

        // 4. Origem: Fabricação (Etapa 3)
        mainVm.SetWorkspaceArea(WorkspaceArea.Fabrication);
        mainVm.OpenNestingIndependentCommand.Execute(null);
        mainVm.IsInNestingWorkspace.Should().BeTrue();
        mainVm.NestingOrigin.Should().Be(NestingEntryOrigin.Manufacturing);
        mainVm.NestingBackButtonLabel.Should().Be("← Voltar para Fabricação");
        mainVm.Nesting.Parts.Should().HaveCount(1);

        mainVm.ReturnFromNestingCommand.Execute(null);
        mainVm.IsInNestingWorkspace.Should().BeFalse();
        mainVm.InDesigner.Should().BeTrue();
        mainVm.ActiveArea.Should().Be(WorkspaceArea.Fabrication);
    }
}
