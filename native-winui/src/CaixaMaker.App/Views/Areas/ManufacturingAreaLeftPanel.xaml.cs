using System.Globalization;
using CaixaMaker.App.Controls.CadPrimitives;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Views.Areas;

/// <summary>
/// Painel esquerdo técnico da área de Fabricação com tabela de resumo 1:1, mapa completo
/// das 6 camadas CNC com controle de visibilidade e lista clicável de chapas para usinagem.
/// </summary>
public sealed partial class ManufacturingAreaLeftPanel : UserControl
{
    private sealed class LayerRowVisual
    {
        public required Border Border { get; init; }
        public required CheckBox CheckBox { get; init; }
        public required Border Swatch { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock DetailText { get; init; }
        public required Border CountBadge { get; init; }
        public required TextBlock CountText { get; init; }
        public required CncLayerItemViewModel Layer;
        public string Tooltip = string.Empty;
    }

    private sealed class SheetRowVisual
    {
        public required Border Border { get; init; }
        public required TextBlock QtyText { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock SizeText { get; init; }
        public string PartId = string.Empty;
        public bool? IsSelected;
    }

    private readonly List<LayerRowVisual> _layerRows = new();
    private readonly List<SheetRowVisual> _sheetRows = new();
    private MainWindowViewModel? _mainVm;
    private ManufacturingWorkspaceViewModel? _mfgVm;
    private bool _syncing;
    private bool _dirtyWhileHidden;

    public ManufacturingAreaLeftPanel()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RebuildAll(forceThemeRefresh: true);
    }

    public void BindViewModel(MainWindowViewModel viewModel)
    {
        UnsubscribeAll();
        _mainVm = viewModel;
        _mfgVm = viewModel.Manufacturing;

        _mainVm.PropertyChanged += OnAnyVmChanged;
        _mainVm.Box.PropertyChanged += OnAnyVmChanged;
        _mainVm.Box.GeometryRebuilt += OnAnyEventChanged;
        _mainVm.Personalization.VisualStateChanged += OnAnyEventChanged;
        _mfgVm.PropertyChanged += OnAnyVmChanged;

        RebuildAll();
    }

    public void BindViewModel(ManufacturingWorkspaceViewModel viewModel)
    {
        UnsubscribeAll();
        _mainVm = null;
        _mfgVm = viewModel;
        _mfgVm.PropertyChanged += OnAnyVmChanged;
        RebuildAll();
    }

    private void UnsubscribeAll()
    {
        if (_mainVm is not null)
        {
            _mainVm.PropertyChanged -= OnAnyVmChanged;
            _mainVm.Box.PropertyChanged -= OnAnyVmChanged;
            _mainVm.Box.GeometryRebuilt -= OnAnyEventChanged;
            _mainVm.Personalization.VisualStateChanged -= OnAnyEventChanged;
        }
        if (_mfgVm is not null)
        {
            _mfgVm.PropertyChanged -= OnAnyVmChanged;
        }
    }

    private void OnAnyVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_mainVm is { Personalization.IsInteractiveTransformActive: true })
        {
            return;
        }

        if (ReferenceEquals(sender, _mainVm))
        {
            if (e.PropertyName is nameof(MainWindowViewModel.ActiveArea)
                or nameof(MainWindowViewModel.IsFabricationArea)
                or nameof(MainWindowViewModel.InDesigner))
            {
                if (_mainVm is { InDesigner: true, IsFabricationArea: true } && _dirtyWhileHidden)
                {
                    RebuildAll();
                }
            }
            return;
        }

