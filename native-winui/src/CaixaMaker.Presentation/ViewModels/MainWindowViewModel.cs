using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaixaMaker.App.ViewModels;

/// <summary>
/// Coordenador global MVVM da janela principal, estado de navegação, menus Win32,
/// persistência de projetos (<c>.cmaker</c>), rascunho automático e comandos CAD/CAM.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly BoxModelRegistry _registry;
    private readonly PortablePackageService _packageService;
    private readonly CmakerPackageService _cmakerPackageService;
    private readonly CmakerMigrationService _migrationService;
    private readonly ProjectLibraryService _library;
    private readonly UiSettingsService _settingsService;
    private readonly AutosaveDraftService _autosaveService;
    private readonly INativeFileDialogService _dialogs;
    private bool _suppressUiSettingsPersist;
    private bool _suppressDirtyTracking;

    public ThemeViewModel Theme { get; }
    public BoxWorkspaceViewModel Box { get; }
    public PersonalizationWorkspaceViewModel Personalization { get; }
    public ManufacturingWorkspaceViewModel Manufacturing { get; }
    public NestingWorkspaceViewModel Nesting { get; }

    public string AppVersionText => AppVersion.Version;
    public string CompanyText => AppVersion.Company;
    public string DeveloperNameText => AppVersion.DeveloperName;
    public string DeveloperEmailText => AppVersion.DeveloperEmail;
    public IReadOnlyList<ShortcutReferenceItem> ShortcutsReference => KeyboardShortcutGuard.GetReferenceShortcuts();

    public ObservableCollection<ProjectSummary> SavedProjects { get; } = new();
    public ObservableCollection<ProjectSummary> RecentProjects { get; } = new();

    [ObservableProperty]
    private bool _inDesigner;

    [ObservableProperty]
    private bool _isInNestingWorkspace;

    [ObservableProperty]
    private NestingEntryOrigin _nestingOrigin = NestingEntryOrigin.Catalog;

    public string NestingBackButtonLabel => NestingOrigin switch
    {
        NestingEntryOrigin.Modeling => "← Voltar para Modelagem",
        NestingEntryOrigin.Personalization => "← Voltar para Personalização",
        NestingEntryOrigin.Manufacturing => "← Voltar para Fabricação",
        _ => "← Voltar ao Catálogo",
    };

    [ObservableProperty]
    private WorkspaceArea _activeArea = WorkspaceArea.Modeling;

    [ObservableProperty]
    private CenterViewMode _centerViewMode = CenterViewMode.Mode3D;

    [ObservableProperty]
    private SplitViewportOrientation _splitOrientation = SplitViewportOrientation.VerticalStacked;

    [ObservableProperty]
    private double _splitViewportRatio = UiSettingsData.DefaultSplitViewportRatio;

    [ObservableProperty]
    private bool _leftSidebarOpen = true;

    [ObservableProperty]
    private bool _rightSidebarOpen = true;

    [ObservableProperty]
    private double _leftSidebarWidth = UiSettingsData.DefaultLeftSidebarWidth;

    [ObservableProperty]
    private double _rightSidebarWidth = UiSettingsData.DefaultRightSidebarWidth;

    [ObservableProperty]
    private bool _toolbarVisible = true;

    [ObservableProperty]
    private bool _isFullscreen;

    [ObservableProperty]
    private string? _savedProjectId;

    [ObservableProperty]
    private string? _savedProjectName;

    [ObservableProperty]
    private string _savedProjectDescription = string.Empty;

    [ObservableProperty]
    private string _savedProjectAuthor = AppVersion.DeveloperName;

    [ObservableProperty]
    private string _savedProjectTagsText = string.Empty;

    [ObservableProperty]
    private string _savedProjectNotes = string.Empty;

    [ObservableProperty]
    private string _savedProjectCreatedAt = string.Empty;

    [ObservableProperty]
    private string _savedProjectUpdatedAt = string.Empty;

    [ObservableProperty]
    private string? _savedProjectPackagePath;

    [ObservableProperty]
    private long _savedProjectPackageSizeBytes;

    [ObservableProperty]
    private int _savedProjectAssetCount;

    [ObservableProperty]
    private string _savedProjectFormatBadge = ".cmaker (v2.0)";

    [ObservableProperty]
    private string _projectSearchQuery = string.Empty;

    [ObservableProperty]
    private int _pendingLegacyMigrationCount;

    [ObservableProperty]
    private bool _isDirty;

    public bool HasPendingLegacyMigrations => PendingLegacyMigrationCount > 0;

    [ObservableProperty]
    private bool _autosaveEnabled = true;

    [ObservableProperty]
    private bool _hasPendingDraft;

    [ObservableProperty]
    private string _draftSummaryText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Pronto — Selecione um modelo ou projeto para iniciar.";

    public AutosaveDraftRecord? PendingDraft { get; private set; }

    public string ActiveModelOrProjectName =>
        !string.IsNullOrWhiteSpace(SavedProjectName)
            ? SavedProjectName!
            : Box.SelectedModel.DisplayName;

    public string WorkspaceHeaderProjectText => string.Format(
        CultureInfo.InvariantCulture,
        "{0} [{1:0.#}×{2:0.#}×{3:0.#} mm • MDF {4:0.#} mm]",
        ActiveModelOrProjectName,
        Box.BoxWidth,
        Box.BoxDepth,
        Box.BoxHeight,
        Box.BoxThickness);

    public string StatusBarMaterialText => string.Format(
        CultureInfo.InvariantCulture,
        "MATERIAL: MDF {0:0.0} mm (Folga {1:+0.00;-0.00;0.00} mm)",
        Box.BoxThickness,
        Box.Folga);

    public string StatusBarDimensionsText => string.Format(
        CultureInfo.InvariantCulture,
        "DIM: {0:0.#} × {1:0.#} × {2:0.#} mm",
        Box.BoxWidth,
        Box.BoxDepth,
        Box.BoxHeight);

    public string StatusBarPiecesText => string.Format(
        CultureInfo.InvariantCulture,
        "PEÇAS: {0} ({1:0.0} cm²)",
        Box.SelectedModel.TotalPanels,
        Box.EstimatedCuttingAreaCm2);

    public string WindowTitle => IsInNestingWorkspace
        ? "Caixa Maker — Otimizador de Corte 2D (Nesting)"
        : InDesigner
            ? string.Format(
                CultureInfo.InvariantCulture,
                "Caixa Maker — {0} ({1:0}×{2:0}×{3:0} mm){4}",
                ActiveModelOrProjectName,
                Math.Round(Box.BoxWidth),
                Math.Round(Box.BoxDepth),
                Math.Round(Box.BoxHeight),
                IsDirty ? " *" : string.Empty)
            : "Caixa Maker — Catálogo de Modelos";

    public string LeftPanelTitle => ActiveArea switch
    {
        WorkspaceArea.Personalization => "Superfícies e Elementos",
        WorkspaceArea.Fabrication => "Estrutura e Camadas",
        _ => "Parâmetros do Projeto",
    };

    public string RightPanelTitle => ActiveArea switch
    {
        WorkspaceArea.Personalization => "Propriedades CNC",
        WorkspaceArea.Fabrication => "Validação e Exportação CAM",
        _ => "Resumo e Peças",
    };

    public bool IsModelingArea => ActiveArea == WorkspaceArea.Modeling;
    public bool IsPersonalizationArea => ActiveArea == WorkspaceArea.Personalization;
    public bool IsFabricationArea => ActiveArea == WorkspaceArea.Fabrication;

    // Fluxo linear guiado (Stage 26: Stepper 1 -> 2 -> 3 e rodapés contextuais)
    public int ActiveWorkspaceStepNumber => !InDesigner ? 0 : ActiveArea switch
    {
        WorkspaceArea.Modeling => 1,
        WorkspaceArea.Personalization => 2,
        WorkspaceArea.Fabrication => 3,
        _ => 0,
    };

    public bool IsModelingStepActive => ActiveWorkspaceStepNumber == 1;
    public bool IsPersonalizationStepActive => ActiveWorkspaceStepNumber == 2;
    public bool IsManufacturingStepActive => ActiveWorkspaceStepNumber == 3;
    public bool IsModelingStepCompleted => ActiveWorkspaceStepNumber >= 2;
    public bool IsPersonalizationStepCompleted => ActiveWorkspaceStepNumber >= 3;
    public bool CanNavigatePreviousWorkspace => ActiveWorkspaceStepNumber > 1;
    public bool CanNavigateNextWorkspace => ActiveWorkspaceStepNumber is 1 or 2;

    public string NextWorkspaceButtonLabel => ActiveWorkspaceStepNumber == 2
        ? "Avançar para Fabricação →"
        : "Avançar para Personalização →";

    public string PreviousWorkspaceButtonLabel => ActiveWorkspaceStepNumber == 3
        ? "← Personalização"
        : "← Modelagem";

    public string WorkflowGuidanceHint => ActiveWorkspaceStepNumber switch
    {
        1 => "Etapa 1/3 (Modelagem): defina dimensões, material e kerf, depois avance para Personalização.",
        2 => "Etapa 2/3 (Personalização): adicione textos, formas 1:1, SVG ou recortes e avance para Fabricação.",
        3 => "Etapa 3/3 (Fabricação): revise a auditoria pré-corte e exporte SVG, DXF, PDF ou pacote .ZIP.",
        _ => string.Empty,
    };

    public bool IsViewMode3D => CenterViewMode == CenterViewMode.Mode3D;
    public bool IsViewMode2D => CenterViewMode == CenterViewMode.Mode2D;
    public bool IsViewModeSplit => CenterViewMode == CenterViewMode.Split;
    public bool IsSplitVertical => SplitOrientation == SplitViewportOrientation.VerticalStacked;
    public bool IsSplitHorizontal => SplitOrientation == SplitViewportOrientation.HorizontalSideBySide;

    public bool CanSaveProject => InDesigner && Box.IsParametersValid;
    public bool CanReturnToCatalog => InDesigner;
    public bool CanUndo => InDesigner && Personalization.CanUndo;
    public bool CanRedo => InDesigner && Personalization.CanRedo;
    public bool CanCopy => InDesigner && Personalization.HasSelection;
    public bool CanPaste => InDesigner && Personalization.HasClipboard;
    public bool CanDuplicate => InDesigner && Personalization.HasSelection;
    public bool CanDelete => InDesigner && Personalization.HasSelection;
    public bool CanClearElements => InDesigner && Personalization.HasElements;
    public bool CanToggleExploded => InDesigner;
    public bool CanToggleLidAngle => InDesigner && Box.SupportsLidArticulation;
    public bool CanExportCam => InDesigner && Box.IsParametersValid && Personalization.IsValidationValid;

    public event EventHandler? MenuStateChanged;
    public event EventHandler? OpenPreferencesRequested;
    public event EventHandler<int>? OpenAboutRequested;
    public event EventHandler<bool>? OpenProjectsManagerRequested;
    public event EventHandler? OpenProjectPropertiesRequested;
    public event EventHandler? FocusTextEditorRequested;
    public event EventHandler<string>? WindowActionRequested;

    public MainWindowViewModel(
        AppPaths? paths = null,
        INativeFileDialogService? dialogService = null,
        BoxModelRegistry? registry = null,
        ResolvedAppTheme initialSystemTheme = ResolvedAppTheme.Light)
    {
        _paths = paths ?? AppPaths.Resolve();
        _paths.EnsureDirs();
        _registry = registry ?? BoxModelRegistry.Default;
        _packageService = new PortablePackageService(_registry);
        _cmakerPackageService = new CmakerPackageService(_registry);
        _migrationService = new CmakerMigrationService(_registry, _cmakerPackageService);
        _library = new ProjectLibraryService(_paths, _registry, _cmakerPackageService, _migrationService);
        _settingsService = new UiSettingsService(_paths);
        _autosaveService = new AutosaveDraftService(_paths);
        _dialogs = dialogService ?? new NullNativeFileDialogService();

        Theme = new ThemeViewModel(_settingsService, initialSystemTheme);
        Box = new BoxWorkspaceViewModel(_registry);

        BoxCalculator initialCalc = Box.CurrentCalculator
            ?? Box.SelectedModel.CreateCalculator(BoxParameters.Default);
        Personalization = new PersonalizationWorkspaceViewModel(initialCalc, Box.SelectedModel);
        Manufacturing = new ManufacturingWorkspaceViewModel(_paths)
        {
            CurrentThicknessMm = Box.BoxThickness,
            CurrentFolgaMm = Box.Folga,
            CurrentSheetGeometry = Box.SheetGeometry,
        };
        Nesting = new NestingWorkspaceViewModel(
            _paths,
            _dialogs,
            activeLaserBedProvider: () =>
            {
                LaserBedSpec bed = Manufacturing.SelectedLaserBed;
                return bed.WidthMm >= 50.0 && bed.HeightMm >= 50.0 ? (bed.WidthMm, bed.HeightMm) : null;
            });

        ApplyLoadedUiSettings();
        WireSubViewModelEvents();
        RefreshSavedProjects();
        RefreshPendingDraftStatus();
        Manufacturing.RefreshLayerCounts(Personalization.Manager);
    }

    private void ApplyLoadedUiSettings()
    {
        _suppressUiSettingsPersist = true;
        try
        {
            UiSettingsData settings = _settingsService.Load();
            LeftSidebarWidth = settings.LeftSidebarWidth;
            RightSidebarWidth = settings.RightSidebarWidth;
            LeftSidebarOpen = settings.LeftSidebarOpen;
            RightSidebarOpen = settings.RightSidebarOpen;
            ToolbarVisible = settings.ToolbarVisible;
            SplitOrientation = Enum.TryParse<SplitViewportOrientation>(
                settings.SplitOrientation,
                ignoreCase: true,
                out SplitViewportOrientation parsedOrientation)
                ? parsedOrientation
                : SplitViewportOrientation.VerticalStacked;
            SplitViewportRatio = settings.SplitViewportRatio;
            LaserBedPreset parsedBed = Enum.TryParse<LaserBedPreset>(
                settings.SelectedLaserBedPreset,
                ignoreCase: true,
                out LaserBedPreset bedPreset)
                ? bedPreset
                : LaserBedPreset.Laser600x400;
            Manufacturing.LoadLaserBedSettings(
                parsedBed,
                settings.CustomBedWidthMm,
                settings.CustomBedHeightMm);
            Box.ShowDimensions = settings.ShowDimensions;
            Personalization.SafetyMarginMm = settings.DefaultSafetyMarginMm;
            Personalization.DefaultHatchSpacingMm = settings.DefaultHatchSpacingMm;
            AutosaveEnabled = settings.AutosaveEnabled;
        }
        finally
        {
            _suppressUiSettingsPersist = false;
        }
    }

    private void WireSubViewModelEvents()
    {
        Box.GeometryRebuilt += (_, _) =>
        {
            Manufacturing.CurrentThicknessMm = Box.BoxThickness;
            Manufacturing.CurrentFolgaMm = Box.Folga;
            Manufacturing.CurrentSheetGeometry = Box.SheetGeometry;
            if (Box.CurrentCalculator is not null)
            {
                Personalization.SyncWithBoxCalculator(
                    Box.CurrentCalculator,
                    Box.SelectedModel,
                    Box.CurrentDrawer,
                    notifyUserMutation: false);
                Manufacturing.RefreshLayerCounts(Personalization.Manager);
            }
            NotifyTitleAndMenus();
        };

        Box.ParameterEditedByUser += (_, _) =>
        {
            if (_suppressDirtyTracking) return;
            MarkDirty($"Parâmetros atualizados ({Box.DimensionsBadgeText}).");
        };

        Box.ViewerStateChanged += (_, _) =>
        {
            if (!_suppressDirtyTracking && InDesigner && !Box.IsInteractiveUpdate)
            {
                PersistDraftIfEnabled();
            }
            NotifyTitleAndMenus();
        };

        Personalization.StateMutatedByUser += (_, _) =>
        {
            Manufacturing.RefreshLayerCounts(Personalization.Manager);
            if (_suppressDirtyTracking)
            {
                NotifyTitleAndMenus();
                return;
            }

            MarkDirty("Personalização atualizada.");
        };

        Personalization.VisualStateChanged += (_, _) =>
        {
            Manufacturing.RefreshLayerCounts(Personalization.Manager);
            NotifyTitleAndMenus();
        };

        Manufacturing.LaserBedSettingsChanged += (_, _) =>
        {
            if (!Manufacturing.IsInteractiveUpdate)
            {
                PersistUiSettings();
            }
            NotifyTitleAndMenus();
        };
    }

    [RelayCommand]
    public void StartNewProjectFromCatalog(string modelId)
    {
        _suppressDirtyTracking = true;
        try
        {
            ResetActiveProjectMetadata();
            IsDirty = false;
            Box.LoadModel(modelId, applyDefaults: true, markAsEdit: false);
            BoxCalculator calc = Box.CurrentCalculator ?? Box.SelectedModel.CreateCalculator(Box.CurrentParameters);
            Personalization.ResetForNewProject(calc, Box.SelectedModel, Box.CurrentDrawer);
            ActiveArea = WorkspaceArea.Modeling;
            CenterViewMode = CenterViewMode.Mode3D;
            InDesigner = true;
            StatusMessage = $"Novo projeto iniciado: {Box.SelectedModel.DisplayName}.";
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        Manufacturing.CurrentSheetGeometry = Box.SheetGeometry;
        Manufacturing.RefreshLayerCounts(Personalization.Manager);
        NotifyTitleAndMenus();
    }

    [RelayCommand]
    public async Task NewProjectInCurrentModelAsync()
    {
        if (!await ConfirmUnsavedIfNeededAsync())
        {
            return;
        }

        StartNewProjectFromCatalog(Box.SelectedModelId);
        _autosaveService.ClearDraft();
        RefreshPendingDraftStatus();
    }

    [RelayCommand]
    public void SetWorkspaceArea(WorkspaceArea area)
    {
        ActiveArea = area;
        if ((area == WorkspaceArea.Personalization || area == WorkspaceArea.Fabrication)
            && CenterViewMode == CenterViewMode.Mode3D)
        {
            CenterViewMode = CenterViewMode.Split;
        }
        else if (area == WorkspaceArea.Modeling && CenterViewMode == CenterViewMode.Mode2D)
        {
            CenterViewMode = CenterViewMode.Mode3D;
        }

        if (InDesigner)
        {
            PersistDraftIfEnabled();
        }

        NotifyTitleAndMenus();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateNextWorkspace))]
    public void NavigateNextWorkspace()
    {
        if (!InDesigner)
        {
            return;
        }

        if (ActiveArea == WorkspaceArea.Modeling)
        {
            SetWorkspaceArea(WorkspaceArea.Personalization);
        }
        else if (ActiveArea == WorkspaceArea.Personalization)
        {
            SetWorkspaceArea(WorkspaceArea.Fabrication);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigatePreviousWorkspace))]
    public void NavigatePreviousWorkspace()
    {
        if (!InDesigner)
        {
            return;
        }

        if (ActiveArea == WorkspaceArea.Fabrication)
        {
            SetWorkspaceArea(WorkspaceArea.Personalization);
        }
        else if (ActiveArea == WorkspaceArea.Personalization)
        {
            SetWorkspaceArea(WorkspaceArea.Modeling);
        }
    }

    [RelayCommand]
    public void SkipToManufacturing()
    {
        if (!InDesigner)
        {
            return;
        }

        SetWorkspaceArea(WorkspaceArea.Fabrication);
    }

    [RelayCommand]
    public void SetCenterViewMode(CenterViewMode mode)
    {
        CenterViewMode = mode;
        NotifyTitleAndMenus();
    }

    [RelayCommand]
    public void SetSplitOrientation(SplitViewportOrientation orientation)
    {
        SplitOrientation = orientation;
        NotifyTitleAndMenus();
    }

    [RelayCommand]
    public void ToggleSplitOrientation()
    {
        SplitOrientation = SplitOrientation == SplitViewportOrientation.VerticalStacked
            ? SplitViewportOrientation.HorizontalSideBySide
            : SplitViewportOrientation.VerticalStacked;
        NotifyTitleAndMenus();
    }

    [RelayCommand]
    public void ResetSplitRatio()
    {
        SplitViewportRatio = 0.5;
    }

    public void RefreshSavedProjects()
    {
        IReadOnlyList<ProjectSummary> allUnfiltered = _library.ListProjects(searchQuery: null);
        IReadOnlyList<ProjectSummary> filtered = string.IsNullOrWhiteSpace(ProjectSearchQuery)
            ? allUnfiltered
            : _library.ListProjects(ProjectSearchQuery);

        SavedProjects.Clear();
        foreach (ProjectSummary item in filtered)
        {
            SavedProjects.Add(item);
        }

        RecentProjects.Clear();
        UiSettingsData settings = _settingsService.Load();
        var byId = allUnfiltered.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string recentId in settings.RecentProjectIds)
        {
            if (byId.TryGetValue(recentId, out ProjectSummary? summary) && added.Add(summary.Id))
            {
                RecentProjects.Add(summary);
            }
        }

        foreach (ProjectSummary summary in allUnfiltered)
        {
            if (RecentProjects.Count >= 6) break;
            if (added.Add(summary.Id))
            {
                RecentProjects.Add(summary);
            }
        }

        PendingLegacyMigrationCount = _library.CountPendingLegacyJsonProjects();
        OnPropertyChanged(nameof(HasPendingLegacyMigrations));
        NotifyTitleAndMenus();
    }

    public bool OpenSavedProject(string projectId)
    {
        try
        {
            LoadedProjectSession session = _library.OpenProject(projectId);
            LoadSessionIntoWorkspace(session, session.Record.Id, session.Record.Name, markDirty: false);
            RecordRecentProject(session.Record.Id);
            _autosaveService.ClearDraft();
            RefreshPendingDraftStatus();
            StatusMessage = !string.IsNullOrWhiteSpace(session.MigrationNotice)
                ? session.MigrationNotice!
                : $"Projeto '{session.Record.Name}' carregado.";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao abrir projeto: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    public async Task<bool> SaveProjectAsync()
    {
        if (!Box.IsParametersValid)
        {
            StatusMessage = Box.ValidationError ?? "Corrija os parâmetros antes de salvar.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SavedProjectId))
        {
            var defaults = new ProjectMetadataPromptResult(
                Name: BuildDefaultProjectName(),
                Description: SavedProjectDescription,
                Author: string.IsNullOrWhiteSpace(SavedProjectAuthor) ? AppVersion.DeveloperName : SavedProjectAuthor,
                Tags: ParseTagsText(SavedProjectTagsText),
                Notes: SavedProjectNotes);

            ProjectMetadataPromptResult? chosen = await _dialogs.PromptProjectMetadataAsync(
                "Salvar Projeto (.cmaker)",
                defaults);

            if (chosen is null || string.IsNullOrWhiteSpace(chosen.Name))
            {
                return false;
            }

            return SaveProjectAs(
                chosen.Name,
                chosen.Description,
                chosen.Author,
                chosen.Tags,
                chosen.Notes);
        }

        CmakerPackageBuildResult updated = _library.UpdateFromRequest(
            SavedProjectId,
            BuildSaveRequest(SavedProjectName ?? BuildDefaultProjectName(), forceNewId: false),
            BuildManufacturingState());

        ApplySavedBuildResult(updated);
        _autosaveService.ClearDraft();
        RefreshPendingDraftStatus();
        RecordRecentProject(updated.Metadata.ProjectId);
        RefreshSavedProjects();
        StatusMessage = $"Projeto '{updated.Metadata.Name}' salvo em .cmaker ({updated.Metadata.AssetCount} ativo(s)).";
        NotifyTitleAndMenus();
        return true;
    }

    public bool SaveProjectAs(
        string projectName,
        string? description = null,
        string? author = null,
        IReadOnlyList<string>? tags = null,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(projectName) || !Box.IsParametersValid)
        {
            return false;
        }

        if (description is not null) SavedProjectDescription = description.Trim();
        if (author is not null) SavedProjectAuthor = author.Trim();
        if (tags is not null) SavedProjectTagsText = string.Join(", ", tags);
        if (notes is not null) SavedProjectNotes = notes.Trim();

        CmakerPackageBuildResult saved = _library.SaveFromRequest(
            BuildSaveRequest(projectName.Trim(), forceNewId: true),
            BuildManufacturingState());

        ApplySavedBuildResult(saved);
        _autosaveService.ClearDraft();
        RefreshPendingDraftStatus();
        RecordRecentProject(saved.Metadata.ProjectId);
        RefreshSavedProjects();
        StatusMessage = $"Projeto salvo como '{saved.Metadata.Name}' (.cmaker).";
        NotifyTitleAndMenus();
        return true;
    }

    public bool DuplicateSavedProject(string projectId, string? newName = null)
    {
        try
        {
            LoadedProjectSession duplicated = _library.DuplicateProject(projectId, newName);
            RefreshSavedProjects();
            StatusMessage = $"Cópia '{duplicated.Record.Name}' criada na biblioteca (.cmaker).";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao duplicar projeto: {ex.Message}";
            return false;
        }
    }

    public async Task<bool> DeleteSavedProjectWithConfirmationAsync(string projectId, string? projectName = null)
    {
        string label = !string.IsNullOrWhiteSpace(projectName)
            ? projectName
            : SavedProjects.FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase))?.Name ?? projectId;

        bool confirmed = await _dialogs.ConfirmActionAsync(
            "Excluir Projeto",
            $"Deseja excluir permanentemente o projeto \"{label}\" da biblioteca local?",
            "Excluir");

        if (!confirmed)
        {
            return false;
        }

        return DeleteSavedProject(projectId);
    }

    public LegacyCmakerMigrationReport MigrateLegacyProjects(bool archiveConvertedJson = true)
    {
        LegacyCmakerMigrationReport report = _library.MigrateLegacyProjects(archiveConvertedJson);
        RefreshSavedProjects();
        StatusMessage = report.MigratedCount > 0
            ? $"Migração concluída: {report.MigratedCount} projeto(s) convertido(s) para .cmaker."
            : "Nenhum projeto legado pendente de migração.";
        return report;
    }

    public async Task<bool> UpdateActiveProjectMetadataAsync(
        string name,
        string description,
        string author,
        IReadOnlyList<string> tags,
        string notes)
    {
        string cleanName = string.IsNullOrWhiteSpace(name) ? ActiveModelOrProjectName : name.Trim();
        SavedProjectName = cleanName;
        SavedProjectDescription = (description ?? string.Empty).Trim();
        SavedProjectAuthor = string.IsNullOrWhiteSpace(author) ? AppVersion.DeveloperName : author.Trim();
        SavedProjectTagsText = tags is { Count: > 0 } ? string.Join(", ", tags) : string.Empty;
        SavedProjectNotes = (notes ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(SavedProjectId) && Box.IsParametersValid)
        {
            return await SaveProjectAsync();
        }

        MarkDirty("Propriedades do projeto atualizadas.");
        return true;
    }

    public bool DeleteSavedProject(string projectId)
    {
        try
        {
            _library.DeleteProject(projectId);
            if (string.Equals(SavedProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                SavedProjectId = null;
                SavedProjectName = null;
                SavedProjectPackagePath = null;
                SavedProjectPackageSizeBytes = 0;
                SavedProjectAssetCount = 0;
                if (InDesigner)
                {
                    IsDirty = true;
                }
            }
            RefreshSavedProjects();
            StatusMessage = "Projeto excluído da biblioteca local.";
            NotifyTitleAndMenus();
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao excluir projeto: {ex.Message}";
            return false;
        }
    }

    public Task<bool> ExportPortablePackageAsync(string? explicitDestinationPath = null)
        => ExportBundleRequestAsync(
            BuildSaveRequest(ActiveModelOrProjectName, forceNewId: false),
            Box.SelectedModel.DisplayName,
            explicitDestinationPath,
            BuildManufacturingState());

    public async Task<bool> ExportSavedProjectPackageAsync(string projectId, string? explicitDestinationPath = null)
    {
        try
        {
            LoadedProjectSession session = _library.OpenProject(projectId);
            IBoxModelDefinition model = _registry.Contains(session.Record.ModelId)
                ? _registry.Get(session.Record.ModelId)
                : Box.SelectedModel;

            var request = new ProjectSaveRequest
            {
                Name = session.Record.Name,
                Description = session.Record.Description,
                Author = string.IsNullOrWhiteSpace(session.Record.Author) ? AppVersion.DeveloperName : session.Record.Author,
                Tags = session.Record.Tags,
                Notes = session.Record.Notes,
                ModelId = session.Record.ModelId,
                ModelName = session.Record.ModelName,
                BoxParameters = session.BoxParameters,
                ViewerState = session.ViewerState,
                Manager = session.Manager,
                SafetyMarginMm = session.Record.Personalization.SafetyMarginMm,
                SavedId = session.Record.Id,
                CreatedAtOverride = session.Record.CreatedAt,
            };

            return await ExportBundleRequestAsync(
                request,
                model.DisplayName,
                explicitDestinationPath,
                BuildManufacturingStateFromViewerState(session.ViewerState));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro ao exportar projeto: {ex.Message}";
            return false;
        }
    }

    private async Task<bool> ExportBundleRequestAsync(
        ProjectSaveRequest request,
        string fallbackModelDisplayName,
        string? explicitDestinationPath,
        CmakerManufacturingState? mfgState)
    {
        string suggested = CmakerPackageService.BuildCmakerFilename(request.Name, fallbackModelDisplayName);

        string? destination = explicitDestinationPath ?? await _dialogs.PickSaveFileAsync(
            "Exportar Projeto Caixa Maker (.cmaker)",
            suggested,
            [
                new NativeFileFilter("Projeto Caixa Maker (*.cmaker)", ["cmaker"]),
                new NativeFileFilter("Pacote Portátil Legado (*.caixamaker.json)", ["json"]),
            ]);

        if (string.IsNullOrWhiteSpace(destination))
        {
            return false;
        }

        if (destination.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _packageService.ExportBundleToFile(request, destination, AppVersion.Version);
            StatusMessage = $"Pacote legado exportado: {Path.GetFileName(destination)}";
            return true;
        }

        string normalizedDestination = Path.HasExtension(destination)
            ? destination
            : destination + CmakerPackageService.FileExtension;

        CmakerPackageBuildResult built = _cmakerPackageService.SaveToFile(
            request,
            normalizedDestination,
            AppVersion.Version,
            mfgState);
        StatusMessage = $"Projeto .cmaker exportado: {Path.GetFileName(normalizedDestination)} ({built.Metadata.AssetCount} ativo(s)).";
        return true;
    }

    public Task<bool> ImportPortablePackageAsync(string? explicitSourcePath = null)
        => OpenExternalProjectFileAsync(explicitSourcePath, registerInLibrary: true);

    public async Task<bool> OpenExternalProjectFileAsync(string? explicitSourcePath = null, bool registerInLibrary = true)
    {
        string? source = explicitSourcePath ?? await _dialogs.PickOpenFileAsync(
            "Importar Projeto (.cmaker / .caixamaker.json)",
            [
                new NativeFileFilter("Projetos Caixa Maker (*.cmaker;*.caixamaker.json;*.json)", ["cmaker", "json"]),
                new NativeFileFilter("Projeto Autocontido (*.cmaker)", ["cmaker"]),
                new NativeFileFilter("Pacote Legado JSON (*.caixamaker.json;*.json)", ["json"]),
            ]);

        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            return false;
        }

        if (!await ConfirmUnsavedIfNeededAsync())
        {
            return false;
        }

        try
        {
            LoadedProjectSession imported = _library.ImportExternalPackage(source, saveToLibrary: registerInLibrary);
            string? savedId = registerInLibrary && AppPaths.IsSafeProjectId(imported.Record.Id)
                ? imported.Record.Id
                : null;
            LoadSessionIntoWorkspace(imported, savedId: savedId, savedName: imported.Record.Name, markDirty: !registerInLibrary);
            if (savedId is not null)
            {
                RecordRecentProject(savedId);
            }
            RefreshSavedProjects();
            StatusMessage = !string.IsNullOrWhiteSpace(imported.MigrationNotice)
                ? $"{imported.MigrationNotice}"
                : $"Projeto '{imported.Record.Name}' importado em modo editável ({imported.AssetCount} ativo(s)).";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao abrir '{Path.GetFileName(source)}': {ex.Message}";
            await _dialogs.ShowMessageAsync("Erro ao Abrir Projeto", StatusMessage);
            return false;
        }
    }

    public async Task<bool> ImportSvgIntoActiveSurfaceAsync(string? explicitFilePath = null)
    {
        if (!InDesigner) return false;

        string? path = explicitFilePath ?? await _dialogs.PickOpenFileAsync(
            "Selecionar arquivo vetorial SVG",
            [new NativeFileFilter("Desenho Vetorial SVG (*.svg)", ["svg"])]);

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        VectorSvgPersonalizationElement added = Personalization.ImportSvgFromFile(path);
        ActiveArea = WorkspaceArea.Personalization;
        StatusMessage = $"SVG '{added.Name}' importado para {Personalization.ActiveSurface?.DisplayName ?? "face"}.";
        NotifyTitleAndMenus();
        return true;
    }

    public async Task<bool> ImportRasterIntoActiveSurfaceAsync(string? explicitFilePath = null, bool vectorize = false)
    {
        if (!InDesigner) return false;

        string? path = explicitFilePath ?? await _dialogs.PickOpenFileAsync(
            "Selecionar imagem para gravação CNC",
            [new NativeFileFilter("Imagens suportadas (PNG, JPG, BMP)", ["png", "jpg", "jpeg", "bmp"])]);

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        RasterImagePersonalizationElement added = Personalization.ImportRasterFromFile(path, vectorize: vectorize);
        ActiveArea = WorkspaceArea.Personalization;
        StatusMessage = $"Imagem '{added.Name}' importada para {Personalization.ActiveSurface?.DisplayName ?? "face"}.";
        NotifyTitleAndMenus();
        return true;
    }

    public async Task<string?> ExportVectorFileAsync(VectorExportFormat format, string? explicitDestinationPath = null)
    {
        if (!InDesigner || !Box.IsParametersValid)
        {
            return null;
        }

        ValidationReport report = Personalization.RunValidation();
        if (!report.IsValid)
        {
            StatusMessage = $"Exportação bloqueada: existem {report.ErrorCount} erro(s) geométricos na personalização.";
            await _dialogs.ShowMessageAsync("Validação Geométrica CNC", StatusMessage);
            return null;
        }

        string suggested = ManufacturingWorkspaceViewModel.BuildSuggestedFileName(
            SavedProjectName,
            Box.SelectedModel.DisplayName,
            format,
            isZip: false);

        NativeFileFilter filter = format switch
        {
            VectorExportFormat.Dxf => new("Desenho CAD DXF R2000 (*.dxf)", ["dxf"]),
            VectorExportFormat.Pdf => new("Documento PDF 1:1 (*.pdf)", ["pdf"]),
            _ => new("Arquivo Vetorial SVG (*.svg)", ["svg"]),
        };

        string? destination = explicitDestinationPath ?? await _dialogs.PickSaveFileAsync(
            $"Exportar {format.ToString().ToUpperInvariant()}",
            suggested,
            [filter]);

        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        byte[] bytes = Manufacturing.GenerateSingleFileBytes(
            Personalization.Manager,
            format,
            Manufacturing.SelectedExportScope,
            Manufacturing.SelectedSinglePanelId);

        string savedPath = Manufacturing.SaveBytesToFile(
            bytes,
            destination,
            format.ToString().ToUpperInvariant());
        StatusMessage = Manufacturing.LastExportStatus;
        return savedPath;
    }

    public async Task<string?> ExportBundleZipAsync(string? explicitDestinationPath = null)
    {
        if (!InDesigner || !Box.IsParametersValid)
        {
            return null;
        }

        ValidationReport report = Personalization.RunValidation();
        if (!report.IsValid)
        {
            StatusMessage = $"Exportação bloqueada: existem {report.ErrorCount} erro(s) geométricos na personalização.";
            await _dialogs.ShowMessageAsync("Validação Geométrica CNC", StatusMessage);
            return null;
        }

        string prefix = ActiveModelOrProjectName;
        string suggested = ManufacturingWorkspaceViewModel.BuildSuggestedFileName(
            SavedProjectName,
            Box.SelectedModel.DisplayName,
            format: null,
            isZip: true);

        string? destination = explicitDestinationPath ?? await _dialogs.PickSaveFileAsync(
            "Exportar Pacote Completo CNC (ZIP)",
            suggested,
            [new NativeFileFilter("Pacote Completo CNC (*.zip)", ["zip"])]);

        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        ExportBundleResult bundle = Manufacturing.GenerateBundleZip(Personalization.Manager, prefix);
        byte[] enrichedZipBytes = AppendCmakerToManufacturingZip(bundle.ZipBytes);
        string savedPath = Manufacturing.SaveBytesToFile(enrichedZipBytes, destination, "ZIP");
        StatusMessage = Manufacturing.LastExportStatus;
        return savedPath;
    }

    private byte[] AppendCmakerToManufacturingZip(byte[] baseZipBytes)
    {
        CmakerPackageBuildResult cmakerBuilt = _cmakerPackageService.BuildPackage(
            BuildSaveRequest(ActiveModelOrProjectName, forceNewId: false),
            AppVersion.Version,
            BuildManufacturingState());

        using var ms = new MemoryStream();
        ms.Write(baseZipBytes, 0, baseZipBytes.Length);
        ms.Position = 0;

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            if (zip.GetEntry(cmakerBuilt.FileName) is { } oldEntry)
            {
                oldEntry.Delete();
            }

            ZipArchiveEntry cmakerEntry = zip.CreateEntry(cmakerBuilt.FileName, CompressionLevel.Optimal);
            using Stream s = cmakerEntry.Open();
            s.Write(cmakerBuilt.PackageBytes, 0, cmakerBuilt.PackageBytes.Length);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Abre diálogo nativo "Salvar Como" (<c>FileSavePicker</c>) para exportar o Pente de Calibração de Kerf
    /// no local e formato escolhidos pelo usuário (SVG, DXF ou PDF), respeitando cancelamento sem gravar arquivos.
    /// </summary>
    public async Task<string?> ExportKerfCalibrationCombWithDialogAsync(
        string? preferredFormat = null,
        string? explicitDestinationPath = null)
    {
        Manufacturing.CurrentThicknessMm = Box.BoxThickness;

        VectorExportFormat defaultFormat = ParseKerfVectorFormat(preferredFormat);
        string suggested = ManufacturingWorkspaceViewModel.BuildSuggestedKerfFileName(
            Manufacturing.CurrentThicknessMm,
            defaultFormat);

        NativeFileFilter[] allFilters =
        [
            new NativeFileFilter("Arquivo Vetorial SVG (*.svg)", ["svg"]),
            new NativeFileFilter("Desenho CAD DXF R2000 (*.dxf)", ["dxf"]),
            new NativeFileFilter("Documento PDF 1:1 (*.pdf)", ["pdf"]),
        ];

        int preferredIndex = defaultFormat switch
        {
            VectorExportFormat.Dxf => 1,
            VectorExportFormat.Pdf => 2,
            _ => 0,
        };
        var orderedFilters = new List<NativeFileFilter>(allFilters.Length) { allFilters[preferredIndex] };
        for (int i = 0; i < allFilters.Length; i++)
        {
            if (i != preferredIndex)
            {
                orderedFilters.Add(allFilters[i]);
            }
        }

        string? destination = explicitDestinationPath ?? await _dialogs.PickSaveFileAsync(
            "Salvar Pente de Calibração de Kerf",
            suggested,
            orderedFilters);

        if (string.IsNullOrWhiteSpace(destination))
        {
            Manufacturing.LastExportStatus = "Exportação do pente cancelada.";
            StatusMessage = Manufacturing.LastExportStatus;
            return null;
        }

        VectorExportFormat resolvedFormat = ResolveKerfFormatFromPathOrFallback(destination, defaultFormat);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(destination)))
        {
            destination += "." + resolvedFormat.ToString().ToLowerInvariant();
        }

        string savedPath = Manufacturing.ExportKerfCalibrationCombToFile(
            resolvedFormat,
            Manufacturing.CurrentThicknessMm,
            destination,
            centerFolgaMm: 0.0,
            slotCount: Manufacturing.KerfSlotCount,
            stepMm: Manufacturing.KerfStepMm);

        StatusMessage = Manufacturing.LastExportStatus;
        return savedPath;
    }

    private static VectorExportFormat ParseKerfVectorFormat(string? format)
    {
        string normalized = string.IsNullOrWhiteSpace(format)
            ? "svg"
            : format.Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "dxf" => VectorExportFormat.Dxf,
            "pdf" => VectorExportFormat.Pdf,
            _ => VectorExportFormat.Svg,
        };
    }

    private static VectorExportFormat ResolveKerfFormatFromPathOrFallback(string path, VectorExportFormat fallback)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "svg" => VectorExportFormat.Svg,
            "dxf" => VectorExportFormat.Dxf,
            "pdf" => VectorExportFormat.Pdf,
            _ => fallback,
        };
    }

    [RelayCommand]
    public void RestorePendingDraft() => RestoreDraft();

    public bool RestoreDraft()
    {
        AutosaveDraftRecord? draft = _autosaveService.TryLoadDraft();
        if (draft is null)
        {
            RefreshPendingDraftStatus();
            return false;
        }

        _suppressDirtyTracking = true;
        try
        {
            BoxParameters parameters = draft.BoxParameters.ToBoxParameters();
            string targetModelId = _registry.Contains(draft.ModelId) ? draft.ModelId : BoxWithLidAndHandlesModel.Id;
            IBoxModelDefinition model = _registry.Get(targetModelId);

            Box.ApplySessionState(targetModelId, parameters, draft.ViewerState);
            SurfaceManager restoredManager = SurfaceManager.FromSnapshot(
                draft.Personalization,
                registry: _registry,
                explicitBoxModel: model);

            SurfaceId activeSurf = TryParseSurfaceId(draft.ViewerState.ActiveSurface, out SurfaceId parsedSurf)
                ? parsedSurf
                : SurfaceId.Front;

            Personalization.ReplaceManager(restoredManager, activeSurf);
            Personalization.SafetyMarginMm = draft.SafetyMarginMm;
            SavedProjectId = string.IsNullOrWhiteSpace(draft.SavedProjectId) ? null : draft.SavedProjectId;
            SavedProjectName = string.IsNullOrWhiteSpace(draft.ProjectName) ? null : draft.ProjectName;
            ActiveArea = Enum.TryParse<WorkspaceArea>(draft.ActiveArea, out WorkspaceArea parsedArea)
                ? parsedArea
                : WorkspaceArea.Modeling;
            CenterViewMode = Enum.TryParse<CenterViewMode>(draft.CenterViewMode, out CenterViewMode parsedMode)
                ? parsedMode
                : CenterViewMode.Mode3D;
            InDesigner = true;
            IsDirty = true;
            HasPendingDraft = false;
            PendingDraft = null;
            DraftSummaryText = string.Empty;
            StatusMessage = "Rascunho automático restaurado.";
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        Manufacturing.CurrentSheetGeometry = Box.SheetGeometry;
        Manufacturing.RefreshLayerCounts(Personalization.Manager);
        NotifyTitleAndMenus();
        return true;
    }

    [RelayCommand]
    public void DiscardDraft()
    {
        _autosaveService.ClearDraft();
        RefreshPendingDraftStatus();
        StatusMessage = "Rascunho temporário descartado.";
    }

    public void ForceClearDirtyForAutomation()
    {
        IsDirty = false;
        NotifyTitleAndMenus();
    }

    [RelayCommand]
    public async Task<bool> ReturnToCatalogAsync()
    {
        if (!InDesigner)
        {
            return true;
        }

        if (!await ConfirmUnsavedIfNeededAsync())
        {
            return false;
        }

        InDesigner = false;
        IsDirty = false;
        ResetActiveProjectMetadata();
        RefreshSavedProjects();
        RefreshPendingDraftStatus();
        StatusMessage = "Central de Trabalho — Selecione um modelo ou projeto.";
        NotifyTitleAndMenus();
        return true;
    }

    public async Task<bool> ConfirmUnsavedIfNeededAsync()
    {
        if (!InDesigner || !IsDirty)
        {
            return true;
        }

        UnsavedChangesDecision decision = await _dialogs.PromptUnsavedChangesAsync(ActiveModelOrProjectName);
        switch (decision)
        {
            case UnsavedChangesDecision.SaveAndContinue:
                return await SaveProjectAsync();
            case UnsavedChangesDecision.DiscardAndContinue:
                _autosaveService.ClearDraft();
                RefreshPendingDraftStatus();
                return true;
            default:
                return false;
        }
    }

    public void UpdatePreferences(double safetyMarginMm, double hatchSpacingMm, bool autosaveEnabled, AppThemePreference themePreference)
    {
        Theme.SetPreference(themePreference);
        Personalization.SafetyMarginMm = Math.Clamp(safetyMarginMm, 0.0, 20.0);
        Personalization.DefaultHatchSpacingMm = Math.Clamp(hatchSpacingMm, 0.05, 5.0);
        AutosaveEnabled = autosaveEnabled;

        if (!autosaveEnabled)
        {
            _autosaveService.ClearDraft();
            RefreshPendingDraftStatus();
        }
        else if (InDesigner && IsDirty)
        {
            PersistDraftIfEnabled();
        }

        PersistUiSettings();
        StatusMessage = "Preferências atualizadas.";
    }

    public async Task HandleAcceleratorAsync(string commandId, KeyboardFocusContext focusContext)
    {
        if (!KeyboardShortcutGuard.ShouldDispatchCommand(focusContext, commandId))
        {
            return;
        }

        await DispatchCommandAsync(commandId);
    }

    public async Task DispatchCommandAsync(string commandId, string? argument = null)
    {
        if (commandId.StartsWith(DesktopCommandIds.FileOpenRecentPrefix, StringComparison.Ordinal))
        {
            string recentId = commandId[DesktopCommandIds.FileOpenRecentPrefix.Length..];
            if (await ConfirmUnsavedIfNeededAsync())
            {
                OpenSavedProject(recentId);
            }
            return;
        }

        switch (commandId)
        {
            case DesktopCommandIds.FileNew:
                if (InDesigner) await NewProjectInCurrentModelAsync();
                else StartNewProjectFromCatalog(argument ?? BoxWithLidAndHandlesModel.Id);
                break;
            case DesktopCommandIds.FileOpenProject:
                OpenProjectsManagerRequested?.Invoke(this, false);
                break;
            case DesktopCommandIds.FileSave:
                if (CanSaveProject) await SaveProjectAsync();
                break;
            case DesktopCommandIds.FileSaveAs:
                if (CanSaveProject) OpenProjectsManagerRequested?.Invoke(this, true);
                break;
            case DesktopCommandIds.FileImportPackage:
                await ImportPortablePackageAsync(argument);
                break;
            case DesktopCommandIds.FileExportPackage:
                if (InDesigner) await ExportPortablePackageAsync(argument);
                break;
            case DesktopCommandIds.FileProjectProperties:
                if (InDesigner) OpenProjectPropertiesRequested?.Invoke(this, EventArgs.Empty);
                break;
            case DesktopCommandIds.FileBackToCatalog:
                await ReturnToCatalogAsync();
                break;
            case DesktopCommandIds.FileExit:
                WindowActionRequested?.Invoke(this, DesktopCommandIds.FileExit);
                break;

            case DesktopCommandIds.EditUndo:
                if (CanUndo) Personalization.Undo();
                break;
            case DesktopCommandIds.EditRedo:
                if (CanRedo) Personalization.Redo();
                break;
            case DesktopCommandIds.EditCopy:
                if (CanCopy) Personalization.CopySelectedElement();
                break;
            case DesktopCommandIds.EditPaste:
                if (CanPaste) Personalization.PasteClipboardElement();
                break;
            case DesktopCommandIds.EditDuplicate:
                if (CanDuplicate) Personalization.DuplicateSelectedElement();
                break;
            case DesktopCommandIds.EditDelete:
                if (CanDelete) Personalization.DeleteSelectedElements();
                break;
            case DesktopCommandIds.EditSelectAll:
                if (InDesigner && IsPersonalizationArea) Personalization.SelectAllElementsOnActiveSurface();
                break;
            case DesktopCommandIds.EditClearAll:
                if (CanClearElements) Personalization.ClearAllElements();
                break;
            case DesktopCommandIds.EditResetParameters:
                if (InDesigner) Box.ResetDefaultParameters();
                break;

            case DesktopCommandIds.ViewMode3D:
                SetCenterViewMode(CenterViewMode.Mode3D);
                break;
            case DesktopCommandIds.ViewMode2D:
                SetCenterViewMode(CenterViewMode.Mode2D);
                break;
            case DesktopCommandIds.ViewModeSplit:
                SetCenterViewMode(CenterViewMode.Split);
                break;
            case DesktopCommandIds.ViewToggleLeftSidebar:
                LeftSidebarOpen = !LeftSidebarOpen;
                break;
            case DesktopCommandIds.ViewToggleRightSidebar:
                RightSidebarOpen = !RightSidebarOpen;
                break;
            case DesktopCommandIds.ViewToggleToolbar:
                ToolbarVisible = !ToolbarVisible;
                break;
            case DesktopCommandIds.ViewToggleDimensions:
                Box.ToggleDimensions();
                PersistUiSettings();
                break;
            case DesktopCommandIds.ViewResetCamera:
                Box.ResetCamera();
                break;
            case DesktopCommandIds.ViewThemeSystem:
                Theme.SetPreference(AppThemePreference.System);
                break;
            case DesktopCommandIds.ViewThemeLight:
                Theme.SetPreference(AppThemePreference.Light);
                break;
            case DesktopCommandIds.ViewThemeDark:
                Theme.SetPreference(AppThemePreference.Dark);
                break;

            case DesktopCommandIds.ProjectAreaModeling:
                if (InDesigner)
                {
                    IsInNestingWorkspace = false;
                    SetWorkspaceArea(WorkspaceArea.Modeling);
                }
                break;
            case DesktopCommandIds.ProjectAreaPersonalization:
                if (InDesigner)
                {
                    IsInNestingWorkspace = false;
                    SetWorkspaceArea(WorkspaceArea.Personalization);
                }
                break;
            case DesktopCommandIds.ProjectAreaFabrication:
                if (InDesigner)
                {
                    IsInNestingWorkspace = false;
                    SetWorkspaceArea(WorkspaceArea.Fabrication);
                }
                break;
            case DesktopCommandIds.ProjectToggleExploded:
                if (InDesigner) Box.ToggleExplodedView();
                break;
            case DesktopCommandIds.ProjectToggleLid:
                if (CanToggleLidAngle) Box.ToggleLidOpenClose();
                break;
            case DesktopCommandIds.ProjectToggleModel:
                if (InDesigner) Box.ToggleAlternativeModel();
                break;
            case DesktopCommandIds.ProjectFocusText:
                if (InDesigner)
                {
                    SetWorkspaceArea(WorkspaceArea.Personalization);
                    FocusTextEditorRequested?.Invoke(this, EventArgs.Empty);
                }
                break;
            case DesktopCommandIds.ProjectImportSvg:
                if (InDesigner) await ImportSvgIntoActiveSurfaceAsync(argument);
                break;
            case DesktopCommandIds.ProjectImportRaster:
                if (InDesigner) await ImportRasterIntoActiveSurfaceAsync(argument);
                break;
            case DesktopCommandIds.ProjectValidate:
                if (InDesigner)
                {
                    ValidationReport report = Personalization.RunValidation();
                    StatusMessage = report.IsValid
                        ? "Validação geométrica SAT concluída sem erros."
                        : $"Validação SAT: {report.ErrorCount} erro(s) e {report.WarningCount} aviso(s).";
                }
                break;

            case DesktopCommandIds.ToolSelect:
            case DesktopCommandIds.ToolCancel:
                if (InDesigner && IsPersonalizationArea)
                {
                    Personalization.SetActiveToolMode(Editor2D.Canvas2DToolMode.Select);
                }
                break;
            case DesktopCommandIds.ToolDrawRectangle:
                if (InDesigner && IsPersonalizationArea)
                {
                    Personalization.SetActiveToolMode(Editor2D.Canvas2DToolMode.DrawRectangle);
                }
                break;
            case DesktopCommandIds.ToolDrawCircle:
                if (InDesigner && IsPersonalizationArea)
                {
                    Personalization.SetActiveToolMode(Editor2D.Canvas2DToolMode.DrawCircle);
                }
                break;
            case DesktopCommandIds.ToolDrawSlot:
                if (InDesigner && IsPersonalizationArea)
                {
                    Personalization.SetActiveToolMode(Editor2D.Canvas2DToolMode.DrawSlot);
                }
                break;
            case DesktopCommandIds.ToolDrawPolygon:
                if (InDesigner && IsPersonalizationArea)
                {
                    Personalization.SetActiveToolMode(Editor2D.Canvas2DToolMode.DrawPolygon);
                }
                break;
            case DesktopCommandIds.ToolDrawCrosshair:
                if (InDesigner && IsPersonalizationArea)
                {
                    Personalization.SetActiveToolMode(Editor2D.Canvas2DToolMode.DrawCrosshair);
                }
                break;

            case DesktopCommandIds.ToolsNesting:
                OpenNestingIndependent();
                break;
            case DesktopCommandIds.ToolsExportSvg:
                await ExportVectorFileAsync(VectorExportFormat.Svg, argument);
                break;
            case DesktopCommandIds.ToolsExportDxf:
                await ExportVectorFileAsync(VectorExportFormat.Dxf, argument);
                break;
            case DesktopCommandIds.ToolsExportPdf:
                await ExportVectorFileAsync(VectorExportFormat.Pdf, argument);
                break;
            case DesktopCommandIds.ToolsExportZip:
                await ExportBundleZipAsync(argument);
                break;
            case DesktopCommandIds.ToolsSavedProjects:
                OpenProjectsManagerRequested?.Invoke(this, false);
                break;
            case DesktopCommandIds.ToolsPreferences:
                OpenPreferencesRequested?.Invoke(this, EventArgs.Empty);
                break;

            case DesktopCommandIds.WindowToggleFullscreen:
            case DesktopCommandIds.WindowMinimize:
            case DesktopCommandIds.WindowToggleMaximize:
                WindowActionRequested?.Invoke(this, commandId);
                break;

            case DesktopCommandIds.HelpAbout:
                OpenAboutRequested?.Invoke(this, 0);
                break;
            case DesktopCommandIds.HelpShortcuts:
                OpenAboutRequested?.Invoke(this, 1);
                break;
            case DesktopCommandIds.HelpInstitution:
                OpenAboutRequested?.Invoke(this, 2);
                break;
        }
    }

    private void LoadSessionIntoWorkspace(
        LoadedProjectSession session,
        string? savedId,
        string? savedName,
        bool markDirty)
    {
        _suppressDirtyTracking = true;
        try
        {
            Box.ApplySessionState(session.Record.ModelId, session.BoxParameters, session.ViewerState);
            SurfaceId activeSurf = TryParseSurfaceId(session.ViewerState.ActiveSurface, out SurfaceId parsedSurf)
                ? parsedSurf
                : SurfaceId.Front;
            Personalization.ReplaceManager(session.Manager, activeSurf);

            ApplyManufacturingViewerState(session.ViewerState);

            SavedProjectId = savedId;
            SavedProjectName = savedName;
            SavedProjectDescription = session.Record.Description ?? string.Empty;
            SavedProjectAuthor = string.IsNullOrWhiteSpace(session.Record.Author)
                ? AppVersion.DeveloperName
                : session.Record.Author;
            SavedProjectTagsText = session.Record.Tags is { Count: > 0 }
                ? string.Join(", ", session.Record.Tags)
                : string.Empty;
            SavedProjectNotes = session.Record.Notes ?? string.Empty;
            SavedProjectCreatedAt = session.Record.CreatedAt ?? string.Empty;
            SavedProjectUpdatedAt = session.Record.UpdatedAt ?? string.Empty;
            SavedProjectPackagePath = session.PackageFilePath;
            SavedProjectPackageSizeBytes = session.PackageSizeBytes;
            SavedProjectAssetCount = session.AssetCount;
            SavedProjectFormatBadge = string.Equals(session.SourceFormat, "cmaker", StringComparison.OrdinalIgnoreCase)
                ? $".cmaker (v{session.PackageSchemaVersion ?? CmakerManifest.CurrentSchemaVersion})"
                : $".cmaker ({session.SourceFormat})";

            ActiveArea = WorkspaceArea.Modeling;
            if (!string.IsNullOrWhiteSpace(session.ViewerState.CenterViewMode) &&
                Enum.TryParse<CenterViewMode>(session.ViewerState.CenterViewMode, ignoreCase: true, out CenterViewMode restoredViewMode))
            {
                CenterViewMode = restoredViewMode;
            }
            else
            {
                CenterViewMode = CenterViewMode.Mode3D;
            }

            if (!string.IsNullOrWhiteSpace(session.ViewerState.SplitOrientation) &&
                Enum.TryParse<SplitViewportOrientation>(session.ViewerState.SplitOrientation, ignoreCase: true, out SplitViewportOrientation restoredOrientation))
            {
                SplitOrientation = restoredOrientation;
            }

            if (session.ViewerState.SplitViewportRatio is double splitRatio && double.IsFinite(splitRatio))
            {
                SplitViewportRatio = splitRatio;
            }

            InDesigner = true;
            IsDirty = markDirty;
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        if (markDirty)
        {
            PersistDraftIfEnabled();
        }

        Manufacturing.CurrentSheetGeometry = Box.SheetGeometry;
        Manufacturing.RefreshLayerCounts(Personalization.Manager);
        NotifyTitleAndMenus();
    }

    private ProjectSaveRequest BuildSaveRequest(string name, bool forceNewId = false)
    {
        ProjectViewerState viewerState = Box.ToViewerState(
            SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(Personalization.ActiveSurfaceId));
        viewerState.CenterViewMode = CenterViewMode.ToString();
        viewerState.SplitOrientation = SplitOrientation.ToString();
        viewerState.SplitViewportRatio = Math.Round(SplitViewportRatio, 4);
        viewerState.LaserBedPreset = Manufacturing.SelectedLaserBedPreset.ToString();
        viewerState.LaserBedWidthMm = Manufacturing.SelectedLaserBed.WidthMm;
        viewerState.LaserBedHeightMm = Manufacturing.SelectedLaserBed.HeightMm;
        viewerState.KerfSlotCount = Manufacturing.KerfSlotCount;
        viewerState.KerfStepMm = Manufacturing.KerfStepMm;

        return new ProjectSaveRequest
        {
            Name = name,
            Description = SavedProjectDescription,
            Author = string.IsNullOrWhiteSpace(SavedProjectAuthor) ? AppVersion.DeveloperName : SavedProjectAuthor,
            Tags = ParseTagsText(SavedProjectTagsText),
            Notes = SavedProjectNotes,
            ModelId = Box.SelectedModelId,
            ModelName = Box.SelectedModel.DisplayName,
            BoxParameters = Box.CurrentParameters,
            ViewerState = viewerState,
            Manager = Personalization.Manager,
            SafetyMarginMm = Personalization.SafetyMarginMm,
            SavedId = forceNewId ? null : SavedProjectId,
            CreatedAtOverride = forceNewId ? null : (string.IsNullOrWhiteSpace(SavedProjectCreatedAt) ? null : SavedProjectCreatedAt),
        };
    }

    private CmakerManufacturingState BuildManufacturingState() => new()
    {
        LaserBedPreset = Manufacturing.SelectedLaserBedPreset.ToString(),
        LaserBedWidthMm = Manufacturing.SelectedLaserBed.WidthMm,
        LaserBedHeightMm = Manufacturing.SelectedLaserBed.HeightMm,
        KerfSlotCount = Manufacturing.KerfSlotCount,
        KerfStepMm = Manufacturing.KerfStepMm,
        IncludeEngraveOnlyInZip = Manufacturing.IncludeEngraveOnlyInZip,
        IncludeFullSheetInZip = Manufacturing.IncludeFullSheetInZip,
    };

    private static CmakerManufacturingState BuildManufacturingStateFromViewerState(ProjectViewerState state) => new()
    {
        LaserBedPreset = state.LaserBedPreset,
        LaserBedWidthMm = state.LaserBedWidthMm,
        LaserBedHeightMm = state.LaserBedHeightMm,
        KerfSlotCount = state.KerfSlotCount,
        KerfStepMm = state.KerfStepMm,
    };

    private void ApplyManufacturingViewerState(ProjectViewerState viewerState)
    {
        if (string.IsNullOrWhiteSpace(viewerState.LaserBedPreset) ||
            !Enum.TryParse<LaserBedPreset>(viewerState.LaserBedPreset, ignoreCase: true, out LaserBedPreset bedPreset))
        {
            return;
        }

        double w = viewerState.LaserBedWidthMm ?? Manufacturing.CustomBedWidthMm;
        double h = viewerState.LaserBedHeightMm ?? Manufacturing.CustomBedHeightMm;
        Manufacturing.LoadLaserBedSettings(bedPreset, w, h);
    }

    private void ApplySavedBuildResult(CmakerPackageBuildResult saved)
    {
        SavedProjectId = saved.Metadata.ProjectId;
        SavedProjectName = saved.Metadata.Name;
        SavedProjectDescription = saved.Metadata.Description;
        SavedProjectAuthor = string.IsNullOrWhiteSpace(saved.Metadata.Author)
            ? AppVersion.DeveloperName
            : saved.Metadata.Author;
        SavedProjectTagsText = saved.Metadata.Tags is { Count: > 0 }
            ? string.Join(", ", saved.Metadata.Tags)
            : string.Empty;
        SavedProjectNotes = saved.Metadata.Notes;
        SavedProjectCreatedAt = saved.Metadata.CreatedAt;
        SavedProjectUpdatedAt = saved.Metadata.UpdatedAt;
        SavedProjectPackagePath = saved.Summary.PackageFilePath;
        SavedProjectPackageSizeBytes = saved.PackageBytes.LongLength;
        SavedProjectAssetCount = saved.Metadata.AssetCount;
        SavedProjectFormatBadge = $".cmaker (v{saved.Manifest.SchemaVersion})";
        IsDirty = false;
    }

    private void ResetActiveProjectMetadata()
    {
        SavedProjectId = null;
        SavedProjectName = null;
        SavedProjectDescription = string.Empty;
        SavedProjectAuthor = AppVersion.DeveloperName;
        SavedProjectTagsText = string.Empty;
        SavedProjectNotes = string.Empty;
        SavedProjectCreatedAt = string.Empty;
        SavedProjectUpdatedAt = string.Empty;
        SavedProjectPackagePath = null;
        SavedProjectPackageSizeBytes = 0;
        SavedProjectAssetCount = 0;
        SavedProjectFormatBadge = ".cmaker (v2.0)";
    }

    private static IReadOnlyList<string> ParseTagsText(string? rawTags)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
        {
            return Array.Empty<string>();
        }

        return rawTags
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    partial void OnProjectSearchQueryChanged(string value)
        => RefreshSavedProjects();

    private string BuildDefaultProjectName() =>
        $"{Box.SelectedModel.DisplayName} ({Math.Round(Box.BoxWidth)}×{Math.Round(Box.BoxDepth)}×{Math.Round(Box.BoxHeight)} mm)";

    private void MarkDirty(string status)
    {
        IsDirty = true;
        StatusMessage = status;
        PersistDraftIfEnabled();
        NotifyTitleAndMenus();
    }

    public void PersistDraftIfEnabled()
    {
        if (!AutosaveEnabled || !InDesigner || Box.IsInteractiveUpdate)
        {
            return;
        }

        var draft = new AutosaveDraftRecord
        {
            SavedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ModelId = Box.SelectedModelId,
            ModelName = Box.SelectedModel.DisplayName,
            ProjectName = SavedProjectName ?? string.Empty,
            SavedProjectId = SavedProjectId,
            ActiveArea = ActiveArea.ToString(),
            CenterViewMode = CenterViewMode.ToString(),
            SafetyMarginMm = Personalization.SafetyMarginMm,
            BoxParameters = ProjectBoxParameters.FromBoxParameters(Box.CurrentParameters),
            ViewerState = Box.ToViewerState(SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(Personalization.ActiveSurfaceId)),
            Personalization = Personalization.Manager.CreateSnapshot(includeRawBase64: true),
        };

        _autosaveService.SaveDraft(draft);
    }

    private void RefreshPendingDraftStatus()
    {
        AutosaveDraftRecord? loaded = _autosaveService.TryLoadDraft();
        PendingDraft = loaded;
        HasPendingDraft = loaded is not null;

        if (loaded is null)
        {
            DraftSummaryText = string.Empty;
            return;
        }

        string label = string.IsNullOrWhiteSpace(loaded.ProjectName) ? loaded.ModelName : loaded.ProjectName;
        double w = loaded.BoxParameters.BoxWidth ?? 200;
        double d = loaded.BoxParameters.BoxDepth ?? 150;
        double h = loaded.BoxParameters.BoxHeight ?? 100;
        DraftSummaryText = string.Format(
            CultureInfo.InvariantCulture,
            "{0} ({1:0}×{2:0}×{3:0} mm) — salvo automaticamente em {4}",
            string.IsNullOrWhiteSpace(label) ? loaded.ModelId : label,
            w,
            d,
            h,
            loaded.SavedAt);
    }

    private void RecordRecentProject(string projectId)
    {
        UiSettingsData settings = _settingsService.Load();
        settings.RecentProjectIds.RemoveAll(id => string.Equals(id, projectId, StringComparison.Ordinal));
        settings.RecentProjectIds.Insert(0, projectId);
        if (settings.RecentProjectIds.Count > 8)
        {
            settings.RecentProjectIds.RemoveRange(8, settings.RecentProjectIds.Count - 8);
        }
        _settingsService.Save(settings);
    }

    public void PersistUiSettings()
    {
        if (_suppressUiSettingsPersist || Manufacturing.IsInteractiveUpdate)
        {
            return;
        }

        UiSettingsData settings = _settingsService.Load();
        settings.LeftSidebarWidth = LeftSidebarWidth;
        settings.RightSidebarWidth = RightSidebarWidth;
        settings.LeftSidebarOpen = LeftSidebarOpen;
        settings.RightSidebarOpen = RightSidebarOpen;
        settings.ToolbarVisible = ToolbarVisible;
        settings.SplitOrientation = SplitOrientation.ToString();
        settings.SplitViewportRatio = SplitViewportRatio;
        settings.SelectedLaserBedPreset = Manufacturing.SelectedLaserBedPreset.ToString();
        settings.CustomBedWidthMm = Manufacturing.CustomBedWidthMm;
        settings.CustomBedHeightMm = Manufacturing.CustomBedHeightMm;
        settings.ShowDimensions = Box.ShowDimensions;
        settings.DefaultSafetyMarginMm = Personalization.SafetyMarginMm;
        settings.DefaultHatchSpacingMm = Personalization.DefaultHatchSpacingMm;
        settings.AutosaveEnabled = AutosaveEnabled;
        _settingsService.Save(settings);
    }

    partial void OnInDesignerChanged(bool value)
    {
        Nesting.HasActiveBoxProject = value;
        if (value && IsInNestingWorkspace && NestingOrigin == NestingEntryOrigin.Catalog)
        {
            IsInNestingWorkspace = false;
        }
        NotifyWorkspaceWorkflowProperties();
        NotifyTitleAndMenus();
    }

    partial void OnActiveAreaChanged(WorkspaceArea value)
    {
        OnPropertyChanged(nameof(LeftPanelTitle));
        OnPropertyChanged(nameof(RightPanelTitle));
        OnPropertyChanged(nameof(IsModelingArea));
        OnPropertyChanged(nameof(IsPersonalizationArea));
        OnPropertyChanged(nameof(IsFabricationArea));
        NotifyWorkspaceWorkflowProperties();
        NotifyTitleAndMenus();
    }

    private void NotifyWorkspaceWorkflowProperties()
    {
        OnPropertyChanged(nameof(ActiveWorkspaceStepNumber));
        OnPropertyChanged(nameof(IsModelingStepActive));
        OnPropertyChanged(nameof(IsPersonalizationStepActive));
        OnPropertyChanged(nameof(IsManufacturingStepActive));
        OnPropertyChanged(nameof(IsModelingStepCompleted));
        OnPropertyChanged(nameof(IsPersonalizationStepCompleted));
        OnPropertyChanged(nameof(CanNavigatePreviousWorkspace));
        OnPropertyChanged(nameof(CanNavigateNextWorkspace));
        OnPropertyChanged(nameof(NextWorkspaceButtonLabel));
        OnPropertyChanged(nameof(PreviousWorkspaceButtonLabel));
        OnPropertyChanged(nameof(WorkflowGuidanceHint));
        NavigateNextWorkspaceCommand.NotifyCanExecuteChanged();
        NavigatePreviousWorkspaceCommand.NotifyCanExecuteChanged();
    }

    partial void OnCenterViewModeChanged(CenterViewMode value)
    {
        OnPropertyChanged(nameof(IsViewMode3D));
        OnPropertyChanged(nameof(IsViewMode2D));
        OnPropertyChanged(nameof(IsViewModeSplit));
        NotifyTitleAndMenus();
    }

    partial void OnSplitOrientationChanged(SplitViewportOrientation value)
    {
        OnPropertyChanged(nameof(IsSplitVertical));
        OnPropertyChanged(nameof(IsSplitHorizontal));
        PersistUiSettings();
        NotifyTitleAndMenus();
    }

    partial void OnSplitViewportRatioChanged(double value)
    {
        double clamped = Math.Clamp(
            double.IsFinite(value) ? value : UiSettingsData.DefaultSplitViewportRatio,
            UiSettingsData.MinSplitViewportRatio,
            UiSettingsData.MaxSplitViewportRatio);
        if (Math.Abs(clamped - value) > 1e-6)
        {
            SplitViewportRatio = clamped;
            return;
        }
        PersistUiSettings();
    }

    partial void OnLeftSidebarOpenChanged(bool value)
    {
        PersistUiSettings();
        NotifyTitleAndMenus();
    }

    partial void OnRightSidebarOpenChanged(bool value)
    {
        PersistUiSettings();
        NotifyTitleAndMenus();
    }

    partial void OnToolbarVisibleChanged(bool value)
    {
        PersistUiSettings();
        NotifyTitleAndMenus();
    }

    partial void OnLeftSidebarWidthChanged(double value)
    {
        double clamped = Math.Clamp(value, UiSettingsData.MinLeftSidebarWidth, UiSettingsData.MaxLeftSidebarWidth);
        if (Math.Abs(clamped - value) > 0.1)
        {
            LeftSidebarWidth = clamped;
            return;
        }
        PersistUiSettings();
    }

    partial void OnRightSidebarWidthChanged(double value)
    {
        double clamped = Math.Clamp(value, UiSettingsData.MinRightSidebarWidth, UiSettingsData.MaxRightSidebarWidth);
        if (Math.Abs(clamped - value) > 0.1)
        {
            RightSidebarWidth = clamped;
            return;
        }
        PersistUiSettings();
    }

    partial void OnSavedProjectNameChanged(string? value) => NotifyTitleAndMenus();
    partial void OnIsDirtyChanged(bool value) => NotifyTitleAndMenus();
    partial void OnIsInNestingWorkspaceChanged(bool value) => NotifyTitleAndMenus();
    partial void OnNestingOriginChanged(NestingEntryOrigin value) => OnPropertyChanged(nameof(NestingBackButtonLabel));

    [RelayCommand]
    public void OpenNestingFromCatalog()
    {
        NestingOrigin = NestingEntryOrigin.Catalog;
        Nesting.HasActiveBoxProject = InDesigner;
        IsInNestingWorkspace = true;
        StatusMessage = "Otimizador de Corte 2D (Nesting) aberto em modo independente.";
    }

    [RelayCommand]
    public void OpenNestingIndependent()
    {
        NestingOrigin = !InDesigner
            ? NestingEntryOrigin.Catalog
            : ActiveArea switch
            {
                WorkspaceArea.Modeling => NestingEntryOrigin.Modeling,
                WorkspaceArea.Personalization => NestingEntryOrigin.Personalization,
                WorkspaceArea.Fabrication => NestingEntryOrigin.Manufacturing,
                _ => NestingEntryOrigin.Catalog,
            };
        Nesting.HasActiveBoxProject = InDesigner;
        IsInNestingWorkspace = true;
        StatusMessage = "Otimizador de Corte 2D (Nesting) aberto em modo independente.";
    }

    [RelayCommand]
    public async Task OpenNestingFromManufacturingAsync()
    {
        NestingOrigin = NestingEntryOrigin.Manufacturing;
        LaserBedSpec bed = Manufacturing.SelectedLaserBed;
        if (bed.WidthMm >= 50.0 && bed.HeightMm >= 50.0)
        {
            Nesting.SheetWidthMm = bed.WidthMm;
            Nesting.SheetHeightMm = bed.HeightMm;
        }

        Nesting.LoadActiveBoxPanels(
            Box.SelectedModel,
            Box.CurrentParameters,
            Personalization.Manager,
            replaceExistingBoxParts: true);

        IsInNestingWorkspace = true;
        StatusMessage = "Chapas da caixa enviadas ao Otimizador de Corte 2D (Nesting).";
        await Nesting.RunOptimizationAsync();
    }

    [RelayCommand]
    public void LoadActiveBoxIntoNesting()
    {
        if (!InDesigner)
        {
            return;
        }

        Nesting.LoadActiveBoxPanels(
            Box.SelectedModel,
            Box.CurrentParameters,
            Personalization.Manager,
            replaceExistingBoxParts: true);
    }

    [RelayCommand]
    public void ReturnFromNesting()
    {
        IsInNestingWorkspace = false;
        switch (NestingOrigin)
        {
            case NestingEntryOrigin.Modeling:
                InDesigner = true;
                ActiveArea = WorkspaceArea.Modeling;
                StatusMessage = "Retornou para a Etapa 1: Modelagem Paramétrica.";
                break;
            case NestingEntryOrigin.Personalization:
                InDesigner = true;
                ActiveArea = WorkspaceArea.Personalization;
                StatusMessage = "Retornou para a Etapa 2: Personalização CNC.";
                break;
            case NestingEntryOrigin.Manufacturing:
                InDesigner = true;
                ActiveArea = WorkspaceArea.Fabrication;
                StatusMessage = "Retornou para a Etapa 3: Fabricação e Exportação CAM.";
                break;
            default:
                StatusMessage = "Retornou para a Central de Trabalho.";
                break;
        }
    }

    private void NotifyTitleAndMenus()
    {
        OnPropertyChanged(nameof(ActiveModelOrProjectName));
        OnPropertyChanged(nameof(WorkspaceHeaderProjectText));
        OnPropertyChanged(nameof(StatusBarMaterialText));
        OnPropertyChanged(nameof(StatusBarDimensionsText));
        OnPropertyChanged(nameof(StatusBarPiecesText));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(CanSaveProject));
        OnPropertyChanged(nameof(CanReturnToCatalog));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanDuplicate));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanClearElements));
        OnPropertyChanged(nameof(CanToggleExploded));
        OnPropertyChanged(nameof(CanToggleLidAngle));
        OnPropertyChanged(nameof(CanExportCam));
        MenuStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryParseSurfaceId(string? raw, out SurfaceId surfaceId)
    {
        surfaceId = SurfaceId.Front;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            surfaceId = SnakeCaseEnumJsonConverter<SurfaceId>.ParseApiString(raw);
            return true;
        }
        catch
        {
            return Enum.TryParse(raw.Trim(), ignoreCase: true, out surfaceId);
        }
    }
}
