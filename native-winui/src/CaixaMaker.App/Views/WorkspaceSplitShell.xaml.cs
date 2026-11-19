using System.Globalization;
using CaixaMaker.App.Controls.CadPrimitives;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Views;

/// <summary>
/// Shell dividido CAD/CAM de engenharia contendo cabeçalho de 34 px, Ribbon contextual estilo Fusion 360 de 48 px
/// com <see cref="CadToolbarGroup"/>/<see cref="CadToolbarButton"/>, painéis retráteis de 26 px,
/// viewports 3D/2D e barra de status técnica de 24 px.
/// </summary>
public sealed partial class WorkspaceSplitShell : UserControl
{
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly SolidColorBrush WhiteTextBrush = new(Colors.White);
    private static readonly SolidColorBrush ActiveStepBadgeBrush = new(Windows.UI.Color.FromArgb(64, 255, 255, 255));
    private static readonly SolidColorBrush CompletedStepBadgeBrush = new(Windows.UI.Color.FromArgb(40, 34, 197, 94));
    private static readonly InputSystemCursor SizeNsCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    private static readonly InputSystemCursor SizeWeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    private MainWindowViewModel? _vm;
    private bool _syncing;
    private bool _draggingLeftSplitter;
    private bool _draggingRightSplitter;
    private bool _draggingViewportDivider;
    private bool _viewportDividerHovered;
    private double _activeDragSplitRatio = UiSettingsData.DefaultSplitViewportRatio;
    private double _splitterStartX;
    private double _splitterStartWidth;

