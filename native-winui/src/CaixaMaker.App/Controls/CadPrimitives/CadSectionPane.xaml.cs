using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Controls.CadPrimitives;

/// <summary>Seção técnica colapsável plana (estilo FreeCAD / Blender Properties) com cabeçalho de 26 px.</summary>
[ContentProperty(Name = nameof(BodyContent))]
public sealed partial class CadSectionPane : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(CadSectionPane),
        new PropertyMetadata("SEÇÃO", OnAppearanceChanged));

    public static readonly DependencyProperty BadgeTextProperty = DependencyProperty.Register(
        nameof(BadgeText),
        typeof(string),
        typeof(CadSectionPane),
        new PropertyMetadata(null, OnAppearanceChanged));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(CadSectionPane),
        new PropertyMetadata(true, OnAppearanceChanged));

    public static readonly DependencyProperty IsCollapsibleProperty = DependencyProperty.Register(
        nameof(IsCollapsible),
        typeof(bool),
        typeof(CadSectionPane),
        new PropertyMetadata(true, OnAppearanceChanged));

    public static readonly DependencyProperty HeaderActionContentProperty = DependencyProperty.Register(
        nameof(HeaderActionContent),
        typeof(object),
        typeof(CadSectionPane),
        new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty BodyContentProperty = DependencyProperty.Register(
        nameof(BodyContent),
        typeof(object),
        typeof(CadSectionPane),
        new PropertyMetadata(null, OnContentChanged));

    public CadSectionPane()
    {
        InitializeComponent();
        SyncVisualState();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? BadgeText
    {
        get => (string?)GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public bool IsCollapsible
    {
        get => (bool)GetValue(IsCollapsibleProperty);
        set => SetValue(IsCollapsibleProperty, value);
    }

    public object? HeaderActionContent
    {
        get => GetValue(HeaderActionContentProperty);
        set => SetValue(HeaderActionContentProperty, value);
    }

    public object? BodyContent
    {
        get => GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadSectionPane pane)
        {
            pane.SyncVisualState();
        }
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadSectionPane pane)
        {
            pane.HeaderActionPresenter.Content = pane.HeaderActionContent;
            pane.BodyPresenter.Content = pane.BodyContent;
        }
    }

    private void SyncVisualState()
    {
        TitleText.Text = string.IsNullOrWhiteSpace(Title)
            ? string.Empty
            : Title.Trim().ToUpperInvariant();

        bool hasBadge = !string.IsNullOrWhiteSpace(BadgeText);
        BadgeBorder.Visibility = hasBadge ? Visibility.Visible : Visibility.Collapsed;
        BadgeTextBlock.Text = BadgeText ?? string.Empty;

        ChevronText.Visibility = IsCollapsible ? Visibility.Visible : Visibility.Collapsed;
        ChevronText.Text = IsExpanded ? "▾" : "▸";
        BodyBorder.Visibility = (!IsCollapsible || IsExpanded) ? Visibility.Visible : Visibility.Collapsed;

        HeaderActionPresenter.Content = HeaderActionContent;
        BodyPresenter.Content = BodyContent;
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsCollapsible)
        {
            return;
        }

        // Evita colapsar a seção quando o usuário clica em um botão hospedado em HeaderActionPresenter.
        if (e.OriginalSource is DependencyObject src && IsInsideElement(src, HeaderActionPresenter))
        {
            return;
        }

        IsExpanded = !IsExpanded;
    }

    private void OnHeaderPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (IsCollapsible && CaixaMaker.App.Helpers.ThemeBrushHelper.ResolveBrush(this, "CmElevatedHoverSurfaceBrush") is Brush hoverBrush)
        {
            HeaderBorder.Background = hoverBrush;
        }
    }

    private void OnHeaderPointerExited(object sender, PointerRoutedEventArgs e)
    {
        HeaderBorder.ClearValue(Border.BackgroundProperty);
    }

    private static bool IsInsideElement(DependencyObject current, DependencyObject container)
    {
        DependencyObject? node = current;
        while (node is not null)
        {
            if (ReferenceEquals(node, container))
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }
}
