using System.Globalization;
using CaixaMaker.App.Controls.CadPrimitives;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Views.Areas;

/// <summary>
/// Inspetor paramétrico compacto da área de Modelagem (modelo estrutural, medidas externas,
/// material/kerf e opções 3D) projetado para operar sem rolagem em 1360×860.
/// </summary>
public sealed partial class ModelingAreaLeftPanel : UserControl
{
    private BoxWorkspaceViewModel? _vm;
    private bool _syncing;

    private sealed class AdvisoryRowVisual
    {
        public required Border Border { get; init; }
        public required TextBlock TextBlock { get; init; }
    }

    private readonly List<AdvisoryRowVisual> _advisoryRows = new();

    public ModelingAreaLeftPanel()
    {
        InitializeComponent();
        WireInteractiveInputs();
        ActualThemeChanged += (_, _) => SyncFromViewModel(forceThemeRefresh: true);
    }

    private void WireInteractiveInputs()
    {
        CadNumericInput[] inputs =
        [
            ThicknessInput,
            WidthInput,
            DepthInput,
            HeightInput,
            FolgaInput,
            TargetFingerWidthInput,
            FixedFingerCountInput,
            LidAngleInput,
        ];
        foreach (CadNumericInput input in inputs)
        {
            input.InteractionStarted += (_, _) => _vm?.BeginInteractiveUpdate();
            input.InteractionCompleted += (_, _) => _vm?.EndInteractiveUpdate();
        }
    }

