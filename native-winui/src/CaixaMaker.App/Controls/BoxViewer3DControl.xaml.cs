using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.System;
using Windows.UI.Core;

namespace CaixaMaker.App.Controls;

/// <summary>
/// Viewport 3D interativo da caixa parametrizada com chapas extrudadas com furos reais (Finger Joint),
/// articulação da tampa no pivô traseiro (0°–110°), vista explodida, projeção 3D de gravações CNC,
/// picking 3D de faces/peças, gizmo XYZ sincronizado e cotas tridimensionais.
/// </summary>
public sealed partial class BoxViewer3DControl : UserControl
{
    private const int GizmoSizePx = 54;

    private enum DragInteractionMode
    {
        None,
        OrbitOrPick,
        Pan,
    }

    private static readonly InputSystemCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private static readonly InputSystemCursor PanCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    private readonly Camera3DController _camera = new();

    private MainWindowViewModel? _mainVm;
    private BoxScene3D? _cachedScene;
    private int _sceneInvalidationVersion = 1;
    private int _builtSceneVersion;
    private BoxSheetGeometry2D? _builtSheetGeometry;
    private double _builtLidAngle = double.NaN;
    private bool _builtExplodedView;
    private bool _overlaysDirty;
    private bool _renderQueued;
    private bool _needsCoalescedRender;
    private bool _isInteracting;
    private long _lastInteractive3DRenderTimestamp;
    private static readonly long Interactive3DMinIntervalTicks =
        (long)(Stopwatch.Frequency * 0.033);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _interactive3DTrailTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleSsaaTimer;
    private SKSurface? _sceneSurface;
    private int _sceneSurfaceW;
    private int _sceneSurfaceH;
    private SKSurface? _gizmoSurface;
    private bool _syncingHud;

    private DragInteractionMode _dragMode;
    private double _lastPointerX;
    private double _lastPointerY;
    private double _movedDistancePx;
    private bool _hasManualOrbit;

    private BoxDimensionAxis? _hoveredDimensionAxis;
    private BoxDimensionAxis? _editingDimensionAxis;
    private bool _closingInlineEditor;
    private bool _applyingInlineEdit;

    private WriteableBitmap? _sceneBitmap;
    private WriteableBitmap? _gizmoBitmap;

