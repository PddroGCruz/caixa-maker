using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaixaMaker.App.Controls.CadPrimitives;

public enum CadBadgeKind
{
    Neutral,
    Valid,
    Warning,
    Error,
    Dirty,
}

/// <summary>Badge plano técnico (Height=20, CornerRadius=2) com indicador semântico.</summary>
public sealed partial class CadStatusBadge : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(CadBadgeKind),
        typeof(CadStatusBadge),
        new PropertyMetadata(CadBadgeKind.Neutral, OnStateChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(CadStatusBadge),
        new PropertyMetadata("PRONTO", OnStateChanged));

    public CadStatusBadge()
    {
        InitializeComponent();
        SyncVisualState();
    }

    public CadBadgeKind Kind
    {
        get => (CadBadgeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadStatusBadge badge)
        {
            badge.SyncVisualState();
        }
    }

    private void SyncVisualState()
    {
        string value = Text ?? string.Empty;
        NeutralText.Text = value;
        ValidText.Text = value;
        WarningText.Text = value;
        ErrorText.Text = value;

        NeutralBorder.Visibility = Kind == CadBadgeKind.Neutral ? Visibility.Visible : Visibility.Collapsed;
        ValidBorder.Visibility = Kind == CadBadgeKind.Valid ? Visibility.Visible : Visibility.Collapsed;
        WarningBorder.Visibility = (Kind == CadBadgeKind.Warning || Kind == CadBadgeKind.Dirty)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ErrorBorder.Visibility = Kind == CadBadgeKind.Error ? Visibility.Visible : Visibility.Collapsed;
    }
}
