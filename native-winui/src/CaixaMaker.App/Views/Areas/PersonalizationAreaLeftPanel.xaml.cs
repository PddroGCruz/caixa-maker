using System.Globalization;
using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.Personalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Views.Areas;

/// <summary>
/// Painel esquerdo compacto da área de Personalização CNC: grade 3×2 de superfícies,
/// criação rápida (Texto / SVG / Raster) em Property Grid e Outliner de elementos da face.
/// </summary>
public sealed partial class PersonalizationAreaLeftPanel : UserControl
{
    private readonly List<ComboOption<TextRenderMode>> _renderModes = new();
    private MainWindowViewModel? _mainVm;
    private bool _syncing;

    public PersonalizationAreaLeftPanel()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => SyncFromViewModel();
    }

    public void FocusTextInput()
    {
        DraftTextBox.Focus(FocusState.Programmatic);
        DraftTextBox.SelectAll();
    }

    public void BindViewModel(MainWindowViewModel mainVm)
    {
        if (_mainVm is not null)
        {
            _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
            _mainVm.Personalization.PropertyChanged -= OnPersPropertyChanged;
            _mainVm.Personalization.VisualStateChanged -= OnPersVisualChanged;
        }

        _mainVm = mainVm;
        _mainVm.PropertyChanged += OnMainVmPropertyChanged;
        _mainVm.Personalization.PropertyChanged += OnPersPropertyChanged;
        _mainVm.Personalization.VisualStateChanged += OnPersVisualChanged;

        _syncing = true;
        try
        {
            DraftFontCombo.ItemsSource = _mainVm.Personalization.AvailableFontFamilies;

            _renderModes.Clear();
            foreach (TextRenderMode mode in _mainVm.Personalization.AvailableTextRenderModes)
            {
                _renderModes.Add(new ComboOption<TextRenderMode>(mode, UiConvert.RenderModeDisplayName(mode)));
            }
            DraftRenderModeCombo.ItemsSource = null;
            DraftRenderModeCombo.ItemsSource = _renderModes;
        }
        finally
        {
            _syncing = false;
        }

        SyncFromViewModel();
    }

    private bool _dirtyWhileHidden;

    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ActiveArea)
            or nameof(MainWindowViewModel.IsPersonalizationArea)
            or nameof(MainWindowViewModel.InDesigner))
        {
            if (_mainVm is { InDesigner: true, IsPersonalizationArea: true } && _dirtyWhileHidden)
            {
                _dirtyWhileHidden = false;
                SyncFromViewModel();
            }
        }
    }

    private void OnPersPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_mainVm is null || _mainVm.Personalization.IsInteractiveTransformActive)
        {
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
            or nameof(PersonalizationWorkspaceViewModel.ValidationErrorCount)
            or nameof(PersonalizationWorkspaceViewModel.ValidationWarningCount)
            or nameof(PersonalizationWorkspaceViewModel.ValidationSummaryBadge))
        {
            return;
        }

        SyncFromViewModel();
    }

    private void OnPersVisualChanged(object? sender, EventArgs e)
        => SyncFromViewModel();

    private void SyncFromViewModel()
    {
        if (_mainVm is null) return;
        if (_mainVm.InDesigner && !_mainVm.IsPersonalizationArea)
        {
            _dirtyWhileHidden = true;
            return;
        }

        _dirtyWhileHidden = false;
        PersonalizationWorkspaceViewModel vm = _mainVm.Personalization;

        _syncing = true;
        try
        {
            if (DraftTextBox.Text != vm.DraftTextContent)
            {
                DraftTextBox.Text = vm.DraftTextContent;
            }

            if (vm.AvailableFontFamilies.Contains(vm.DraftFontFamily))
            {
                DraftFontCombo.SelectedItem = vm.DraftFontFamily;
            }

            DraftSizeInput.Value = vm.DraftFontSizeMm;
            DraftRenderModeCombo.SelectedItem = _renderModes.FirstOrDefault(o => o.Value == vm.DraftRenderMode);

            SurfaceDefinition? active = vm.ActiveSurface;
            string activeShort = UiConvert.SurfaceShortName(vm.ActiveSurfaceId);
            SurfaceSelectorSection.BadgeText = activeShort;
            ActiveSurfaceDimensionsText.Text = active is not null
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "Útil: {0:F0}×{1:F0} mm · {2} zona(s) restrita(s)",
                    active.SafeRectMm.WidthMm,
                    active.SafeRectMm.HeightMm,
                    active.ForbiddenZones.Count)
                : "Selecione uma face da caixa.";

            int faceCount = vm.ElementsOnActiveSurface.Count;
            ElementsOutlinerSection.Title = $"ELEMENTOS NA FACE ({faceCount})";
            ElementsOutlinerSection.BadgeText = $"{vm.TotalElementCount} total";

            PasteBtn.IsEnabled = vm.HasClipboard;
            ClearAllBtn.IsEnabled = vm.HasElements;

            SyncDrawToolButtons(vm.ActiveToolMode);
            RebuildSurfacesGrid(vm);
            RebuildElementsOutliner(vm);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncDrawToolButtons(Canvas2DToolMode activeMode)
    {
        Style activeStyle = (Style)Application.Current.Resources["PrimaryAccentButtonStyle"];
        Style idleStyle = (Style)Application.Current.Resources["CadSegmentedButtonStyle"];

        LeftToolSelectBtn.Style = activeMode == Canvas2DToolMode.Select ? activeStyle : idleStyle;
        LeftToolDrawRectBtn.Style = activeMode == Canvas2DToolMode.DrawRectangle ? activeStyle : idleStyle;
        LeftToolDrawCircleBtn.Style = activeMode == Canvas2DToolMode.DrawCircle ? activeStyle : idleStyle;
        LeftToolDrawSlotBtn.Style = activeMode == Canvas2DToolMode.DrawSlot ? activeStyle : idleStyle;
        LeftToolDrawPolyBtn.Style = activeMode == Canvas2DToolMode.DrawPolygon ? activeStyle : idleStyle;
        LeftToolDrawCrossBtn.Style = activeMode == Canvas2DToolMode.DrawCrosshair ? activeStyle : idleStyle;
    }

    private void OnSelectDrawToolClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || sender is not Button { Tag: string raw }) return;
        if (Enum.TryParse<Canvas2DToolMode>(raw, out Canvas2DToolMode mode))
        {
            _mainVm.Personalization.SetActiveToolMode(mode);
        }
    }

    private void RebuildSurfacesGrid(PersonalizationWorkspaceViewModel vm)
    {
        SurfacesGrid.Children.Clear();
        Brush activeBg = ThemeBrushHelper.ResolveBrush(this, "CmAccentSelectionBrush")!;
        Brush activeBorder = ThemeBrushHelper.ResolveBrush(this, "CmAccentPrimaryBrush")!;
        Brush idleBg = ThemeBrushHelper.ResolveBrush(this, "CmElevatedSurfaceBrush")!;
        Brush idleBorder = ThemeBrushHelper.ResolveBrush(this, "CmSubtleBorderBrush")!;
        Brush primaryText = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush")!;
        Brush secondaryText = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush")!;
        Brush accentText = ThemeBrushHelper.ResolveBrush(this, "CmAccentHighlightBrush")!;
        Brush inputBg = ThemeBrushHelper.ResolveBrush(this, "CmInputBackgroundBrush")!;
        FontFamily monoFont = (FontFamily)Application.Current.Resources["CmMonoFontFamily"];

        for (int i = 0; i < vm.Surfaces.Count && i < 6; i++)
        {
            SurfaceDefinition surface = vm.Surfaces[i];
            bool isSelected = surface.PanelId == vm.ActiveSurfaceId;
            int count = vm.Manager.ListElements(surface.PanelId).Count;

            var topRow = new Grid { ColumnSpacing = 4 };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = isSelected ? accentText : primaryText,
                Text = UiConvert.SurfaceShortName(surface.PanelId),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(nameText, 0);

            var countBadge = new Border
            {
                MinWidth = 17,
                Height = 15,
                Padding = new Thickness(4, 0, 4, 0),
                Background = count > 0 ? activeBg : inputBg,
                BorderBrush = count > 0 ? activeBorder : idleBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = monoFont,
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = count > 0 ? accentText : secondaryText,
                    Text = count.ToString(CultureInfo.InvariantCulture),
                },
            };
            Grid.SetColumn(countBadge, 1);
            topRow.Children.Add(nameText);
            topRow.Children.Add(countBadge);

            var dimsText = new TextBlock
            {
                FontFamily = monoFont,
                FontSize = 10,
                Foreground = secondaryText,
                Text = string.Format(CultureInfo.InvariantCulture, "{0:F0}×{1:F0}", surface.WidthMm, surface.HeightMm),
            };

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 1,
                Children = { topRow, dimsText },
            };

            var cellBorder = new Border
            {
                Height = 42,
                Padding = new Thickness(7, 4, 7, 4),
                Background = isSelected ? activeBg : idleBg,
                BorderBrush = isSelected ? activeBorder : idleBorder,
                BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                CornerRadius = new CornerRadius(2),
                Child = stack,
            };

            ToolTipService.SetToolTip(
                cellBorder,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} — Chapa {1:F1} × {2:F1} mm ({3} elemento(s))",
                    surface.DisplayName,
                    surface.WidthMm,
                    surface.HeightMm,
                    count));

            SurfaceId targetId = surface.PanelId;
            cellBorder.PointerPressed += (_, _) => vm.SelectSurface(targetId);

            Grid.SetRow(cellBorder, i / 3);
            Grid.SetColumn(cellBorder, i % 3);
            SurfacesGrid.Children.Add(cellBorder);
        }
    }

    private void RebuildElementsOutliner(PersonalizationWorkspaceViewModel vm)
    {
        ElementsOutlinerList.Children.Clear();

        Brush rowEvenBg = ThemeBrushHelper.ResolveBrush(this, "CmTableRowEvenBrush")!;
        Brush rowAltBg = ThemeBrushHelper.ResolveBrush(this, "CmTableRowAltBrush")!;
        Brush activeBg = ThemeBrushHelper.ResolveBrush(this, "CmAccentSelectionBrush")!;
        Brush activeBorder = ThemeBrushHelper.ResolveBrush(this, "CmAccentPrimaryBrush")!;
        Brush hairline = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush")!;
        Brush primaryText = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush")!;
        Brush secondaryText = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush")!;
        Brush dangerText = ThemeBrushHelper.ResolveBrush(this, "CmStatusDangerBrush")!;
        Brush accentText = ThemeBrushHelper.ResolveBrush(this, "CmAccentHighlightBrush")!;
        Brush inputBg = ThemeBrushHelper.ResolveBrush(this, "CmInputBackgroundBrush")!;
        FontFamily monoFont = (FontFamily)Application.Current.Resources["CmMonoFontFamily"];

        if (vm.ElementsOnActiveSurface.Count == 0)
        {
            ElementsOutlinerList.Children.Add(new Border
            {
                Padding = new Thickness(8, 7, 8, 7),
                Background = inputBg,
                BorderBrush = hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock
                {
                    FontSize = 11,
                    Foreground = secondaryText,
                    Text = "Nenhum elemento nesta face. Insira texto, SVG ou raster acima.",
                    TextWrapping = TextWrapping.Wrap,
                },
            });
            return;
        }

        for (int i = 0; i < vm.ElementsOnActiveSurface.Count; i++)
        {
            PersonalizationElement el = vm.ElementsOnActiveSurface[i];
            bool isSelected = vm.IsElementSelected(el.Id);
            bool isAnchor = vm.HasMultipleSelection &&
                            string.Equals(vm.FirstSelectedElementId, el.Id, StringComparison.Ordinal);
            CncLayerSpec layer = CncLayerSpecs.Get(el.Operation);

            string typeTag = el.ElementType switch
            {
                ElementType.Text => "T",
                ElementType.VectorSvg => "SVG",
                ElementType.RasterImage => "IMG",
                ElementType.Shape => "GEO",
                _ => "EL",
            };

            var rowGrid = new Grid { ColumnSpacing = 5 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var typeBadge = new Border
            {
                MinWidth = 24,
                Height = 16,
                Padding = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = inputBg,
                BorderBrush = isSelected ? activeBorder : hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = monoFont,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isSelected ? accentText : secondaryText,
                    Text = typeTag,
                },
            };
            Grid.SetColumn(typeBadge, 0);

            var colorDot = new Border
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(1),
                Background = UiConvert.HexToBrush(layer.Stroke != "none" ? layer.Stroke : layer.Fill),
            };
            Grid.SetColumn(colorDot, 1);

            var nameText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.5,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = !el.Visible ? secondaryText : (isSelected ? accentText : primaryText),
                Text = (isAnchor ? "[1º] " : string.Empty) + el.Name + (el.Locked ? " [🔒]" : string.Empty),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(nameText, 2);

            var sizeText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = monoFont,
                FontSize = 10,
                Foreground = secondaryText,
                Text = string.Format(CultureInfo.InvariantCulture, "{0:F0}×{1:F0}", el.WidthMm, el.HeightMm),
            };
            Grid.SetColumn(sizeText, 3);

            var removeBtn = new Button
            {
                Width = 20,
                Height = 20,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(2),
                Content = new TextBlock
                {
                    FontSize = 11,
                    Foreground = dangerText,
                    Text = "×",
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };
            ToolTipService.SetToolTip(removeBtn, "Remover este elemento");
            string capturedId = el.Id;
            removeBtn.Click += (_, _) =>
            {
                vm.SelectElement(capturedId);
                vm.DeleteSelectedElement();
            };
            Grid.SetColumn(removeBtn, 4);

            rowGrid.Children.Add(typeBadge);
            rowGrid.Children.Add(colorDot);
            rowGrid.Children.Add(nameText);
            rowGrid.Children.Add(sizeText);
            rowGrid.Children.Add(removeBtn);

            var rowBorder = new Border
            {
                Height = 28,
                Padding = new Thickness(6, 2, 4, 2),
                Background = isSelected ? activeBg : (i % 2 == 0 ? rowEvenBg : rowAltBg),
                BorderBrush = isSelected ? activeBorder : hairline,
                BorderThickness = isSelected ? new Thickness(2, 1, 1, 1) : new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Child = rowGrid,
            };

            rowBorder.PointerPressed += (_, _) => vm.SelectElement(capturedId);
            ElementsOutlinerList.Children.Add(rowBorder);
        }
    }

    private void OnDraftTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _mainVm is null) return;
        _mainVm.Personalization.DraftTextContent = DraftTextBox.Text;
    }

    private void OnDraftFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _mainVm is null) return;
        if (DraftFontCombo.SelectedItem is string font)
        {
            _mainVm.Personalization.DraftFontFamily = font;
        }
    }

    private void OnDraftSizeChanged(object? sender, double value)
    {
        if (_syncing || _mainVm is null) return;
        _mainVm.Personalization.DraftFontSizeMm = value;
    }

    private void OnDraftRenderModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _mainVm is null) return;
        if (DraftRenderModeCombo.SelectedItem is ComboOption<TextRenderMode> opt)
        {
            _mainVm.Personalization.DraftRenderMode = opt.Value;
        }
    }

    private void OnAddTextClick(object sender, RoutedEventArgs e)
    {
        _mainVm?.Personalization.AddTextElement();
    }

    private void OnAddShapeClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null || sender is not Button btn || btn.Tag is not string rawKind)
        {
            return;
        }

        GeometricShapeKind kind = rawKind switch
        {
            "Circle" => GeometricShapeKind.Circle,
            "Slot" => GeometricShapeKind.Slot,
            "Polygon" => GeometricShapeKind.Polygon,
            "Crosshair" => GeometricShapeKind.Crosshair,
            _ => GeometricShapeKind.Rectangle,
        };

        _mainVm.Personalization.AddShape(kind);
    }

    private async void OnImportSvgClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null)
        {
            await _mainVm.DispatchCommandAsync(DesktopCommandIds.ProjectImportSvg);
        }
    }

    private async void OnImportRasterClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null)
        {
            await _mainVm.DispatchCommandAsync(DesktopCommandIds.ProjectImportRaster);
        }
    }

    private void OnPasteClick(object sender, RoutedEventArgs e)
        => _mainVm?.Personalization.PasteClipboardElement();

    private void OnClearAllClick(object sender, RoutedEventArgs e)
        => _mainVm?.Personalization.ClearAllElements();
}