    public WorkspaceSplitShell()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RefreshShellLayout();
    }

    public void BindViewModel(MainWindowViewModel viewModel)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnMainPropertyChanged;
            _vm.MenuStateChanged -= OnMenuOrStateChanged;
            _vm.Theme.ThemeChanged -= OnThemeChanged;
            _vm.FocusTextEditorRequested -= OnFocusTextEditorRequested;
            _vm.Box.PropertyChanged -= OnMainPropertyChanged;
            _vm.Personalization.PropertyChanged -= OnMainPropertyChanged;
            _vm.Manufacturing.PropertyChanged -= OnMainPropertyChanged;
        }

        _vm = viewModel;
        _vm.PropertyChanged += OnMainPropertyChanged;
        _vm.MenuStateChanged += OnMenuOrStateChanged;
        _vm.Theme.ThemeChanged += OnThemeChanged;
        _vm.FocusTextEditorRequested += OnFocusTextEditorRequested;
        _vm.Box.PropertyChanged += OnMainPropertyChanged;
        _vm.Personalization.PropertyChanged += OnMainPropertyChanged;
        _vm.Manufacturing.PropertyChanged += OnMainPropertyChanged;

        HeaderThemeSwitcher.Bind(_vm.Theme);
        Viewer3D.BindViewModel(_vm);
        Editor2D.BindViewModel(_vm);
        ModelingLeftPanel.BindViewModel(_vm.Box);
        ModelingRightPanel.BindViewModel(_vm.Box);
        ModelingRightPanel.BindMainViewModel(_vm);
        PersonalizationLeftPanel.BindViewModel(_vm);
        PersonalizationRightPanel.BindViewModel(_vm.Personalization);
        PersonalizationRightPanel.BindMainViewModel(_vm);
        ManufacturingLeftPanel.BindViewModel(_vm);
        ManufacturingRightPanel.BindViewModel(_vm);

        RefreshShellLayout();
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _vm?.Box) &&
            e.PropertyName is nameof(BoxWorkspaceViewModel.CameraYawDeg)
                or nameof(BoxWorkspaceViewModel.CameraPitchDeg)
                or nameof(BoxWorkspaceViewModel.CameraZoom)
                or nameof(BoxWorkspaceViewModel.CameraPanX)
                or nameof(BoxWorkspaceViewModel.CameraPanY)
                or nameof(BoxWorkspaceViewModel.ActiveCameraPreset))
        {
            return;
        }

        RefreshShellLayout();
    }

    private void OnMenuOrStateChanged(object? sender, EventArgs e)
        => RefreshShellLayout();

    private void OnThemeChanged(object? sender, ResolvedAppTheme e)
        => RefreshShellLayout();

    private void OnFocusTextEditorRequested(object? sender, EventArgs e)
        => PersonalizationLeftPanel.FocusTextInput();

    public void RefreshShellLayout()
    {
        if (_vm is null) return;

        _syncing = true;
        try
        {
            // Cabeçalho 34 px
            WorkspaceHeaderProjectTextBlock.Text = _vm.WorkspaceHeaderProjectText;
            UnsavedStatusBadge.Visibility = _vm.IsDirty ? Visibility.Visible : Visibility.Collapsed;

            ApplyStepTabVisual(
                TabModelingBtn,
                TabModelingStepBadge,
                TabModelingStepText,
                TabModelingText,
                "1",
                _vm.IsModelingStepActive,
                _vm.IsModelingStepCompleted);
            ApplyStepTabVisual(
                TabPersonalizationBtn,
                TabPersonalizationStepBadge,
                TabPersonalizationStepText,
                TabPersonalizationText,
                "2",
                _vm.IsPersonalizationStepActive,
                _vm.IsPersonalizationStepCompleted);
            ApplyStepTabVisual(
                TabFabricationBtn,
                TabFabricationStepBadge,
                TabFabricationStepText,
                TabFabricationText,
                "3",
                _vm.IsManufacturingStepActive,
                completed: false);

            if (ThemeBrushHelper.ResolveBrush(this, _vm.IsModelingStepCompleted ? "CmStatusSuccessBrush" : "CmTextSecondaryBrush") is Brush c12)
            {
                StepperConnector12.Foreground = c12;
                StepperConnector12.Opacity = _vm.IsModelingStepCompleted ? 0.95 : 0.6;
            }
            if (ThemeBrushHelper.ResolveBrush(this, _vm.IsPersonalizationStepCompleted ? "CmStatusSuccessBrush" : "CmTextSecondaryBrush") is Brush c23)
            {
                StepperConnector23.Foreground = c23;
                StepperConnector23.Opacity = _vm.IsPersonalizationStepCompleted ? 0.95 : 0.6;
            }

            if (!_vm.Personalization.IsValidationValid)
            {
                TopCncStatusBadge.Kind = CadBadgeKind.Error;
                TopCncStatusBadge.Text = "Colisão SAT";
            }
            else if (_vm.Personalization.ValidationWarningCount > 0)
            {
                TopCncStatusBadge.Kind = CadBadgeKind.Warning;
                TopCncStatusBadge.Text = $"{_vm.Personalization.ValidationWarningCount} aviso(s)";
            }
            else
            {
                TopCncStatusBadge.Kind = CadBadgeKind.Valid;
                TopCncStatusBadge.Text = "CNC OK";
            }

            // Painéis por área
            LeftPanelTitleText.Text = _vm.LeftPanelTitle.ToUpperInvariant();
            RightPanelTitleText.Text = _vm.RightPanelTitle.ToUpperInvariant();

            ModelingLeftPanel.Visibility = _vm.IsModelingArea ? Visibility.Visible : Visibility.Collapsed;
            ModelingRightPanel.Visibility = _vm.IsModelingArea ? Visibility.Visible : Visibility.Collapsed;
            PersonalizationLeftPanel.Visibility = _vm.IsPersonalizationArea ? Visibility.Visible : Visibility.Collapsed;
            PersonalizationRightPanel.Visibility = _vm.IsPersonalizationArea ? Visibility.Visible : Visibility.Collapsed;
            ManufacturingLeftPanel.Visibility = _vm.IsFabricationArea ? Visibility.Visible : Visibility.Collapsed;
            ManufacturingRightPanel.Visibility = _vm.IsFabricationArea ? Visibility.Visible : Visibility.Collapsed;

            // Ribbon CAD Contextual 48 px (grupos dinâmicos por estágio)
            CadToolbarBorder.Visibility = _vm.ToolbarVisible ? Visibility.Visible : Visibility.Collapsed;
            SaveToolbarBtn.IsEnabled = _vm.CanSaveProject;
            UndoToolbarBtn.IsEnabled = _vm.CanUndo;
            RedoToolbarBtn.IsEnabled = _vm.CanRedo;

            ModelingRibbonGroups.Visibility = _vm.IsModelingArea ? Visibility.Visible : Visibility.Collapsed;
            PersonalizationRibbonGroups.Visibility = _vm.IsPersonalizationArea ? Visibility.Visible : Visibility.Collapsed;
            ManufacturingRibbonGroups.Visibility = _vm.IsFabricationArea ? Visibility.Visible : Visibility.Collapsed;

            // Modelagem contextual
            ExternalDimToolbarBtn.IsActive = _vm.Box.IsExternalDimensionMode;
            InternalDimToolbarBtn.IsActive = _vm.Box.IsInternalDimensionMode;
            double thickness = _vm.Box.BoxThickness;
            Mdf3ToolbarBtn.IsActive = Math.Abs(thickness - 3.0) < 0.05;
            Mdf4ToolbarBtn.IsActive = Math.Abs(thickness - 4.0) < 0.05;
            Mdf6ToolbarBtn.IsActive = Math.Abs(thickness - 6.0) < 0.05;
            Mdf9ToolbarBtn.IsActive = Math.Abs(thickness - 9.0) < 0.05;
            ExplodeToolbarBtn.IsActive = _vm.Box.ExplodedView;
            DimensionsToolbarBtn.IsActive = _vm.Box.ShowDimensions;

            bool supportsLid = _vm.Box.SupportsLidArticulation;
            double lidAngle = _vm.Box.LidAngle;
            Lid0ToolbarBtn.IsEnabled = supportsLid;
            Lid45ToolbarBtn.IsEnabled = supportsLid;
            Lid90ToolbarBtn.IsEnabled = supportsLid;
            Lid0ToolbarBtn.IsActive = supportsLid && Math.Abs(lidAngle - 0.0) < 1.5;
            Lid45ToolbarBtn.IsActive = supportsLid && Math.Abs(lidAngle - 45.0) < 1.5;
            Lid90ToolbarBtn.IsActive = supportsLid && Math.Abs(lidAngle - 90.0) < 1.5;

            // Personalização CAM contextual
            bool canEditCam = _vm.Personalization.CanEditSelection;
            CenterToolbarBtn.IsEnabled = canEditCam;
            AlignLeftToolbarBtn.IsEnabled = canEditCam;
            AlignRightToolbarBtn.IsEnabled = canEditCam;
            AlignTopToolbarBtn.IsEnabled = canEditCam;
            AlignBottomToolbarBtn.IsEnabled = canEditCam;
            Rotate90ToolbarBtn.IsEnabled = canEditCam;
            CopyToolbarBtn.IsEnabled = _vm.CanCopy;
            PasteToolbarBtn.IsEnabled = _vm.CanPaste;
            DuplicateToolbarBtn.IsEnabled = _vm.CanDuplicate;
            DeleteToolbarBtn.IsEnabled = _vm.CanDelete;

            // Fabricação contextual
            PersonalizationExportMode scope = _vm.Manufacturing.SelectedExportScope;
            ScopeFullToolbarBtn.IsActive = scope == PersonalizationExportMode.FullBox;
            ScopeSingleToolbarBtn.IsActive = scope == PersonalizationExportMode.PanelCombined;
            ScopeEngraveToolbarBtn.IsActive = scope == PersonalizationExportMode.PanelEngraveOnly;

            if (ToolbarLaserBedCombo.ItemsSource is null)
            {
                ToolbarLaserBedCombo.ItemsSource = _vm.Manufacturing.AvailableLaserBeds;
            }
            ToolbarLaserBedCombo.SelectedItem = _vm.Manufacturing.AvailableLaserBeds
                .FirstOrDefault(b => b.Preset == _vm.Manufacturing.SelectedLaserBedPreset);
            LaserBedFitResult bedFit = _vm.Manufacturing.EvaluateLaserBedFit(_vm.Box.SheetGeometry);
            (string bedBgKey, string bedBorderKey, string bedFgKey, string bedBadgeLabel) = bedFit.Severity switch
            {
                LaserBedFitSeverity.Fits => (
                    "CmStatusSuccessSoftBrush",
                    "CmStatusSuccessBrush",
                    "CmStatusSuccessBrush",
                    string.Format(CultureInfo.InvariantCulture, "✓ {0:0}%", bedFit.UtilizationPercent)),
                LaserBedFitSeverity.FitsRotatedOnly or LaserBedFitSeverity.ExceedsBed => (
                    "CmStatusWarningSoftBrush",
                    "CmStatusWarningBrush",
                    "CmStatusWarningBrush",
                    BuildCompactExcessBadgeText(bedFit)),
                _ => (
                    "CmInputBackgroundBrush",
                    "CmHairlineBorderBrush",
                    "CmTextSecondaryBrush",
                    "Livre"),
            };
            ToolbarLaserBedBadge.Background = ThemeBrushHelper.ResolveBrush(this, bedBgKey);
            ToolbarLaserBedBadge.BorderBrush = ThemeBrushHelper.ResolveBrush(this, bedBorderKey);
            ToolbarLaserBedBadgeText.Foreground = ThemeBrushHelper.ResolveBrush(this, bedFgKey);
            ToolbarLaserBedBadgeText.Text = bedBadgeLabel;
            ToolTipService.SetToolTip(ToolbarLaserBedBadge, bedFit.SummaryText);
            ToolTipService.SetToolTip(ToolbarLaserBedCombo, $"Mesa Laser/CNC: {_vm.Manufacturing.SelectedLaserBed.Label}");

            LeftPanelToolbarBtn.IsActive = _vm.LeftSidebarOpen;
            RightPanelToolbarBtn.IsActive = _vm.RightSidebarOpen;
            View3DBtn.IsActive = _vm.IsViewMode3D;
            ViewSplitBtn.IsActive = _vm.IsViewModeSplit;
            View2DBtn.IsActive = _vm.IsViewMode2D;
            SplitVerticalBtn.IsActive = _vm.IsSplitVertical;
            SplitHorizontalBtn.IsActive = _vm.IsSplitHorizontal;

            // Larguras e visibilidade dos painéis laterais
            if (_vm.LeftSidebarOpen)
            {
                LeftColumnDef.Width = new GridLength(_vm.LeftSidebarWidth);
                LeftSplitterColDef.Width = new GridLength(4);
                LeftSidebarContainer.Visibility = Visibility.Visible;
                LeftSplitterBar.Visibility = Visibility.Visible;
            }
            else
            {
                LeftColumnDef.Width = new GridLength(0);
                LeftSplitterColDef.Width = new GridLength(0);
                LeftSidebarContainer.Visibility = Visibility.Collapsed;
                LeftSplitterBar.Visibility = Visibility.Collapsed;
            }

            if (_vm.RightSidebarOpen)
            {
                RightColumnDef.Width = new GridLength(_vm.RightSidebarWidth);
                RightSplitterColDef.Width = new GridLength(4);
                RightSidebarContainer.Visibility = Visibility.Visible;
                RightSplitterBar.Visibility = Visibility.Visible;
            }
            else
            {
                RightColumnDef.Width = new GridLength(0);
                RightSplitterColDef.Width = new GridLength(0);
                RightSidebarContainer.Visibility = Visibility.Collapsed;
                RightSplitterBar.Visibility = Visibility.Collapsed;
            }

            // Viewport central 3D / 2D / Split (vertical empilhado ou lado a lado com divisor arrastável de 5 px)
            ApplyViewportLayout(_vm.CenterViewMode, _vm.SplitOrientation, _draggingViewportDivider ? _activeDragSplitRatio : _vm.SplitViewportRatio);

            // Barra de status técnica inferior (24 px)
            ToolTipService.SetToolTip(StatusStatePanel, _vm.StatusMessage);
            StatusStateText.Text = _vm.IsDirty ? "Modificado *" : "Pronto";
            if (ThemeBrushHelper.ResolveBrush(this, _vm.IsDirty ? "CmStatusWarningBrush" : "CmStatusSuccessBrush") is Brush dotBrush)
            {
                StatusStateDot.Fill = dotBrush;
            }

            StatusModelText.Text = $"MODELO: {_vm.Box.SelectedModel.DisplayName}";
            StatusMaterialText.Text = _vm.StatusBarMaterialText;
            StatusDimensionsText.Text = _vm.StatusBarDimensionsText;
            StatusPiecesText.Text = _vm.StatusBarPiecesText;
            StatusCamText.Text = $"CAM: {_vm.Personalization.TotalElementCount} elem.";
            StatusGuidanceHintText.Text = _vm.WorkflowGuidanceHint;
            ToolTipService.SetToolTip(StatusGuidanceHintText, _vm.WorkflowGuidanceHint);

            if (!_vm.Personalization.IsValidationValid)
            {
                StatusCncFeedbackText.Text = $"✕ {_vm.Personalization.ValidationErrorCount} erro(s) SAT";
                if (ThemeBrushHelper.ResolveBrush(this, "CmStatusDangerBrush") is Brush errBrush)
                {
                    StatusCncFeedbackText.Foreground = errBrush;
                }
            }
            else if (_vm.Personalization.ValidationWarningCount > 0)
            {
                StatusCncFeedbackText.Text = $"⚠ {_vm.Personalization.ValidationWarningCount} aviso(s) CAM";
                if (ThemeBrushHelper.ResolveBrush(this, "CmStatusWarningBrush") is Brush warnBrush)
                {
                    StatusCncFeedbackText.Foreground = warnBrush;
                }
            }
            else
            {
                StatusCncFeedbackText.Text = "✓ Pronto para fabricação";
                if (ThemeBrushHelper.ResolveBrush(this, "CmStatusSuccessBrush") is Brush okBrush)
                {
                    StatusCncFeedbackText.Foreground = okBrush;
                }
            }

            StatusVersionText.Text = $"v{_vm.AppVersionText}";
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ApplyStepTabVisual(
        Button button,
        Border stepBadge,
        TextBlock stepText,
        TextBlock label,
        string stepNumber,
        bool active,
        bool completed)
    {
        stepText.Text = completed ? "✓" : stepNumber;

        if (active)
        {
            Brush? accentPrimary = ThemeBrushHelper.ResolveBrush(this, "CmAccentPrimaryBrush");
            Brush? accentHighlight = ThemeBrushHelper.ResolveBrush(this, "CmAccentHighlightBrush");
            button.Background = accentPrimary ?? TransparentBrush;
            button.BorderBrush = accentHighlight ?? accentPrimary ?? TransparentBrush;
            label.Foreground = WhiteTextBrush;
            label.FontWeight = FontWeights.SemiBold;
            stepBadge.Background = ActiveStepBadgeBrush;
            stepText.Foreground = WhiteTextBrush;
            return;
        }

        button.Background = TransparentBrush;
        button.BorderBrush = TransparentBrush;
        if (completed)
        {
            Brush? successBrush = ThemeBrushHelper.ResolveBrush(this, "CmStatusSuccessBrush");
            Brush? textPrimary = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush");
            label.Foreground = textPrimary ?? WhiteTextBrush;
            label.FontWeight = FontWeights.Medium;
            stepBadge.Background = CompletedStepBadgeBrush;
            stepText.Foreground = successBrush ?? WhiteTextBrush;
            return;
        }

        label.Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush") ?? WhiteTextBrush;
        label.FontWeight = FontWeights.Normal;
        stepBadge.Background = ThemeBrushHelper.ResolveBrush(this, "CmElevatedSurfaceBrush") ?? TransparentBrush;
        stepText.Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush") ?? WhiteTextBrush;
    }

    private async void OnBackToCatalogClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.ReturnToCatalogAsync();
    }

    private void OnAreaTabClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: string tag }) return;
        if (Enum.TryParse<WorkspaceArea>(tag, out WorkspaceArea area))
        {
            _vm.SetWorkspaceArea(area);
        }
    }

    private void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not CadToolbarButton { Tag: string tag }) return;
        if (Enum.TryParse<CenterViewMode>(tag, out CenterViewMode mode))
        {
            _vm.SetCenterViewMode(mode);
        }
    }

    private void OnSplitOrientationClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not CadToolbarButton { Tag: string tag }) return;
        if (Enum.TryParse<SplitViewportOrientation>(tag, out SplitViewportOrientation orientation))
        {
            _vm.SetSplitOrientation(orientation);
            if (_vm.CenterViewMode != CenterViewMode.Split)
            {
                _vm.SetCenterViewMode(CenterViewMode.Split);
            }
        }
    }

    private async void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.NewProjectInCurrentModelAsync();
    }

    private async void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.FileOpenProject);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SaveProjectAsync();
    }

    private async void OnImportPackageClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.FileImportPackage);
    }

    private async void OnExportPackageClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.FileExportPackage);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => _vm?.Personalization.Undo();
    private void OnRedoClick(object sender, RoutedEventArgs e) => _vm?.Personalization.Redo();

    private void OnOpenNestingHeaderClick(object sender, RoutedEventArgs e) => _vm?.OpenNestingIndependent();

    private async void OnAddTextClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ProjectFocusText);
    }

    private async void OnAddSvgClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ProjectImportSvg);
    }

    private async void OnAddRasterClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ProjectImportRaster);
    }

    private void OnExternalDimModeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing) return;
        _vm.Box.SetDimensionReferenceMode(DimensionReferenceMode.External);
    }

    private void OnInternalDimModeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing) return;
        _vm.Box.SetDimensionReferenceMode(DimensionReferenceMode.InternalUsable);
    }

    private void OnQuickThicknessClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing || sender is not CadToolbarButton { Tag: string raw }) return;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double thickness))
        {
            _vm.Box.SetQuickThickness(thickness);
        }
    }

    private void OnQuickLidAngleClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing || sender is not CadToolbarButton { Tag: string raw }) return;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
        {
            _vm.Box.LidAngle = angle;
        }
    }

    private void OnCenterSelectedClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.ApplyQuickAlignment(QuickAlignmentPreset.CenterSurface);

    private void OnAlignLeftClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.ApplyQuickAlignment(QuickAlignmentPreset.AlignLeft);

    private void OnAlignRightClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.ApplyQuickAlignment(QuickAlignmentPreset.AlignRight);

    private void OnAlignTopClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.ApplyQuickAlignment(QuickAlignmentPreset.AlignTop);

    private void OnAlignBottomClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.ApplyQuickAlignment(QuickAlignmentPreset.AlignBottom);

    private void OnExportScopeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing || sender is not CadToolbarButton { Tag: string tag }) return;
        if (Enum.TryParse<PersonalizationExportMode>(tag, out PersonalizationExportMode mode))
        {
            _vm.Manufacturing.SelectedExportScope = mode;
        }
    }

    private void OnToolbarLaserBedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _syncing) return;
        if (ToolbarLaserBedCombo.SelectedItem is LaserBedSpec spec)
        {
            _vm.Manufacturing.SelectedLaserBedPreset = spec.Preset;
        }
    }

    private static string BuildCompactExcessBadgeText(LaserBedFitResult fit)
    {
        if (fit.ExcessXMm > 1e-6 && fit.ExcessYMm > 1e-6)
        {
            return string.Format(CultureInfo.InvariantCulture, "⚠ +{0:0.#}×+{1:0.#}", fit.ExcessXMm, fit.ExcessYMm);
        }

        if (fit.ExcessXMm > 1e-6)
        {
            return string.Format(CultureInfo.InvariantCulture, "⚠ +{0:0.#} X", fit.ExcessXMm);
        }

        if (fit.ExcessYMm > 1e-6)
        {
            return string.Format(CultureInfo.InvariantCulture, "⚠ +{0:0.#} Y", fit.ExcessYMm);
        }

        return fit.FitsIfRotated90 ? "↻ 90°" : "⚠";
    }

    private async void OnExportKerfSvgClick(object sender, RoutedEventArgs e)
        => await ExportKerfCombAsync("svg");

    private async void OnExportKerfDxfClick(object sender, RoutedEventArgs e)
        => await ExportKerfCombAsync("dxf");

    private async Task ExportKerfCombAsync(string format)
    {
        if (_vm is null || _syncing) return;
        await _vm.ExportKerfCalibrationCombWithDialogAsync(format);
    }

    private async void OnExportSvgToolbarClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ToolsExportSvg);
    }

    private async void OnExportDxfToolbarClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ToolsExportDxf);
    }

    private async void OnExportPdfToolbarClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ToolsExportPdf);
    }

    private async void OnExportZipToolbarClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ToolsExportZip);
    }

    private void OnRotate90Click(object sender, RoutedEventArgs e)
        => _vm?.Personalization.RotateSelected90();

    private void OnCopyClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.CopySelectedElement();

    private void OnPasteClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.PasteClipboardElement();

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.DuplicateSelectedElement();

    private void OnDeleteClick(object sender, RoutedEventArgs e)
        => _vm?.Personalization.DeleteSelectedElement();

    private void OnResetCameraClick(object sender, RoutedEventArgs e)
        => _vm?.Box.ResetCamera();

    private void OnExplodeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing) return;
        _vm.Box.ToggleExplodedView();
    }

    private void OnToggleDimensionsClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing) return;
        _vm.Box.ShowDimensions = !_vm.Box.ShowDimensions;
        _vm.PersistUiSettings();
    }

    private async void OnValidateToolbarClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ProjectValidate);
    }

    private void OnToggleLeftSidebarClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing) return;
        _vm.LeftSidebarOpen = !_vm.LeftSidebarOpen;
    }

    private void OnToggleRightSidebarClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _syncing) return;
        _vm.RightSidebarOpen = !_vm.RightSidebarOpen;
    }

    private async void OnOpenPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.ToolsPreferences);
    }

    private async void OnOpenAboutClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.DispatchCommandAsync(DesktopCommandIds.HelpAbout);
    }

    // Arraste dos splitters laterais (240–440 px esquerda, 260–480 px direita)
    private void OnLeftSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null) return;
        _draggingLeftSplitter = true;
        _splitterStartX = e.GetCurrentPoint(SplitGrid).Position.X;
        _splitterStartWidth = _vm.LeftSidebarWidth;
        LeftSplitterBar.CapturePointer(e.Pointer);
    }

    private void OnLeftSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingLeftSplitter || _vm is null) return;
        double dx = e.GetCurrentPoint(SplitGrid).Position.X - _splitterStartX;
        double clamped = Math.Clamp(_splitterStartWidth + dx, UiSettingsData.MinLeftSidebarWidth, UiSettingsData.MaxLeftSidebarWidth);
        LeftColumnDef.Width = new GridLength(clamped);
    }

    private void OnLeftSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingLeftSplitter || _vm is null) return;
        _draggingLeftSplitter = false;
        LeftSplitterBar.ReleasePointerCapture(e.Pointer);
        _vm.LeftSidebarWidth = LeftColumnDef.Width.Value;
    }

    private void OnRightSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null) return;
        _draggingRightSplitter = true;
        _splitterStartX = e.GetCurrentPoint(SplitGrid).Position.X;
        _splitterStartWidth = _vm.RightSidebarWidth;
        RightSplitterBar.CapturePointer(e.Pointer);
    }

    private void OnRightSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingRightSplitter || _vm is null) return;
        double dx = _splitterStartX - e.GetCurrentPoint(SplitGrid).Position.X;
        double clamped = Math.Clamp(_splitterStartWidth + dx, UiSettingsData.MinRightSidebarWidth, UiSettingsData.MaxRightSidebarWidth);
        RightColumnDef.Width = new GridLength(clamped);
    }

    private void OnRightSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingRightSplitter || _vm is null) return;
        _draggingRightSplitter = false;
        RightSplitterBar.ReleasePointerCapture(e.Pointer);
        _vm.RightSidebarWidth = RightColumnDef.Width.Value;
    }

    private void ApplyViewportLayout(CenterViewMode viewMode, SplitViewportOrientation orientation, double splitRatio)
    {
        double r = Math.Clamp(splitRatio, UiSettingsData.MinSplitViewportRatio, UiSettingsData.MaxSplitViewportRatio);
        switch (viewMode)
        {
            case CenterViewMode.Mode3D:
                Viewport3DRowDef.Height = new GridLength(1, GridUnitType.Star);
                ViewportSplitDividerRowDef.Height = new GridLength(0);
                Viewport2DRowDef.Height = new GridLength(0);
                Viewport3DColDef.Width = new GridLength(1, GridUnitType.Star);
                ViewportSplitDividerColDef.Width = new GridLength(0);
                Viewport2DColDef.Width = new GridLength(0);
                Grid.SetRow(Viewer3D, 0);
                Grid.SetColumn(Viewer3D, 0);
                Grid.SetRowSpan(Viewer3D, 3);
                Grid.SetColumnSpan(Viewer3D, 3);
                Viewer3D.Visibility = Visibility.Visible;
                ViewportSplitDivider.Visibility = Visibility.Collapsed;
                Editor2D.Visibility = Visibility.Collapsed;
                break;

            case CenterViewMode.Mode2D:
                Viewport3DRowDef.Height = new GridLength(0);
                ViewportSplitDividerRowDef.Height = new GridLength(0);
                Viewport2DRowDef.Height = new GridLength(1, GridUnitType.Star);
                Viewport3DColDef.Width = new GridLength(0);
                ViewportSplitDividerColDef.Width = new GridLength(0);
                Viewport2DColDef.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(Editor2D, 2);
                Grid.SetColumn(Editor2D, 2);
                Grid.SetRowSpan(Editor2D, 1);
                Grid.SetColumnSpan(Editor2D, 1);
                Viewer3D.Visibility = Visibility.Collapsed;
                ViewportSplitDivider.Visibility = Visibility.Collapsed;
                Editor2D.Visibility = Visibility.Visible;
                break;

            default:
                Viewer3D.Visibility = Visibility.Visible;
                ViewportSplitDivider.Visibility = Visibility.Visible;
                Editor2D.Visibility = Visibility.Visible;

                if (orientation == SplitViewportOrientation.HorizontalSideBySide)
                {
                    Viewport3DRowDef.Height = new GridLength(1, GridUnitType.Star);
                    ViewportSplitDividerRowDef.Height = new GridLength(0);
                    Viewport2DRowDef.Height = new GridLength(0);
                    Viewport3DColDef.Width = new GridLength(r, GridUnitType.Star);
                    ViewportSplitDividerColDef.Width = new GridLength(5);
                    Viewport2DColDef.Width = new GridLength(1.0 - r, GridUnitType.Star);

                    Grid.SetRow(Viewer3D, 0);
                    Grid.SetColumn(Viewer3D, 0);
                    Grid.SetRowSpan(Viewer3D, 3);
                    Grid.SetColumnSpan(Viewer3D, 1);

                    Grid.SetRow(ViewportSplitDivider, 0);
                    Grid.SetColumn(ViewportSplitDivider, 1);
                    Grid.SetRowSpan(ViewportSplitDivider, 3);
                    Grid.SetColumnSpan(ViewportSplitDivider, 1);

                    Grid.SetRow(Editor2D, 0);
                    Grid.SetColumn(Editor2D, 2);
                    Grid.SetRowSpan(Editor2D, 3);
                    Grid.SetColumnSpan(Editor2D, 1);
                }
                else
                {
                    Viewport3DRowDef.Height = new GridLength(r, GridUnitType.Star);
                    ViewportSplitDividerRowDef.Height = new GridLength(5);
                    Viewport2DRowDef.Height = new GridLength(1.0 - r, GridUnitType.Star);
                    Viewport3DColDef.Width = new GridLength(1, GridUnitType.Star);
                    ViewportSplitDividerColDef.Width = new GridLength(0);
                    Viewport2DColDef.Width = new GridLength(0);

                    Grid.SetRow(Viewer3D, 0);
                    Grid.SetColumn(Viewer3D, 0);
                    Grid.SetRowSpan(Viewer3D, 1);
                    Grid.SetColumnSpan(Viewer3D, 3);

                    Grid.SetRow(ViewportSplitDivider, 1);
                    Grid.SetColumn(ViewportSplitDivider, 0);
                    Grid.SetRowSpan(ViewportSplitDivider, 1);
                    Grid.SetColumnSpan(ViewportSplitDivider, 3);

                    Grid.SetRow(Editor2D, 2);
                    Grid.SetColumn(Editor2D, 0);
                    Grid.SetRowSpan(Editor2D, 1);
                    Grid.SetColumnSpan(Editor2D, 3);
                }
                UpdateViewportSplitDividerVisuals(orientation);
                break;
        }
    }

    private void ApplyViewportSplitStars(SplitViewportOrientation orientation, double ratio)
    {
        double r = Math.Clamp(ratio, UiSettingsData.MinSplitViewportRatio, UiSettingsData.MaxSplitViewportRatio);
        if (orientation == SplitViewportOrientation.HorizontalSideBySide)
        {
            Viewport3DColDef.Width = new GridLength(r, GridUnitType.Star);
            Viewport2DColDef.Width = new GridLength(1.0 - r, GridUnitType.Star);
        }
        else
        {
            Viewport3DRowDef.Height = new GridLength(r, GridUnitType.Star);
            Viewport2DRowDef.Height = new GridLength(1.0 - r, GridUnitType.Star);
        }
    }

    private void UpdateViewportSplitDividerVisuals(SplitViewportOrientation orientation)
    {
        bool active = _draggingViewportDivider || _viewportDividerHovered;
        double thickness = active ? 2.0 : 1.0;

        if (orientation == SplitViewportOrientation.HorizontalSideBySide)
        {
            ViewportSplitDividerHairline.Width = thickness;
            ViewportSplitDividerHairline.Height = double.NaN;
            ViewportSplitDividerHairline.HorizontalAlignment = HorizontalAlignment.Center;
            ViewportSplitDividerHairline.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            ViewportSplitDividerHairline.Height = thickness;
            ViewportSplitDividerHairline.Width = double.NaN;
            ViewportSplitDividerHairline.HorizontalAlignment = HorizontalAlignment.Stretch;
            ViewportSplitDividerHairline.VerticalAlignment = VerticalAlignment.Center;
        }

        string brushKey = active ? "CmAccentPrimaryBrush" : "CmHairlineBorderBrush";
        if (ThemeBrushHelper.ResolveBrush(this, brushKey) is Brush hairlineBrush)
        {
            ViewportSplitDividerHairline.Background = hairlineBrush;
        }
    }

    private void OnViewportSplitDividerPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null) return;
        _viewportDividerHovered = true;
        ProtectedCursor = _vm.IsSplitHorizontal ? SizeWeCursor : SizeNsCursor;
        UpdateViewportSplitDividerVisuals(_vm.SplitOrientation);
    }

    private void OnViewportSplitDividerPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _viewportDividerHovered = false;
        if (!_draggingViewportDivider)
        {
            ProtectedCursor = null;
        }
        if (_vm is not null)
        {
            UpdateViewportSplitDividerVisuals(_vm.SplitOrientation);
        }
    }

    private void OnViewportSplitDividerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm is null || _vm.CenterViewMode != CenterViewMode.Split) return;
        _draggingViewportDivider = true;
        _activeDragSplitRatio = _vm.SplitViewportRatio;
        ProtectedCursor = _vm.IsSplitHorizontal ? SizeWeCursor : SizeNsCursor;
        ViewportSplitDivider.CapturePointer(e.Pointer);
        UpdateViewportSplitDividerVisuals(_vm.SplitOrientation);
        e.Handled = true;
    }

    private void OnViewportSplitDividerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingViewportDivider || _vm is null) return;
        Windows.Foundation.Point pos = e.GetCurrentPoint(CenterViewportGrid).Position;
        double total = _vm.IsSplitHorizontal
            ? Math.Max(1.0, CenterViewportGrid.ActualWidth - 5.0)
            : Math.Max(1.0, CenterViewportGrid.ActualHeight - 5.0);
        double offset = _vm.IsSplitHorizontal ? pos.X - 2.5 : pos.Y - 2.5;
        double ratio = Math.Clamp(offset / total, UiSettingsData.MinSplitViewportRatio, UiSettingsData.MaxSplitViewportRatio);
        _activeDragSplitRatio = ratio;
        ApplyViewportSplitStars(_vm.SplitOrientation, ratio);
        e.Handled = true;
    }

    private void OnViewportSplitDividerPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingViewportDivider || _vm is null) return;
        _draggingViewportDivider = false;
        ViewportSplitDivider.ReleasePointerCapture(e.Pointer);
        if (!_viewportDividerHovered)
        {
            ProtectedCursor = null;
        }
        _vm.SplitViewportRatio = _activeDragSplitRatio;
        UpdateViewportSplitDividerVisuals(_vm.SplitOrientation);
        e.Handled = true;
    }

    private void OnViewportSplitDividerPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingViewportDivider)
        {
            _draggingViewportDivider = false;
            if (_vm is not null)
            {
                _vm.SplitViewportRatio = _activeDragSplitRatio;
                UpdateViewportSplitDividerVisuals(_vm.SplitOrientation);
            }
        }
        if (!_viewportDividerHovered)
        {
            ProtectedCursor = null;
        }
    }

    private void OnViewportSplitDividerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ResetSplitRatio();
        e.Handled = true;
    }
}
