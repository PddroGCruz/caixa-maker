using System.Globalization;
using CaixaMaker.App.Controls.CadPrimitives;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.Personalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace CaixaMaker.App.Views.Areas;

/// <summary>
/// Inspetor Property Grid de alta densidade do elemento selecionado na Personalização CNC
/// (identidade, transformação em mm, superfície destino, âncora de resize, camada CAM e SAT).
/// </summary>
public sealed partial class PersonalizationAreaRightPanel : UserControl
{
    private readonly List<ComboOption<SurfaceId>> _surfaceOptions = new();
    private readonly List<ComboOption<AnchorMode>> _anchorOptions = new();
    private readonly List<ComboOption<CncOperationType>> _operationOptions = new();
    private readonly List<ComboOption<TextRenderMode>> _renderModeOptions = new();
    private readonly List<ComboOption<AlignmentReferenceMode>> _alignmentRefOptions = new();
    private readonly DispatcherTimer _textDebounceTimer;

    private PersonalizationWorkspaceViewModel? _vm;
    private MainWindowViewModel? _mainVm;
    private string? _lastSyncedElementId;
    private string? _lastIssueSignature;
    private SurfaceId[] _lastSurfaceIds = Array.Empty<SurfaceId>();
    private string? _pendingTextElementId;
    private bool _pendingTextEdit;
    private bool _syncing;

    public PersonalizationAreaRightPanel()
    {
        InitializeComponent();
        _textDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _textDebounceTimer.Tick += OnTextDebounceTick;
        ActualThemeChanged += (_, _) => SyncFromViewModel();

        CadNumericInput[] transformInputs =
        [
            PosXInput,
            PosYInput,
            WidthInput,
            HeightInput,
            RotationInput,
            ShapeRectWidthInput,
            ShapeRectHeightInput,
            ShapeCornerRadiusInput,
            ShapeDiameterInput,
            ShapeSlotLengthInput,
            ShapeSlotWidthInput,
            ShapePolygonSidesInput,
            ShapeCrosshairSizeInput,
        ];
        foreach (CadNumericInput input in transformInputs)
        {
            input.InteractionStarted += (_, _) => _vm?.BeginInteractiveTransform();
            input.InteractionCompleted += (_, _) => _vm?.EndInteractiveTransform();
        }
    }

