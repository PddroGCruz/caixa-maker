using System.Xml.Linq;
using CaixaMaker.Core.Storage;
using CaixaMaker.Presentation.Diagnostics;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class CadDesignSystemAndScreenshotTests
{
    private static string ResolveRepoRoot()
    {
        string? versionFile = AppVersion.ResolveVersionFile(AppContext.BaseDirectory);
        versionFile.Should().NotBeNull("o arquivo VERSION na raiz do repositório deve estar acessível");
        return Path.GetDirectoryName(versionFile!)!;
    }

    private static Dictionary<string, string> ReadBrushColors(XElement themeDict)
    {
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace winNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        return themeDict
            .Elements(winNs + "SolidColorBrush")
            .ToDictionary(
                el => el.Attribute(xNs + "Key")!.Value,
                el => el.Attribute("Color")!.Value,
                StringComparer.Ordinal);
    }

    [Fact]
    public void ThemeResources_EnforcesGraphiteCadPaletteAndCompactTypographyTokens()
    {
        string repoRoot = ResolveRepoRoot();
        string themePath = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Themes",
            "ThemeResources.xaml");

        File.Exists(themePath).Should().BeTrue();

        XDocument doc = XDocument.Load(themePath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace winNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement? root = doc.Root;
        root.Should().NotBeNull();

        XElement? themeDictsWrapper = root!.Element(winNs + "ResourceDictionary.ThemeDictionaries");
        themeDictsWrapper.Should().NotBeNull();

        List<XElement> dictionaries = themeDictsWrapper!.Elements(winNs + "ResourceDictionary").ToList();
        XElement? defaultTheme = dictionaries.FirstOrDefault(d => d.Attribute(xNs + "Key")?.Value == "Default");
        XElement? darkTheme = dictionaries.FirstOrDefault(d => d.Attribute(xNs + "Key")?.Value == "Dark");
        XElement? lightTheme = dictionaries.FirstOrDefault(d => d.Attribute(xNs + "Key")?.Value == "Light");

        defaultTheme.Should().NotBeNull();
        darkTheme.Should().NotBeNull();
        lightTheme.Should().NotBeNull();

        Dictionary<string, string> darkBrushes = ReadBrushColors(defaultTheme!);
        darkBrushes["CmViewportCanvasBrush"].Should().Be("#111317");
        darkBrushes["CmShellBackgroundBrush"].Should().Be("#14171C");
        darkBrushes["CmPanelBackgroundBrush"].Should().Be("#181C22");
        darkBrushes["CmSectionHeaderBackgroundBrush"].Should().Be("#1E232B");
        darkBrushes["CmInputBackgroundBrush"].Should().Be("#101317");
        darkBrushes["CmElevatedSurfaceBrush"].Should().Be("#222831");
        darkBrushes["CmHairlineBorderBrush"].Should().Be("#2A303A");
        darkBrushes["CmSubtleBorderBrush"].Should().Be("#343C49");
        darkBrushes["CmAccentPrimaryBrush"].Should().Be("#16A34A");
        darkBrushes["CmAccentPrimaryHoverBrush"].Should().Be("#15803D");
        darkBrushes["CmAccentHighlightBrush"].Should().Be("#22C55E");
        darkBrushes["CmAccentSelectionBrush"].Should().Be("#1F16A34A");
        darkBrushes["CmStatusSuccessBrush"].Should().Be("#22C55E");
        darkBrushes["CmStatusWarningBrush"].Should().Be("#F59E0B");
        darkBrushes["CmStatusDangerBrush"].Should().Be("#EF4444");

        Dictionary<string, string> explicitDark = ReadBrushColors(darkTheme!);
        explicitDark.Should().BeEquivalentTo(darkBrushes);

        Dictionary<string, string> lightBrushes = ReadBrushColors(lightTheme!);
        lightBrushes["CmViewportCanvasBrush"].Should().Be("#DFE4EC");
        lightBrushes["CmShellBackgroundBrush"].Should().Be("#EAEFF5");
        lightBrushes["CmPanelBackgroundBrush"].Should().Be("#F4F6F9");
        lightBrushes["CmSectionHeaderBackgroundBrush"].Should().Be("#E4E9F0");
        lightBrushes["CmInputBackgroundBrush"].Should().Be("#FFFFFF");
        lightBrushes["CmHairlineBorderBrush"].Should().Be("#C8D0DC");
        lightBrushes["CmAccentPrimaryBrush"].Should().Be("#16A34A");

        root.Elements(winNs + "CornerRadius")
            .FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == "CmControlCornerRadius")?.Value
            .Should().Be("2");
        root.Elements(winNs + "CornerRadius")
            .FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == "CmPanelCornerRadius")?.Value
            .Should().Be("0");
        root.Elements(winNs + "CornerRadius")
            .FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == "CmHudCornerRadius")?.Value
            .Should().Be("4");

        root.Elements(winNs + "FontFamily")
            .FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == "CmMonoFontFamily")?.Value
            .Should().Contain("Cascadia Mono");
        root.Elements(winNs + "FontFamily")
            .FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == "CmUiFontFamily")?.Value
            .Should().Contain("Segoe UI");

        root.Elements(xNs + "Double")
            .FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == "CmFontSizeCaption")?.Value
            .Should().Be("11");
        root.Elements(xNs + "Double")
            .FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == "CmFontSizeBody")?.Value
            .Should().Be("12");
        root.Elements(xNs + "Double")
            .FirstOrDefault(e => e.Attribute(xNs + "Key")?.Value == "CmFontSizePanelTitle")?.Value
            .Should().Be("13");
    }

    [Theory]
    [InlineData("CadSectionPane")]
    [InlineData("CadPropertyRow")]
    [InlineData("CadNumericInput")]
    [InlineData("CadToolbarButton")]
    [InlineData("CadToolbarGroup")]
    [InlineData("CadMetricTable")]
    [InlineData("CadStatusBadge")]
    public void CadPrimitives_AllControlsExistWithValidXamlAndCodeBehind(string controlName)
    {
        string repoRoot = ResolveRepoRoot();
        string primitivesDir = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Controls",
            "CadPrimitives");

        string xamlPath = Path.Combine(primitivesDir, $"{controlName}.xaml");
        string csPath = Path.Combine(primitivesDir, $"{controlName}.xaml.cs");

        File.Exists(xamlPath).Should().BeTrue();
        File.Exists(csPath).Should().BeTrue();

        XDocument xaml = XDocument.Load(xamlPath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        xaml.Root?.Attribute(xNs + "Class")?.Value
            .Should().Be($"CaixaMaker.App.Controls.CadPrimitives.{controlName}");

        string cs = File.ReadAllText(csPath);
        cs.Should().Contain($"partial class {controlName}");
    }

    [Fact]
    public void ScreenshotManifest_DefinesSixRequiredScenesAndDetectsBlankVsRenderedFrames()
    {
        UiScreenshotManifest.TargetWindowWidth.Should().Be(1360);
        UiScreenshotManifest.TargetWindowHeight.Should().Be(860);
        UiScreenshotManifest.DefaultScenes.Select(s => s.FileName).Should().Equal(
            "01-catalogo-dark.png",
            "02-modelagem-dark.png",
            "03-personalizacao-split-dark.png",
            "04-fabricacao-dark.png",
            "05-modelagem-light.png",
            "06-sobre-institucional-dark.png");

        // Frame totalmente preto -> inválido
        byte[] blackFrame = new byte[64 * 64 * 4];
        for (int i = 3; i < blackFrame.Length; i += 4) blackFrame[i] = 255;
        UiScreenshotManifest.EvaluateBgraFrameContent(blackFrame, 64, 64).IsNonBlank.Should().BeFalse();

        // Frame totalmente branco -> inválido
        byte[] whiteFrame = Enumerable.Repeat((byte)255, 64 * 64 * 4).ToArray();
        UiScreenshotManifest.EvaluateBgraFrameContent(whiteFrame, 64, 64).IsNonBlank.Should().BeFalse();

        // Frame com UI realista (gradiente + área de destaque) -> válido
        byte[] uiFrame = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int idx = (y * 64 + x) * 4;
                uiFrame[idx] = (byte)(x * 3);
                uiFrame[idx + 1] = (byte)(y * 3 + (x > 40 ? 60 : 0));
                uiFrame[idx + 2] = 24;
                uiFrame[idx + 3] = 255;
            }
        }
        UiScreenshotManifest.EvaluateBgraFrameContent(uiFrame, 64, 64).IsNonBlank.Should().BeTrue();
    }

    [Fact]
    public void ScreenshotCaptureCli_IsWiredInProgramAndApp()
    {
        string repoRoot = ResolveRepoRoot();
        string appDir = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App");

        string programCs = File.ReadAllText(Path.Combine(appDir, "Program.cs"));
        string appCs = File.ReadAllText(Path.Combine(appDir, "App.xaml.cs"));
        string captureServiceCs = File.ReadAllText(Path.Combine(appDir, "Diagnostics", "UiScreenshotCaptureService.cs"));

        programCs.Should().Contain("--capture-ui-screenshots");
        appCs.Should().Contain("UiScreenshotCaptureService.RunAsync");
        captureServiceCs.Should().Contain("TryCaptureWindowBgra");
        captureServiceCs.Should().Contain("RenderTargetBitmap");
    }

    [Fact]
    public void CatalogHomeView_RebalancesBrandingAndUsesCompactCadEngineeringLayout()
    {
        string repoRoot = ResolveRepoRoot();
        string catalogXamlPath = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Views",
            "CatalogHomeView.xaml");
        string catalogCsPath = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Views",
            "CatalogHomeView.xaml.cs");

        string xamlText = File.ReadAllText(catalogXamlPath);
        string csText = File.ReadAllText(catalogCsPath);

        // Coexistência das identidades sem banners institucionais pesados no fluxo diário
        xamlText.Should().NotContain("ifmaker-jurua.png");
        xamlText.Should().NotContain("ifac-horizontal.png");
        xamlText.Should().NotContain("UniformGridLayout");
        xamlText.Should().NotContain("CmBrandSurfaceBrush");
        xamlText.Should().Contain("caixa-maker-symbol.png");
        xamlText.Should().Contain("Sobre &amp; Identidade Institucional");
        xamlText.Should().Contain("Width=\"204\"");
        xamlText.Should().Contain("Height=\"36\"");
        xamlText.Should().Contain("Height=\"24\"");
        xamlText.Should().Contain("MODELOS PARAMÉTRICOS DE CAIXAS");
        xamlText.Should().Contain("BIBLIOTECA DE PROJETOS (.CMAKER)");
        xamlText.Should().Contain("DIMENSÕES (MM)");
        xamlText.Should().Contain("CadThemeSwitcher");
        xamlText.Should().Contain("CadStatusBadge");

        // Code-behind deve montar fichas com CadStatusBadge e CadMetricTable no grid 2 colunas
        csText.Should().Contain("CatalogModelsGrid");
        csText.Should().Contain("CadStatusBadge");
        csText.Should().Contain("CadMetricTable");
        csText.Should().Contain("ExportSavedProjectPackageAsync");

        XDocument doc = XDocument.Load(catalogXamlPath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace winNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement? modelsGrid = doc.Descendants(winNs + "Grid")
            .FirstOrDefault(g => g.Attribute(xNs + "Name")?.Value == "CatalogModelsGrid");
        modelsGrid.Should().NotBeNull();

        List<string> modelGridCols = modelsGrid!
            .Element(winNs + "Grid.ColumnDefinitions")!
            .Elements(winNs + "ColumnDefinition")
            .Select(c => c.Attribute("Width")?.Value ?? string.Empty)
            .ToList();
        modelGridCols.Should().Equal("*", "*");
    }

    [Fact]
    public void WorkspaceSplitShell_UsesCadPrimitivesAndEngineeringFrameHeights()
    {
        string repoRoot = ResolveRepoRoot();
        string shellXamlPath = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Views",
            "WorkspaceSplitShell.xaml");
        string shellCsPath = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Views",
            "WorkspaceSplitShell.xaml.cs");

        string xamlText = File.ReadAllText(shellXamlPath);
        string csText = File.ReadAllText(shellCsPath);

        xamlText.Should().NotContain("AppBarSeparator");
        xamlText.Should().NotContain("DimensionsBadgeText");
        xamlText.Should().Contain("caixa-maker-symbol.png");
        xamlText.Should().Contain("Height=\"34\"");
        xamlText.Should().Contain("Height=\"48\"");
        xamlText.Should().Contain("Height=\"26\"");
        xamlText.Should().Contain("GroupLabel=\"ARQUIVO\"");
        xamlText.Should().Contain("Height=\"24\"");
        xamlText.Should().Contain("Alt+1..3");
        xamlText.Should().Contain("IsDestructive=\"True\"");
        xamlText.Should().Contain("UNID: mm (1:1)");
        xamlText.Should().Contain("CadToolbarGroup");
        xamlText.Should().Contain("CadToolbarButton");
        xamlText.Should().Contain("CadStatusBadge");
        xamlText.Should().Contain("CadThemeSwitcher");
        xamlText.Should().Contain("3D+2D");

        csText.Should().Contain("WorkspaceHeaderProjectText");
        csText.Should().Contain("StatusBarMaterialText");
        csText.Should().Contain("StatusBarDimensionsText");
        csText.Should().Contain("StatusBarPiecesText");
        csText.Should().Contain("HeaderThemeSwitcher.Bind");
        csText.Should().Contain("RotateSelected90");
    }

    [Fact]
    public void ThemeResources_IncludesPhase3CadFormStylesAndContrastTokens()
    {
        string repoRoot = ResolveRepoRoot();
        string themePath = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Themes",
            "ThemeResources.xaml");

        XDocument doc = XDocument.Load(themePath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace winNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        List<string> styleKeys = doc.Root!
            .Elements(winNs + "Style")
            .Select(s => s.Attribute(xNs + "Key")?.Value ?? string.Empty)
            .ToList();

        styleKeys.Should().Contain(["CadComboBoxStyle", "CadTextBoxStyle", "CadSegmentedButtonStyle", "CadActionButtonStyle"]);

        List<XElement> dicts = doc.Root!
            .Element(winNs + "ResourceDictionary.ThemeDictionaries")!
            .Elements(winNs + "ResourceDictionary")
            .ToList();

        foreach (string themeKey in new[] { "Default", "Dark", "Light" })
        {
            XElement dict = dicts.Single(d => d.Attribute(xNs + "Key")?.Value == themeKey);
            Dictionary<string, string> brushes = ReadBrushColors(dict);
            brushes.Should().ContainKeys(
                "TextControlBackground",
                "TextControlForeground",
                "TextControlBorderBrush",
                "ComboBoxBackground",
                "ComboBoxForeground",
                "ComboBoxBorderBrush",
                "ComboBoxDropDownBackground",
                "ComboBoxItemForeground",
                "ComboBoxItemBackgroundSelected",
                "ButtonBackgroundPointerOver");
        }
    }

    [Theory]
    [InlineData("ModelingAreaLeftPanel")]
    [InlineData("ModelingAreaRightPanel")]
    [InlineData("PersonalizationAreaLeftPanel")]
    [InlineData("PersonalizationAreaRightPanel")]
    [InlineData("ManufacturingAreaLeftPanel")]
    [InlineData("ManufacturingAreaRightPanel")]
    public void AreaPanels_Phase3_UseCadPropertyGridPrimitivesAndComboOptions(string panelName)
    {
        string repoRoot = ResolveRepoRoot();
        string areasDir = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Views",
            "Areas");

        string xamlPath = Path.Combine(areasDir, $"{panelName}.xaml");
        string csPath = Path.Combine(areasDir, $"{panelName}.xaml.cs");

        File.Exists(xamlPath).Should().BeTrue();
        File.Exists(csPath).Should().BeTrue();

        string xaml = File.ReadAllText(xamlPath);
        string cs = File.ReadAllText(csPath);

        xaml.Should().Contain("cad:CadSectionPane");
        xaml.Should().NotContain("CadCardBorderStyle");
        cs.Should().NotContain("EnumOption<");

        switch (panelName)
        {
            case "ModelingAreaLeftPanel":
                xaml.Should().Contain("MODELO ESTRUTURAL");
                xaml.Should().Contain("DIMENSÕES EXTERNAS (MM)");
                xaml.Should().Contain("MATERIAL E AJUSTE CNC (KERF)");
                xaml.Should().Contain("OPÇÕES DE VISUALIZAÇÃO 3D");
                xaml.Should().Contain("cad:CadNumericInput");
                xaml.Should().Contain("cad:CadPropertyRow");
                break;
            case "ModelingAreaRightPanel":
                xaml.Should().Contain("DIAGNÓSTICO GEOMÉTRICO");
                xaml.Should().Contain("ESPECIFICAÇÕES CALCULADAS");
                xaml.Should().Contain("ÁRVORE DE CHAPAS");
                xaml.Should().Contain("cad:CadStatusBadge");
                xaml.Should().Contain("cad:CadMetricTable");
                break;
            case "PersonalizationAreaLeftPanel":
                xaml.Should().Contain("SELETOR DE SUPERFÍCIE (FACE ATIVA)");
                xaml.Should().Contain("INSERIR ELEMENTO CNC NA FACE");
                xaml.Should().Contain("ELEMENTOS NA FACE");
                xaml.Should().Contain("CadTextBoxStyle");
                xaml.Should().Contain("CadComboBoxStyle");
                cs.Should().Contain("ComboOption<");
                break;
            case "PersonalizationAreaRightPanel":
                xaml.Should().Contain("ELEMENTO SELECIONADO");
                xaml.Should().Contain("TRANSFORMAÇÃO E GEOMETRIA (MM)");
                xaml.Should().Contain("ANCORAGEM E CAMADA CAM");
                xaml.Should().Contain("DIAGNÓSTICO SAT DO ELEMENTO");
                xaml.Should().Contain("CadComboBoxStyle");
                xaml.Should().Contain("cad:CadNumericInput");
                cs.Should().Contain("ComboOption<");
                cs.Should().Contain("MoveSelectedElementToSurface");
                break;
            case "ManufacturingAreaLeftPanel":
                xaml.Should().Contain("RESUMO DE FABRICAÇÃO 1:1");
                xaml.Should().Contain("MAPA DE CAMADAS CNC (6 LAYERS)");
                xaml.Should().Contain("CHAPAS PARA USINAGEM");
                xaml.Should().Contain("cad:CadMetricTable");
                break;
            case "ManufacturingAreaRightPanel":
                xaml.Should().Contain("AUDITORIA PRÉ-CORTE (SAT + KERF)");
                xaml.Should().Contain("ESCOPO E EXPORTAÇÃO 1:1 (MM)");
                xaml.Should().Contain("ARQUIVOS GERADOS NA SESSÃO");
                xaml.Should().Contain("cad:CadStatusBadge");
                xaml.Should().Contain("CadComboBoxStyle");
                cs.Should().Contain("ComboOption<");
                break;
        }
    }

    [Fact]
    public void Phase4_ViewportsSplitAndDialogs_EnforceCompactCadHudAndEngineeringStandards()
    {
        string repoRoot = ResolveRepoRoot();
        string appRoot = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App");

        string viewer3DXaml = File.ReadAllText(Path.Combine(appRoot, "Controls", "BoxViewer3DControl.xaml"));
        string viewer3DCs = File.ReadAllText(Path.Combine(appRoot, "Controls", "BoxViewer3DControl.xaml.cs"));
        viewer3DXaml.Should().NotContain("AppBarSeparator");
        viewer3DXaml.Should().NotContain("Clique na caixa para selecionar face");
        viewer3DXaml.Should().Contain("CmViewportCanvasBrush");
        viewer3DXaml.Should().Contain("CmSectionHeaderBackgroundBrush");
        viewer3DXaml.Should().Contain("Width=\"54\"");
        viewer3DXaml.Should().Contain("Height=\"54\"");
        viewer3DXaml.Should().Contain("Width=\"80\"");
        viewer3DXaml.Should().Contain("Tag=\"0\"");
        viewer3DXaml.Should().Contain("Tag=\"90\"");
        viewer3DCs.Should().Contain("GizmoSizePx = 54");
        viewer3DCs.Should().Contain("FACE ATIVA:");
        viewer3DCs.Should().Contain("PEÇA:");
        viewer3DCs.Should().Contain("ESCALA 1:1");

        string editor2DXaml = File.ReadAllText(Path.Combine(appRoot, "Controls", "Surface2DEditorControl.xaml"));
        string editor2DCs = File.ReadAllText(Path.Combine(appRoot, "Controls", "Surface2DEditorControl.xaml.cs"));
        editor2DXaml.Should().NotContain("AppBarSeparator");
        editor2DXaml.Should().Contain("Height=\"28\"");
        editor2DXaml.Should().Contain("CmSectionHeaderBackgroundBrush");
        editor2DXaml.Should().Contain("Área segura");
        editor2DXaml.Should().Contain("Encaixes / Restrito");
        editor2DXaml.Should().Contain("Ajustar Vista");
        editor2DXaml.Should().Contain("Centralizar");
        editor2DXaml.Should().Contain("Duplicar");
        editor2DXaml.Should().Contain("Excluir");
        editor2DCs.Should().Contain("UpdateResponsiveHeaderText");

        string shellXaml = File.ReadAllText(Path.Combine(appRoot, "Views", "WorkspaceSplitShell.xaml"));
        string shellCs = File.ReadAllText(Path.Combine(appRoot, "Views", "WorkspaceSplitShell.xaml.cs"));
        shellXaml.Should().Contain("Viewport3DRowDef");
        shellXaml.Should().Contain("ViewportSplitDividerRowDef");
        shellXaml.Should().Contain("Viewport2DRowDef");
        shellXaml.Should().Contain("ViewportSplitDivider");
        shellCs.Should().Contain("ViewportSplitDivider.Visibility = Visibility.Visible");

        string dialogsCs = File.ReadAllText(Path.Combine(appRoot, "Views", "Dialogs", "AppDialogs.cs"));
        dialogsCs.Should().Contain("caixa-maker-symbol.png");
        dialogsCs.Should().Contain("ifmaker-jurua.png");
        dialogsCs.Should().Contain("ifac-horizontal.png");
        dialogsCs.Should().Contain("BuildIdentityCard");
        dialogsCs.Should().Contain("BuildShortcutCell");
        dialogsCs.Should().Contain("CmMonoFontFamily");
        dialogsCs.Should().Contain("PrimaryAccentButtonStyle");
        dialogsCs.Should().Contain("CadSecondaryButtonStyle");
        dialogsCs.Should().Contain("DangerOutlineButtonStyle");
        dialogsCs.Should().Contain("ResolveTheme");
    }
}