    public BoxViewer3DControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            UpdateResponsiveHud();
            RenderScene();
            RepositionOpenInlineEditorIfNeeded();
        };
        Loaded += (_, _) => RenderScene();
        Unloaded += (_, _) => DisposeCachedSurfaces();
    }

    public void BindViewModel(MainWindowViewModel viewModel)
    {
        if (_mainVm is not null)
        {
            _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
            _mainVm.Box.GeometryRebuilt -= OnGeometryOrStateChanged;
            _mainVm.Box.ViewerStateChanged -= OnViewerStateChanged;
            _mainVm.Personalization.VisualStateChanged -= OnPersonalizationVisualStateChanged;
            _mainVm.Personalization.InteractiveTransformChanged -= OnPersonalizationInteractiveTransform;
            _mainVm.Theme.ThemeChanged -= OnThemeChanged;
        }

        _mainVm = viewModel;
        _mainVm.PropertyChanged += OnMainVmPropertyChanged;
        _mainVm.Box.GeometryRebuilt += OnGeometryOrStateChanged;
        _mainVm.Box.ViewerStateChanged += OnViewerStateChanged;
        _mainVm.Personalization.VisualStateChanged += OnPersonalizationVisualStateChanged;
        _mainVm.Personalization.InteractiveTransformChanged += OnPersonalizationInteractiveTransform;
        _mainVm.Theme.ThemeChanged += OnThemeChanged;

        _camera.SyncFromViewModel(_mainVm.Box);
        _sceneInvalidationVersion++;
        _builtSceneVersion = 0;
        _overlaysDirty = true;
        SyncHudFromViewModel();
        RenderScene();
    }

    private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ActiveArea)
            or nameof(MainWindowViewModel.CenterViewMode)
            or nameof(MainWindowViewModel.IsPersonalizationArea)
            or nameof(MainWindowViewModel.IsFabricationArea)
            or nameof(MainWindowViewModel.SplitOrientation)
            or nameof(MainWindowViewModel.InDesigner))
        {
            if (_mainVm is not null && (!_mainVm.InDesigner || _mainVm.CenterViewMode == CenterViewMode.Mode2D))
            {
                CloseInlineDimensionEditor();
            }
            SyncHudFromViewModel();
            RenderScene();
        }
    }

    private void OnGeometryOrStateChanged(object? sender, EventArgs e)
    {
        _sceneInvalidationVersion++;
        _overlaysDirty = true;
        SyncHudFromViewModel();
        if (_isInteracting || _mainVm?.Box.IsInteractiveUpdate == true)
        {
            NotifyInteractionStarted();
            RequestCoalescedRender();
        }
        else
        {
            RenderScene();
        }

        if (!_applyingInlineEdit && _editingDimensionAxis is not null)
        {
            if (_mainVm is null || !_mainVm.Box.ShowDimensions)
            {
                CloseInlineDimensionEditor();
            }
            else
            {
                RepositionOpenInlineEditorIfNeeded();
            }
        }
    }

    private void OnPersonalizationVisualStateChanged(object? sender, EventArgs e)
    {
        // Apenas overlays de gravação foram modificados (não re-triangula chapas MDF)
        _overlaysDirty = true;
        _interactive3DTrailTimer?.Stop();
        _idleSsaaTimer?.Stop();
        _isInteracting = false;
        RenderScene();
    }

    private void OnPersonalizationInteractiveTransform(object? sender, EventArgs e)
    {
        _overlaysDirty = true;
        if (_mainVm is null || !_mainVm.InDesigner || _mainVm.CenterViewMode == CenterViewMode.Mode2D)
        {
            return;
        }

        // Mantém Modo Split 3D + 2D em 60+ FPS: atualiza o 3D a ~30 Hz em 1.0x sem travar o ponteiro 2D
        NotifyInteractionStarted();

        long nowTicks = Stopwatch.GetTimestamp();
        if (_lastInteractive3DRenderTimestamp == 0 ||
            (nowTicks - _lastInteractive3DRenderTimestamp) >= Interactive3DMinIntervalTicks)
        {
            _lastInteractive3DRenderTimestamp = nowTicks;
            _interactive3DTrailTimer?.Stop();
            RequestCoalescedRender();
            return;
        }

        if (_interactive3DTrailTimer is null && DispatcherQueue is not null)
        {
            _interactive3DTrailTimer = DispatcherQueue.CreateTimer();
            _interactive3DTrailTimer.Interval = TimeSpan.FromMilliseconds(36);
            _interactive3DTrailTimer.IsRepeating = false;
            _interactive3DTrailTimer.Tick += (_, _) =>
            {
                _lastInteractive3DRenderTimestamp = Stopwatch.GetTimestamp();
                RequestCoalescedRender();
            };
        }

        if (_interactive3DTrailTimer is not null && !_interactive3DTrailTimer.IsRunning)
        {
            _interactive3DTrailTimer.Start();
        }
    }

    private void OnViewerStateChanged(object? sender, EventArgs e)
    {
        if (_mainVm is not null)
        {
            _camera.SyncFromViewModel(_mainVm.Box);
            _hasManualOrbit = false;
        }

        SyncHudFromViewModel();

        if (_mainVm is null || !_mainVm.Box.ShowDimensions)
        {
            _hoveredDimensionAxis = null;
            CloseInlineDimensionEditor();
        }
        else if (_editingDimensionAxis is not null)
        {
            RepositionOpenInlineEditorIfNeeded();
        }

        if (_isInteracting || _mainVm?.Box.IsInteractiveUpdate == true)
        {
            NotifyInteractionStarted();
            RequestCoalescedRender();
        }
        else
        {
            RenderScene();
        }
    }

    private void OnThemeChanged(object? sender, ResolvedAppTheme e) => RenderScene();

    private void SyncHudFromViewModel()
    {
        if (_mainVm is null) return;

        _syncingHud = true;
        try
        {
            BoxWorkspaceViewModel box = _mainVm.Box;
            bool isPersonalization = _mainVm.IsPersonalizationArea;
            bool showViewModesHud = isPersonalization || _mainVm.IsFabricationArea;
            PersonalizationViewModesPanel.Visibility = showViewModesHud ? Visibility.Visible : Visibility.Collapsed;
            BtnViewMode2D.Content = _mainVm.IsFabricationArea ? "Plano 2D" : "Editor 2D";

            if (isPersonalization)
            {
                ActiveFaceDot.Visibility = Visibility.Visible;
                string activeFaceName = _mainVm.Personalization.ActiveSurface?.DisplayName
                    ?? UiConvert.SurfaceShortName(_mainVm.Personalization.ActiveSurfaceId);
                HudPrimaryPillText.Text = $"FACE ATIVA: {activeFaceName}";
            }
            else
            {
                ActiveFaceDot.Visibility = Visibility.Collapsed;
                string partLabel = string.IsNullOrWhiteSpace(box.SelectedPartId)
                    ? "Todas"
                    : box.Parts.FirstOrDefault(p => string.Equals(p.Id, box.SelectedPartId, StringComparison.OrdinalIgnoreCase))?.Name
                        ?? box.SelectedPartId;
                HudPrimaryPillText.Text = $"PEÇA: {partLabel} • ESCALA 1:1";
            }

            ExplodeToggle.IsChecked = box.ExplodedView;
            DimensionsToggle.IsChecked = box.ShowDimensions;
            LidHudPanel.Visibility = box.SupportsLidArticulation ? Visibility.Visible : Visibility.Collapsed;
            LidAngleSlider.Value = box.LidAngle;
            int roundedAngle = (int)Math.Round(box.LidAngle);
            LidToggleButton.Content = string.Format(CultureInfo.InvariantCulture, "Tampa: {0}°", roundedAngle);

            Style activeStyle = (Style)Application.Current.Resources["PrimaryAccentButtonStyle"];
            Style idleStyle = (Style)Application.Current.Resources["CompactToolbarButtonStyle"];

            BtnLid0.Style = roundedAngle <= 0 ? activeStyle : idleStyle;
            BtnLid90.Style = roundedAngle == 90 ? activeStyle : idleStyle;

            CameraPreset activePreset = box.ActiveCameraPreset;
            BtnIso.Style = !_hasManualOrbit && activePreset == CameraPreset.Isometric ? activeStyle : idleStyle;
            BtnFront.Style = !_hasManualOrbit && activePreset == CameraPreset.Front ? activeStyle : idleStyle;
            BtnBack.Style = !_hasManualOrbit && activePreset == CameraPreset.Back ? activeStyle : idleStyle;
            BtnLeft.Style = !_hasManualOrbit && activePreset == CameraPreset.Left ? activeStyle : idleStyle;
            BtnRight.Style = !_hasManualOrbit && activePreset == CameraPreset.Right ? activeStyle : idleStyle;
            BtnTop.Style = !_hasManualOrbit && activePreset == CameraPreset.Top ? activeStyle : idleStyle;

            BtnViewMode3D.Style = _mainVm.CenterViewMode == CenterViewMode.Mode3D ? activeStyle : idleStyle;
            BtnViewModeSplit.Style = _mainVm.CenterViewMode == CenterViewMode.Split ? activeStyle : idleStyle;
            BtnViewMode2D.Style = _mainVm.CenterViewMode == CenterViewMode.Mode2D ? activeStyle : idleStyle;
            BtnSplitVertical.Style = _mainVm.IsSplitVertical ? activeStyle : idleStyle;
            BtnSplitHorizontal.Style = _mainVm.IsSplitHorizontal ? activeStyle : idleStyle;
            UpdateResponsiveHud();
        }
        finally
        {
            _syncingHud = false;
        }
    }

    private void UpdateResponsiveHud()
    {
        double w = ActualWidth > 0 ? ActualWidth : CanvasHost.ActualWidth;
        if (w <= 1.0)
        {
            return;
        }

        bool narrow = w < 520.0;
        bool veryNarrow = w < 410.0;

        HudPrimaryPill.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        LidAngleSlider.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        BtnResetCamera.Visibility = veryNarrow ? Visibility.Collapsed : Visibility.Visible;
        ExplodeToggle.Content = narrow ? "Explodir" : "Peças separadas";
        DimensionsToggle.Content = narrow ? "Cotas" : "Cotas 3D";
        BtnResetCamera.Content = narrow ? "↻" : "Resetar";

        BtnViewMode3D.Content = veryNarrow ? "3D" : "Vista 3D";
        BtnViewModeSplit.Content = "3D + 2D";
        if (_mainVm is not null)
        {
            BtnViewMode2D.Content = veryNarrow
                ? "2D"
                : (_mainVm.IsFabricationArea ? "Plano 2D" : "Editor 2D");
        }
        BtnSplitVertical.Content = narrow ? "⬒" : "⬒ Vert";
        BtnSplitHorizontal.Content = narrow ? "◫" : "◫ Lado";
    }

    private void OnCameraPresetClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || sender is not FrameworkElement { Tag: string tag }) return;
        if (Enum.TryParse<CameraPreset>(tag, out CameraPreset preset))
        {
            _hasManualOrbit = false;
            _mainVm.Box.SetCameraPreset(preset);
        }
    }

    private void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || sender is not FrameworkElement { Tag: string tag }) return;
        if (Enum.TryParse<CenterViewMode>(tag, out CenterViewMode mode))
        {
            _mainVm.SetCenterViewMode(mode);
        }
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

    private void OnResetCameraClick(object sender, RoutedEventArgs e)
    {
        _hasManualOrbit = false;
        _mainVm?.Box.ResetCamera();
    }

    private void OnLidToggleClick(object sender, RoutedEventArgs e)
        => _mainVm?.Box.ToggleLidOpenClose();

    private void OnLidPresetClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || sender is not FrameworkElement { Tag: string raw }) return;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg))
        {
            _mainVm.Box.LidAngle = deg;
        }
    }

    private void OnExplodeToggleClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || _syncingHud) return;
        _mainVm.Box.ExplodedView = ExplodeToggle.IsChecked == true;
    }

    private void OnDimensionsToggleClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || _syncingHud) return;
        bool show = DimensionsToggle.IsChecked == true;
        if (!show)
        {
            _hoveredDimensionAxis = null;
            CloseInlineDimensionEditor();
        }
        _mainVm.Box.ShowDimensions = show;
    }

    private void OnLidSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_mainVm is null || _syncingHud) return;
        NotifyInteractionStarted();
        _mainVm.Box.LidAngle = e.NewValue;
    }

    private void OnViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mainVm is null) return;
        PointerPoint pt = e.GetCurrentPoint(CanvasHost);
        bool shiftHeld = IsKeyDown(VirtualKey.Shift);

        if (pt.Properties.IsRightButtonPressed ||
            pt.Properties.IsMiddleButtonPressed ||
            (pt.Properties.IsLeftButtonPressed && shiftHeld))
        {
            _dragMode = DragInteractionMode.Pan;
            CloseInlineDimensionEditor();
        }
        else if (pt.Properties.IsLeftButtonPressed)
        {
            _dragMode = DragInteractionMode.OrbitOrPick;
        }
        else
        {
            return;
        }

        _camera.SyncFromViewModel(_mainVm.Box);
        _lastPointerX = pt.Position.X;
        _lastPointerY = pt.Position.Y;
        _movedDistancePx = 0.0;
        CanvasHost.CapturePointer(e.Pointer);
        UpdateDragCursor();
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_mainVm is null) return;

        PointerPoint pt = e.GetCurrentPoint(CanvasHost);
        if (_dragMode == DragInteractionMode.None)
        {
            UpdateDimensionHover(pt.Position.X, pt.Position.Y);
            return;
        }

        double dx = pt.Position.X - _lastPointerX;
        double dy = pt.Position.Y - _lastPointerY;
        _lastPointerX = pt.Position.X;
        _lastPointerY = pt.Position.Y;
        _movedDistancePx += Math.Sqrt(dx * dx + dy * dy);

        if (_dragMode == DragInteractionMode.Pan)
        {
            NotifyInteractionStarted();
            CloseInlineDimensionEditor();
            double sceneScale = Math.Max(100.0, Math.Max(_mainVm.Box.BoxWidth, _mainVm.Box.BoxDepth));
            _camera.Pan(dx, dy, sceneScale);
            _camera.SyncToViewModel(_mainVm.Box);
            _hasManualOrbit = true;
            RequestCoalescedRender();
        }
        else if (_movedDistancePx > 3.5)
        {
            NotifyInteractionStarted();
            CloseInlineDimensionEditor();
            _camera.Orbit(dx, dy);
            _camera.SyncToViewModel(_mainVm.Box);
            if (!_hasManualOrbit)
            {
                _hasManualOrbit = true;
                SyncHudFromViewModel();
            }
            RequestCoalescedRender();
        }
    }

    private void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragInteractionMode.None || _mainVm is null) return;

        PointerPoint pt = e.GetCurrentPoint(CanvasHost);
        bool wasClickPick = _dragMode == DragInteractionMode.OrbitOrPick && _movedDistancePx <= 4.0;

        _dragMode = DragInteractionMode.None;
        CanvasHost.ReleasePointerCapture(e.Pointer);

        if (wasClickPick)
        {
            EnsureSceneBuilt();
            if (_cachedScene is not null)
            {
                (int pixelW, int pixelH, double hostW, double hostH) = GetViewportScaleMetrics();
                float px = (float)(pt.Position.X * pixelW / hostW);
                float py = (float)(pt.Position.Y * pixelH / hostH);
                float scale = GetRenderScaleFactor();

                if (_mainVm.Box.ShowDimensions)
                {
                    DimensionCalloutHitResult? dimHit = OffscreenViewportRenderer.HitTestDimensionCallout(
                        _cachedScene,
                        _camera,
                        pixelW,
                        pixelH,
                        px,
                        py,
                        showDimensions: true,
                        scaleFactor: scale);

                    if (dimHit is not null)
                    {
                        _hoveredDimensionAxis = dimHit.Value.Axis;
                        UpdateDragCursor();
                        OpenInlineDimensionEditor(dimHit.Value);
                        e.Handled = true;
                        return;
                    }
                }

                if (_editingDimensionAxis is not null)
                {
                    CloseInlineDimensionEditor();
                    UpdateDimensionHover(pt.Position.X, pt.Position.Y);
                    e.Handled = true;
                    return;
                }

                ExtrudedPanel3D? picked = _camera.PickPanel(_cachedScene, px, py, pixelW, pixelH);
                if (_mainVm.IsPersonalizationArea)
                {
                    if (picked is not null)
                    {
                        _mainVm.Personalization.SelectSurface(picked.SurfaceId);
                    }
                }
                else
                {
                    if (picked is not null)
                    {
                        _mainVm.Box.SelectPart(picked.PartId);
                    }
                    else if (!string.IsNullOrEmpty(_mainVm.Box.SelectedPartId))
                    {
                        _mainVm.Box.SelectPart(null);
                    }
                }
            }

            UpdateDimensionHover(pt.Position.X, pt.Position.Y);
        }
        else
        {
            if (_needsCoalescedRender)
            {
                RenderScene();
            }
            UpdateDimensionHover(pt.Position.X, pt.Position.Y);
            if (_mainVm.InDesigner)
            {
                _mainVm.PersistDraftIfEnabled();
            }
        }

        e.Handled = true;
    }

    private void OnViewportPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragInteractionMode.None) return;

        if (_hoveredDimensionAxis is not null)
        {
            _hoveredDimensionAxis = null;
            UpdateDragCursor();
            RenderScene();
        }
        else
        {
            UpdateDragCursor();
        }
    }

    private void OnViewportPointerLost(object sender, PointerRoutedEventArgs e)
    {
        _dragMode = DragInteractionMode.None;
        _hoveredDimensionAxis = null;
        UpdateDragCursor();
    }

    private void OnViewportPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_mainVm is null) return;
        NotifyInteractionStarted();
        CloseInlineDimensionEditor();
        int delta = e.GetCurrentPoint(CanvasHost).Properties.MouseWheelDelta;
        double factor = delta > 0 ? 1.12 : (1.0 / 1.12);
        _camera.SyncFromViewModel(_mainVm.Box);
        _camera.ZoomBy(factor);
        _camera.SyncToViewModel(_mainVm.Box);
        RequestCoalescedRender();
        e.Handled = true;
    }

    private void UpdateDragCursor()
    {
        ProtectedCursor = _dragMode switch
        {
            DragInteractionMode.Pan => PanCursor,
            DragInteractionMode.OrbitOrPick => HandCursor,
            _ => _hoveredDimensionAxis is not null ? HandCursor : null,
        };
    }

    private (int PixelW, int PixelH, double HostW, double HostH) GetViewportScaleMetrics()
    {
        (int pixelW, int pixelH) = ResolveRenderDimensions();
        double hostW = Math.Max(1.0, CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : ActualWidth);
        double hostH = Math.Max(1.0, CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : ActualHeight);
        return (pixelW, pixelH, hostW, hostH);
    }

    private void UpdateDimensionHover(double pointerDipX, double pointerDipY)
    {
        if (_mainVm is null || !_mainVm.Box.ShowDimensions)
        {
            if (_hoveredDimensionAxis is not null)
            {
                _hoveredDimensionAxis = null;
                UpdateDragCursor();
                RenderScene();
            }
            return;
        }

        EnsureSceneBuilt();
        if (_cachedScene is null) return;

        (int pixelW, int pixelH, double hostW, double hostH) = GetViewportScaleMetrics();
        float px = (float)(pointerDipX * pixelW / hostW);
        float py = (float)(pointerDipY * pixelH / hostH);

        DimensionCalloutHitResult? hit = OffscreenViewportRenderer.HitTestDimensionCallout(
            _cachedScene,
            _camera,
            pixelW,
            pixelH,
            px,
            py,
            showDimensions: true,
            scaleFactor: GetRenderScaleFactor());

        BoxDimensionAxis? nextHover = hit?.Axis;
        if (nextHover != _hoveredDimensionAxis)
        {
            _hoveredDimensionAxis = nextHover;
            UpdateDragCursor();
            RenderScene();
        }
    }

    private void OpenInlineDimensionEditor(DimensionCalloutHitResult hit)
    {
        if (_mainVm is null) return;

        _editingDimensionAxis = hit.Axis;
        InlineDimHeaderText.Text = _mainVm.Box.GetDimensionAxisEditHeader(hit.Axis);
        InlineDimTextBox.Text = _mainVm.Box.FormatDimensionAxisEditValue(hit.Axis);
        InlineDimErrorText.Visibility = Visibility.Collapsed;
        InlineDimensionEditorOverlay.Visibility = Visibility.Visible;

        PositionInlineDimensionOverlay(hit);
        RenderScene();

        InlineDimTextBox.Focus(FocusState.Programmatic);
        InlineDimTextBox.SelectAll();
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_editingDimensionAxis == hit.Axis &&
                InlineDimensionEditorOverlay.Visibility == Visibility.Visible)
            {
                InlineDimTextBox.Focus(FocusState.Programmatic);
                InlineDimTextBox.SelectAll();
            }
        });
    }

    private void OpenInlineDimensionEditorForAxis(BoxDimensionAxis axis)
    {
        if (_mainVm is null || !_mainVm.Box.ShowDimensions)
        {
            CloseInlineDimensionEditor();
            return;
        }

        EnsureSceneBuilt();
        if (_cachedScene is null)
        {
            CloseInlineDimensionEditor();
            return;
        }

        (int pixelW, int pixelH) = ResolveRenderDimensions();
        IReadOnlyList<DimensionCalloutHitResult> bounds = OffscreenViewportRenderer.ProjectDimensionCalloutBounds(
            _cachedScene,
            _camera,
            pixelW,
            pixelH,
            showDimensions: true,
            scaleFactor: GetRenderScaleFactor());

        DimensionCalloutHitResult match = bounds.FirstOrDefault(b => b.Axis == axis);
        if (match.Label is null)
        {
            var fallbackRect = new SKRect(pixelW * 0.5f - 50f, pixelH * 0.5f - 14f, pixelW * 0.5f + 50f, pixelH * 0.5f + 14f);
            match = new DimensionCalloutHitResult(axis, fallbackRect, new System.Numerics.Vector2(pixelW * 0.5f, pixelH * 0.5f), string.Empty);
        }

        OpenInlineDimensionEditor(match);
    }

    private void PositionInlineDimensionOverlay(DimensionCalloutHitResult hit)
    {
        (int pixelW, int pixelH, double hostW, double hostH) = GetViewportScaleMetrics();
        double scaleX = hostW / Math.Max(1, pixelW);
        double scaleY = hostH / Math.Max(1, pixelH);

        double overlayW = InlineDimensionEditorOverlay.ActualWidth > 0 ? InlineDimensionEditorOverlay.ActualWidth : 216.0;
        double overlayH = InlineDimensionEditorOverlay.ActualHeight > 0 ? InlineDimensionEditorOverlay.ActualHeight : 88.0;

        double targetX = hit.CenterScreen.X * scaleX - overlayW * 0.5;
        double targetY = hit.CenterScreen.Y * scaleY - overlayH * 0.5;

        double maxX = Math.Max(8.0, hostW - overlayW - 8.0);
        double maxY = Math.Max(40.0, hostH - overlayH - 48.0);

        InlineDimensionOverlayTransform.X = Math.Clamp(targetX, 8.0, maxX);
        InlineDimensionOverlayTransform.Y = Math.Clamp(targetY, 40.0, maxY);
    }

    private void RepositionOpenInlineEditorIfNeeded()
    {
        if (_editingDimensionAxis is null || _mainVm is null || !_mainVm.Box.ShowDimensions)
        {
            return;
        }

        EnsureSceneBuilt();
        if (_cachedScene is null) return;

        (int pixelW, int pixelH) = ResolveRenderDimensions();
        IReadOnlyList<DimensionCalloutHitResult> bounds = OffscreenViewportRenderer.ProjectDimensionCalloutBounds(
            _cachedScene,
            _camera,
            pixelW,
            pixelH,
            showDimensions: true,
            scaleFactor: GetRenderScaleFactor());

        DimensionCalloutHitResult match = bounds.FirstOrDefault(b => b.Axis == _editingDimensionAxis.Value);
        if (match.Label is not null)
        {
            PositionInlineDimensionOverlay(match);
        }
    }

    private void CloseInlineDimensionEditor()
    {
        if (_closingInlineEditor || _editingDimensionAxis is null) return;

        _closingInlineEditor = true;
        try
        {
            _editingDimensionAxis = null;
            InlineDimensionEditorOverlay.Visibility = Visibility.Collapsed;
            InlineDimErrorText.Visibility = Visibility.Collapsed;
            UpdateDragCursor();
            RenderScene();
        }
        finally
        {
            _closingInlineEditor = false;
        }
    }

    private bool TryCommitInlineDimensionEdit()
    {
        if (_mainVm is null || _editingDimensionAxis is null) return false;

        _applyingInlineEdit = true;
        try
        {
            bool ok = _mainVm.Box.TryApplyDimensionAxisExpression(_editingDimensionAxis.Value, InlineDimTextBox.Text);
            if (!ok)
            {
                InlineDimErrorText.Visibility = Visibility.Visible;
                InlineDimTextBox.Focus(FocusState.Programmatic);
                InlineDimTextBox.SelectAll();
                return false;
            }

            InlineDimErrorText.Visibility = Visibility.Collapsed;
            if (_mainVm.InDesigner)
            {
                _mainVm.PersistDraftIfEnabled();
            }
            return true;
        }
        finally
        {
            _applyingInlineEdit = false;
        }
    }

    private void OnInlineDimPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_mainVm is null || _editingDimensionAxis is null) return;

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (TryCommitInlineDimensionEdit())
            {
                CloseInlineDimensionEditor();
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseInlineDimensionEditor();
        }
        else if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            BoxDimensionAxis currentAxis = _editingDimensionAxis.Value;
            if (TryCommitInlineDimensionEdit())
            {
                bool reverse = IsKeyDown(VirtualKey.Shift);
                BoxDimensionAxis nextAxis = BoxWorkspaceViewModel.GetNextDimensionAxis(currentAxis, reverse);
                OpenInlineDimensionEditorForAxis(nextAxis);
            }
        }
    }

    private void OnInlineDimLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_closingInlineEditor && !_applyingInlineEdit && _editingDimensionAxis is not null)
        {
            CloseInlineDimensionEditor();
        }
    }

    private void OnInlineDimCloseClick(object sender, RoutedEventArgs e)
        => CloseInlineDimensionEditor();

    private void OnInlineDimTextChanged(object sender, TextChangedEventArgs e)
    {
        if (InlineDimErrorText.Visibility == Visibility.Visible)
        {
            InlineDimErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsKeyDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private float GetRenderScaleFactor()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            return 1.0f;
        }
        return (float)Math.Clamp(scale, 1.0, 2.5);
    }

    private (int WidthPx, int HeightPx) ResolveRenderDimensions()
    {
        double w = CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : ActualWidth;
        double h = CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : ActualHeight;
        float scale = GetRenderScaleFactor();
        return (
            Math.Clamp((int)Math.Round(w * scale), 240, 2800),
            Math.Clamp((int)Math.Round(h * scale), 180, 2200));
    }

    private void RequestCoalescedRender()
    {
        _needsCoalescedRender = true;
        if (_renderQueued)
        {
            return;
        }

        if (DispatcherQueue is null)
        {
            RenderScene();
            return;
        }

        _renderQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _renderQueued = false;
            if (_needsCoalescedRender)
            {
                RenderScene();
            }
        }))
        {
            _renderQueued = false;
            RenderScene();
        }
    }

    private void NotifyInteractionStarted()
    {
        _isInteracting = true;
        if (_idleSsaaTimer is null && DispatcherQueue is not null)
        {
            _idleSsaaTimer = DispatcherQueue.CreateTimer();
            _idleSsaaTimer.Interval = TimeSpan.FromMilliseconds(100);
            _idleSsaaTimer.IsRepeating = false;
            _idleSsaaTimer.Tick += (_, _) =>
            {
                _isInteracting = false;
                RenderScene();
            };
        }

        _idleSsaaTimer?.Stop();
        _idleSsaaTimer?.Start();
    }

    private void DisposeCachedSurfaces()
    {
        _interactive3DTrailTimer?.Stop();
        _interactive3DTrailTimer = null;
        _idleSsaaTimer?.Stop();
        _idleSsaaTimer = null;
        _isInteracting = false;

        _sceneSurface?.Dispose();
        _sceneSurface = null;
        _sceneSurfaceW = 0;
        _sceneSurfaceH = 0;

        _gizmoSurface?.Dispose();
        _gizmoSurface = null;
    }

    private void EnsureSceneBuilt()
    {
        if (_mainVm is null) return;

        BoxCalculator? calc = _mainVm.Box.CurrentCalculator;
        IBoxModelDefinition model = _mainVm.Box.SelectedModel;
        BoxSheetGeometry2D? sheet = _mainVm.Box.SheetGeometry;
        if (calc is null || sheet is null)
        {
            return;
        }

        double lidAngle = _mainVm.Box.LidAngle;
        bool exploded = _mainVm.Box.ExplodedView;
        bool structuralInvalid =
            _cachedScene is null ||
            _builtSceneVersion != _sceneInvalidationVersion ||
            !ReferenceEquals(_builtSheetGeometry, sheet) ||
            Math.Abs(_builtLidAngle - lidAngle) > 1e-4 ||
            _builtExplodedView != exploded;

        if (structuralInvalid)
        {
            _cachedScene = BoxScene3DBuilder.BuildScene(
                calc,
                model,
                sheet,
                _mainVm.Personalization.Manager,
                lidAngle,
                exploded);
            _builtSceneVersion = _sceneInvalidationVersion;
            _builtSheetGeometry = sheet;
            _builtLidAngle = lidAngle;
            _builtExplodedView = exploded;
            _overlaysDirty = false;
            return;
        }

        if (_overlaysDirty && _cachedScene is not null)
        {
            _cachedScene = BoxScene3DBuilder.RebuildSurfaceOverlays(
                _cachedScene,
                model,
                _mainVm.Personalization.Manager);
            _overlaysDirty = false;
        }
    }

    public void RenderScene()
    {
        _needsCoalescedRender = false;
        if (_mainVm is null) return;
        if (!_mainVm.InDesigner || _mainVm.CenterViewMode == CenterViewMode.Mode2D)
        {
            return;
        }

        (int pixelW, int pixelH) = ResolveRenderDimensions();
        if (pixelW <= 0 || pixelH <= 0) return;

        EnsureSceneBuilt();
        if (_cachedScene is null) return;

        float scale = GetRenderScaleFactor();
        bool dark = _mainVm.Theme.IsDarkTheme;
        HashSet<string>? errorIds = _mainVm.Personalization.ValidationReport?.Errors
            .Where(err => !string.IsNullOrEmpty(err.ElementId))
            .Select(err => err.ElementId!)
            .ToHashSet(StringComparer.Ordinal);

        var options = new RenderScene3DOptions
        {
            WidthPx = pixelW,
            HeightPx = pixelH,
            ScaleFactor = scale,
            DarkTheme = dark,
            ShowDimensions = _mainVm.Box.ShowDimensions,
            ShowSurfaceGuides = _mainVm.IsPersonalizationArea,
            ActiveSurfaceId = _mainVm.Personalization.ActiveSurfaceId,
            SelectedPartId = _mainVm.Box.SelectedPartId,
            SelectedElementId = _mainVm.Personalization.SelectedElementId,
            ErrorElementIds = errorIds,
            SafetyMarginMm = _mainVm.Personalization.SafetyMarginMm,
            HighlightedDimensionAxis = _editingDimensionAxis ?? _hoveredDimensionAxis,
            SupersampleScale = _isInteracting ? 1.0f : 2.0f,
        };

        if (_sceneSurface is null || _sceneSurfaceW != pixelW || _sceneSurfaceH != pixelH)
        {
            _sceneSurface?.Dispose();
            var info = new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
            _sceneSurface = SKSurface.Create(info);
            _sceneSurfaceW = pixelW;
            _sceneSurfaceH = pixelH;
        }

        if (_sceneSurface is not null)
        {
            _sceneSurface.Canvas.Save();
            try
            {
                OffscreenViewportRenderer.RenderScene3DToCanvas(
                    _sceneSurface.Canvas,
                    pixelW,
                    pixelH,
                    _cachedScene,
                    _camera,
                    _mainVm.Personalization.Manager,
                    options);
            }
            finally
            {
                _sceneSurface.Canvas.Restore();
            }

            _sceneBitmap = SkiaBitmapBridge.BlitSurface(_sceneSurface, RenderImage, _sceneBitmap, pixelW, pixelH);
        }

        if (_gizmoSurface is null)
        {
            var gizmoInfo = new SKImageInfo(GizmoSizePx, GizmoSizePx, SKColorType.Bgra8888, SKAlphaType.Premul);
            _gizmoSurface = SKSurface.Create(gizmoInfo);
        }

        if (_gizmoSurface is not null)
        {
            _gizmoSurface.Canvas.Save();
            try
            {
                OffscreenViewportRenderer.RenderAxisGizmoToCanvas(_gizmoSurface.Canvas, GizmoSizePx, _camera, dark);
            }
            finally
            {
                _gizmoSurface.Canvas.Restore();
            }

            _gizmoBitmap = SkiaBitmapBridge.BlitSurface(_gizmoSurface, AxisGizmoImage, _gizmoBitmap, GizmoSizePx, GizmoSizePx);
        }
    }
}