    public void BindViewModel(BoxWorkspaceViewModel viewModel)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.GeometryRebuilt -= OnVmGeometryRebuilt;
        }

        _vm = viewModel;
        _vm.PropertyChanged += OnVmChanged;
        _vm.GeometryRebuilt += OnVmGeometryRebuilt;
        SyncFromViewModel(forceThemeRefresh: false);
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BoxWorkspaceViewModel.CameraYawDeg)
            or nameof(BoxWorkspaceViewModel.CameraPitchDeg)
            or nameof(BoxWorkspaceViewModel.CameraZoom)
            or nameof(BoxWorkspaceViewModel.CameraPanX)
            or nameof(BoxWorkspaceViewModel.CameraPanY)
            or nameof(BoxWorkspaceViewModel.ActiveCameraPreset)
            or nameof(BoxWorkspaceViewModel.SelectedPartId))
        {
            return;
        }

        SyncFromViewModel(forceThemeRefresh: false);
    }

    private void OnVmGeometryRebuilt(object? sender, EventArgs e)
        => SyncFromViewModel(forceThemeRefresh: false);

    private void SyncFromViewModel(bool forceThemeRefresh = false)
    {
        if (_vm is null) return;
        _syncing = true;
        try
        {
            bool isLid = _vm.SelectedModelId == BoxWithLidAndHandlesModel.Id;
            ApplySegmentedHighlight(ModelWithLidBtn, isLid);
            ApplySegmentedHighlight(ModelOpenBoxBtn, !isLid);

            ModelIdSummaryText.Text = BoxWorkspaceViewModel.FormatModelSummary(_vm.SelectedModel);

            bool isInternal = _vm.DimensionMode == DimensionReferenceMode.InternalUsable;
            DimensionsSectionPane.Title = isInternal
                ? "DIMENSÕES INTERNAS ÚTEIS (MM)"
                : "DIMENSÕES EXTERNAS (MM)";
            DimensionsSectionPane.BadgeText = isInternal ? "mm útil" : "mm ext";
            ApplySegmentedHighlight(DimensionExternalBtn, !isInternal);
            ApplySegmentedHighlight(DimensionInternalBtn, isInternal);

            double t = _vm.BoxThickness;
            if (isInternal)
            {
                WidthInput.Minimum = Math.Max(1.0, BoxParameters.MinWidth - 2.0 * t);
                WidthInput.Maximum = Math.Max(WidthInput.Minimum, BoxParameters.MaxWidth - 2.0 * t);
                DepthInput.Minimum = Math.Max(1.0, BoxParameters.MinDepth - 2.0 * t);
                DepthInput.Maximum = Math.Max(DepthInput.Minimum, BoxParameters.MaxDepth - 2.0 * t);
                HeightInput.Minimum = Math.Max(1.0, BoxParameters.MinHeight - t);
                HeightInput.Maximum = Math.Max(HeightInput.Minimum, BoxParameters.MaxHeight - t);
            }
            else
            {
                WidthInput.Minimum = BoxParameters.MinWidth;
                WidthInput.Maximum = BoxParameters.MaxWidth;
                DepthInput.Minimum = BoxParameters.MinDepth;
                DepthInput.Maximum = BoxParameters.MaxDepth;
                HeightInput.Minimum = BoxParameters.MinHeight;
                HeightInput.Maximum = BoxParameters.MaxHeight;
            }

            ThicknessInput.Value = t;
            WidthInput.Value = _vm.InputWidth;
            DepthInput.Value = _vm.InputDepth;
            HeightInput.Value = _vm.InputHeight;
            FolgaInput.Value = _vm.Folga;
            LidAngleInput.Value = _vm.LidAngle;

            ExplodedCheckBox.IsChecked = _vm.ExplodedView;
            DimensionsCheckBox.IsChecked = _vm.ShowDimensions;

            Visibility lidVis = _vm.SupportsLidArticulation ? Visibility.Visible : Visibility.Collapsed;
            LidRow.Visibility = lidVis;
            LidQuickRow.Visibility = lidVis;

            ApplySegmentedHighlight(PresetSmallBtn, Approx(_vm.BoxWidth, 120) && Approx(_vm.BoxDepth, 90) && Approx(_vm.BoxHeight, 60));
            ApplySegmentedHighlight(PresetDefaultBtn, Approx(_vm.BoxWidth, 200) && Approx(_vm.BoxDepth, 150) && Approx(_vm.BoxHeight, 100));
            ApplySegmentedHighlight(PresetLargeBtn, Approx(_vm.BoxWidth, 300) && Approx(_vm.BoxDepth, 220) && Approx(_vm.BoxHeight, 140));

            ApplySegmentedHighlight(Mdf3Btn, Approx(_vm.BoxThickness, 3.0));
            ApplySegmentedHighlight(Mdf4Btn, Approx(_vm.BoxThickness, 4.0));
            ApplySegmentedHighlight(Mdf6Btn, Approx(_vm.BoxThickness, 6.0));
            ApplySegmentedHighlight(Mdf9Btn, Approx(_vm.BoxThickness, 9.0));

            ApplySegmentedHighlight(FolgaTightBtn, Approx(_vm.Folga, -0.10));
            ApplySegmentedHighlight(FolgaZeroBtn, Approx(_vm.Folga, 0.00));
            ApplySegmentedHighlight(FolgaLooseBtn, Approx(_vm.Folga, 0.10));

            ApplySegmentedHighlight(FingerJointAutoBtn, _vm.IsFingerJointAutoMode);
            ApplySegmentedHighlight(FingerJointSizeBtn, _vm.IsFingerJointTargetSizeVisible);
            ApplySegmentedHighlight(FingerJointCountBtn, _vm.IsFingerJointFixedCountVisible);
            FingerJointAutoHintText.Visibility = _vm.IsFingerJointAutoMode ? Visibility.Visible : Visibility.Collapsed;
            FingerJointAutoHintText.Text = _vm.FingerJointAutoHintText;
            FingerJointTargetSizeRow.Visibility = _vm.IsFingerJointTargetSizeVisible ? Visibility.Visible : Visibility.Collapsed;
            FingerJointFixedCountRow.Visibility = _vm.IsFingerJointFixedCountVisible ? Visibility.Visible : Visibility.Collapsed;
            TargetFingerWidthInput.Value = _vm.TargetFingerWidthMm;
            FixedFingerCountInput.Value = _vm.FixedFingerCount;

            ApplySegmentedHighlight(Lid0Btn, Approx(_vm.LidAngle, 0.0));
            ApplySegmentedHighlight(Lid45Btn, Approx(_vm.LidAngle, 45.0));
            ApplySegmentedHighlight(Lid90Btn, Approx(_vm.LidAngle, 90.0));

            bool hasError = !_vm.IsParametersValid && !string.IsNullOrWhiteSpace(_vm.ValidationError);
            ValidationErrorBanner.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
            ValidationErrorText.Text = _vm.ValidationError ?? string.Empty;

            RebuildAdvisories(forceThemeRefresh);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RebuildAdvisories(bool forceThemeRefresh)
    {
        if (_vm is null) return;
        if (_vm.Advisories.Count == 0)
        {
            _advisoryRows.Clear();
            AdvisoriesPanel.Children.Clear();
            AdvisoriesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AdvisoriesPanel.Visibility = Visibility.Visible;
        while (_advisoryRows.Count > _vm.Advisories.Count)
        {
            int lastIdx = _advisoryRows.Count - 1;
            _advisoryRows.RemoveAt(lastIdx);
            AdvisoriesPanel.Children.RemoveAt(lastIdx);
        }

        Brush? warnBg = null;
        Brush? warnBorder = null;
        void EnsureWarnBrushes()
        {
            warnBg ??= LookupBrush("CmStatusWarningSoftBrush");
            warnBorder ??= LookupBrush("CmStatusWarningBrush");
        }

        if (forceThemeRefresh)
        {
            EnsureWarnBrushes();
        }

        for (int i = 0; i < _vm.Advisories.Count; i++)
        {
            string msg = _vm.Advisories[i];
            if (i >= _advisoryRows.Count)
            {
                EnsureWarnBrushes();
                var textBlock = new TextBlock
                {
                    Text = msg,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = warnBorder,
                };
                var border = new Border
                {
                    Padding = new Thickness(6, 4, 6, 4),
                    CornerRadius = new CornerRadius(2),
                    Background = warnBg,
                    BorderBrush = warnBorder,
                    BorderThickness = new Thickness(1),
                    Child = textBlock,
                };
                _advisoryRows.Add(new AdvisoryRowVisual { Border = border, TextBlock = textBlock });
                AdvisoriesPanel.Children.Add(border);
                continue;
            }

            AdvisoryRowVisual existing = _advisoryRows[i];
            if (!string.Equals(existing.TextBlock.Text, msg, StringComparison.Ordinal))
            {
                existing.TextBlock.Text = msg;
            }

            if (forceThemeRefresh)
            {
                existing.Border.Background = warnBg;
                existing.Border.BorderBrush = warnBorder;
                existing.TextBlock.Foreground = warnBorder;
            }
        }
    }

    private void ApplySegmentedHighlight(Button button, bool active)
    {
        button.Background = LookupBrush(active ? "CmAccentSelectionBrush" : "CmElevatedSurfaceBrush");
        button.BorderBrush = LookupBrush(active ? "CmAccentPrimaryBrush" : "CmSubtleBorderBrush");
        button.Foreground = LookupBrush(active ? "CmAccentHighlightBrush" : "CmTextPrimaryBrush");
    }

    private Brush LookupBrush(string key)
        => ThemeBrushHelper.ResolveBrush(this, key)!;

    private static bool Approx(double a, double b) => Math.Abs(a - b) <= 0.015;

    private void OnSelectModelWithLidClick(object sender, RoutedEventArgs e)
        => _vm?.LoadModel(BoxWithLidAndHandlesModel.Id, applyDefaults: false, markAsEdit: true);

    private void OnSelectModelOpenBoxClick(object sender, RoutedEventArgs e)
        => _vm?.LoadModel(OpenBoxModel.Id, applyDefaults: false, markAsEdit: true);

    private void OnDimensionModeExternalClick(object sender, RoutedEventArgs e)
        => _vm?.SetDimensionReferenceMode(DimensionReferenceMode.External);

    private void OnDimensionModeInternalClick(object sender, RoutedEventArgs e)
        => _vm?.SetDimensionReferenceMode(DimensionReferenceMode.InternalUsable);

    private void OnPresetSmallClick(object sender, RoutedEventArgs e)
        => _vm?.ApplyCustomPresetDimensions(120, 90, 60);

    private void OnPresetDefaultClick(object sender, RoutedEventArgs e)
        => _vm?.ApplyCustomPresetDimensions(200, 150, 100);

    private void OnPresetLargeClick(object sender, RoutedEventArgs e)
        => _vm?.ApplyCustomPresetDimensions(300, 220, 140);

    private void OnQuickThicknessClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn || btn.Tag is not string raw) return;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm))
        {
            _vm.SetQuickThickness(mm);
        }
    }

    private void OnQuickFolgaClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn || btn.Tag is not string raw) return;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm))
        {
            _vm.SetQuickFolga(mm);
        }
    }

    private void OnQuickLidAngleClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn || btn.Tag is not string raw) return;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg))
        {
            _vm.LidAngle = deg;
        }
    }

    private void OnThicknessChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.BoxThickness = value;
    }

    private void OnWidthChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.InputWidth = value;
    }

    private void OnDepthChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.InputDepth = value;
    }

    private void OnHeightChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.InputHeight = value;
    }

    private void OnFolgaChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.Folga = value;
    }

    private void OnFingerJointModeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn || btn.Tag is not string raw) return;
        if (Enum.TryParse<FingerJointSizingMode>(raw, ignoreCase: true, out FingerJointSizingMode mode))
        {
            _vm.SetFingerJointMode(mode);
        }
    }

    private void OnTargetFingerWidthChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.TargetFingerWidthMm = value;
    }

    private void OnFixedFingerCountChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.FixedFingerCount = Math.Clamp((int)Math.Round(value), BoxParameters.MinFixedFingerCount, BoxParameters.MaxFixedFingerCount);
    }

    private void OnLidAngleChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.LidAngle = value;
    }

    private void OnExplodedChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.ExplodedView = ExplodedCheckBox.IsChecked == true;
    }

    private void OnShowDimensionsChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.ShowDimensions = DimensionsCheckBox.IsChecked == true;
    }

    private void OnResetParametersClick(object sender, RoutedEventArgs e)
        => _vm?.ResetDefaultParameters();
}
