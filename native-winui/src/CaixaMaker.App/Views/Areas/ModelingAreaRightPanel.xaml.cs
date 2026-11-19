using System.Globalization;
using CaixaMaker.App.Controls.CadPrimitives;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Views.Areas;

/// <summary>
/// Inspetor direito da área de Modelagem com diagnóstico geométrico, tabela comparativa externa/interna
/// em formato Property Table e Outliner interativo das chapas da caixa.
/// </summary>
public sealed partial class ModelingAreaRightPanel : UserControl
{
    private BoxWorkspaceViewModel? _vm;
    private MainWindowViewModel? _mainVm;

    private sealed class PartRowVisual
    {
        public required Border ItemBorder { get; init; }
        public required Border QtyBadge { get; init; }
        public required TextBlock QtyText { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock InfoText { get; init; }
        public required TextBlock DimsText { get; init; }
        public string PartId = string.Empty;
        public string Tooltip = string.Empty;
        public bool? IsSelected;
    }

    private readonly List<PartRowVisual> _partRows = new();

    public ModelingAreaRightPanel()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RebuildAll(forceThemeRefresh: true);
    }

    public void BindViewModel(BoxWorkspaceViewModel viewModel)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.GeometryRebuilt -= OnVmGeometryRebuilt;
        }

        _vm = viewModel;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.GeometryRebuilt += OnVmGeometryRebuilt;
        RebuildAll(forceThemeRefresh: false);
    }

    public void BindMainViewModel(MainWindowViewModel mainViewModel)
        => _mainVm = mainViewModel;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BoxWorkspaceViewModel.CameraYawDeg)
            or nameof(BoxWorkspaceViewModel.CameraPitchDeg)
            or nameof(BoxWorkspaceViewModel.CameraZoom)
            or nameof(BoxWorkspaceViewModel.CameraPanX)
            or nameof(BoxWorkspaceViewModel.CameraPanY)
            or nameof(BoxWorkspaceViewModel.ActiveCameraPreset)
            or nameof(BoxWorkspaceViewModel.LidAngle)
            or nameof(BoxWorkspaceViewModel.ExplodedView)
            or nameof(BoxWorkspaceViewModel.ShowDimensions))
        {
            return;
        }

        RebuildAll(forceThemeRefresh: false);
    }

    private void OnVmGeometryRebuilt(object? sender, EventArgs e)
        => RebuildAll(forceThemeRefresh: false);

    private void RebuildAll(bool forceThemeRefresh = false)
    {
        if (_vm is null) return;
        BoxCalculationSummary? summary = _vm.Summary;

        // 1. Diagnóstico Geométrico
        if (!_vm.IsParametersValid)
        {
            DiagnosticStatusBadge.Kind = CadBadgeKind.Error;
            DiagnosticStatusBadge.Text = "PARÂMETROS INVÁLIDOS";
            CheckGeomDot.Fill = LookupBrush("CmStatusDangerBrush");
            CheckGeomText.Text = _vm.ValidationError ?? "Revise as dimensões da caixa";
        }
        else if (_vm.Advisories.Count > 0)
        {
            DiagnosticStatusBadge.Kind = CadBadgeKind.Warning;
            DiagnosticStatusBadge.Text = $"VÁLIDA · {_vm.Advisories.Count} AVISO(S)";
            CheckGeomDot.Fill = LookupBrush("CmStatusWarningBrush");
            CheckGeomText.Text = _vm.Advisories[0];
        }
        else
        {
            DiagnosticStatusBadge.Kind = CadBadgeKind.Valid;
            DiagnosticStatusBadge.Text = "GEOMETRIA VÁLIDA 1:1";
            CheckGeomDot.Fill = LookupBrush("CmStatusSuccessBrush");
            CheckGeomText.Text = $"Parâmetros dentro da janela CNC ({_vm.SelectedModel.DisplayName})";
        }

        if (summary is not null)
        {
            CheckJointsText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} verticais · {1} front. / {2} lat. na base",
                summary.Joints.Vertical.Count,
                summary.Joints.FrontBottom.TabCount,
                summary.Joints.SideBottom.TabCount);
            CheckKerfText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Slot efetivo: {0:F2} mm (t {1:F1} + folga {2:+0.00;-0.00;0.00})",
                summary.ComputedDimensions.EffectiveSlotWidthMm,
                _vm.BoxThickness,
                _vm.Folga);

            // 2. Especificações Calculadas
            bool isInternalLocked = _vm.DimensionMode == DimensionReferenceMode.InternalUsable;
            Brush textPrimary = LookupBrush("CmTextPrimaryBrush");
            Brush textSecondary = LookupBrush("CmTextSecondaryBrush");
            Brush accentHighlight = LookupBrush("CmAccentHighlightBrush");

            OuterMeasureTitleText.Text = isInternalLocked ? "MEDIDA EXTERNA" : "EXTERNA · FIXA";
            OuterMeasureTitleText.Foreground = isInternalLocked ? textSecondary : accentHighlight;
            OuterMeasureValueText.Foreground = isInternalLocked ? textPrimary : accentHighlight;

            InnerMeasureTitleText.Text = isInternalLocked ? "INTERNA · TRAVADA" : "INTERNA ÚTIL";
            InnerMeasureTitleText.Foreground = isInternalLocked ? accentHighlight : textSecondary;
            InnerMeasureValueText.Foreground = isInternalLocked ? accentHighlight : textPrimary;

            OuterMeasureValueText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F0} × {1:F0} × {2:F0}",
                _vm.BoxWidth,
                _vm.BoxDepth,
                _vm.BoxHeight);
            OuterMeasureSubText.Text = _vm.SelectedModel.HasLid
                ? string.Format(CultureInfo.InvariantCulture, "c/ abas {0:F0} × {1:F0} mm",
                    summary.ComputedDimensions.OuterWidthWithFlange,
                    summary.ComputedDimensions.OuterDepthWithHinge)
                : "mm nominais externas";

            InnerMeasureValueText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F0} × {1:F0} × {2:F0}",
                summary.ComputedDimensions.InnerWidth,
                summary.ComputedDimensions.InnerDepth,
                summary.ComputedDimensions.InnerHeight);
            InnerMeasureSubText.Text = _vm.SelectedModel.HasLid
                ? string.Format(CultureInfo.InvariantCulture, "Alt. c/ tampa: {0:F1} mm", summary.ComputedDimensions.TotalHeightWithLid)
                : "Volume interno livre (mm)";

            EngineeringMetricsTable.SetMetrics(
            [
                new CadMetricItem(
                    "Encaixes verticais",
                    string.Format(CultureInfo.GetCultureInfo("pt-BR"), "{0} dentes ({1:0.0} mm)",
                        summary.Joints.Vertical.Count, summary.Joints.Vertical.TabLengthMm)),
                new CadMetricItem(
                    "Base (F / L)",
                    _vm.BaseJointsSummaryDisplay,
                    Tooltip: _vm.BaseJointsSummaryTooltip),
                new CadMetricItem(
                    "Slot efetivo (t + folga)",
                    string.Format(CultureInfo.InvariantCulture, "{0:F2} mm", summary.ComputedDimensions.EffectiveSlotWidthMm),
                    IsHighlighted: true),
                new CadMetricItem(
                    "Folha de corte 1:1",
                    string.Format(CultureInfo.InvariantCulture, "{0:F0} × {1:F0} mm",
                        summary.SvgCanvas.WidthMm, summary.SvgCanvas.HeightMm)),
                new CadMetricItem(
                    "Área bruta estimada",
                    string.Format(CultureInfo.InvariantCulture, "{0:F1} cm² ({1} chapas)",
                        summary.EstimatedCuttingAreaCm2, _vm.TotalPartUnits)),
                new CadMetricItem(
                    "Perímetro de corte",
                    _vm.TotalCutPerimeterDisplay),
            ]);
        }

        // 3. Árvore de Chapas Outliner
        PartsTreeSection.BadgeText = $"{_vm.TotalPartUnits}p";
        RebuildPartsTree(forceThemeRefresh);
    }

    private void RebuildPartsTree(bool forceThemeRefresh)
    {
        if (_vm is null) return;
        int partCount = _vm.Parts.Count;
        if (partCount == 0)
        {
            _partRows.Clear();
            PartsTreeList.Children.Clear();
            return;
        }

        while (_partRows.Count > partCount)
        {
            int lastIdx = _partRows.Count - 1;
            _partRows.RemoveAt(lastIdx);
            PartsTreeList.Children.RemoveAt(lastIdx);
        }

        Brush evenBg = LookupBrush("CmTableRowEvenBrush");
        Brush altBg = LookupBrush("CmTableRowAltBrush");
        Brush inputBg = LookupBrush("CmInputBackgroundBrush");
        Brush selectedBg = LookupBrush("CmAccentSelectionBrush");
        Brush selectedBorder = LookupBrush("CmAccentPrimaryBrush");
        Brush hairlineBorder = LookupBrush("CmHairlineBorderBrush");
        Brush textPrimary = LookupBrush("CmTextPrimaryBrush");
        Brush textSecondary = LookupBrush("CmTextSecondaryBrush");
        Brush accentText = LookupBrush("CmAccentHighlightBrush");
        FontFamily monoFont = (FontFamily)Application.Current.Resources["CmMonoFontFamily"];

        for (int i = 0; i < partCount; i++)
        {
            PartSummary part = _vm.Parts[i];
            bool isSelected = string.Equals(_vm.SelectedPartId, part.Id, StringComparison.OrdinalIgnoreCase);
            string qtyStr = $"{part.Quantity}×";
            string infoStr = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F1} cm² · MDF {1:F1} mm",
                part.EstimatedAreaCm2,
                part.ThicknessMm);
            string dimsStr = string.Format(CultureInfo.InvariantCulture, "{0:F0}×{1:F0}", part.WidthMm, part.HeightMm);
            string tooltipStr = string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1}×) — {2:F1} × {3:F1} mm · {4}",
                part.Name,
                part.Quantity,
                part.WidthMm,
                part.HeightMm,
                string.IsNullOrWhiteSpace(part.Notes) ? $"MDF {part.ThicknessMm:F1} mm" : part.Notes);

            if (i >= _partRows.Count)
            {
                var rowGrid = new Grid { ColumnSpacing = 6 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var qtyText = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = monoFont,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isSelected ? accentText : textSecondary,
                    Text = qtyStr,
                };
                var qtyBadge = new Border
                {
                    Width = 22,
                    Height = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = isSelected ? selectedBg : inputBg,
                    BorderBrush = isSelected ? selectedBorder : hairlineBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Child = qtyText,
                };
                Grid.SetColumn(qtyBadge, 0);

                var infoStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 0,
                };
                var nameText = new TextBlock
                {
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isSelected ? accentText : textPrimary,
                    Text = part.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var infoText = new TextBlock
                {
                    FontSize = 10,
                    Foreground = textSecondary,
                    Text = infoStr,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                infoStack.Children.Add(nameText);
                infoStack.Children.Add(infoText);
                Grid.SetColumn(infoStack, 1);

                var dimsText = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = monoFont,
                    FontSize = 11,
                    Foreground = isSelected ? textPrimary : textSecondary,
                    Text = dimsStr,
                };
                Grid.SetColumn(dimsText, 2);

                rowGrid.Children.Add(qtyBadge);
                rowGrid.Children.Add(infoStack);
                rowGrid.Children.Add(dimsText);

                var itemBorder = new Border
                {
                    MinHeight = 34,
                    Padding = new Thickness(7, 3, 7, 3),
                    Background = isSelected ? selectedBg : (i % 2 == 0 ? evenBg : altBg),
                    BorderBrush = isSelected ? selectedBorder : hairlineBorder,
                    BorderThickness = isSelected ? new Thickness(2, 1, 1, 1) : new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Child = rowGrid,
                };
                ToolTipService.SetToolTip(itemBorder, tooltipStr);

                var row = new PartRowVisual
                {
                    ItemBorder = itemBorder,
                    QtyBadge = qtyBadge,
                    QtyText = qtyText,
                    NameText = nameText,
                    InfoText = infoText,
                    DimsText = dimsText,
                    PartId = part.Id,
                    Tooltip = tooltipStr,
                    IsSelected = isSelected,
                };
                itemBorder.PointerPressed += (_, _) => _vm?.SelectPart(row.PartId);
                _partRows.Add(row);
                PartsTreeList.Children.Add(itemBorder);
                continue;
            }

            PartRowVisual existing = _partRows[i];
            existing.PartId = part.Id;
            if (!string.Equals(existing.QtyText.Text, qtyStr, StringComparison.Ordinal)) existing.QtyText.Text = qtyStr;
            if (!string.Equals(existing.NameText.Text, part.Name, StringComparison.Ordinal)) existing.NameText.Text = part.Name;
            if (!string.Equals(existing.InfoText.Text, infoStr, StringComparison.Ordinal)) existing.InfoText.Text = infoStr;
            if (!string.Equals(existing.DimsText.Text, dimsStr, StringComparison.Ordinal)) existing.DimsText.Text = dimsStr;

            if (!string.Equals(existing.Tooltip, tooltipStr, StringComparison.Ordinal))
            {
                ToolTipService.SetToolTip(existing.ItemBorder, tooltipStr);
                existing.Tooltip = tooltipStr;
            }

            if (forceThemeRefresh || existing.IsSelected != isSelected)
            {
                existing.QtyBadge.Background = isSelected ? selectedBg : inputBg;
                existing.QtyBadge.BorderBrush = isSelected ? selectedBorder : hairlineBorder;
                existing.QtyText.Foreground = isSelected ? accentText : textSecondary;
                existing.NameText.Foreground = isSelected ? accentText : textPrimary;
                existing.InfoText.Foreground = textSecondary;
                existing.DimsText.Foreground = isSelected ? textPrimary : textSecondary;
                existing.ItemBorder.Background = isSelected ? selectedBg : (i % 2 == 0 ? evenBg : altBg);
                existing.ItemBorder.BorderBrush = isSelected ? selectedBorder : hairlineBorder;
                existing.ItemBorder.BorderThickness = isSelected ? new Thickness(2, 1, 1, 1) : new Thickness(1);
                existing.IsSelected = isSelected;
            }
        }
    }

    private Brush LookupBrush(string key)
        => ThemeBrushHelper.ResolveBrush(this, key)!;

    private void OnAdvanceToPersonalizationClick(object sender, RoutedEventArgs e)
        => _mainVm?.NavigateNextWorkspace();

    private void OnSkipToManufacturingClick(object sender, RoutedEventArgs e)
        => _mainVm?.SkipToManufacturing();
}
