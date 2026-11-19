using System.Globalization;
using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.System;
using Windows.UI.Core;

namespace CaixaMaker.App.Controls;

/// <summary>
/// Editor CAD 2D milimétrico interativo da face ativa exibindo contorno real da chapa MDF,
/// zonas proibidas estruturais, área segura tracejada, elementos CNC coloridos por camada,
/// zoom focado no cursor, pan e gizmo verde #16A34A com 8 alças de resize + rotação livre.
/// </summary>
public sealed partial class Surface2DEditorControl : UserControl
{
    private readonly SurfaceCanvas2DController _controller = new();

    private MainWindowViewModel? _mainVm;
    private SurfaceId? _lastRenderedSurfaceId;
    private bool _lastSheetModeRendered;
    private WriteableBitmap? _canvasBitmap;
    private SKSurface? _editorSurface;
    private int _editorSurfaceW;
    private int _editorSurfaceH;
    private bool _renderQueued;
    private bool _needsCoalescedRender;
    private BoxSheetGeometry2D? _cachedContourSheet;
    private SurfaceId _cachedContourSurfaceId;
    private SurfaceContour2D? _cachedActiveContour;

    private double _sheetZoom = 1.0;
    private double _sheetPanPxX;
    private double _sheetPanPxY;
    private double _sheetCursorMmX;
    private double _sheetCursorMmY;
    private bool _sheetPanning;
    private bool _sheetPointerDown;
    private bool _sheetDragged;
    private double _sheetDownPxX;
    private double _sheetDownPxY;
    private double _sheetOrigPanX;
    private double _sheetOrigPanY;
    private string? _lastRenderedSelectedPartId;
    private bool _suppressNextContextMenu;

    private bool IsSheetNestingMode
        => _mainVm is { IsFabricationArea: true } && _mainVm.Manufacturing.ShowFullSheetPreviewInFabrication;

