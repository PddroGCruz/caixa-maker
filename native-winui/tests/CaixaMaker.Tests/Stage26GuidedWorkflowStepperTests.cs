using System.Xml.Linq;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage26GuidedWorkflowStepperTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public Stage26GuidedWorkflowStepperTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Stage26_" + Guid.NewGuid().ToString("N")[..10]);
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

    private MainWindowViewModel CreateMainVmInDesigner()
    {
        var vm = new MainWindowViewModel(
            _paths,
            new NullNativeFileDialogService(),
            BoxModelRegistry.Default,
            ResolvedAppTheme.Light);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        return vm;
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
    public void StepperCommands_AdvanceAndReturnSequentiallyBetweenSteps123()
    {
        MainWindowViewModel vm = CreateMainVmInDesigner();

        // Estado inicial: Etapa 1 (Modelagem)
        vm.ActiveArea.Should().Be(WorkspaceArea.Modeling);
        vm.ActiveWorkspaceStepNumber.Should().Be(1);
        vm.IsModelingStepActive.Should().BeTrue();
        vm.IsPersonalizationStepActive.Should().BeFalse();
        vm.IsManufacturingStepActive.Should().BeFalse();
        vm.IsModelingStepCompleted.Should().BeFalse();
        vm.IsPersonalizationStepCompleted.Should().BeFalse();
        vm.CanNavigatePreviousWorkspace.Should().BeFalse();
        vm.CanNavigateNextWorkspace.Should().BeTrue();
        vm.NavigatePreviousWorkspaceCommand.CanExecute(null).Should().BeFalse();
        vm.NavigateNextWorkspaceCommand.CanExecute(null).Should().BeTrue();
        vm.NextWorkspaceButtonLabel.Should().Be("Avançar para Personalização →");
        vm.PreviousWorkspaceButtonLabel.Should().Be("← Modelagem");
        vm.WorkflowGuidanceHint.Should().Contain("Etapa 1/3");

        // 1 -> 2
        vm.NavigateNextWorkspaceCommand.Execute(null);

        vm.ActiveArea.Should().Be(WorkspaceArea.Personalization);
        vm.ActiveWorkspaceStepNumber.Should().Be(2);
        vm.IsModelingStepActive.Should().BeFalse();
        vm.IsPersonalizationStepActive.Should().BeTrue();
        vm.IsManufacturingStepActive.Should().BeFalse();
        vm.IsModelingStepCompleted.Should().BeTrue();
        vm.IsPersonalizationStepCompleted.Should().BeFalse();
        vm.CanNavigatePreviousWorkspace.Should().BeTrue();
        vm.CanNavigateNextWorkspace.Should().BeTrue();
        vm.NavigatePreviousWorkspaceCommand.CanExecute(null).Should().BeTrue();
        vm.NavigateNextWorkspaceCommand.CanExecute(null).Should().BeTrue();
        vm.PreviousWorkspaceButtonLabel.Should().Be("← Modelagem");
        vm.NextWorkspaceButtonLabel.Should().Be("Avançar para Fabricação →");
        vm.WorkflowGuidanceHint.Should().Contain("Etapa 2/3");

        // 2 -> 3
        vm.NavigateNextWorkspaceCommand.Execute(null);

        vm.ActiveArea.Should().Be(WorkspaceArea.Fabrication);
        vm.ActiveWorkspaceStepNumber.Should().Be(3);
        vm.IsModelingStepActive.Should().BeFalse();
        vm.IsPersonalizationStepActive.Should().BeFalse();
        vm.IsManufacturingStepActive.Should().BeTrue();
        vm.IsModelingStepCompleted.Should().BeTrue();
        vm.IsPersonalizationStepCompleted.Should().BeTrue();
        vm.CanNavigatePreviousWorkspace.Should().BeTrue();
        vm.CanNavigateNextWorkspace.Should().BeFalse();
        vm.NavigatePreviousWorkspaceCommand.CanExecute(null).Should().BeTrue();
        vm.NavigateNextWorkspaceCommand.CanExecute(null).Should().BeFalse();
        vm.PreviousWorkspaceButtonLabel.Should().Be("← Personalização");
        vm.NextWorkspaceButtonLabel.Should().Be("Avançar para Personalização →");
        vm.WorkflowGuidanceHint.Should().Contain("Etapa 3/3");

        // Clamping superior ao chamar novamente em Etapa 3
        vm.NavigateNextWorkspace();
        vm.ActiveArea.Should().Be(WorkspaceArea.Fabrication);

        // 3 -> 2 -> 1
        vm.NavigatePreviousWorkspaceCommand.Execute(null);
        vm.ActiveArea.Should().Be(WorkspaceArea.Personalization);
        vm.ActiveWorkspaceStepNumber.Should().Be(2);

        vm.NavigatePreviousWorkspaceCommand.Execute(null);
        vm.ActiveArea.Should().Be(WorkspaceArea.Modeling);
        vm.ActiveWorkspaceStepNumber.Should().Be(1);

        // Clamping inferior ao chamar novamente em Etapa 1
        vm.NavigatePreviousWorkspace();
        vm.ActiveArea.Should().Be(WorkspaceArea.Modeling);
    }

    [Fact]
    public void SkipToManufacturingCommand_JumpsDirectlyFromStep1ToStep3()
    {
        MainWindowViewModel vm = CreateMainVmInDesigner();

        vm.ActiveArea.Should().Be(WorkspaceArea.Modeling);
        vm.SkipToManufacturingCommand.Execute(null);

        vm.ActiveArea.Should().Be(WorkspaceArea.Fabrication);
        vm.ActiveWorkspaceStepNumber.Should().Be(3);
        vm.IsModelingStepCompleted.Should().BeTrue();
        vm.IsPersonalizationStepCompleted.Should().BeTrue();
        vm.CanNavigateNextWorkspace.Should().BeFalse();
        vm.CanNavigatePreviousWorkspace.Should().BeTrue();

        // Retorno sequencial cai na Etapa 2 (Personalização)
        vm.NavigatePreviousWorkspaceCommand.Execute(null);
        vm.ActiveArea.Should().Be(WorkspaceArea.Personalization);
        vm.ActiveWorkspaceStepNumber.Should().Be(2);
    }

    [Fact]
    public void DirectAreaSelection_NotifiesAllWorkflowPropertiesAndMaintainsCompatibility()
    {
        MainWindowViewModel vm = CreateMainVmInDesigner();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                changed.Add(e.PropertyName);
            }
        };

        vm.SetWorkspaceArea(WorkspaceArea.Fabrication);

        vm.ActiveWorkspaceStepNumber.Should().Be(3);
        changed.Should().Contain(new[]
        {
            nameof(MainWindowViewModel.ActiveWorkspaceStepNumber),
            nameof(MainWindowViewModel.IsModelingStepActive),
            nameof(MainWindowViewModel.IsPersonalizationStepActive),
            nameof(MainWindowViewModel.IsManufacturingStepActive),
            nameof(MainWindowViewModel.IsModelingStepCompleted),
            nameof(MainWindowViewModel.IsPersonalizationStepCompleted),
            nameof(MainWindowViewModel.CanNavigatePreviousWorkspace),
            nameof(MainWindowViewModel.CanNavigateNextWorkspace),
            nameof(MainWindowViewModel.NextWorkspaceButtonLabel),
            nameof(MainWindowViewModel.PreviousWorkspaceButtonLabel),
            nameof(MainWindowViewModel.WorkflowGuidanceHint),
        });

        vm.SetWorkspaceArea(WorkspaceArea.Personalization);
        vm.ActiveWorkspaceStepNumber.Should().Be(2);
        vm.IsModelingStepCompleted.Should().BeTrue();
        vm.IsPersonalizationStepCompleted.Should().BeFalse();
    }

    [Fact]
    public void StepTransitions_PreserveBoxDimensionsAndPersonalizationElements()
    {
        MainWindowViewModel vm = CreateMainVmInDesigner();
        vm.Box.BoxWidth = 245.0;
        vm.Box.BoxDepth = 175.0;
        vm.Box.BoxHeight = 115.0;

        vm.NavigateNextWorkspaceCommand.Execute(null);
        vm.Personalization.AddShape(GeometricShapeKind.Circle, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Circle,
            DiameterMm = 28.0,
        });
        vm.Personalization.DraftTextContent = "IFMAKER-26";
        vm.Personalization.AddTextElement();
        int elementCountBefore = vm.Personalization.TotalElementCount;
        elementCountBefore.Should().Be(2);

        // Avança 2 -> 3 e retorna 3 -> 2 -> 1 -> 2
        vm.NavigateNextWorkspaceCommand.Execute(null);
        vm.ActiveArea.Should().Be(WorkspaceArea.Fabrication);

        vm.NavigatePreviousWorkspaceCommand.Execute(null);
        vm.NavigatePreviousWorkspaceCommand.Execute(null);
        vm.ActiveArea.Should().Be(WorkspaceArea.Modeling);

        vm.Box.BoxWidth.Should().Be(245.0);
        vm.Box.BoxDepth.Should().Be(175.0);
        vm.Box.BoxHeight.Should().Be(115.0);

        vm.NavigateNextWorkspaceCommand.Execute(null);
        vm.Personalization.TotalElementCount.Should().Be(elementCountBefore);
    }

    [Fact]
    public void GuidedWorkflowXamlFiles_ContainStepperAndFixedRightPanelFooters()
    {
        string repoRoot = FindRepoRoot();
        string viewsRoot = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App", "Views");

        // 1. WorkspaceSplitShell.xaml: Stepper 1 -> 2 -> 3 e StatusGuidanceHintText
        string shellPath = Path.Combine(viewsRoot, "WorkspaceSplitShell.xaml");
        XDocument shellDoc = XDocument.Load(shellPath);
        string shellXml = shellDoc.ToString();
        shellXml.Should().Contain("TabModelingBtn");
        shellXml.Should().Contain("TabPersonalizationBtn");
        shellXml.Should().Contain("TabFabricationBtn");
        shellXml.Should().Contain("TabModelingStepBadge");
        shellXml.Should().Contain("TabPersonalizationStepBadge");
        shellXml.Should().Contain("TabFabricationStepBadge");
        shellXml.Should().Contain("StepperConnector12");
        shellXml.Should().Contain("StepperConnector23");
        shellXml.Should().Contain("StatusGuidanceHintText");

        // 2. ModelingAreaRightPanel.xaml: Footer fixo fora do ScrollViewer
        XDocument modelingDoc = XDocument.Load(Path.Combine(viewsRoot, "Areas", "ModelingAreaRightPanel.xaml"));
        string modelingXml = modelingDoc.ToString();
        modelingXml.Should().Contain("AdvanceToPersonalizationBtn");
        modelingXml.Should().Contain("Avançar para Personalização →");
        modelingXml.Should().Contain("SkipToManufacturingBtn");
        modelingXml.Should().Contain("Pular direto para Fabricação »");
        modelingDoc.Descendants()
            .First(e => (string?)e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == "AdvanceToPersonalizationBtn")
            .Ancestors()
            .Any(a => a.Name.LocalName == "ScrollViewer")
            .Should().BeFalse();

        // 3. PersonalizationAreaRightPanel.xaml: Footer fixo com banner SAT + ← Modelagem + Avançar para Fabricação →
        XDocument persDoc = XDocument.Load(Path.Combine(viewsRoot, "Areas", "PersonalizationAreaRightPanel.xaml"));
        string persXml = persDoc.ToString();
        persXml.Should().Contain("FooterSatWarningBanner");
        persXml.Should().Contain("⚠ Há alertas SAT pendentes — revise antes do corte");
        persXml.Should().Contain("BackToModelingBtn");
        persXml.Should().Contain("← Modelagem");
        persXml.Should().Contain("AdvanceToManufacturingBtn");
        persXml.Should().Contain("Avançar para Fabricação →");
        persDoc.Descendants()
            .First(e => (string?)e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == "AdvanceToManufacturingBtn")
            .Ancestors()
            .Any(a => a.Name.LocalName == "ScrollViewer")
            .Should().BeFalse();

        // 4. ManufacturingAreaRightPanel.xaml: Footer fixo compacto ← Voltar para Personalização
        XDocument mfgDoc = XDocument.Load(Path.Combine(viewsRoot, "Areas", "ManufacturingAreaRightPanel.xaml"));
        string mfgXml = mfgDoc.ToString();
        mfgXml.Should().Contain("BackToPersonalizationBtn");
        mfgXml.Should().Contain("← Voltar para Personalização");
        mfgDoc.Descendants()
            .First(e => (string?)e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == "BackToPersonalizationBtn")
            .Ancestors()
            .Any(a => a.Name.LocalName == "ScrollViewer")
            .Should().BeFalse();
    }
}
