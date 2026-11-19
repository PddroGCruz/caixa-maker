using CaixaMaker.App.Controls.CadPrimitives;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Foundation;

namespace CaixaMaker.App.Views.Nesting;

/// <summary>
/// Tela dedicada do Otimizador de Corte 2D (Nesting True-Shape) com fila de peças,
/// viewport SkiaSharp interativo (zoom centrado no cursor + pan) e exportação 1:1.
/// </summary>
public sealed partial class NestingWorkspaceView : UserControl
{
    private readonly NestingViewportRenderer _renderer = new();
    private MainWindowViewModel? _mainVm;
    private NestingWorkspaceViewModel? _vm;

    private SKBitmap? _renderBitmap;
    private WriteableBitmap? _writeableBitmap;
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _isPanning;
    private Point _lastPanPoint;
    private bool _syncing;

    public NestingWorkspaceView()
    {
        InitializeComponent();
        GlobalRotationCombo.SelectedIndex = 0;
        ActualThemeChanged += (_, _) => RenderViewport();
        Unloaded += (_, _) =>
        {
            _renderBitmap?.Dispose();
            _renderBitmap = null;
        };
    }

    public void BindViewModel(MainWindowViewModel mainViewModel)
    {
        if (_mainVm is not null)
        {
            _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
            _mainVm.Theme.ThemeChanged -= OnThemeChanged;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnNestingVmPropertyChanged;
            _vm.VisualStateChanged -= OnNestingVisualChanged;
        }

        _mainVm = mainViewModel;
        _vm = mainViewModel.Nesting;

        NestingThemeSwitcher.Bind(_mainVm.Theme);
        PartsQueueItemsControl.ItemsSource = _vm.Parts;

        _mainVm.PropertyChanged += OnMainVmPropertyChanged;
        _mainVm.Theme.ThemeChanged += OnThemeChanged;
        _vm.PropertyChanged += OnNestingVmPropertyChanged;
        _vm.VisualStateChanged += OnNestingVisualChanged;

        SyncAllFromViewModel();
    }

    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsInNestingWorkspace)
            or nameof(MainWindowViewModel.NestingOrigin)
            or nameof(MainWindowViewModel.NestingBackButtonLabel))
        {
            SyncAllFromViewModel();
            if (_mainVm?.IsInNestingWorkspace == true)
            {
                ResetCamera();
                RenderViewport();
            }
        }
    }

    private void OnThemeChanged(object? sender, ResolvedAppTheme theme)
        => RenderViewport();

    private void OnNestingVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => SyncAllFromViewModel();

    private void OnNestingVisualChanged(object? sender, EventArgs e)
    {
        SyncAllFromViewModel();
        RenderViewport();
    }

    private void SyncAllFromViewModel()
    {
        if (_mainVm is null || _vm is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            BackFromNestingBtn.Content = _mainVm.NestingBackButtonLabel;

            HeaderUtilBadge.Text = $"Aproveitamento: {_vm.OverallUtilizationLabel}";
            HeaderPlacedBadge.Text = _vm.PlacedSummaryLabel;
            HeaderSheetsBadge.Text = _vm.SheetCountLabel;

            LoadActiveBoxBtn.Visibility = _vm.HasActiveBoxProject ? Visibility.Visible : Visibility.Collapsed;
            ActiveBoxBatchCard.Visibility = _vm.HasActiveBoxProject ? Visibility.Visible : Visibility.Collapsed;
            BoxCopiesValueText.Text = _vm.BoxCopiesCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            BoxCopiesDecBtn.IsEnabled = _vm.BoxCopiesCount > 1;

            KeepSinglePartCheck.IsChecked = _vm.KeepImportedFileAsSinglePart;
            EmptyQueueState.Visibility = _vm.HasParts ? Visibility.Collapsed : Visibility.Visible;

            SheetCounterText.Text = _vm.SheetCounterLabel;
            PrevSheetBtn.IsEnabled = _vm.CanGoPreviousSheet;
            NextSheetBtn.IsEnabled = _vm.CanGoNextSheet;

            ShowBoundaryCheck.IsChecked = _vm.ShowSheetBoundary;
            ShowLabelsCheck.IsChecked = _vm.ShowPartLabels;

            UnplacedWarningBanner.Visibility = _vm.HasUnplacedParts ? Visibility.Visible : Visibility.Collapsed;
            if (_vm.HasUnplacedParts)
            {
                UnplacedWarningText.Text = _vm.UnplacedPartsSummary;
            }

            if (Math.Abs(SheetWidthInput.Value - _vm.SheetWidthMm) > 1e-4) SheetWidthInput.Value = _vm.SheetWidthMm;
            if (Math.Abs(SheetHeightInput.Value - _vm.SheetHeightMm) > 1e-4) SheetHeightInput.Value = _vm.SheetHeightMm;
            if (Math.Abs(MarginInput.Value - _vm.MarginMm) > 1e-4) MarginInput.Value = _vm.MarginMm;
            if (Math.Abs(SpacingInput.Value - _vm.PartSpacingMm) > 1e-4) SpacingInput.Value = _vm.PartSpacingMm;
            AllowHolesCheck.IsChecked = _vm.AllowNestingInHoles;

            int rotIdx = _vm.SelectedGlobalRotationLabel switch
            {
                NestingWorkspaceViewModel.GlobalRotationLocked0 => 1,
                NestingWorkspaceViewModel.GlobalRotationStep180 => 2,
                NestingWorkspaceViewModel.GlobalRotationStep90 => 3,
                NestingWorkspaceViewModel.GlobalRotationStep45 => 4,
                _ => 0,
            };
            if (GlobalRotationCombo.SelectedIndex != rotIdx)
            {
                GlobalRotationCombo.SelectedIndex = rotIdx;
            }

            RunOptimizationBtn.IsEnabled = !_vm.IsOptimizing && _vm.TotalRequestedInstances > 0;
            OptimizationProgressBox.Visibility = _vm.IsOptimizing ? Visibility.Visible : Visibility.Collapsed;
            OptimizationProgressBar.Value = _vm.ProgressPercent;
            OptimizationProgressText.Text = _vm.ProgressStatusText;

            bool canExport = _vm.CanExport;
            ExportSvgBtn.IsEnabled = canExport;
            ExportDxfBtn.IsEnabled = canExport;
            ExportPdfBtn.IsEnabled = canExport;
            ExportZipBtn.IsEnabled = canExport;

            NestingStatusText.Text = _vm.ProgressStatusText;

            NestingMetricsTable.SetRows([
                new CadMetricItem("Aproveitamento", _vm.OverallUtilizationLabel, IsHighlighted: _vm.HasResult),
                new CadMetricItem("Peças Alocadas", _vm.PlacedSummaryLabel, IsHighlighted: false),
                new CadMetricItem("Chapas Usadas", _vm.SheetCountLabel, IsHighlighted: false),
                new CadMetricItem("Tempo de Cálculo", _vm.ElapsedLabel, IsHighlighted: false),
            ]);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RenderViewport()
    {
        if (_vm is null || _mainVm is null || Visibility != Visibility.Visible)
        {
            return;
        }

        double rawW = ViewportHost.ActualWidth;
        double rawH = ViewportHost.ActualHeight;
        if (rawW < 32 || rawH < 32)
        {
            return;
        }

        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        int pixelW = Math.Clamp((int)Math.Round(rawW * scale), 64, 3840);
        int pixelH = Math.Clamp((int)Math.Round(rawH * scale), 64, 2160);

        if (_renderBitmap is null || _renderBitmap.Width != pixelW || _renderBitmap.Height != pixelH)
        {
            _renderBitmap?.Dispose();
            _renderBitmap = new SKBitmap(new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul));
        }

        bool isDark = _mainVm.Theme.ResolvedTheme == ResolvedAppTheme.Dark;
        _renderer.RenderToBitmap(
            _renderBitmap,
            _vm.CurrentSheet,
            _vm.BuildCurrentConfig(),
            isDark,
            _vm.ShowSheetBoundary,
            _vm.ShowUsableMargin,
            _vm.ShowPartLabels,
            _zoom,
            _panX * scale,
            _panY * scale);

        _writeableBitmap = SkiaBitmapBridge.BlitBitmap(_renderBitmap, NestingCanvasImage, _writeableBitmap);
    }

    private void ResetCamera()
    {
        _zoom = 1.0;
        _panX = 0.0;
        _panY = 0.0;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
        => RenderViewport();

    private void OnViewportPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint pt = e.GetCurrentPoint(ViewportHost);
        int delta = pt.Properties.MouseWheelDelta;
        if (delta == 0) return;

        double factor = delta > 0 ? 1.15 : (1.0 / 1.15);
        double oldZoom = _zoom;
        double newZoom = Math.Clamp(oldZoom * factor, 0.35, 16.0);
        double ratio = newZoom / oldZoom;

        double cx = ViewportHost.ActualWidth * 0.5;
        double cy = (ViewportHost.ActualHeight - 24.0) * 0.5;
        double dx = pt.Position.X - cx;
        double dy = pt.Position.Y - cy;

        _panX = dx - ((dx - _panX) * ratio);
        _panY = dy - ((dy - _panY) * ratio);
        _zoom = newZoom;

        e.Handled = true;
        RenderViewport();
    }

    private void OnViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint pt = e.GetCurrentPoint(ViewportHost);
        if (pt.Properties.IsLeftButtonPressed || pt.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPanPoint = pt.Position;
            ViewportHost.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        PointerPoint pt = e.GetCurrentPoint(ViewportHost);
        _panX += pt.Position.X - _lastPanPoint.X;
        _panY += pt.Position.Y - _lastPanPoint.Y;
        _lastPanPoint = pt.Position;
        e.Handled = true;
        RenderViewport();
    }

    private void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        ViewportHost.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnReturnFromNestingClick(object sender, RoutedEventArgs e)
        => _mainVm?.ReturnFromNesting();

    private async void OnImportExternalFilesClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.PickAndImportExternalFilesAsync();
        }
    }

    private void OnClearQueueClick(object sender, RoutedEventArgs e)
        => _vm?.ClearParts();

    private void OnLoadActiveBoxClick(object sender, RoutedEventArgs e)
        => _mainVm?.LoadActiveBoxIntoNesting();

    private void OnBoxCopiesDecClick(object sender, RoutedEventArgs e)
        => _vm?.DecrementBoxCopies();

    private void OnBoxCopiesIncClick(object sender, RoutedEventArgs e)
        => _vm?.IncrementBoxCopies();

    private void OnKeepSinglePartChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.KeepImportedFileAsSinglePart = KeepSinglePartCheck.IsChecked == true;
    }

    private void OnPrevSheetClick(object sender, RoutedEventArgs e)
        => _vm?.PreviousSheet();

    private void OnNextSheetClick(object sender, RoutedEventArgs e)
        => _vm?.NextSheet();

    private void OnFitToScreenClick(object sender, RoutedEventArgs e)
    {
        ResetCamera();
        RenderViewport();
    }

    private void OnVisualToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.ShowSheetBoundary = ShowBoundaryCheck.IsChecked == true;
        _vm.ShowPartLabels = ShowLabelsCheck.IsChecked == true;
    }

    private void OnSheetPresetClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: string key }) return;
        _vm.ApplySheetPreset(key);
    }

    private void OnSheetDimensionsChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.SheetWidthMm = SheetWidthInput.Value;
        _vm.SheetHeightMm = SheetHeightInput.Value;
        _vm.MarginMm = MarginInput.Value;
        _vm.PartSpacingMm = SpacingInput.Value;
    }

    private void OnGlobalRotationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.SelectedGlobalRotationLabel = GlobalRotationCombo.SelectedIndex switch
        {
            1 => NestingWorkspaceViewModel.GlobalRotationLocked0,
            2 => NestingWorkspaceViewModel.GlobalRotationStep180,
            3 => NestingWorkspaceViewModel.GlobalRotationStep90,
            4 => NestingWorkspaceViewModel.GlobalRotationStep45,
            _ => NestingWorkspaceViewModel.GlobalRotationPerPart,
        };
    }

    private void OnAllowHolesChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.AllowNestingInHoles = AllowHolesCheck.IsChecked == true;
    }

    private async void OnRunOptimizationClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.RunOptimizationAsync();
        }
    }

    private void OnCancelOptimizationClick(object sender, RoutedEventArgs e)
        => _vm?.CancelOptimization();

    private async void OnExportSvgClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.PickAndExportSvgAsync();
        }
    }

    private async void OnExportDxfClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.PickAndExportDxfAsync();
        }
    }

    private async void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.PickAndExportPdfAsync();
        }
    }

    private async void OnExportZipClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.PickAndExportZipAsync();
        }
    }
}