    public void BindViewModel(PersonalizationWorkspaceViewModel viewModel)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.VisualStateChanged -= OnVmVisualChanged;
            _vm.InteractiveTransformChanged -= OnVmInteractiveTransformChanged;
        }

        _vm = viewModel;
        _vm.PropertyChanged += OnVmChanged;
        _vm.VisualStateChanged += OnVmVisualChanged;
        _vm.InteractiveTransformChanged += OnVmInteractiveTransformChanged;

        _syncing = true;
        try
        {
            SelectedFontCombo.ItemsSource = _vm.AvailableFontFamilies;

            _anchorOptions.Clear();
            foreach (AnchorMode mode in _vm.AvailableAnchorModes)
            {
                _anchorOptions.Add(new ComboOption<AnchorMode>(mode, UiConvert.AnchorDisplayName(mode)));
            }
            AnchorCombo.ItemsSource = null;
            AnchorCombo.ItemsSource = _anchorOptions;

            _operationOptions.Clear();
            foreach (CncOperationType op in _vm.AvailableOperations)
            {
                _operationOptions.Add(new ComboOption<CncOperationType>(op, UiConvert.OperationDisplayName(op)));
            }
            OperationCombo.ItemsSource = null;
            OperationCombo.ItemsSource = _operationOptions;

            _renderModeOptions.Clear();
            foreach (TextRenderMode mode in _vm.AvailableTextRenderModes)
            {
                _renderModeOptions.Add(new ComboOption<TextRenderMode>(mode, UiConvert.RenderModeDisplayName(mode)));
            }
            SelectedTextRenderModeCombo.ItemsSource = null;
            SelectedTextRenderModeCombo.ItemsSource = _renderModeOptions;

            _alignmentRefOptions.Clear();
            _alignmentRefOptions.Add(new(AlignmentReferenceMode.SurfaceSafeArea, "Chapa (Área Útil)"));
            _alignmentRefOptions.Add(new(AlignmentReferenceMode.FirstSelected, "Primeiro Selecionado (1º)"));
            _alignmentRefOptions.Add(new(AlignmentReferenceMode.LastSelected, "Último Selecionado"));
            _alignmentRefOptions.Add(new(AlignmentReferenceMode.SelectionBounds, "Caixa de Seleção"));
            AlignmentReferenceCombo.ItemsSource = null;
            AlignmentReferenceCombo.ItemsSource = _alignmentRefOptions;
        }
        finally
        {
            _syncing = false;
        }

        SyncFromViewModel();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_vm?.IsInteractiveTransformActive == true)
        {
            return;
        }

        if (e.PropertyName is nameof(PersonalizationWorkspaceViewModel.ValidationErrorCount)
            or nameof(PersonalizationWorkspaceViewModel.ValidationWarningCount)
            or nameof(PersonalizationWorkspaceViewModel.IsValidationValid)
            or nameof(PersonalizationWorkspaceViewModel.ValidationSummaryBadge))
        {
            UpdateFooterSatBanner();
            return;
        }

        if (e.PropertyName is nameof(PersonalizationWorkspaceViewModel.SelectedXMm)
            or nameof(PersonalizationWorkspaceViewModel.SelectedYMm)
            or nameof(PersonalizationWorkspaceViewModel.SelectedWidthMm)
            or nameof(PersonalizationWorkspaceViewModel.SelectedHeightMm)
            or nameof(PersonalizationWorkspaceViewModel.SelectedRotationDeg)
            or nameof(PersonalizationWorkspaceViewModel.SelectedShapeWidthMm)
            or nameof(PersonalizationWorkspaceViewModel.SelectedShapeHeightMm)
            or nameof(PersonalizationWorkspaceViewModel.SelectedShapeDiameterMm)
            or nameof(PersonalizationWorkspaceViewModel.SelectedShapeCornerRadiusMm)
            or nameof(PersonalizationWorkspaceViewModel.SelectedShapeSides)
            or nameof(PersonalizationWorkspaceViewModel.DraftTextContent)
            or nameof(PersonalizationWorkspaceViewModel.DraftFontFamily)
            or nameof(PersonalizationWorkspaceViewModel.DraftFontSizeMm)
            or nameof(PersonalizationWorkspaceViewModel.DraftBold)
            or nameof(PersonalizationWorkspaceViewModel.DraftItalic)
            or nameof(PersonalizationWorkspaceViewModel.DraftRenderMode)
            or nameof(PersonalizationWorkspaceViewModel.DraftHatchSpacingMm))
        {
            return;
        }

        SyncFromViewModel();
    }

    private void OnVmVisualChanged(object? sender, EventArgs e)
        => SyncFromViewModel();

    private void OnVmInteractiveTransformChanged(object? sender, EventArgs e)
        => SyncTransformAndDiagnosticsOnly();

    private void SyncTransformAndDiagnosticsOnly()
    {
        if (_vm?.SelectedElement is not PersonalizationElement el)
        {
            return;
        }

        _syncing = true;
        try
        {
            SyncNumericInputIfChanged(PosXInput, Math.Round(el.XMm, 2));
            SyncNumericInputIfChanged(PosYInput, Math.Round(el.YMm, 2));
            SyncNumericInputIfChanged(WidthInput, Math.Round(el.WidthMm, 2));
            SyncNumericInputIfChanged(HeightInput, Math.Round(el.HeightMm, 2));
            SyncNumericInputIfChanged(RotationInput, Math.Round(el.RotationDeg, 1));
            if (el is ShapePersonalizationElement shapeEl)
            {
                SyncShapeParametersInPlace(shapeEl);
            }
            SyncElementSatDiagnostic(el);
            UpdateFooterSatBanner();
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void SyncNumericInputIfChanged(CadNumericInput input, double target)
    {
        if (Math.Abs(input.Value - target) > 1e-6)
        {
            input.Value = target;
        }
    }

    private void SyncFromViewModel()
    {
        if (_vm is null) return;

        UpdateFooterSatBanner();
        _syncing = true;
        try
        {
            PersonalizationElement? selected = _vm.SelectedElement;
            bool hasSel = selected is not null;

            EmptyStateContainer.Visibility = hasSel ? Visibility.Collapsed : Visibility.Visible;
            InspectorSectionsContainer.Visibility = hasSel ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectionBanner.Visibility = hasSel && _vm.HasMultipleSelection ? Visibility.Visible : Visibility.Collapsed;
            if (hasSel && _vm.HasMultipleSelection)
            {
                MultiSelectionBannerText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} elementos selecionados (1º item = âncora)",
                    _vm.SelectedElementCount);
            }

            if (!hasSel)
            {
                _textDebounceTimer.Stop();
                _pendingTextEdit = false;
                _lastSyncedElementId = null;
                _lastIssueSignature = null;
                SyncEmptySurfaceSummary();
                return;
            }

            PersonalizationElement el = selected!;
            bool elementChanged = !string.Equals(_lastSyncedElementId, el.Id, StringComparison.Ordinal);
            if (elementChanged)
            {
                _textDebounceTimer.Stop();
                _pendingTextEdit = false;
                _pendingTextElementId = null;
                _lastIssueSignature = null;
            }
            _lastSyncedElementId = el.Id;

            SelectedElementSection.BadgeText = el.ElementType switch
            {
                ElementType.Text => "TEXTO",
                ElementType.VectorSvg => "SVG",
                ElementType.RasterImage => "RASTER",
                ElementType.Shape => "FORMA 1:1",
                _ => "CNC",
            };

            if ((elementChanged || SelectedNameBox.FocusState == FocusState.Unfocused) &&
                SelectedNameBox.Text != el.Name)
            {
                SelectedNameBox.Text = el.Name;
            }

            ApplyToggleVisual(ToggleVisibleBtn, el.Visible, "Visível", "Oculto");
            ApplyToggleVisual(ToggleLockBtn, el.Locked, "Travado", "Travar");

            // Lista de superfícies disponíveis no modelo atual (reconstrói somente quando a topologia da caixa muda)
            if (_lastSurfaceIds.Length != _vm.Surfaces.Count ||
                !_vm.Surfaces.Select(s => s.PanelId).SequenceEqual(_lastSurfaceIds))
            {
                _surfaceOptions.Clear();
                foreach (SurfaceDefinition surf in _vm.Surfaces)
                {
                    _surfaceOptions.Add(new ComboOption<SurfaceId>(surf.PanelId, UiConvert.SurfaceShortName(surf.PanelId)));
                }
                _lastSurfaceIds = _vm.Surfaces.Select(s => s.PanelId).ToArray();
                MoveSurfaceCombo.ItemsSource = null;
                MoveSurfaceCombo.ItemsSource = _surfaceOptions;
            }
            MoveSurfaceCombo.SelectedItem = _surfaceOptions.FirstOrDefault(o => o.Value == el.PanelId);
            MoveSurfaceCombo.IsEnabled = !el.Locked && _surfaceOptions.Count > 1;

            SyncNumericInputIfChanged(PosXInput, Math.Round(el.XMm, 2));
            SyncNumericInputIfChanged(PosYInput, Math.Round(el.YMm, 2));
            SyncNumericInputIfChanged(WidthInput, Math.Round(el.WidthMm, 2));
            SyncNumericInputIfChanged(HeightInput, Math.Round(el.HeightMm, 2));
            SyncNumericInputIfChanged(RotationInput, Math.Round(el.RotationDeg, 1));
            PreserveAspectCheckBox.IsChecked = el.PreserveAspectRatio;

            AnchorCombo.SelectedItem = _anchorOptions.FirstOrDefault(o => o.Value == el.Anchor);
            OperationCombo.SelectedItem = _operationOptions.FirstOrDefault(o => o.Value == el.Operation);

            TextPropsPanel.Visibility = el is TextPersonalizationElement ? Visibility.Visible : Visibility.Collapsed;
            RasterPropsPanel.Visibility = el is RasterImagePersonalizationElement ? Visibility.Visible : Visibility.Collapsed;
            ShapeParamsSection.Visibility = el is ShapePersonalizationElement ? Visibility.Visible : Visibility.Collapsed;
            AutoCamStatusStrip.Visibility = (el is TextPersonalizationElement or RasterImagePersonalizationElement or ShapePersonalizationElement)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (el is ShapePersonalizationElement shapeEl)
            {
                SyncShapeParametersInPlace(shapeEl);
            }
            else if (el is TextPersonalizationElement textEl)
            {
                if ((elementChanged || (SelectedTextBox.FocusState == FocusState.Unfocused && !_pendingTextEdit)) &&
                    SelectedTextBox.Text != textEl.Content)
                {
                    SelectedTextBox.Text = textEl.Content;
                }
                if (_vm.AvailableFontFamilies.Contains(textEl.FontFamily))
                {
                    SelectedFontCombo.SelectedItem = textEl.FontFamily;
                }
                SelectedFontSizeInput.Value = textEl.FontSizeMm;
                ApplySegmentedAccent(BoldToggleBtn, textEl.Bold);
                ApplySegmentedAccent(ItalicToggleBtn, textEl.Italic);

                ComboOption<TextRenderMode>? selectedRender = _renderModeOptions.FirstOrDefault(o => o.Value == textEl.RenderMode);
                SelectedTextRenderModeCombo.SelectedItem = selectedRender;
                SelectedHatchInput.Value = textEl.HatchSpacingMm;
                SelectedHatchRow.Visibility = textEl.RenderMode == TextRenderMode.Hatch ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (el is RasterImagePersonalizationElement rasterEl)
            {
                RasterThresholdInput.Value = rasterEl.ProcessingOptions.Threshold;
                RasterInvertCheckBox.IsChecked = rasterEl.ProcessingOptions.Invert;
                RasterMonochromeCheckBox.IsChecked = rasterEl.ProcessingOptions.Monochrome;
                RasterDitheringCheckBox.IsChecked = rasterEl.ProcessingOptions.Dithering;
                RasterVectorizeCheckBox.IsChecked = rasterEl.Vectorize;
            }

            SyncNumericInputIfChanged(ArrayCountInput, _vm.ArrayCount);
            SyncNumericInputIfChanged(ArrayStepXInput, Math.Round(_vm.ArrayStepXMm, 2));
            SyncNumericInputIfChanged(ArrayStepYInput, Math.Round(_vm.ArrayStepYMm, 2));

            AlignmentReferenceCombo.SelectedItem = _alignmentRefOptions.FirstOrDefault(o => o.Value == _vm.SelectedAlignmentReference);
            bool canAlign = _vm.CanAlignSelectedElements;
            AlignLeftBtn.IsEnabled = canAlign;
            AlignCenterXBtn.IsEnabled = canAlign;
            AlignRightBtn.IsEnabled = canAlign;
            AlignTopBtn.IsEnabled = canAlign;
            AlignCenterYBtn.IsEnabled = canAlign;
            AlignBottomBtn.IsEnabled = canAlign;

            bool canDist = _vm.CanDistributeSelectedElements;
            DistributeHorizontalBtn.IsEnabled = canDist;
            DistributeVerticalBtn.IsEnabled = canDist;

            SyncElementSatDiagnostic(el);
            UpdateFooterSatBanner();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncEmptySurfaceSummary()
    {
        if (_vm is null) return;
        SurfaceDefinition? active = _vm.ActiveSurface;
        string shortName = UiConvert.SurfaceShortName(_vm.ActiveSurfaceId);
        ActiveSurfacePane.BadgeText = shortName;

        if (active is not null)
        {
            Rect2D safe = active.SafeAreaForMargin(_vm.SafetyMarginMm);
            ActiveSurfaceSummaryTable.SetMetrics(
            [
                new CadMetricItem("Superfície ativa", active.DisplayName),
                new CadMetricItem("Dimensão da chapa", string.Format(CultureInfo.InvariantCulture, "{0:F1} × {1:F1} mm", active.WidthMm, active.HeightMm)),
                new CadMetricItem("Área segura útil", string.Format(CultureInfo.InvariantCulture, "{0:F1} × {1:F1} mm", safe.WidthMm, safe.HeightMm), IsHighlighted: true),
                new CadMetricItem("Zonas Finger Joint", $"{active.ForbiddenZones.Count} restrita(s)"),
                new CadMetricItem("Elementos na face", $"{_vm.ElementsOnActiveSurface.Count} / {_vm.TotalElementCount} total"),
            ]);
        }

        if (_vm.ValidationErrorCount > 0)
        {
            SurfaceSatStatusBadge.Kind = CadBadgeKind.Error;
            SurfaceSatStatusBadge.Text = $"{_vm.ValidationErrorCount} ERRO(S) SAT";
        }
        else if (_vm.ValidationWarningCount > 0)
        {
            SurfaceSatStatusBadge.Kind = CadBadgeKind.Warning;
            SurfaceSatStatusBadge.Text = $"{_vm.ValidationWarningCount} AVISO(S) SAT";
        }
        else
        {
            SurfaceSatStatusBadge.Kind = CadBadgeKind.Valid;
            SurfaceSatStatusBadge.Text = "FACE VALIDADA 1:1";
        }
    }

    private void SyncElementSatDiagnostic(PersonalizationElement el)
    {
        if (_vm is null) return;

        List<GeometryValidationIssue> elementIssues = _vm.ValidationIssues
            .Where(i => string.Equals(i.ElementId, el.Id, StringComparison.Ordinal))
            .ToList();

        int errors = elementIssues.Count(i => i.Severity == ValidationSeverity.Error);
        int warns = elementIssues.Count(i => i.Severity == ValidationSeverity.Warning);

        if (errors > 0)
        {
            ElementSatBadge.Kind = CadBadgeKind.Error;
            ElementSatBadge.Text = $"COLISÃO SAT ({errors} ERRO)";
        }
        else if (warns > 0)
        {
            ElementSatBadge.Kind = CadBadgeKind.Warning;
            ElementSatBadge.Text = $"AVISO MARGEM ({warns})";
        }
        else
        {
            ElementSatBadge.Kind = CadBadgeKind.Valid;
            ElementSatBadge.Text = "DENTRO DA ÁREA SEGURA";
        }

        double minMargin = 0;
        if (_vm.Manager.Surfaces.TryGetSurface(el.PanelId, out SurfaceDefinition? surface) && surface is not null)
        {
            Rect2D safe = surface.SafeAreaForMargin(_vm.SafetyMarginMm);
            double leftGap = el.XMm - safe.XMm;
            double rightGap = safe.Right - (el.XMm + el.WidthMm);
            double topGap = el.YMm - safe.YMm;
            double bottomGap = safe.Bottom - (el.YMm + el.HeightMm);
            minMargin = Math.Min(Math.Min(leftGap, rightGap), Math.Min(topGap, bottomGap));
        }

        CncLayerSpec layer = CncLayerSpecs.Get(el.Operation);
        ElementSatMetricsTable.SetMetrics(
        [
            new CadMetricItem("Margem até borda útil", string.Format(CultureInfo.InvariantCulture, "{0:F1} mm", minMargin), IsHighlighted: minMargin >= 0),
            new CadMetricItem("Folga de segurança SAT", string.Format(CultureInfo.InvariantCulture, "{0:F1} mm", _vm.SafetyMarginMm)),
            new CadMetricItem("Camada CAM destino", layer.Id),
        ]);

        string issueSignature = string.Join(
            '|',
            elementIssues.Select(i => $"{(int)i.Severity}:{i.Code}:{i.Message}"));
        if (string.Equals(issueSignature, _lastIssueSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastIssueSignature = issueSignature;
        ElementValidationList.Children.Clear();
        if (elementIssues.Count == 0)
        {
            ElementValidationList.Visibility = Visibility.Collapsed;
            return;
        }

        ElementValidationList.Visibility = Visibility.Visible;
        foreach (GeometryValidationIssue issue in elementIssues)
        {
            bool isErr = issue.Severity == ValidationSeverity.Error;
            Brush bg = ThemeBrushHelper.ResolveBrush(this, isErr ? "CmStatusDangerSoftBrush" : "CmStatusWarningSoftBrush")!;
            Brush border = ThemeBrushHelper.ResolveBrush(this, isErr ? "CmStatusDangerBrush" : "CmStatusWarningBrush")!;

            ElementValidationList.Children.Add(new Border
            {
                Padding = new Thickness(7, 4, 7, 4),
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock
                {
                    FontSize = 11,
                    Foreground = border,
                    Text = issue.Message,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }
    }

    private void ApplyToggleVisual(Button button, bool active, string activeLabel, string inactiveLabel)
    {
        button.Content = active ? activeLabel : inactiveLabel;
        ApplySegmentedAccent(button, active);
    }

    private void ApplySegmentedAccent(Button button, bool active)
    {
        button.Background = ThemeBrushHelper.ResolveBrush(this, active ? "CmAccentSelectionBrush" : "CmElevatedSurfaceBrush")!;
        button.BorderBrush = ThemeBrushHelper.ResolveBrush(this, active ? "CmAccentPrimaryBrush" : "CmSubtleBorderBrush")!;
        button.Foreground = ThemeBrushHelper.ResolveBrush(this, active ? "CmAccentHighlightBrush" : "CmTextPrimaryBrush")!;
    }

    private void CommitSelectedNameIfChanged()
    {
        if (_syncing || _vm?.SelectedElement is null) return;
        string trimmed = SelectedNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            _syncing = true;
            try
            {
                SelectedNameBox.Text = _vm.SelectedElement.Name;
            }
            finally
            {
                _syncing = false;
            }
        }
        else if (!string.Equals(trimmed, _vm.SelectedElement.Name, StringComparison.Ordinal))
        {
            _vm.SelectedName = trimmed;
        }
        _vm.FlushHistoryCoalescing();
    }

    private void OnSelectedNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.SelectedName = SelectedNameBox.Text;
    }

    private void OnSelectedNameLostFocus(object sender, RoutedEventArgs e)
        => CommitSelectedNameIfChanged();

    private void OnMoveSurfaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        if (MoveSurfaceCombo.SelectedItem is ComboOption<SurfaceId> opt &&
            _vm.SelectedElement is not null &&
            opt.Value != _vm.SelectedElement.PanelId)
        {
            _vm.MoveSelectedElementToSurface(opt.Value);
        }
    }

    private void OnPosXChanged(object? sender, double value)
    {
        if (_syncing || _vm?.SelectedElement is null) return;
        _vm.UpdateSelectedElementTransform(xMm: value, recordUndo: true, coalesceKey: $"pos-x:{_vm.SelectedElement.Id}");
    }

    private void OnPosYChanged(object? sender, double value)
    {
        if (_syncing || _vm?.SelectedElement is null) return;
        _vm.UpdateSelectedElementTransform(yMm: value, recordUndo: true, coalesceKey: $"pos-y:{_vm.SelectedElement.Id}");
    }

    private void OnWidthChanged(object? sender, double value)
    {
        if (_syncing || _vm?.SelectedElement is null) return;
        _vm.UpdateSelectedElementTransform(widthMm: value, recordUndo: true, coalesceKey: $"size-w:{_vm.SelectedElement.Id}");
    }

    private void OnHeightChanged(object? sender, double value)
    {
        if (_syncing || _vm?.SelectedElement is null) return;
        _vm.UpdateSelectedElementTransform(heightMm: value, recordUndo: true, coalesceKey: $"size-h:{_vm.SelectedElement.Id}");
    }

    private void OnRotationChanged(object? sender, double value)
    {
        if (_syncing || _vm?.SelectedElement is null) return;
        _vm.UpdateSelectedElementTransform(rotationDeg: value, recordUndo: true, coalesceKey: $"rot:{_vm.SelectedElement.Id}");
    }

    private void OnPreserveAspectChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        _vm.UpdateSelectedElementTransform(
            preserveAspectRatio: PreserveAspectCheckBox.IsChecked == true,
            recordUndo: true);
    }

    private void OnAnchorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        if (AnchorCombo.SelectedItem is ComboOption<AnchorMode> opt)
        {
            _vm.SetSelectedAnchor(opt.Value);
        }
    }

    private void OnOperationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        if (OperationCombo.SelectedItem is ComboOption<CncOperationType> opt)
        {
            _vm.SetSelectedOperation(opt.Value);
        }
    }

    private void CommitPendingTextContent(bool flushHistory)
    {
        _textDebounceTimer.Stop();
        bool hadPending = _pendingTextEdit;
        string? targetId = _pendingTextElementId;
        _pendingTextEdit = false;
        _pendingTextElementId = null;

        if (_syncing || _vm is null || !_vm.IsSelectedText) return;
        if (hadPending && targetId is not null && !string.Equals(targetId, _vm.SelectedElement?.Id, StringComparison.Ordinal))
        {
            return;
        }

        _vm.SelectedTextContent = SelectedTextBox.Text;
        if (flushHistory)
        {
            _vm.FlushHistoryCoalescing();
        }
    }

    private void OnSelectedTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _vm is null || !_vm.IsSelectedText) return;
        _pendingTextEdit = true;
        _pendingTextElementId = _vm.SelectedElement?.Id;
        _textDebounceTimer.Stop();
        _textDebounceTimer.Start();
    }

    private void OnTextDebounceTick(object? sender, object e)
        => CommitPendingTextContent(flushHistory: false);

    private void OnSelectedTextLostFocus(object sender, RoutedEventArgs e)
        => CommitPendingTextContent(flushHistory: true);

    private void OnSelectedTextPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitPendingTextContent(flushHistory: true);
        }
    }

    private void OnSelectedFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null || !_vm.IsSelectedText) return;
        if (SelectedFontCombo.SelectedItem is string font)
        {
            _vm.SelectedTextFontFamily = font;
        }
    }

    private void OnSelectedFontSizeChanged(object? sender, double value)
    {
        if (_syncing || _vm is null || !_vm.IsSelectedText) return;
        _vm.SelectedTextFontSizeMm = value;
    }

    private void OnSelectedTextRenderModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        if (SelectedTextRenderModeCombo.SelectedItem is ComboOption<TextRenderMode> opt)
        {
            SelectedHatchRow.Visibility = opt.Value == TextRenderMode.Hatch ? Visibility.Visible : Visibility.Collapsed;
            _vm.SelectedTextRenderMode = opt.Value;
        }
    }

    private void OnSelectedHatchSpacingChanged(object? sender, double value)
    {
        if (_syncing || _vm is null || !_vm.IsSelectedText) return;
        _vm.SelectedTextHatchSpacingMm = value;
    }

    private void OnBoldToggleClick(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null || !_vm.IsSelectedText) return;
        _vm.SelectedTextBold = !_vm.SelectedTextBold;
    }

    private void OnItalicToggleClick(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null || !_vm.IsSelectedText) return;
        _vm.SelectedTextItalic = !_vm.SelectedTextItalic;
    }

    private void OnRasterThresholdChanged(object? sender, double value)
    {
        if (_syncing || _vm is null || !_vm.IsSelectedRaster) return;
        _vm.SelectedRasterThreshold = (int)Math.Clamp(Math.Round(value), 0, 255);
    }

    private void OnRasterOptionToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null || !_vm.IsSelectedRaster) return;
        if (ReferenceEquals(sender, RasterInvertCheckBox))
        {
            _vm.SelectedRasterInvert = RasterInvertCheckBox.IsChecked == true;
        }
        else if (ReferenceEquals(sender, RasterMonochromeCheckBox))
        {
            _vm.SelectedRasterMonochrome = RasterMonochromeCheckBox.IsChecked == true;
        }
        else if (ReferenceEquals(sender, RasterDitheringCheckBox))
        {
            _vm.SelectedRasterDithering = RasterDitheringCheckBox.IsChecked == true;
        }
        else if (ReferenceEquals(sender, RasterVectorizeCheckBox))
        {
            _vm.SelectedRasterVectorize = RasterVectorizeCheckBox.IsChecked == true;
        }
    }

    private void OnApplyChangesClick(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedElement is null) return;

        _textDebounceTimer.Stop();
        _pendingTextEdit = false;
        _vm.FlushHistoryCoalescing();

        _vm.SelectedName = string.IsNullOrWhiteSpace(SelectedNameBox.Text) ? _vm.SelectedElement.Name : SelectedNameBox.Text;
        _vm.SelectedXMm = PosXInput.Value;
        _vm.SelectedYMm = PosYInput.Value;
        _vm.SelectedWidthMm = Math.Max(0.5, WidthInput.Value);
        _vm.SelectedHeightMm = Math.Max(0.5, HeightInput.Value);
        _vm.SelectedRotationDeg = RotationInput.Value;
        _vm.SelectedPreserveAspectRatio = PreserveAspectCheckBox.IsChecked == true;

        if (_vm.IsSelectedText)
        {
            _vm.SelectedTextContent = SelectedTextBox.Text;
            if (SelectedFontCombo.SelectedItem is string font)
            {
                _vm.SelectedTextFontFamily = font;
            }
            _vm.SelectedTextFontSizeMm = SelectedFontSizeInput.Value;
            if (SelectedTextRenderModeCombo.SelectedItem is ComboOption<TextRenderMode> modeOpt)
            {
                _vm.SelectedTextRenderMode = modeOpt.Value;
            }
            _vm.SelectedTextHatchSpacingMm = SelectedHatchInput.Value;
        }
        else if (_vm.IsSelectedRaster)
        {
            _vm.SelectedRasterThreshold = (int)Math.Clamp(RasterThresholdInput.Value, 0, 255);
            _vm.SelectedRasterInvert = RasterInvertCheckBox.IsChecked == true;
            _vm.SelectedRasterMonochrome = RasterMonochromeCheckBox.IsChecked == true;
            _vm.SelectedRasterDithering = RasterDitheringCheckBox.IsChecked == true;
            _vm.SelectedRasterVectorize = RasterVectorizeCheckBox.IsChecked == true;
        }
        else if (_vm.IsShapeSelected && _vm.SelectedElement is ShapePersonalizationElement shapeEl)
        {
            shapeEl.RebuildGeometry(preserveAnchorPosition: true);
        }

        _vm.ApplySelectedInspectorChanges();
    }

    private void SyncShapeParametersInPlace(ShapePersonalizationElement shapeEl)
    {
        GeometricShapeSpec spec = shapeEl.ShapeSpec ?? new GeometricShapeSpec();
        GeometricShapeKind kind = spec.Kind;

        ShapeParamsSection.BadgeText = kind switch
        {
            GeometricShapeKind.Rectangle => "RETÂNGULO",
            GeometricShapeKind.Circle => "CÍRCULO",
            GeometricShapeKind.Slot => "OBLONGO",
            GeometricShapeKind.Polygon => "POLÍGONO",
            GeometricShapeKind.Crosshair => "CRUZ",
            _ => "1:1",
        };

        ApplySegmentedAccent(ShapeOpCutBtn, shapeEl.OperationMode == OperationMode.Cut);
        ApplySegmentedAccent(ShapeOpEngraveBtn, shapeEl.OperationMode == OperationMode.Engrave);
        ApplySegmentedAccent(ShapeOpBothBtn, shapeEl.OperationMode == OperationMode.Both);

        bool isRect = kind == GeometricShapeKind.Rectangle;
        bool isCircle = kind == GeometricShapeKind.Circle;
        bool isSlot = kind == GeometricShapeKind.Slot;
        bool isPoly = kind == GeometricShapeKind.Polygon;
        bool isCross = kind == GeometricShapeKind.Crosshair;

        ShapeRectWidthRow.Visibility = isRect ? Visibility.Visible : Visibility.Collapsed;
        ShapeRectHeightRow.Visibility = isRect ? Visibility.Visible : Visibility.Collapsed;
        ShapeCornerRadiusRow.Visibility = isRect ? Visibility.Visible : Visibility.Collapsed;

        ShapeDiameterRow.Visibility = (isCircle || isPoly) ? Visibility.Visible : Visibility.Collapsed;
        ShapeDiameterRow.ShowBottomDivider = isPoly;

        ShapeSlotLengthRow.Visibility = isSlot ? Visibility.Visible : Visibility.Collapsed;
        ShapeSlotWidthRow.Visibility = isSlot ? Visibility.Visible : Visibility.Collapsed;

        ShapePolygonSidesRow.Visibility = isPoly ? Visibility.Visible : Visibility.Collapsed;
        ShapeCrosshairSizeRow.Visibility = isCross ? Visibility.Visible : Visibility.Collapsed;

        ShapeFillHatchCheckBox.Visibility = !isCross && shapeEl.OperationMode is OperationMode.Engrave or OperationMode.Both
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShapeFillHatchCheckBox.IsChecked = spec.FillHatch;

        SyncNumericInputIfChanged(ShapeRectWidthInput, Math.Round(spec.WidthMm, 2));
        SyncNumericInputIfChanged(ShapeRectHeightInput, Math.Round(spec.HeightMm, 2));
        ShapeCornerRadiusInput.Maximum = Math.Max(0.0, Math.Round(Math.Min(spec.WidthMm, spec.HeightMm) * 0.5, 2));
        SyncNumericInputIfChanged(ShapeCornerRadiusInput, Math.Round(spec.CornerRadiusMm, 2));
        SyncNumericInputIfChanged(ShapeDiameterInput, Math.Round(spec.DiameterMm, 2));
        SyncNumericInputIfChanged(ShapeSlotLengthInput, Math.Round(Math.Max(spec.WidthMm, spec.HeightMm), 2));
        SyncNumericInputIfChanged(ShapeSlotWidthInput, Math.Round(Math.Min(spec.WidthMm, spec.HeightMm), 2));
        SyncNumericInputIfChanged(ShapePolygonSidesInput, spec.Sides);
        SyncNumericInputIfChanged(ShapeCrosshairSizeInput, Math.Round(spec.DiameterMm, 2));
    }

    private void OnShapeOperationModeClick(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null || !_vm.IsShapeSelected || sender is not Button btn || btn.Tag is not string raw)
        {
            return;
        }

        OperationMode mode = raw switch
        {
            "Engrave" => OperationMode.Engrave,
            "Both" => OperationMode.Both,
            _ => OperationMode.Cut,
        };
        _vm.SetSelectedOperationMode(mode);
    }

    private void OnShapeWidthChanged(object? sender, double value)
    {
        if (_syncing || _vm is null || !_vm.IsShapeSelected) return;
        _vm.SelectedShapeWidthMm = value;
    }

    private void OnShapeHeightChanged(object? sender, double value)
    {
        if (_syncing || _vm is null || !_vm.IsShapeSelected) return;
        _vm.SelectedShapeHeightMm = value;
    }

    private void OnShapeCornerRadiusChanged(object? sender, double value)
    {
        if (_syncing || _vm is null || !_vm.IsShapeSelected) return;
        _vm.SelectedShapeCornerRadiusMm = value;
    }

    private void OnShapeDiameterChanged(object? sender, double value)
    {
        if (_syncing || _vm is null || !_vm.IsShapeSelected) return;
        _vm.SelectedShapeDiameterMm = value;
    }

    private void OnShapeSidesChanged(object? sender, double value)
    {
        if (_syncing || _vm is null || !_vm.IsShapeSelected) return;
        _vm.SelectedShapeSides = (int)Math.Round(value);
    }

    private void OnShapeFillHatchToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || _vm is null || !_vm.IsShapeSelected) return;
        _vm.SelectedShapeFillHatch = ShapeFillHatchCheckBox.IsChecked == true;
    }

    private void OnMirrorFourCornersClick(object sender, RoutedEventArgs e)
        => _vm?.MirrorFourCornersSelectedElement();

    private void OnCreateLinearArrayClick(object sender, RoutedEventArgs e)
        => _vm?.CreateLinearArraySelectedElement();

    private void OnArrayCountChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.ArrayCount = Math.Clamp((int)Math.Round(value), 2, 20);
    }

    private void OnArrayStepXChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.ArrayStepXMm = value;
    }

    private void OnArrayStepYChanged(object? sender, double value)
    {
        if (_syncing || _vm is null) return;
        _vm.ArrayStepYMm = value;
    }

    private void OnAlignmentReferenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null) return;
        if (AlignmentReferenceCombo.SelectedItem is ComboOption<AlignmentReferenceMode> opt)
        {
            _vm.SelectedAlignmentReference = opt.Value;
        }
    }

    private void OnAlignActionClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: string raw }) return;
        if (Enum.TryParse<AlignmentAction>(raw, out AlignmentAction action))
        {
            _vm.AlignSelectedElements(action);
        }
    }

    private void OnQuickAlignClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn || btn.Tag is not string raw) return;
        QuickAlignmentPreset preset = raw switch
        {
            "Left" => QuickAlignmentPreset.AlignLeft,
            "Right" => QuickAlignmentPreset.AlignRight,
            "Top" => QuickAlignmentPreset.AlignTop,
            "Bottom" => QuickAlignmentPreset.AlignBottom,
            _ => QuickAlignmentPreset.CenterSurface,
        };
        _vm.ApplyQuickAlignment(preset);
    }

    private void OnRotate90Click(object sender, RoutedEventArgs e) => _vm?.RotateSelected90();
    private void OnToggleVisibleClick(object sender, RoutedEventArgs e) => _vm?.ToggleSelectedVisibility();
    private void OnToggleLockClick(object sender, RoutedEventArgs e) => _vm?.ToggleSelectedLock();
    private void OnDuplicateClick(object sender, RoutedEventArgs e) => _vm?.DuplicateSelectedElement();
    private void OnDeleteClick(object sender, RoutedEventArgs e) => _vm?.DeleteSelectedElement();
    private void OnQuickInsertTextClick(object sender, RoutedEventArgs e) => _vm?.AddTextElement();
    private void OnRevalidateClick(object sender, RoutedEventArgs e) => _vm?.RunValidation();

    public void BindMainViewModel(MainWindowViewModel mainViewModel)
        => _mainVm = mainViewModel;

    private void UpdateFooterSatBanner()
    {
        if (_vm is null)
        {
            FooterSatWarningBanner.Visibility = Visibility.Collapsed;
            return;
        }

        bool hasAlerts = !_vm.IsValidationValid || _vm.ValidationErrorCount > 0 || _vm.ValidationWarningCount > 0;
        FooterSatWarningBanner.Visibility = hasAlerts ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBackToModelingClick(object sender, RoutedEventArgs e)
        => _mainVm?.NavigatePreviousWorkspace();

    private void OnAdvanceToManufacturingClick(object sender, RoutedEventArgs e)
        => _mainVm?.NavigateNextWorkspace();
}