        if (ReferenceEquals(sender, _mainVm?.Box) &&
            e.PropertyName is nameof(BoxWorkspaceViewModel.CameraYawDeg)
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

    private void OnAnyEventChanged(object? sender, EventArgs e)
        => RebuildAll(forceThemeRefresh: false);

    private void RebuildAll(bool forceThemeRefresh = false)
    {
        if (_mfgVm is null) return;
        if (_mainVm is not null && (!_mainVm.InDesigner || !_mainVm.IsFabricationArea))
        {
            _dirtyWhileHidden = true;
            return;
        }

        _dirtyWhileHidden = false;

        RebuildSummaryTable();
        RebuildLayersList(forceThemeRefresh);
        RebuildSheetsList(forceThemeRefresh);
    }

    private void RebuildSummaryTable()
    {
        if (_mainVm is null)
        {
            ManufacturingSummaryTable.SetMetrics(
            [
                new CadMetricItem("Camadas ativas", $"{_mfgVm!.Layers.Count(l => l.IsVisible)} / {_mfgVm.Layers.Count}"),
                new CadMetricItem("Status CAM", _mfgVm.LastExportStatus, IsHighlighted: true),
            ]);
            return;
        }

        BoxWorkspaceViewModel box = _mainVm.Box;
        PersonalizationWorkspaceViewModel pers = _mainVm.Personalization;
        BoxCalculationSummary? summary = box.Summary;

        ManufacturingSummaryTable.SetMetrics(
        [
            new CadMetricItem("Modelo", box.SelectedModel.DisplayName),
            new CadMetricItem(
                "Dimensões (X×Z×Y)",
                string.Format(CultureInfo.InvariantCulture, "{0:F0} × {1:F0} × {2:F0} mm", box.BoxWidth, box.BoxDepth, box.BoxHeight)),
            new CadMetricItem(
                "Material / Folga",
                string.Format(CultureInfo.InvariantCulture, "MDF {0:F1} mm ({1:+0.00;-0.00;0.00})", box.BoxThickness, box.Folga)),
            new CadMetricItem(
                "Chapas de corte",
                $"{box.TotalPartUnits} unidade(s)",
                IsHighlighted: true),
            new CadMetricItem(
                "Área bruta / Folha",
                summary is not null
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F1} cm² · {1:F0}×{2:F0}", summary.EstimatedCuttingAreaCm2, summary.SvgCanvas.WidthMm, summary.SvgCanvas.HeightMm)
                    : string.Format(CultureInfo.InvariantCulture, "{0:F1} cm²", box.EstimatedCuttingAreaCm2)),
            new CadMetricItem(
                "Perímetro de corte",
                box.TotalCutPerimeterDisplay),
            new CadMetricItem(
                "Personalizações CNC",
                $"{pers.TotalElementCount} elemento(s)"),
        ]);
    }

