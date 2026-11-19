using System.Diagnostics;
using System.Globalization;
using CaixaMaker.App.Controls.CadPrimitives;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.Personalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Views.Areas;

/// <summary>
/// Painel direito da área de Fabricação com auditoria pré-corte (SAT + Kerf), seletor de escopo 2×2
/// sem ComboBox em branco, botões de exportação CAM 1:1 e histórico com abertura rápida no Windows.
/// </summary>
public sealed partial class ManufacturingAreaRightPanel : UserControl
{
    private sealed class AuditIssueRowVisual
    {
        public required Border Border { get; init; }
        public required TextBlock TextBlock { get; init; }
        public bool? IsError;
    }

    private sealed class RecentOutputRowVisual
    {
        public required Border Border { get; init; }
        public required Border FmtBadge { get; init; }
        public required TextBlock FmtText { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock TimeText { get; init; }
        public required Button OpenButton { get; init; }
        public string FullPath = string.Empty;
    }

    private readonly List<ComboOption<SurfaceId>> _panelOptions = new();
    private readonly List<AuditIssueRowVisual> _issueRows = new();
    private readonly List<RecentOutputRowVisual> _recentOutputRows = new();
    private Border? _emptyOutputsPlaceholder;
    private MainWindowViewModel? _mainVm;
    private bool _syncing;
    private bool _zipPresetSelected;
    private bool _dirtyWhileHidden;

    public ManufacturingAreaRightPanel()
    {
        InitializeComponent();
        WireInteractiveInputs();
        ActualThemeChanged += (_, _) => SyncFromViewModel(forceThemeRefresh: true);
    }

    private void WireInteractiveInputs()
    {
        CustomBedWidthInput.InteractionStarted += (_, _) => _mainVm?.Manufacturing.BeginInteractiveUpdate();
        CustomBedWidthInput.InteractionCompleted += (_, _) => _mainVm?.Manufacturing.EndInteractiveUpdate();
        CustomBedHeightInput.InteractionStarted += (_, _) => _mainVm?.Manufacturing.BeginInteractiveUpdate();
        CustomBedHeightInput.InteractionCompleted += (_, _) => _mainVm?.Manufacturing.EndInteractiveUpdate();
    }