    public Surface2DEditorControl()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            UpdateResponsiveHeaderText(e.NewSize.Width);
            RenderSurface2D();
        };
        Unloaded += (_, _) => DisposeCachedSurfaces();
    }

    public void BindViewModel(MainWindowViewModel viewModel)
    {
        if (_mainVm is not null)
        {
            _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
            _mainVm.Personalization.PropertyChanged -= OnPersPropertyChanged;
            _mainVm.Personalization.VisualStateChanged -= OnStateChanged;
            _mainVm.Personalization.InteractiveTransformChanged -= OnInteractiveTransformChanged;
            _mainVm.Box.GeometryRebuilt -= OnStateChanged;
            _mainVm.Box.ViewerStateChanged -= OnBoxViewerStateChanged;
            _mainVm.Manufacturing.PropertyChanged -= OnManufacturingPropertyChanged;
            _mainVm.Manufacturing.LayerVisibilityChanged -= OnStateChanged;
            _mainVm.Theme.ThemeChanged -= OnThemeChanged;
        }

        _mainVm = viewModel;
        _mainVm.PropertyChanged += OnMainVmPropertyChanged;
        _mainVm.Personalization.PropertyChanged += OnPersPropertyChanged;
        _mainVm.Personalization.VisualStateChanged += OnStateChanged;
        _mainVm.Personalization.InteractiveTransformChanged += OnInteractiveTransformChanged;
        _mainVm.Box.GeometryRebuilt += OnStateChanged;
        _mainVm.Box.ViewerStateChanged += OnBoxViewerStateChanged;
        _mainVm.Manufacturing.PropertyChanged += OnManufacturingPropertyChanged;
        _mainVm.Manufacturing.LayerVisibilityChanged += OnStateChanged;
        _mainVm.Theme.ThemeChanged += OnThemeChanged;

        _lastRenderedSurfaceId = null;
        _lastSheetModeRendered = false;
        _lastRenderedSelectedPartId = _mainVm.Box.SelectedPartId;
        _cachedContourSheet = null;
        _cachedActiveContour = null;
        RebuildContextualHeader();
        RenderSurface2D();
    }

    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ActiveArea)
            or nameof(MainWindowViewModel.CenterViewMode)
            or nameof(MainWindowViewModel.IsPersonalizationArea)
            or nameof(MainWindowViewModel.IsFabricationArea)
            or nameof(MainWindowViewModel.SplitOrientation)
            or nameof(MainWindowViewModel.InDesigner))
        {
            if (IsSheetNestingMode != _lastSheetModeRendered)
            {
                _sheetZoom = 1.0;
                _sheetPanPxX = 0.0;
                _sheetPanPxY = 0.0;
            }
            RebuildContextualHeader();
            RenderSurface2D();
        }
    }

    private void OnManufacturingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ManufacturingWorkspaceViewModel.SelectedLaserBedPreset)
            or nameof(ManufacturingWorkspaceViewModel.CustomBedWidthMm)
            or nameof(ManufacturingWorkspaceViewModel.CustomBedHeightMm)
            or nameof(ManufacturingWorkspaceViewModel.SelectedLaserBed)
            or nameof(ManufacturingWorkspaceViewModel.SelectedExportScope)
            or nameof(ManufacturingWorkspaceViewModel.ShowFullSheetPreviewInFabrication)
            or nameof(ManufacturingWorkspaceViewModel.SelectedSinglePanelId)
            or nameof(ManufacturingWorkspaceViewModel.CurrentSheetGeometry))
        {
            RebuildContextualHeader();
            if (_mainVm?.Manufacturing.IsInteractiveUpdate == true)
            {
                RequestCoalescedRender();
            }
            else
            {
                RenderSurface2D();
            }
        }
    }

    private void OnBoxViewerStateChanged(object? sender, EventArgs e)
    {
        if (_mainVm is null) return;
        string? selectedPartId = _mainVm.Box.SelectedPartId;
        if (IsSheetNestingMode && !string.Equals(_lastRenderedSelectedPartId, selectedPartId, StringComparison.Ordinal))
        {
            _lastRenderedSelectedPartId = selectedPartId;
            RebuildContextualHeader();
            RenderSurface2D();
            return;
        }

        _lastRenderedSelectedPartId = selectedPartId;
    }

    private void OnPersPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_mainVm is null) return;
        if (e.PropertyName == nameof(PersonalizationWorkspaceViewModel.ActiveToolMode))
        {
            Canvas2DToolMode mode = _mainVm.Personalization.ActiveToolMode;
            if (mode == Canvas2DToolMode.Select && _controller.IsDrawingShape)
            {
                _controller.CancelActiveDrawing(_mainVm.Personalization);
            }
            _controller.ActiveToolMode = mode;
            UpdateDrawToolStrip();
            UpdateCursorAndReadout();
            RenderSurface2D();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        RebuildContextualHeader();
        RenderSurface2D();
    }

    private void OnThemeChanged(object? sender, ResolvedAppTheme e) => RenderSurface2D();

    private void RebuildContextualHeader()
    {
        if (_mainVm is null) return;

        bool sheetMode = IsSheetNestingMode;
        EditingHeaderLeftPanel.Visibility = sheetMode ? Visibility.Collapsed : Visibility.Visible;
        SheetHeaderLeftPanel.Visibility = sheetMode ? Visibility.Visible : Visibility.Collapsed;
        BtnReturnFullSheet.Visibility = !sheetMode && _mainVm.IsFabricationArea
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateDrawToolStrip();

        Style activeStyle = (Style)Application.Current.Resources["PrimaryAccentButtonStyle"];
        Style idleStyle = (Style)Application.Current.Resources["CompactToolbarButtonStyle"];

        bool showSplitButtons = _mainVm.IsPersonalizationArea || _mainVm.IsFabricationArea;
        SplitLayoutButtonsPanel.Visibility = showSplitButtons ? Visibility.Visible : Visibility.Collapsed;
        if (showSplitButtons)
        {
            BtnSplitVertical.Style = _mainVm.IsSplitVertical ? activeStyle : idleStyle;
            BtnSplitHorizontal.Style = _mainVm.IsSplitHorizontal ? activeStyle : idleStyle;
        }

        PersonalizationWorkspaceViewModel pers = _mainVm.Personalization;
        if (sheetMode && _mainVm.Box.SheetGeometry is { } sheetGeom)
        {
            SheetHeaderTitleText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "PLANO DE CORTE 1:1 ({0} CHAPAS)",
                sheetGeom.Panels.Count);

            double perimM = _mainVm.Box.TotalCutPerimeterMeters;
            bool wide = ActualWidth >= 920.0 || ActualWidth <= 1.0;
            SheetMetricsLabel.Text = string.Format(
                CultureInfo.InvariantCulture,
                wide ? "Folha: {0:0.#}×{1:0.#} mm • Corte: {2:0.00} m" : "{0:0.#}×{1:0.#}mm ({2:0.0}m)",
                sheetGeom.Layout.SheetWidth,
                sheetGeom.Layout.SheetHeight,
                perimM);

            SelectionActionsPanel.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(
                BtnFitZoom,
                string.Format(CultureInfo.InvariantCulture, "Enquadrar a folha de corte completa ({0:0}%)", Math.Round(_sheetZoom * 100.0)));
            UpdateResponsiveHeaderText(ActualWidth);
            UpdateCursorAndReadout();
            return;
        }

        SurfaceTabsPanel.Children.Clear();

        foreach (SurfaceDefinition surf in pers.Surfaces)
        {
            bool active = surf.PanelId == pers.ActiveSurfaceId;
            int count = pers.Manager.ListElements(surf.PanelId).Count;
            string baseName = FormatCompactSurfaceTabName(surf.PanelId);
            string label = count > 0
                ? $"{baseName} ({count})"
                : baseName;

            var btn = new Button
            {
                Content = label,
                Tag = surf.PanelId,
                Height = 20,
                MinHeight = 20,
                FontSize = 10,
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(2),
                Style = active ? activeStyle : idleStyle,
            };
            btn.Click += OnSurfaceTabClick;
            SurfaceTabsPanel.Children.Add(btn);
        }

        SurfaceDefinition? activeSurface = pers.ActiveSurface;
        if (activeSurface is not null)
        {
            bool wide = ActualWidth >= 920.0;
            SurfaceDimensionsLabel.Text = string.Format(
                CultureInfo.InvariantCulture,
                wide ? "{0:F1} × {1:F1} mm" : "{0:0.#}×{1:0.#}",
                activeSurface.WidthMm,
                activeSurface.HeightMm);
        }

        SelectionActionsPanel.Visibility = pers.HasSelection ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(
            BtnFitZoom,
            string.Format(CultureInfo.InvariantCulture, "Enquadrar a face ativa ({0:0}%)", Math.Round(_controller.UserZoom * 100.0)));
        UpdateResponsiveHeaderText(ActualWidth);
        UpdateCursorAndReadout();
    }

    private static string FormatCompactSurfaceTabName(SurfaceId panelId) => panelId switch
    {
        SurfaceId.Front => "Frente",
        SurfaceId.Back => "Trás",
        SurfaceId.Left => "Esq",
        SurfaceId.Right => "Dir",
        SurfaceId.Lid => "Tampa",
        SurfaceId.Bottom => "Base",
        _ => UiConvert.SurfaceShortName(panelId),
    };

    private void UpdateResponsiveHeaderText(double availableWidth)
    {
        if (availableWidth <= 1.0)
        {
            return;
        }

        bool hasSelectionActions = !IsSheetNestingMode && (_mainVm?.Personalization.HasSelection ?? false);
        bool compactSplit = availableWidth < (hasSelectionActions ? 820.0 : 680.0);
        bool ultraCompact = availableWidth < 520.0;
        BtnSplitVertical.Content = compactSplit ? "⬒" : "⬒ Vert";
        BtnSplitHorizontal.Content = compactSplit ? "◫" : "◫ Lado";

        bool sheetMode = IsSheetNestingMode;
        if (sheetMode)
        {
            LegendPanel.Visibility = Visibility.Collapsed;
            bool wide = availableWidth >= 880.0;
            bool veryNarrow = availableWidth < 460.0;
            SheetLegendPanel.Visibility = availableWidth < 620.0 ? Visibility.Collapsed : Visibility.Visible;
            SheetExtLegendText.Text = wide ? "Corte Ext." : "Ext";
            SheetIntLegendText.Text = wide ? "Interno" : "Int";
            SheetBedLegendText.Text = wide ? "Mesa CNC" : "Mesa";
            BtnSheetModeFull.Content = wide ? "Folha Completa" : (ultraCompact ? "Folha" : "Folha 1:1");
            BtnSheetModeSingle.Content = wide ? "Chapa Isolada" : (ultraCompact ? "Chapa" : "Isolada");
            BtnFitZoom.Content = wide ? "Ajustar Vista" : "Ajustar";
            SheetMetricsLabel.Visibility = ultraCompact ? Visibility.Collapsed : Visibility.Visible;
            SheetMetricsDivider.Visibility = ultraCompact ? Visibility.Collapsed : Visibility.Visible;
            CursorCoordsText.Visibility = ultraCompact ? Visibility.Collapsed : Visibility.Visible;
            CoordsDivider.Visibility = ultraCompact ? Visibility.Collapsed : Visibility.Visible;

            if (_mainVm?.Box.SheetGeometry is { } sheetGeom)
            {
                SheetHeaderTitleText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    wide ? "PLANO DE CORTE 1:1 ({0} CHAPAS)" : (veryNarrow ? "1:1 ({0}P)" : "PLANO 1:1 ({0}P)"),
                    sheetGeom.Panels.Count);
                SheetMetricsLabel.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    wide
                        ? "Folha: {0:0.#}×{1:0.#} mm • Corte: {2:0.00} m"
                        : "{0:0.#}×{1:0.#}mm • {2:0.00}m",
                    sheetGeom.Layout.SheetWidth,
                    sheetGeom.Layout.SheetHeight,
                    _mainVm.Box.TotalCutPerimeterMeters);
            }
            UpdateCursorAndReadout();
            return;
        }

        SheetLegendPanel.Visibility = Visibility.Collapsed;
        SheetMetricsDivider.Visibility = Visibility.Collapsed;
        bool showCoords = !ultraCompact;
        CursorCoordsText.Visibility = showCoords ? Visibility.Visible : Visibility.Collapsed;
        CoordsDivider.Visibility = showCoords ? Visibility.Visible : Visibility.Collapsed;

        bool hideEditDims = availableWidth < (hasSelectionActions ? 820.0 : 540.0);
        SurfaceDimensionsLabel.Visibility = hideEditDims ? Visibility.Collapsed : Visibility.Visible;
        EditingDimsDivider.Visibility = hideEditDims ? Visibility.Collapsed : Visibility.Visible;
        double legendCollapseWidth = hasSelectionActions ? 840.0 : 620.0;
        if (availableWidth < legendCollapseWidth)
        {
            LegendPanel.Visibility = Visibility.Collapsed;
            BtnFitZoom.Content = "Ajustar";
        }
        else if (availableWidth < 920.0)
        {
            LegendPanel.Visibility = Visibility.Visible;
            SafeLegendText.Text = "Segura";
            RestrictedLegendText.Text = "Restrito";
            BtnFitZoom.Content = "Ajustar";
        }
        else
        {
            LegendPanel.Visibility = Visibility.Visible;
            SafeLegendText.Text = "Área segura";
            RestrictedLegendText.Text = "Encaixes / Restrito";
            BtnFitZoom.Content = "Ajustar Vista";
        }

        if (_mainVm?.Personalization.ActiveSurface is SurfaceDefinition activeSurface)
        {
            SurfaceDimensionsLabel.Text = string.Format(
                CultureInfo.InvariantCulture,
                availableWidth >= 920.0 ? "{0:F1} × {1:F1} mm" : "{0:0.#}×{1:0.#}",
                activeSurface.WidthMm,
                activeSurface.HeightMm);
        }

        DrawToolHintText.Visibility = availableWidth >= 900.0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCursorAndReadout();
    }

    private void UpdateDrawToolStrip()
    {
        if (_mainVm is null)
        {
            DrawToolStripBorder.Visibility = Visibility.Collapsed;
            return;
        }

        bool showStrip = !IsSheetNestingMode && _mainVm.IsPersonalizationArea;
        DrawToolStripBorder.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        if (!showStrip)
        {
            return;
        }

        Style activeStyle = (Style)Application.Current.Resources["PrimaryAccentButtonStyle"];
        Style idleStyle = (Style)Application.Current.Resources["CompactToolbarButtonStyle"];
        Canvas2DToolMode mode = _mainVm.Personalization.ActiveToolMode;

        ToolSelectBtn.Style = mode == Canvas2DToolMode.Select ? activeStyle : idleStyle;
        ToolDrawRectBtn.Style = mode == Canvas2DToolMode.DrawRectangle ? activeStyle : idleStyle;
        ToolDrawCircleBtn.Style = mode == Canvas2DToolMode.DrawCircle ? activeStyle : idleStyle;
        ToolDrawSlotBtn.Style = mode == Canvas2DToolMode.DrawSlot ? activeStyle : idleStyle;
        ToolDrawPolyBtn.Style = mode == Canvas2DToolMode.DrawPolygon ? activeStyle : idleStyle;
        ToolDrawCrossBtn.Style = mode == Canvas2DToolMode.DrawCrosshair ? activeStyle : idleStyle;
    }

    private void OnDrawToolClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || sender is not Button { Tag: string raw }) return;
        if (Enum.TryParse<Canvas2DToolMode>(raw, out Canvas2DToolMode mode))
        {
            _mainVm.Personalization.SetActiveToolMode(mode);
            CanvasHost.Focus(FocusState.Programmatic);
        }
    }

    private void OnEditorPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_mainVm is null || IsSheetNestingMode || !_mainVm.IsPersonalizationArea)
        {
            return;
        }

        bool ctrl = IsKeyDown(VirtualKey.Control);
        bool alt = IsKeyDown(VirtualKey.Menu);
        if (ctrl || alt)
        {
            return;
        }

        object? focused = XamlRoot is not null ? FocusManager.GetFocusedElement(XamlRoot) : null;
        FocusControlKind focusKind = UiConvert.ClassifyFocusedControl(focused, out bool isReadOnly);
        var context = new KeyboardFocusContext(focusKind, isReadOnly, HasOpenModal: false);

        string? cmdId = e.Key switch
        {
            VirtualKey.V => DesktopCommandIds.ToolSelect,
            VirtualKey.R => DesktopCommandIds.ToolDrawRectangle,
            VirtualKey.C => DesktopCommandIds.ToolDrawCircle,
            VirtualKey.S => DesktopCommandIds.ToolDrawSlot,
            VirtualKey.P => DesktopCommandIds.ToolDrawPolygon,
            VirtualKey.X => DesktopCommandIds.ToolDrawCrosshair,
            VirtualKey.Escape => DesktopCommandIds.ToolCancel,
            _ => null,
        };
        if (cmdId is null || !KeyboardShortcutGuard.ShouldDispatchCommand(context, cmdId))
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            bool cancelledMarquee = _controller.CancelActiveMarquee();
            bool cancelledDraw = _controller.CancelActiveDrawing(_mainVm.Personalization);
            _mainVm.Personalization.SetActiveToolMode(Canvas2DToolMode.Select);
            if (cancelledMarquee || cancelledDraw)
            {
                RenderSurface2D();
            }
            e.Handled = true;
            return;
        }

        Canvas2DToolMode? nextMode = e.Key switch
        {
            VirtualKey.V => Canvas2DToolMode.Select,
            VirtualKey.R => Canvas2DToolMode.DrawRectangle,
            VirtualKey.C => Canvas2DToolMode.DrawCircle,
            VirtualKey.S => Canvas2DToolMode.DrawSlot,
            VirtualKey.P => Canvas2DToolMode.DrawPolygon,
            VirtualKey.X => Canvas2DToolMode.DrawCrosshair,
            _ => null,
        };
        if (nextMode.HasValue)
        {
            _mainVm.Personalization.SetActiveToolMode(nextMode.Value);
            e.Handled = true;
        }
    }

    private void OnSheetModeFullClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null) return;
        _mainVm.Manufacturing.ShowFullSheetPreviewInFabrication = true;
    }

    private void OnSheetModeSingleClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null) return;
        _mainVm.Manufacturing.ShowFullSheetPreviewInFabrication = false;
    }

    private void OnSplitOrientationClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || sender is not FrameworkElement { Tag: string tag }) return;
        if (Enum.TryParse<SplitViewportOrientation>(tag, out SplitViewportOrientation orientation))
        {
            _mainVm.SetSplitOrientation(orientation);
            if (_mainVm.CenterViewMode != CenterViewMode.Split)
            {
                _mainVm.SetCenterViewMode(CenterViewMode.Split);
            }
        }
    }

    private void OnSurfaceTabClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || sender is not Button { Tag: SurfaceId id }) return;
        _mainVm.Personalization.SelectSurface(id);
    }

    private void OnCenterSelectionClick(object sender, RoutedEventArgs e)
        => _mainVm?.Personalization.ApplyQuickAlignment(QuickAlignmentPreset.CenterSurface);

    private void OnRotate90Click(object sender, RoutedEventArgs e)
        => _mainVm?.Personalization.RotateSelected90();

    private void OnDuplicateSelectionClick(object sender, RoutedEventArgs e)
        => _mainVm?.Personalization.DuplicateSelectedElement();

    private void OnDeleteSelectionClick(object sender, RoutedEventArgs e)
        => _mainVm?.Personalization.DeleteSelectedElement();

    private void OnContextCopyClick(object sender, RoutedEventArgs e)
        => _mainVm?.Personalization.CopySelectedElement();

    private void OnContextPasteClick(object sender, RoutedEventArgs e)
        => _mainVm?.Personalization.PasteClipboardElement();

    private void OnContextMenuOpening(object sender, object e)
    {
        if (_mainVm is null) return;
        bool sheetMode = IsSheetNestingMode;
        bool hasSel = !sheetMode && _mainVm.Personalization.HasSelection;
        bool unlocked = _mainVm.Personalization.SelectedElement is { Locked: false };

        CtxCenterItem.IsEnabled = hasSel && unlocked;
        CtxRotate90Item.IsEnabled = hasSel && unlocked;
        CtxCopyItem.IsEnabled = hasSel;
        CtxDuplicateItem.IsEnabled = hasSel;
        CtxDeleteItem.IsEnabled = hasSel && unlocked;
        CtxPasteItem.IsEnabled = !sheetMode && _mainVm.Personalization.HasClipboard;
    }

    private void OnFitZoomClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null) return;
        if (IsSheetNestingMode)
        {
            _sheetZoom = 1.0;
            _sheetPanPxX = 0.0;
            _sheetPanPxY = 0.0;
            RebuildContextualHeader();
            RenderSurface2D();
            return;
        }

        if (_mainVm.Personalization.ActiveSurface is not SurfaceDefinition surf) return;
        (int pixelW, int pixelH) = ResolveRenderDimensions();
        SurfaceContour2D? contour = ResolveActiveContour(surf);
        _controller.FitToView(surf, pixelW, pixelH, contour);
        RebuildContextualHeader();
        RenderSurface2D();
    }

    private (double PxX, double PxY) ToCanvasPixels(PointerPoint pt, int pixelW, int pixelH)
    {
        double hostW = Math.Max(1.0, CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : ActualWidth);
        double hostH = Math.Max(1.0, CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : ActualHeight - 72);
        return (pt.Position.X * pixelW / hostW, pt.Position.Y * pixelH / hostH);
    }

    private RenderManufacturingSheet2DOptions BuildSheetRenderOptions(int pixelW, int pixelH)
    {
        LaserBedSpec bed = _mainVm!.Manufacturing.SelectedLaserBed;
        HashSet<string>? errorIds = _mainVm.Personalization.ValidationReport?.Errors
            .Where(err => !string.IsNullOrEmpty(err.ElementId))
            .Select(err => err.ElementId!)
            .ToHashSet(StringComparer.Ordinal);

        return new RenderManufacturingSheet2DOptions
        {
            WidthPx = pixelW,
            HeightPx = pixelH,
            DarkTheme = _mainVm.Theme.IsDarkTheme,
            SelectedPartId = _mainVm.Box.SelectedPartId,
            SelectedElementId = _mainVm.Personalization.SelectedElementId,
            ErrorElementIds = errorIds,
            IsLayerVisible = op => _mainVm.Manufacturing.IsLayerVisible(op),
            LaserBedWidthMm = bed.WidthMm,
            LaserBedHeightMm = bed.HeightMm,
            LaserBedLabel = bed.HasLimit ? $"{bed.ShortLabel} mm" : null,
            ShowLaserBedOverlay = bed.HasLimit,
            ShowSheetDimensions = true,
            UserZoom = _sheetZoom,
            PanPxX = _sheetPanPxX,
            PanPxY = _sheetPanPxY,
            CursorMmX = _sheetCursorMmX,
            CursorMmY = _sheetCursorMmY,
        };
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mainVm is null) return;
        (int pixelW, int pixelH) = ResolveRenderDimensions();
        PointerPoint pt = e.GetCurrentPoint(CanvasHost);
        Editor2DMouseButton? btn = pt.Properties.IsMiddleButtonPressed
            ? Editor2DMouseButton.Middle
            : pt.Properties.IsRightButtonPressed
                ? Editor2DMouseButton.Right
                : pt.Properties.IsLeftButtonPressed
                    ? Editor2DMouseButton.Left
                    : null;
        if (btn is null) return;

        (double pxX, double pxY) = ToCanvasPixels(pt, pixelW, pixelH);
        bool spaceHeld = IsKeyDown(VirtualKey.Space);

        if (IsSheetNestingMode && _mainVm.Box.SheetGeometry is { } sheet)
        {
            if (btn == Editor2DMouseButton.Right)
            {
                return;
            }
            RenderManufacturingSheet2DOptions opts = BuildSheetRenderOptions(pixelW, pixelH);
            SheetViewTransform2D tx = OffscreenViewportRenderer.ComputeManufacturingSheetTransform(sheet, opts);
            _sheetCursorMmX = (pxX - tx.OriginPxX) / tx.EffectiveScalePxPerMm;
            _sheetCursorMmY = (pxY - tx.OriginPxY) / tx.EffectiveScalePxPerMm;

            _sheetPointerDown = true;
            _sheetDragged = false;
            _sheetPanning = btn == Editor2DMouseButton.Middle || (btn == Editor2DMouseButton.Left && spaceHeld);
            _sheetDownPxX = pxX;
            _sheetDownPxY = pxY;
            _sheetOrigPanX = _sheetPanPxX;
            _sheetOrigPanY = _sheetPanPxY;

            CanvasHost.CapturePointer(e.Pointer);
            UpdateCursorAndReadout();
            RenderSurface2D();
            e.Handled = true;
            return;
        }

        if (_mainVm.Personalization.ActiveSurface is not SurfaceDefinition surf) return;
        SurfaceContour2D? contour = ResolveActiveContour(surf);
        _controller.UpdateLayout(surf, pixelW, pixelH, contour);

        if (btn == Editor2DMouseButton.Right)
        {
            if (_controller.IsDrawingShape ||
                _controller.IsMarqueeSelecting ||
                _mainVm.Personalization.ActiveToolMode != Canvas2DToolMode.Select)
            {
                _suppressNextContextMenu = true;
                _controller.OnPointerPressed(_mainVm.Personalization, pxX, pxY, Editor2DMouseButton.Right, spaceHeld, shiftHeld: false);
                UpdateDrawToolStrip();
                UpdateCursorAndReadout();
                RenderSurface2D();
                e.Handled = true;
            }
            return;
        }

        CanvasHost.Focus(FocusState.Pointer);
        bool shiftHeld = IsKeyDown(VirtualKey.Shift);
        _controller.OnPointerPressed(_mainVm.Personalization, pxX, pxY, btn.Value, spaceHeld, shiftHeld);
        CanvasHost.CapturePointer(e.Pointer);
        UpdateCursorAndReadout();
        RenderSurface2D();
        e.Handled = true;
    }

    private void OnCanvasContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_suppressNextContextMenu || _controller.IsDrawingShape)
        {
            _suppressNextContextMenu = false;
            args.Handled = true;
        }
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_mainVm is null) return;
        (int pixelW, int pixelH) = ResolveRenderDimensions();
        PointerPoint pt = e.GetCurrentPoint(CanvasHost);
        (double pxX, double pxY) = ToCanvasPixels(pt, pixelW, pixelH);

        if (IsSheetNestingMode && _mainVm.Box.SheetGeometry is { } sheet)
        {
            RenderManufacturingSheet2DOptions opts = BuildSheetRenderOptions(pixelW, pixelH);
            SheetViewTransform2D tx = OffscreenViewportRenderer.ComputeManufacturingSheetTransform(sheet, opts);
            _sheetCursorMmX = (pxX - tx.OriginPxX) / tx.EffectiveScalePxPerMm;
            _sheetCursorMmY = (pxY - tx.OriginPxY) / tx.EffectiveScalePxPerMm;

            if (_sheetPointerDown)
            {
                double dx = pxX - _sheetDownPxX;
                double dy = pxY - _sheetDownPxY;
                if (_sheetPanning || (dx * dx + dy * dy) > 9.0)
                {
                    _sheetDragged = true;
                    _sheetPanning = true;
                    _sheetPanPxX = _sheetOrigPanX + dx;
                    _sheetPanPxY = _sheetOrigPanY + dy;
                    RequestCoalescedRender();
                }
            }
            else
            {
                RequestCoalescedRender();
            }
            UpdateCursorAndReadout();
            return;
        }

        bool shiftHeld = IsKeyDown(VirtualKey.Shift);
        bool altHeld = IsKeyDown(VirtualKey.Menu);
        bool changed = _controller.OnPointerMoved(_mainVm.Personalization, pxX, pxY, shiftHeld, altHeld);
        UpdateCursorAndReadout();
        if (changed)
        {
            RequestCoalescedRender();
        }
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_mainVm is null) return;
        CanvasHost.ReleasePointerCapture(e.Pointer);

        if (IsSheetNestingMode)
        {
            bool wasClick = _sheetPointerDown && !_sheetDragged && !_sheetPanning;
            _sheetPointerDown = false;
            _sheetPanning = false;
            _sheetDragged = false;

            if (wasClick && _mainVm.Box.SheetGeometry is { } sheet)
            {
                (int pixelW, int pixelH) = ResolveRenderDimensions();
                PointerPoint pt = e.GetCurrentPoint(CanvasHost);
                (double pxX, double pxY) = ToCanvasPixels(pt, pixelW, pixelH);
                RenderManufacturingSheet2DOptions opts = BuildSheetRenderOptions(pixelW, pixelH);
                ManufacturingSheetPanelHit? hit = OffscreenViewportRenderer.HitTestManufacturingSheetPanel(sheet, opts, pxX, pxY);
                if (hit is not null)
                {
                    _mainVm.Box.SelectPart(hit.Value.PartId);
                    _mainVm.Manufacturing.SelectedSinglePanelId = hit.Value.SurfaceId;
                }
                else
                {
                    _mainVm.Box.SelectPart(null);
                }
                RenderSurface2D();
            }

            UpdateCursorAndReadout();
            return;
        }

        bool shiftHeld = IsKeyDown(VirtualKey.Shift);
        bool committed = _controller.OnPointerReleased(_mainVm.Personalization, shiftHeld);
        UpdateCursorAndReadout();
        if (committed)
        {
            RebuildContextualHeader();
            RenderSurface2D();
        }
    }

    private void OnCanvasPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_mainVm is null) return;
        if (IsSheetNestingMode)
        {
            _sheetPointerDown = false;
            _sheetPanning = false;
            _sheetDragged = false;
            UpdateCursorAndReadout();
            RenderSurface2D();
            return;
        }

        _controller.OnPointerReleased(_mainVm.Personalization);
        UpdateCursorAndReadout();
        RenderSurface2D();
    }

    private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_controller.IsDragging && !_sheetPanning)
        {
            ProtectedCursor = null;
        }
    }

    private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_mainVm is null) return;
        (int pixelW, int pixelH) = ResolveRenderDimensions();
        PointerPoint pt = e.GetCurrentPoint(CanvasHost);
        (double pxX, double pxY) = ToCanvasPixels(pt, pixelW, pixelH);
        int delta = pt.Properties.MouseWheelDelta;
        double factor = delta > 0 ? 1.15 : (1.0 / 1.15);

        if (IsSheetNestingMode && _mainVm.Box.SheetGeometry is { } sheet)
        {
            RenderManufacturingSheet2DOptions beforeOpts = BuildSheetRenderOptions(pixelW, pixelH);
            SheetViewTransform2D txBefore = OffscreenViewportRenderer.ComputeManufacturingSheetTransform(sheet, beforeOpts);
            double mmX = (pxX - txBefore.OriginPxX) / txBefore.EffectiveScalePxPerMm;
            double mmY = (pxY - txBefore.OriginPxY) / txBefore.EffectiveScalePxPerMm;

            double nextZoom = Math.Clamp(_sheetZoom * factor, 0.35, 10.0);
            if (Math.Abs(nextZoom - _sheetZoom) > 1e-6)
            {
                _sheetZoom = nextZoom;
                RenderManufacturingSheet2DOptions afterOpts = BuildSheetRenderOptions(pixelW, pixelH);
                SheetViewTransform2D txAfter = OffscreenViewportRenderer.ComputeManufacturingSheetTransform(sheet, afterOpts);
                double newPxX = txAfter.OriginPxX + mmX * txAfter.EffectiveScalePxPerMm;
                double newPxY = txAfter.OriginPxY + mmY * txAfter.EffectiveScalePxPerMm;
                _sheetPanPxX += pxX - newPxX;
                _sheetPanPxY += pxY - newPxY;
            }

            RebuildContextualHeader();
            RequestCoalescedRender();
            e.Handled = true;
            return;
        }

        if (_mainVm.Personalization.ActiveSurface is not SurfaceDefinition surf) return;
        SurfaceContour2D? contour = ResolveActiveContour(surf);

        _controller.UpdateLayout(surf, pixelW, pixelH, contour);
        _controller.ZoomAtPoint(surf, factor, pxX, pxY, contour);
        RebuildContextualHeader();
        RequestCoalescedRender();
        e.Handled = true;
    }

    private void UpdateCursorAndReadout()
    {
        bool wide = ActualWidth >= 920.0 || ActualWidth <= 1.0;
        if (IsSheetNestingMode)
        {
            CursorCoordsText.Text = string.Format(
                CultureInfo.InvariantCulture,
                wide ? "X:{0,6:F1}  Y:{1,6:F1} mm" : "X:{0:0.#} Y:{1:0.#}",
                _sheetCursorMmX,
                _sheetCursorMmY);
            ProtectedCursor = _sheetPanning
                ? InputSystemCursor.Create(InputSystemCursorShape.SizeAll)
                : null;
            return;
        }

        CursorCoordsText.Text = string.Format(
            CultureInfo.InvariantCulture,
            wide ? "X:{0,6:F1}  Y:{1,6:F1} mm" : "X:{0:0.#} Y:{1:0.#}",
            _controller.LastPointerMmX,
            _controller.LastPointerMmY);

        Editor2DHandleType active = _controller.IsDragging
            ? _controller.ActiveHandle
            : _controller.HoveredHandle;

        if (_controller.IsDrawingShape)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
            return;
        }

        if (_controller.IsDragging && active == Editor2DHandleType.None)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            return;
        }

        if ((_mainVm?.Personalization.ActiveToolMode ?? Canvas2DToolMode.Select) != Canvas2DToolMode.Select)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
            return;
        }

        double rotDeg = _mainVm?.Personalization.SelectedElement?.RotationDeg ?? 0.0;
        ProtectedCursor = active switch
        {
            Editor2DHandleType.Body => InputSystemCursor.Create(InputSystemCursorShape.SizeAll),
            Editor2DHandleType.Rotate => InputSystemCursor.Create(InputSystemCursorShape.Hand),
            Editor2DHandleType.East or Editor2DHandleType.West
                or Editor2DHandleType.North or Editor2DHandleType.South
                or Editor2DHandleType.NorthWest or Editor2DHandleType.SouthEast
                or Editor2DHandleType.NorthEast or Editor2DHandleType.SouthWest
                => InputSystemCursor.Create(ResolveResizeCursorShape(active, rotDeg)),
            _ => null,
        };
    }

    private static InputSystemCursorShape ResolveResizeCursorShape(Editor2DHandleType handle, double elementRotationDeg)
    {
        double baseDeg = handle switch
        {
            Editor2DHandleType.East => 0.0,
            Editor2DHandleType.SouthEast => 45.0,
            Editor2DHandleType.South => 90.0,
            Editor2DHandleType.SouthWest => 135.0,
            Editor2DHandleType.West => 180.0,
            Editor2DHandleType.NorthWest => 225.0,
            Editor2DHandleType.North => 270.0,
            Editor2DHandleType.NorthEast => 315.0,
            _ => 0.0,
        };

        double angle = ((baseDeg + elementRotationDeg) % 180.0 + 180.0) % 180.0;
        if (angle < 22.5 || angle >= 157.5) return InputSystemCursorShape.SizeWestEast;
        if (angle < 67.5) return InputSystemCursorShape.SizeNorthwestSoutheast;
        if (angle < 112.5) return InputSystemCursorShape.SizeNorthSouth;
        return InputSystemCursorShape.SizeNortheastSouthwest;
    }

    private static bool IsKeyDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private (int WidthPx, int HeightPx) ResolveRenderDimensions()
    {
        double w = CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : ActualWidth;
        double h = CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : ActualHeight - 72;
        return (
            Math.Clamp((int)Math.Round(w), 240, 2200),
            Math.Clamp((int)Math.Round(h), 180, 1600));
    }

    private SurfaceContour2D? ResolveActiveContour(SurfaceDefinition surfaceDef)
    {
        BoxSheetGeometry2D? sheet = _mainVm?.Box.SheetGeometry;
        if (sheet is null) return null;

        if (ReferenceEquals(_cachedContourSheet, sheet) && _cachedContourSurfaceId == surfaceDef.PanelId)
        {
            return _cachedActiveContour;
        }

        string panelKey = surfaceDef.PanelId switch
        {
            SurfaceId.Front => "front",
            SurfaceId.Back => "back",
            SurfaceId.Left => "left",
            SurfaceId.Right => "right",
            SurfaceId.Lid => "lid",
            SurfaceId.Bottom => "bottom",
            _ => surfaceDef.PanelId.ToString().ToLowerInvariant(),
        };

        SurfaceContour2D? resolved = sheet.PanelsById.TryGetValue(panelKey, out PanelGeometry2D? panel)
            ? SurfaceCanvas2DController.MapPanelContourToSurface(panel, surfaceDef)
            : null;
        _cachedContourSheet = sheet;
        _cachedContourSurfaceId = surfaceDef.PanelId;
        _cachedActiveContour = resolved;
        return resolved;
    }

    private void OnInteractiveTransformChanged(object? sender, EventArgs e)
        => RequestCoalescedRender();

    private void RequestCoalescedRender()
    {
        _needsCoalescedRender = true;
        if (_renderQueued)
        {
            return;
        }

        if (DispatcherQueue is null)
        {
            RenderSurface2D();
            return;
        }

        _renderQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _renderQueued = false;
            if (_needsCoalescedRender)
            {
                RenderSurface2D();
            }
        }))
        {
            _renderQueued = false;
            RenderSurface2D();
        }
    }

    private void DisposeCachedSurfaces()
    {
        _editorSurface?.Dispose();
        _editorSurface = null;
        _editorSurfaceW = 0;
        _editorSurfaceH = 0;
        _controller.Dispose();
    }

    public void RenderSurface2D()
    {
        _needsCoalescedRender = false;
        if (_mainVm is null) return;
        if (!_mainVm.InDesigner || _mainVm.CenterViewMode == CenterViewMode.Mode3D)
        {
            return;
        }

        (int pixelW, int pixelH) = ResolveRenderDimensions();
        if (pixelW <= 0 || pixelH <= 0) return;

        bool sheetMode = IsSheetNestingMode;
        _lastSheetModeRendered = sheetMode;

        if (_editorSurface is null || _editorSurfaceW != pixelW || _editorSurfaceH != pixelH)
        {
            _editorSurface?.Dispose();
            var info = new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
            _editorSurface = SKSurface.Create(info);
            _editorSurfaceW = pixelW;
            _editorSurfaceH = pixelH;
        }

        SKSurface? skSurface = _editorSurface;
        if (skSurface is null) return;

        skSurface.Canvas.Save();
        try
        {
            if (sheetMode && _mainVm.Box.SheetGeometry is { } sheet)
            {
                _lastRenderedSelectedPartId = _mainVm.Box.SelectedPartId;
                RenderManufacturingSheet2DOptions sheetOptions = BuildSheetRenderOptions(pixelW, pixelH);
                OffscreenViewportRenderer.RenderManufacturingSheet2DToCanvas(
                    skSurface.Canvas,
                    pixelW,
                    pixelH,
                    sheet,
                    _mainVm.Personalization.Manager.Surfaces,
                    _mainVm.Personalization.Manager.ListElements(),
                    sheetOptions,
                    _controller);

                _canvasBitmap = SkiaBitmapBridge.BlitSurface(skSurface, CanvasImage, _canvasBitmap, pixelW, pixelH);
                return;
            }

            SurfaceDefinition? surfaceDef = _mainVm.Personalization.ActiveSurface;
            if (surfaceDef is null) return;

            SurfaceContour2D? contour = ResolveActiveContour(surfaceDef);
            if (_lastRenderedSurfaceId != surfaceDef.PanelId)
            {
                _lastRenderedSurfaceId = surfaceDef.PanelId;
                _controller.FitToView(surfaceDef, pixelW, pixelH, contour);
                ToolTipService.SetToolTip(BtnFitZoom, "Enquadrar a face ativa (100%)");
            }
            else
            {
                _controller.UpdateLayout(surfaceDef, pixelW, pixelH, contour);
            }

            HashSet<string>? errorIds = _mainVm.Personalization.ValidationReport?.Errors
                .Where(err => !string.IsNullOrEmpty(err.ElementId))
                .Select(err => err.ElementId!)
                .ToHashSet(StringComparer.Ordinal);

            var options = new RenderEditor2DOptions
            {
                WidthPx = pixelW,
                HeightPx = pixelH,
                DarkTheme = _mainVm.Theme.IsDarkTheme,
                DrawForbiddenZones = true,
                DrawSafeArea = true,
                SafetyMarginMm = _mainVm.Personalization.SafetyMarginMm,
                SelectedElementId = _mainVm.Personalization.SelectedElementId,
                SelectedElementIds = _mainVm.Personalization.SelectedElementIds,
                ErrorElementIds = errorIds,
                ShowLiveDimensionBadge = _controller.IsDrawingShape
                    || _controller.IsDragging
                    || _mainVm.Personalization.IsInteractiveTransformActive,
            };

            OffscreenViewportRenderer.RenderEditor2DToCanvas(
                skSurface.Canvas,
                pixelW,
                pixelH,
                _controller,
                surfaceDef,
                _mainVm.Personalization.Manager.ListElements(),
                contour,
                options);

            _canvasBitmap = SkiaBitmapBridge.BlitSurface(skSurface, CanvasImage, _canvasBitmap, pixelW, pixelH);
        }
        finally
        {
            skSurface.Canvas.Restore();
        }
    }
}