    private void RebuildLayersList(bool forceThemeRefresh)
    {
        if (_mfgVm is null) return;
        _syncing = true;
        try
        {
            int count = _mfgVm.Layers.Count;
            while (_layerRows.Count > count)
            {
                int lastIdx = _layerRows.Count - 1;
                _layerRows.RemoveAt(lastIdx);
                LayersListPanel.Children.RemoveAt(lastIdx);
            }

            Brush rowEven = ThemeBrushHelper.ResolveBrush(this, "CmTableRowEvenBrush")!;
            Brush rowAlt = ThemeBrushHelper.ResolveBrush(this, "CmTableRowAltBrush")!;
            Brush hairline = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush")!;
            Brush primary = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush")!;
            Brush secondary = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush")!;
            Brush inputBg = ThemeBrushHelper.ResolveBrush(this, "CmInputBackgroundBrush")!;
            FontFamily monoFont = (FontFamily)Application.Current.Resources["CmMonoFontFamily"];

            for (int i = 0; i < count; i++)
            {
                CncLayerItemViewModel layer = _mfgVm.Layers[i];
                string nameStr = UiConvert.OperationDisplayName(layer.Operation);
                string detailStr = $"{layer.LayerId} · {layer.HexColor}";
                string countStr = layer.ElementCount.ToString(CultureInfo.InvariantCulture);
                string tooltipStr = $"{layer.Label} ({layer.LayerId})";

                if (i >= _layerRows.Count)
                {
                    var grid = new Grid { ColumnSpacing = 6 };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var cb = new CheckBox
                    {
                        MinWidth = 0,
                        MinHeight = 0,
                        Width = 22,
                        Padding = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                        IsChecked = layer.IsVisible,
                    };
                    Grid.SetColumn(cb, 0);

                    var swatch = new Border
                    {
                        Width = 10,
                        Height = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        CornerRadius = new CornerRadius(1),
                        BorderBrush = hairline,
                        BorderThickness = new Thickness(1),
                        Background = UiConvert.HexToBrush(layer.HexColor),
                    };
                    Grid.SetColumn(swatch, 1);

                    var labels = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Spacing = 0,
                    };
                    var nameText = new TextBlock
                    {
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = primary,
                        Text = nameStr,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    var detailText = new TextBlock
                    {
                        FontFamily = monoFont,
                        FontSize = 10,
                        Foreground = secondary,
                        Text = detailStr,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    labels.Children.Add(nameText);
                    labels.Children.Add(detailText);
                    Grid.SetColumn(labels, 2);

                    var countText = new TextBlock
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontFamily = monoFont,
                        FontSize = 10.5,
                        Foreground = secondary,
                        Text = countStr,
                    };
                    var countBadge = new Border
                    {
                        MinWidth = 22,
                        Height = 18,
                        Padding = new Thickness(5, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = inputBg,
                        BorderBrush = hairline,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(2),
                        Child = countText,
                    };
                    Grid.SetColumn(countBadge, 3);

                    grid.Children.Add(cb);
                    grid.Children.Add(swatch);
                    grid.Children.Add(labels);
                    grid.Children.Add(countBadge);

                    var layerBorder = new Border
                    {
                        Height = 34,
                        Padding = new Thickness(6, 2, 6, 2),
                        Background = i % 2 == 0 ? rowEven : rowAlt,
                        BorderBrush = hairline,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(2),
                        Child = grid,
                    };
                    ToolTipService.SetToolTip(layerBorder, tooltipStr);

                    var row = new LayerRowVisual
                    {
                        Border = layerBorder,
                        CheckBox = cb,
                        Swatch = swatch,
                        NameText = nameText,
                        DetailText = detailText,
                        CountBadge = countBadge,
                        CountText = countText,
                        Layer = layer,
                        Tooltip = tooltipStr,
                    };
                    cb.Checked += (_, _) =>
                    {
                        if (!_syncing) row.Layer.IsVisible = true;
                    };
                    cb.Unchecked += (_, _) =>
                    {
                        if (!_syncing) row.Layer.IsVisible = false;
                    };

                    _layerRows.Add(row);
                    LayersListPanel.Children.Add(layerBorder);
                    continue;
                }

                LayerRowVisual existing = _layerRows[i];
                bool layerSwapped = !ReferenceEquals(existing.Layer, layer);
                existing.Layer = layer;
                if (existing.CheckBox.IsChecked != layer.IsVisible)
                {
                    existing.CheckBox.IsChecked = layer.IsVisible;
                }
                if (layerSwapped || !string.Equals(existing.NameText.Text, nameStr, StringComparison.Ordinal))
                {
                    existing.NameText.Text = nameStr;
                    existing.Swatch.Background = UiConvert.HexToBrush(layer.HexColor);
                }
                if (!string.Equals(existing.DetailText.Text, detailStr, StringComparison.Ordinal))
                {
                    existing.DetailText.Text = detailStr;
                }
                if (!string.Equals(existing.CountText.Text, countStr, StringComparison.Ordinal))
                {
                    existing.CountText.Text = countStr;
                }
                if (!string.Equals(existing.Tooltip, tooltipStr, StringComparison.Ordinal))
                {
                    ToolTipService.SetToolTip(existing.Border, tooltipStr);
                    existing.Tooltip = tooltipStr;
                }
                if (forceThemeRefresh)
                {
                    existing.Swatch.BorderBrush = hairline;
                    existing.NameText.Foreground = primary;
                    existing.DetailText.Foreground = secondary;
                    existing.CountBadge.Background = inputBg;
                    existing.CountBadge.BorderBrush = hairline;
                    existing.CountText.Foreground = secondary;
                    existing.Border.Background = i % 2 == 0 ? rowEven : rowAlt;
                    existing.Border.BorderBrush = hairline;
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RebuildSheetsList(bool forceThemeRefresh)
    {
        if (_mainVm is null)
        {
            _sheetRows.Clear();
            SheetsListPanel.Children.Clear();
            SheetsListSection.Title = "CHAPAS PARA USINAGEM (0)";
            SheetsListSection.BadgeText = "0p";
            return;
        }

        BoxWorkspaceViewModel box = _mainVm.Box;
        SheetsListSection.Title = $"CHAPAS PARA USINAGEM ({box.TotalPartUnits})";
        SheetsListSection.BadgeText = $"{box.TotalPartUnits}p";

        int count = box.Parts.Count;
        while (_sheetRows.Count > count)
        {
            int lastIdx = _sheetRows.Count - 1;
            _sheetRows.RemoveAt(lastIdx);
            SheetsListPanel.Children.RemoveAt(lastIdx);
        }

        Brush rowEven = ThemeBrushHelper.ResolveBrush(this, "CmTableRowEvenBrush")!;
        Brush rowAlt = ThemeBrushHelper.ResolveBrush(this, "CmTableRowAltBrush")!;
        Brush activeBg = ThemeBrushHelper.ResolveBrush(this, "CmAccentSelectionBrush")!;
        Brush activeBorder = ThemeBrushHelper.ResolveBrush(this, "CmAccentPrimaryBrush")!;
        Brush hairline = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush")!;
        Brush primary = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush")!;
        Brush secondary = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush")!;
        Brush accent = ThemeBrushHelper.ResolveBrush(this, "CmAccentHighlightBrush")!;
        FontFamily monoFont = (FontFamily)Application.Current.Resources["CmMonoFontFamily"];

        for (int i = 0; i < count; i++)
        {
            PartSummary part = box.Parts[i];
            bool isSelected = string.Equals(box.SelectedPartId, part.Id, StringComparison.OrdinalIgnoreCase);
            string qtyStr = $"{part.Quantity}×";
            string sizeStr = string.Format(CultureInfo.InvariantCulture, "{0:F0} × {1:F0} mm", part.WidthMm, part.HeightMm);

            if (i >= _sheetRows.Count)
            {
                var grid = new Grid { ColumnSpacing = 6 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var qtyText = new TextBlock
                {
                    Width = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = monoFont,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isSelected ? accent : secondary,
                    Text = qtyStr,
                };
                Grid.SetColumn(qtyText, 0);

                var nameText = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isSelected ? accent : primary,
                    Text = part.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(nameText, 1);

                var sizeText = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = monoFont,
                    FontSize = 10.5,
                    Foreground = secondary,
                    Text = sizeStr,
                };
                Grid.SetColumn(sizeText, 2);

                grid.Children.Add(qtyText);
                grid.Children.Add(nameText);
                grid.Children.Add(sizeText);

                var rowBorder = new Border
                {
                    Height = 30,
                    Padding = new Thickness(7, 2, 7, 2),
                    Background = isSelected ? activeBg : (i % 2 == 0 ? rowEven : rowAlt),
                    BorderBrush = isSelected ? activeBorder : hairline,
                    BorderThickness = isSelected ? new Thickness(2, 1, 1, 1) : new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Child = grid,
                };

                var row = new SheetRowVisual
                {
                    Border = rowBorder,
                    QtyText = qtyText,
                    NameText = nameText,
                    SizeText = sizeText,
                    PartId = part.Id,
                    IsSelected = isSelected,
                };
                rowBorder.PointerPressed += (_, _) => _mainVm?.Box.SelectPart(row.PartId);
                _sheetRows.Add(row);
                SheetsListPanel.Children.Add(rowBorder);
                continue;
            }

            SheetRowVisual existing = _sheetRows[i];
            existing.PartId = part.Id;
            if (!string.Equals(existing.QtyText.Text, qtyStr, StringComparison.Ordinal)) existing.QtyText.Text = qtyStr;
            if (!string.Equals(existing.NameText.Text, part.Name, StringComparison.Ordinal)) existing.NameText.Text = part.Name;
            if (!string.Equals(existing.SizeText.Text, sizeStr, StringComparison.Ordinal)) existing.SizeText.Text = sizeStr;

            if (forceThemeRefresh || existing.IsSelected != isSelected)
            {
                existing.QtyText.Foreground = isSelected ? accent : secondary;
                existing.NameText.Foreground = isSelected ? accent : primary;
                existing.SizeText.Foreground = secondary;
                existing.Border.Background = isSelected ? activeBg : (i % 2 == 0 ? rowEven : rowAlt);
                existing.Border.BorderBrush = isSelected ? activeBorder : hairline;
                existing.Border.BorderThickness = isSelected ? new Thickness(2, 1, 1, 1) : new Thickness(1);
                existing.IsSelected = isSelected;
            }
        }
    }
}