    public void BindViewModel(MainWindowViewModel mainVm)
    {
        if (_mainVm is not null)
        {
            _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
            _mainVm.Manufacturing.PropertyChanged -= OnAnyPropertyChanged;
            _mainVm.Personalization.VisualStateChanged -= OnPersVisualChanged;
            _mainVm.Box.GeometryRebuilt -= OnBoxGeometryRebuilt;
        }

        _mainVm = mainVm;
        _mainVm.PropertyChanged += OnMainVmPropertyChanged;
        _mainVm.Manufacturing.PropertyChanged += OnAnyPropertyChanged;
        _mainVm.Personalization.VisualStateChanged += OnPersVisualChanged;
        _mainVm.Box.GeometryRebuilt += OnBoxGeometryRebuilt;

        SyncFromViewModel();
    }

    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ActiveArea)
            or nameof(MainWindowViewModel.IsFabricationArea)
            or nameof(MainWindowViewModel.InDesigner))
        {
            if (_mainVm is { InDesigner: true, IsFabricationArea: true } && _dirtyWhileHidden)
            {
                SyncFromViewModel();
            }
        }
    }

    private void OnAnyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_syncing)
        {
            SyncFromViewModel(forceThemeRefresh: false);
        }
    }

    private void OnPersVisualChanged(object? sender, EventArgs e)
        => SyncFromViewModel(forceThemeRefresh: false);

    private void OnBoxGeometryRebuilt(object? sender, EventArgs e)
        => SyncFromViewModel(forceThemeRefresh: false);

    private void SyncFromViewModel(bool forceThemeRefresh = false)
    {
        if (_mainVm is null) return;
        if (!_mainVm.InDesigner || !_mainVm.IsFabricationArea)
        {
            _dirtyWhileHidden = true;
            return;
        }

        _dirtyWhileHidden = false;
        ManufacturingWorkspaceViewModel mfg = _mainVm.Manufacturing;
        PersonalizationWorkspaceViewModel pers = _mainVm.Personalization;
        BoxWorkspaceViewModel box = _mainVm.Box;

        _syncing = true;
        try
        {
            // 1. Auditoria pré-corte
            int errCount = pers.ValidationErrorCount;
            int warnCount = pers.ValidationWarningCount;
            if (!box.IsParametersValid)
            {
                PreCutAuditBadge.Kind = CadBadgeKind.Error;
                PreCutAuditBadge.Text = "PARÂMETROS DA CAIXA INVÁLIDOS";
            }
            else if (errCount > 0)
            {
                PreCutAuditBadge.Kind = CadBadgeKind.Error;
                PreCutAuditBadge.Text = $"BLOQUEADA · {errCount} COLISÃO SAT";
            }
            else if (warnCount > 0)
            {
                PreCutAuditBadge.Kind = CadBadgeKind.Warning;
                PreCutAuditBadge.Text = $"APROVADA · {warnCount} AVISO(S)";
            }
            else
            {
                PreCutAuditBadge.Kind = CadBadgeKind.Valid;
                PreCutAuditBadge.Text = "APROVADA PARA CORTE";
            }

            double effSlot = box.Summary?.ComputedDimensions.EffectiveSlotWidthMm ?? (box.BoxThickness + box.Folga);
            int visibleLayers = mfg.Layers.Count(l => l.IsVisible);
            LaserBedFitResult bedFit = mfg.EvaluateLaserBedFit(box.SheetGeometry);
            string sheetSummary = box.SheetGeometry is { } sheetGeom
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} × {1:0.#} mm", sheetGeom.Layout.SheetWidth, sheetGeom.Layout.SheetHeight)
                : "—";

            PreCutMetricsTable.SetMetrics(
            [
                new CadMetricItem("Slot efetivo (t + folga)", string.Format(CultureInfo.InvariantCulture, "{0:F2} mm", effSlot), IsHighlighted: true),
                new CadMetricItem("Folha completa 1:1", sheetSummary),
                new CadMetricItem("Erros / Avisos SAT", $"{errCount} erro(s) · {warnCount} aviso(s)"),
                new CadMetricItem("Camadas ativas", $"{visibleLayers} / {mfg.Layers.Count} camadas"),
            ]);

            if (LaserBedCombo.ItemsSource is null)
            {
                LaserBedCombo.ItemsSource = mfg.AvailableLaserBeds;
            }
            LaserBedCombo.SelectedItem = mfg.AvailableLaserBeds.FirstOrDefault(b => b.Preset == mfg.SelectedLaserBedPreset);

            bool isCustomBed = mfg.IsCustomLaserBedSelected;
            CustomLaserBedPanel.Visibility = isCustomBed ? Visibility.Visible : Visibility.Collapsed;
            LaserBedRow.ShowBottomDivider = isCustomBed;
            if (Math.Abs(CustomBedWidthInput.Value - mfg.CustomBedWidthMm) > 1e-4)
            {
                CustomBedWidthInput.Value = mfg.CustomBedWidthMm;
            }
            if (Math.Abs(CustomBedHeightInput.Value - mfg.CustomBedHeightMm) > 1e-4)
            {
                CustomBedHeightInput.Value = mfg.CustomBedHeightMm;
            }

            (string bgKey, string borderKey, string fgKey) = bedFit.Severity switch
            {
                LaserBedFitSeverity.Fits => ("CmStatusSuccessSoftBrush", "CmStatusSuccessBrush", "CmStatusSuccessBrush"),
                LaserBedFitSeverity.FitsRotatedOnly or LaserBedFitSeverity.ExceedsBed
                    => ("CmStatusWarningSoftBrush", "CmStatusWarningBrush", "CmStatusWarningBrush"),
                _ => ("CmInputBackgroundBrush", "CmHairlineBorderBrush", "CmTextSecondaryBrush"),
            };
            LaserBedFitBanner.Background = ThemeBrushHelper.ResolveBrush(this, bgKey);
            LaserBedFitBanner.BorderBrush = ThemeBrushHelper.ResolveBrush(this, borderKey);
            LaserBedFitText.Foreground = ThemeBrushHelper.ResolveBrush(this, fgKey);
            LaserBedFitText.Text = bedFit.SummaryText;

            RebuildIssuesList(pers, forceThemeRefresh);

            // 2. Escopo 2x2 e Chapa alvo
            PersonalizationExportMode scope = mfg.SelectedExportScope;
            ApplyScopeHighlight(ScopeFullBoxBtn, !_zipPresetSelected && scope == PersonalizationExportMode.FullBox);
            ApplyScopeHighlight(ScopePanelCombinedBtn, !_zipPresetSelected && scope == PersonalizationExportMode.PanelCombined);
            ApplyScopeHighlight(ScopeEngraveOnlyBtn, !_zipPresetSelected && scope == PersonalizationExportMode.PanelEngraveOnly);
            ApplyScopeHighlight(ScopeZipPresetBtn, _zipPresetSelected);

            SyncPanelOptionsIfChanged(pers);
            ComboOption<SurfaceId>? selectedPanel = _panelOptions.FirstOrDefault(o => o.Value == mfg.SelectedSinglePanelId);
            if (selectedPanel is null && _panelOptions.Count > 0)
            {
                selectedPanel = _panelOptions[0];
                mfg.SelectedSinglePanelId = selectedPanel.Value;
            }
            if (!ReferenceEquals(SinglePanelCombo.SelectedItem, selectedPanel))
            {
                SinglePanelCombo.SelectedItem = selectedPanel;
            }

            bool needsSinglePanel = !_zipPresetSelected && scope != PersonalizationExportMode.FullBox;
            SinglePanelRow.Visibility = needsSinglePanel ? Visibility.Visible : Visibility.Collapsed;

            IncludeFullSheetCheck.IsChecked = mfg.IncludeFullSheetInZip;
            IncludeEngraveOnlyCheck.IsChecked = mfg.IncludeEngraveOnlyInZip;

            // 3. Calibração de Lote MDF (Pente de Kerf)
            if (Math.Abs(mfg.CurrentThicknessMm - box.BoxThickness) > 1e-4)
            {
                mfg.CurrentThicknessMm = box.BoxThickness;
            }
            KerfCalibrationSection.BadgeText = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F1} mm",
                box.BoxThickness);
            KerfCombSummaryText.Text = mfg.KerfCombRangeSummary;
            if (Math.Abs(KerfStepInput.Value - mfg.KerfStepMm) > 1e-4)
            {
                KerfStepInput.Value = mfg.KerfStepMm;
            }

            ApplyScopeHighlight(KerfPresetDefaultBtn, mfg.KerfSlotCount == 11 && Math.Abs(mfg.KerfStepMm - 0.05) < 1e-4);
            ApplyScopeHighlight(KerfPresetWideBtn, mfg.KerfSlotCount == 11 && Math.Abs(mfg.KerfStepMm - 0.10) < 1e-4);
            ApplyScopeHighlight(KerfPresetExtendedBtn, mfg.KerfSlotCount == 15 && Math.Abs(mfg.KerfStepMm - 0.05) < 1e-4);
            ApplyScopeHighlight(KerfSlots7Btn, mfg.KerfSlotCount == 7);
            ApplyScopeHighlight(KerfSlots11Btn, mfg.KerfSlotCount == 11);
            ApplyScopeHighlight(KerfSlots15Btn, mfg.KerfSlotCount == 15);
            ApplyScopeHighlight(KerfSlots21Btn, mfg.KerfSlotCount == 21);

            // 4. Arquivos gerados na sessão
            LastExportStatusText.Text = mfg.LastExportStatus;
            RecentOutputsSection.BadgeText = mfg.RecentOutputs.Count.ToString(CultureInfo.InvariantCulture);
            RebuildRecentOutputs(mfg, forceThemeRefresh);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncPanelOptionsIfChanged(PersonalizationWorkspaceViewModel pers)
    {
        bool same = _panelOptions.Count == pers.Surfaces.Count;
        if (same)
        {
            for (int i = 0; i < pers.Surfaces.Count; i++)
            {
                if (_panelOptions[i].Value != pers.Surfaces[i].PanelId ||
                    !string.Equals(_panelOptions[i].Label, pers.Surfaces[i].DisplayName, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same && SinglePanelCombo.ItemsSource is not null)
        {
            return;
        }

        _panelOptions.Clear();
        foreach (SurfaceDefinition surf in pers.Surfaces)
        {
            _panelOptions.Add(new ComboOption<SurfaceId>(surf.PanelId, surf.DisplayName));
        }
        SinglePanelCombo.ItemsSource = null;
        SinglePanelCombo.ItemsSource = _panelOptions;
    }

    private void RebuildIssuesList(PersonalizationWorkspaceViewModel pers, bool forceThemeRefresh)
    {
        int issueCount = Math.Min(5, pers.ValidationIssues.Count);
        if (issueCount == 0)
        {
            _issueRows.Clear();
            AuditIssuesList.Children.Clear();
            AuditIssuesList.Visibility = Visibility.Collapsed;
            return;
        }

        AuditIssuesList.Visibility = Visibility.Visible;
        while (_issueRows.Count > issueCount)
        {
            int lastIdx = _issueRows.Count - 1;
            _issueRows.RemoveAt(lastIdx);
            AuditIssuesList.Children.RemoveAt(lastIdx);
        }

        for (int i = 0; i < issueCount; i++)
        {
            GeometryValidationIssue issue = pers.ValidationIssues[i];
            bool isErr = issue.Severity == ValidationSeverity.Error;
            string msg = $"[{issue.PanelId}] {issue.Message}";

            if (i >= _issueRows.Count)
            {
                Brush bg = ThemeBrushHelper.ResolveBrush(this, isErr ? "CmStatusDangerSoftBrush" : "CmStatusWarningSoftBrush")!;
                Brush border = ThemeBrushHelper.ResolveBrush(this, isErr ? "CmStatusDangerBrush" : "CmStatusWarningBrush")!;
                var tb = new TextBlock
                {
                    FontSize = 11,
                    Foreground = border,
                    Text = msg,
                    TextWrapping = TextWrapping.Wrap,
                };
                var boxBorder = new Border
                {
                    Padding = new Thickness(7, 4, 7, 4),
                    Background = bg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Child = tb,
                };
                _issueRows.Add(new AuditIssueRowVisual { Border = boxBorder, TextBlock = tb, IsError = isErr });
                AuditIssuesList.Children.Add(boxBorder);
                continue;
            }

            AuditIssueRowVisual existing = _issueRows[i];
            if (!string.Equals(existing.TextBlock.Text, msg, StringComparison.Ordinal))
            {
                existing.TextBlock.Text = msg;
            }
            if (forceThemeRefresh || existing.IsError != isErr)
            {
                Brush bg = ThemeBrushHelper.ResolveBrush(this, isErr ? "CmStatusDangerSoftBrush" : "CmStatusWarningSoftBrush")!;
                Brush border = ThemeBrushHelper.ResolveBrush(this, isErr ? "CmStatusDangerBrush" : "CmStatusWarningBrush")!;
                existing.Border.Background = bg;
                existing.Border.BorderBrush = border;
                existing.TextBlock.Foreground = border;
                existing.IsError = isErr;
            }
        }
    }

    private void RebuildRecentOutputs(ManufacturingWorkspaceViewModel mfg, bool forceThemeRefresh)
    {
        Brush hairline = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush")!;
        Brush secondary = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush")!;
        Brush inputBg = ThemeBrushHelper.ResolveBrush(this, "CmInputBackgroundBrush")!;

        if (mfg.RecentOutputs.Count == 0)
        {
            _recentOutputRows.Clear();
            RecentOutputsPanel.Children.Clear();
            _emptyOutputsPlaceholder ??= new Border
            {
                Padding = new Thickness(8, 7, 8, 7),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Child = new TextBlock
                {
                    FontSize = 11,
                    Text = "Nenhum arquivo exportado nesta sessão.",
                },
            };
            _emptyOutputsPlaceholder.Background = inputBg;
            _emptyOutputsPlaceholder.BorderBrush = hairline;
            if (_emptyOutputsPlaceholder.Child is TextBlock emptyTb)
            {
                emptyTb.Foreground = secondary;
            }
            RecentOutputsPanel.Children.Add(_emptyOutputsPlaceholder);
            return;
        }

        if (_emptyOutputsPlaceholder is not null && RecentOutputsPanel.Children.Contains(_emptyOutputsPlaceholder))
        {
            RecentOutputsPanel.Children.Remove(_emptyOutputsPlaceholder);
        }

        int count = mfg.RecentOutputs.Count;
        while (_recentOutputRows.Count > count)
        {
            int lastIdx = _recentOutputRows.Count - 1;
            _recentOutputRows.RemoveAt(lastIdx);
            RecentOutputsPanel.Children.RemoveAt(lastIdx);
        }

        Brush rowEven = ThemeBrushHelper.ResolveBrush(this, "CmTableRowEvenBrush")!;
        Brush rowAlt = ThemeBrushHelper.ResolveBrush(this, "CmTableRowAltBrush")!;
        Brush primary = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush")!;
        Brush accentBg = ThemeBrushHelper.ResolveBrush(this, "CmAccentSelectionBrush")!;
        Brush accentFg = ThemeBrushHelper.ResolveBrush(this, "CmAccentHighlightBrush")!;
        Brush btnBg = ThemeBrushHelper.ResolveBrush(this, "CmElevatedSurfaceBrush")!;
        Brush btnBorder = ThemeBrushHelper.ResolveBrush(this, "CmSubtleBorderBrush")!;
        FontFamily monoFont = (FontFamily)Application.Current.Resources["CmMonoFontFamily"];

        for (int i = 0; i < count; i++)
        {
            GeneratedOutputRecord item = mfg.RecentOutputs[i];
            if (i >= _recentOutputRows.Count)
            {
                var grid = new Grid { ColumnSpacing = 6 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var fmtText = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = monoFont,
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = accentFg,
                    Text = item.FormatLabel,
                };
                var fmtBadge = new Border
                {
                    MinWidth = 34,
                    Height = 18,
                    Padding = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = accentBg,
                    CornerRadius = new CornerRadius(2),
                    Child = fmtText,
                };
                Grid.SetColumn(fmtBadge, 0);

                var nameText = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = primary,
                    Text = item.Filename,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(nameText, 1);

                var timeText = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = monoFont,
                    FontSize = 10,
                    Foreground = secondary,
                    Text = item.TimestampText,
                };
                Grid.SetColumn(timeText, 2);

                var openBtn = new Button
                {
                    Height = 20,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(6, 0, 6, 0),
                    Background = btnBg,
                    BorderBrush = btnBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    FontSize = 10,
                    Foreground = primary,
                    Content = "Abrir",
                };
                Grid.SetColumn(openBtn, 3);

                grid.Children.Add(fmtBadge);
                grid.Children.Add(nameText);
                grid.Children.Add(timeText);
                grid.Children.Add(openBtn);

                var rowBorder = new Border
                {
                    Height = 32,
                    Padding = new Thickness(6, 2, 5, 2),
                    Background = i % 2 == 0 ? rowEven : rowAlt,
                    BorderBrush = hairline,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Child = grid,
                };

                var row = new RecentOutputRowVisual
                {
                    Border = rowBorder,
                    FmtBadge = fmtBadge,
                    FmtText = fmtText,
                    NameText = nameText,
                    TimeText = timeText,
                    OpenButton = openBtn,
                    FullPath = item.FullPath,
                };
                ToolTipService.SetToolTip(openBtn, $"Abrir ou localizar {row.FullPath}");
                openBtn.Click += (_, _) => OpenPathInShell(row.FullPath);

                _recentOutputRows.Add(row);
                RecentOutputsPanel.Children.Add(rowBorder);
                continue;
            }

            RecentOutputRowVisual existing = _recentOutputRows[i];
            if (!string.Equals(existing.FullPath, item.FullPath, StringComparison.Ordinal))
            {
                existing.FullPath = item.FullPath;
                ToolTipService.SetToolTip(existing.OpenButton, $"Abrir ou localizar {existing.FullPath}");
            }
            if (!string.Equals(existing.FmtText.Text, item.FormatLabel, StringComparison.Ordinal)) existing.FmtText.Text = item.FormatLabel;
            if (!string.Equals(existing.NameText.Text, item.Filename, StringComparison.Ordinal)) existing.NameText.Text = item.Filename;
            if (!string.Equals(existing.TimeText.Text, item.TimestampText, StringComparison.Ordinal)) existing.TimeText.Text = item.TimestampText;

            if (forceThemeRefresh)
            {
                existing.FmtBadge.Background = accentBg;
                existing.FmtText.Foreground = accentFg;
                existing.NameText.Foreground = primary;
                existing.TimeText.Foreground = secondary;
                existing.OpenButton.Background = btnBg;
                existing.OpenButton.BorderBrush = btnBorder;
                existing.OpenButton.Foreground = primary;
                existing.Border.Background = i % 2 == 0 ? rowEven : rowAlt;
                existing.Border.BorderBrush = hairline;
            }
        }
    }

    private void ApplyScopeHighlight(Button button, bool active)
    {
        button.Background = ThemeBrushHelper.ResolveBrush(this, active ? "CmAccentSelectionBrush" : "CmElevatedSurfaceBrush")!;
        button.BorderBrush = ThemeBrushHelper.ResolveBrush(this, active ? "CmAccentPrimaryBrush" : "CmSubtleBorderBrush")!;
        button.Foreground = ThemeBrushHelper.ResolveBrush(this, active ? "CmAccentHighlightBrush" : "CmTextPrimaryBrush")!;
    }

    private void OnScopeFullBoxClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null) return;
        _zipPresetSelected = false;
        _mainVm.Manufacturing.SelectedExportScope = PersonalizationExportMode.FullBox;
        SyncFromViewModel();
    }

    private void OnScopePanelCombinedClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null) return;
        _zipPresetSelected = false;
        _mainVm.Manufacturing.SelectedExportScope = PersonalizationExportMode.PanelCombined;
        SyncFromViewModel();
    }

    private void OnScopeEngraveOnlyClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null) return;
        _zipPresetSelected = false;
        _mainVm.Manufacturing.SelectedExportScope = PersonalizationExportMode.PanelEngraveOnly;
        SyncFromViewModel();
    }

    private void OnScopeZipPresetClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null) return;
        _zipPresetSelected = true;
        SyncFromViewModel();
    }

    private void OnSinglePanelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _mainVm is null) return;
        if (SinglePanelCombo.SelectedItem is ComboOption<SurfaceId> opt)
        {
            _mainVm.Manufacturing.SelectedSinglePanelId = opt.Value;
            _mainVm.Personalization.SelectSurface(opt.Value);
        }
    }

    private void OnLaserBedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _mainVm is null) return;
        if (LaserBedCombo.SelectedItem is LaserBedSpec spec)
        {
            _mainVm.Manufacturing.SelectedLaserBedPreset = spec.Preset;
        }
    }

    private void OnCustomBedWidthChanged(object? sender, double newValue)
    {
        if (_syncing || _mainVm is null) return;
        _mainVm.Manufacturing.CustomBedWidthMm = newValue;
    }

    private void OnCustomBedHeightChanged(object? sender, double newValue)
    {
        if (_syncing || _mainVm is null) return;
        _mainVm.Manufacturing.CustomBedHeightMm = newValue;
    }

    private void OnKerfPresetClick(object sender, RoutedEventArgs e)
    {
        if (_syncing || _mainVm is null || sender is not FrameworkElement { Tag: string key }) return;
        _mainVm.Manufacturing.ApplyKerfCombPreset(key);
    }

    private void OnKerfSlotCountClick(object sender, RoutedEventArgs e)
    {
        if (_syncing || _mainVm is null || sender is not FrameworkElement { Tag: string raw }) return;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slots))
        {
            _mainVm.Manufacturing.KerfSlotCount = slots;
        }
    }

    private void OnKerfStepChanged(object? sender, double newValue)
    {
        if (_syncing || _mainVm is null) return;
        _mainVm.Manufacturing.KerfStepMm = newValue;
    }

    private void OnZipOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _mainVm is null) return;
        _mainVm.Manufacturing.IncludeFullSheetInZip = IncludeFullSheetCheck.IsChecked == true;
        _mainVm.Manufacturing.IncludeEngraveOnlyInZip = IncludeEngraveOnlyCheck.IsChecked == true;
    }

    private async void OnRevalidateClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null)
        {
            await _mainVm.DispatchCommandAsync(DesktopCommandIds.ProjectValidate);
        }
    }

    private async void OnExportSvgClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null) await _mainVm.DispatchCommandAsync(DesktopCommandIds.ToolsExportSvg);
    }

    private async void OnExportDxfClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null) await _mainVm.DispatchCommandAsync(DesktopCommandIds.ToolsExportDxf);
    }

    private async void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null) await _mainVm.DispatchCommandAsync(DesktopCommandIds.ToolsExportPdf);
    }

    private async void OnExportZipClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null) await _mainVm.DispatchCommandAsync(DesktopCommandIds.ToolsExportZip);
    }

    private async void OnExportProjectPackageClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null) await _mainVm.DispatchCommandAsync(DesktopCommandIds.FileExportPackage);
    }

    private async void OnExportKerfCombSvgClick(object sender, RoutedEventArgs e)
        => await ExportKerfCombFormatAsync("svg");

    private async void OnExportKerfCombDxfClick(object sender, RoutedEventArgs e)
        => await ExportKerfCombFormatAsync("dxf");

    private async void OnExportKerfCombPdfClick(object sender, RoutedEventArgs e)
        => await ExportKerfCombFormatAsync("pdf");

    private async Task ExportKerfCombFormatAsync(string format)
    {
        if (_mainVm is null) return;
        await _mainVm.ExportKerfCalibrationCombWithDialogAsync(format);
        SyncFromViewModel();
    }

    private void OnOpenOutputsFolderClick(object sender, RoutedEventArgs e)
    {
        if (_mainVm is null) return;
        string path = _mainVm.Manufacturing.RecentOutputs.FirstOrDefault()?.FullPath
            is string latest && File.Exists(latest)
            ? Path.GetDirectoryName(latest)!
            : Path.GetDirectoryName(_mainVm.Manufacturing.BuildDefaultOutputPath("ref.svg"))!;
        OpenPathInShell(path);
    }

    private static void OpenPathInShell(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // Ignora falha caso shell externo esteja indisponível em ambiente headless.
        }
    }

    private void OnBackToPersonalizationClick(object sender, RoutedEventArgs e)
        => _mainVm?.NavigatePreviousWorkspace();
}
