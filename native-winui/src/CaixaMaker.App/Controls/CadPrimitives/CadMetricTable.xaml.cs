using CaixaMaker.App.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Controls.CadPrimitives;

public sealed record CadMetricItem(string Key, string Value, bool IsHighlighted = false, string? Tooltip = null);

/// <summary>Grade técnica compacta (linhas de 22 px alternadas) para especificações calculadas Chave -> Valor.</summary>
public sealed partial class CadMetricTable : UserControl
{
    internal int VisualRowCount => RowsStack.Children.Count;

    internal UIElement GetVisualRowAt(int index) => RowsStack.Children[index];

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IEnumerable<CadMetricItem>),
        typeof(CadMetricTable),
        new PropertyMetadata(null, OnItemsChanged));

    private sealed class MetricRowVisual
    {
        public required Border RowBorder { get; init; }
        public required TextBlock KeyText { get; init; }
        public required TextBlock ValueText { get; init; }
        public string? Key;
        public string? Value;
        public string? Tooltip;
        public bool? IsHighlighted;
        public bool? IsLast;
    }

    private readonly List<MetricRowVisual> _rows = new();

    public CadMetricTable()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => SyncRows(forceBrushRefresh: true);
    }

    public IEnumerable<CadMetricItem>? Items
    {
        get => (IEnumerable<CadMetricItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public void SetMetrics(IEnumerable<CadMetricItem> items) => Items = items;

    public void SetRows(IEnumerable<CadMetricItem> items) => Items = items;

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadMetricTable table)
        {
            table.SyncRows(forceBrushRefresh: false);
        }
    }

    private void SyncRows(bool forceBrushRefresh)
    {
        if (Items is null)
        {
            _rows.Clear();
            RowsStack.Children.Clear();
            return;
        }

        IReadOnlyList<CadMetricItem> list = Items as IReadOnlyList<CadMetricItem> ?? Items.ToList();
        if (list.Count == 0)
        {
            _rows.Clear();
            RowsStack.Children.Clear();
            return;
        }

        while (_rows.Count > list.Count)
        {
            int lastIdx = _rows.Count - 1;
            _rows.RemoveAt(lastIdx);
            RowsStack.Children.RemoveAt(lastIdx);
        }

        Brush? evenBrush = null;
        Brush? altBrush = null;
        Brush? hairlineBrush = null;
        Brush? textSecondaryBrush = null;
        Brush? textPrimaryBrush = null;
        Brush? accentBrush = null;
        FontFamily? uiFont = null;
        FontFamily? monoFont = null;

        void EnsureResources()
        {
            if (hairlineBrush is not null) return;
            ResourceDictionary resources = Application.Current.Resources;
            uiFont = (FontFamily)resources["CmUiFontFamily"];
            monoFont = (FontFamily)resources["CmMonoFontFamily"];
            evenBrush = ResolveThemeBrush("CmTableRowEvenBrush");
            altBrush = ResolveThemeBrush("CmTableRowAltBrush");
            hairlineBrush = ResolveThemeBrush("CmHairlineBorderBrush");
            textSecondaryBrush = ResolveThemeBrush("CmTextSecondaryBrush");
            textPrimaryBrush = ResolveThemeBrush("CmTextPrimaryBrush");
            accentBrush = ResolveThemeBrush("CmAccentHighlightBrush");
        }

        if (forceBrushRefresh)
        {
            EnsureResources();
        }

        for (int i = 0; i < list.Count; i++)
        {
            CadMetricItem item = list[i];
            bool isLast = i == list.Count - 1;

            if (i >= _rows.Count)
            {
                EnsureResources();
                string bgKey = i % 2 == 0 ? "CmTableRowEvenBrush" : "CmTableRowAltBrush";

                var rowBorder = new Border
                {
                    Height = 22,
                    Padding = new Thickness(8, 0, 8, 0),
                    Background = i % 2 == 0 ? evenBrush : altBrush,
                    BorderBrush = hairlineBrush,
                    BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                };

                if (!string.IsNullOrWhiteSpace(item.Tooltip))
                {
                    ToolTipService.SetToolTip(rowBorder, item.Tooltip);
                }

                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var keyText = new TextBlock
                {
                    Text = item.Key,
                    FontFamily = uiFont,
                    FontSize = 11.5,
                    Foreground = textSecondaryBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(keyText, 0);

                var valueText = new TextBlock
                {
                    Text = item.Value,
                    FontFamily = monoFont,
                    FontSize = 11.5,
                    FontWeight = item.IsHighlighted ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = item.IsHighlighted ? accentBrush : textPrimaryBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                Grid.SetColumn(valueText, 1);

                grid.Children.Add(keyText);
                grid.Children.Add(valueText);
                rowBorder.Child = grid;
                RowsStack.Children.Add(rowBorder);

                _rows.Add(new MetricRowVisual
                {
                    RowBorder = rowBorder,
                    KeyText = keyText,
                    ValueText = valueText,
                    Key = item.Key,
                    Value = item.Value,
                    Tooltip = item.Tooltip,
                    IsHighlighted = item.IsHighlighted,
                    IsLast = isLast,
                });
                continue;
            }

            MetricRowVisual existing = _rows[i];
            if (!string.Equals(existing.Key, item.Key, StringComparison.Ordinal))
            {
                existing.KeyText.Text = item.Key;
                existing.Key = item.Key;
            }

            if (!string.Equals(existing.Value, item.Value, StringComparison.Ordinal))
            {
                existing.ValueText.Text = item.Value;
                existing.Value = item.Value;
            }

            if (forceBrushRefresh)
            {
                existing.RowBorder.Background = i % 2 == 0 ? evenBrush : altBrush;
                existing.RowBorder.BorderBrush = hairlineBrush;
                existing.KeyText.Foreground = textSecondaryBrush;
            }

            if (forceBrushRefresh || existing.IsHighlighted != item.IsHighlighted)
            {
                EnsureResources();
                existing.ValueText.FontWeight = item.IsHighlighted ? FontWeights.SemiBold : FontWeights.Normal;
                existing.ValueText.Foreground = item.IsHighlighted ? accentBrush : textPrimaryBrush;
                existing.IsHighlighted = item.IsHighlighted;
            }

            if (existing.IsLast != isLast)
            {
                existing.RowBorder.BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1);
                existing.IsLast = isLast;
            }

            if (!string.Equals(existing.Tooltip, item.Tooltip, StringComparison.Ordinal))
            {
                ToolTipService.SetToolTip(existing.RowBorder, string.IsNullOrWhiteSpace(item.Tooltip) ? null : item.Tooltip);
                existing.Tooltip = item.Tooltip;
            }
        }
    }

    private Brush? ResolveThemeBrush(string key)
        => ThemeBrushHelper.ResolveBrush(this, key);
}
