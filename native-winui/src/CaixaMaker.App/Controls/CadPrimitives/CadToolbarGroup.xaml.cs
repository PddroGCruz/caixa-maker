using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace CaixaMaker.App.Controls.CadPrimitives;

/// <summary>Agrupador Ribbon estilo Fusion 360 com controles na linha superior (28 px), título de seção na base (14 px) e divisória vertical de 38 px.</summary>
[ContentProperty(Name = nameof(GroupContent))]
public sealed partial class CadToolbarGroup : UserControl
{
    public static readonly DependencyProperty GroupLabelProperty = DependencyProperty.Register(
        nameof(GroupLabel),
        typeof(string),
        typeof(CadToolbarGroup),
        new PropertyMetadata(null, OnStateChanged));

    public static readonly DependencyProperty ShowSeparatorProperty = DependencyProperty.Register(
        nameof(ShowSeparator),
        typeof(bool),
        typeof(CadToolbarGroup),
        new PropertyMetadata(true, OnStateChanged));

    public static readonly DependencyProperty GroupContentProperty = DependencyProperty.Register(
        nameof(GroupContent),
        typeof(object),
        typeof(CadToolbarGroup),
        new PropertyMetadata(null, OnStateChanged));

    public CadToolbarGroup()
    {
        InitializeComponent();
        SyncVisualState();
    }

    public string? GroupLabel
    {
        get => (string?)GetValue(GroupLabelProperty);
        set => SetValue(GroupLabelProperty, value);
    }

    public bool ShowSeparator
    {
        get => (bool)GetValue(ShowSeparatorProperty);
        set => SetValue(ShowSeparatorProperty, value);
    }

    public object? GroupContent
    {
        get => GetValue(GroupContentProperty);
        set => SetValue(GroupContentProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadToolbarGroup group)
        {
            group.SyncVisualState();
        }
    }

    private void SyncVisualState()
    {
        LeadingSeparator.Visibility = ShowSeparator ? Visibility.Visible : Visibility.Collapsed;

        bool hasLabel = !string.IsNullOrWhiteSpace(GroupLabel);
        GroupLabelText.Visibility = hasLabel ? Visibility.Visible : Visibility.Collapsed;
        GroupLabelText.Text = hasLabel ? GroupLabel!.Trim().ToUpperInvariant() : string.Empty;

        GroupContentPresenter.Content = GroupContent;
    }
}
