using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Controls.CadPrimitives;

/// <summary>Botão técnico compacto (26×26 px em modo ícone ou 26 px de altura com texto curto) e tooltip "{Label} ({Shortcut})".</summary>
public sealed partial class CadToolbarButton : UserControl
{
    private static readonly SolidColorBrush PrimaryForegroundBrush = new(Microsoft.UI.Colors.White);

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(CadToolbarButton),
        new PropertyMetadata("Ação", OnStateChanged));

    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(
        nameof(Shortcut),
        typeof(string),
        typeof(CadToolbarButton),
        new PropertyMetadata(null, OnStateChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(CadToolbarButton),
        new PropertyMetadata("\uE710", OnStateChanged));

    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(
        nameof(ShowLabel),
        typeof(bool),
        typeof(CadToolbarButton),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(CadToolbarButton),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty IsDestructiveProperty = DependencyProperty.Register(
        nameof(IsDestructive),
        typeof(bool),
        typeof(CadToolbarButton),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty IsPrimaryProperty = DependencyProperty.Register(
        nameof(IsPrimary),
        typeof(bool),
        typeof(CadToolbarButton),
        new PropertyMetadata(false, OnStateChanged));

    private bool _isHovered;

    public event RoutedEventHandler? Click;

    public CadToolbarButton()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => SyncVisualState();
        IsEnabledChanged += (_, _) => SyncVisualState();
        SyncVisualState();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Shortcut
    {
        get => (string?)GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool ShowLabel
    {
        get => (bool)GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsDestructive
    {
        get => (bool)GetValue(IsDestructiveProperty);
        set => SetValue(IsDestructiveProperty, value);
    }

    public bool IsPrimary
    {
        get => (bool)GetValue(IsPrimaryProperty);
        set => SetValue(IsPrimaryProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadToolbarButton btn)
        {
            btn.SyncVisualState();
        }
    }

    private void SyncVisualState()
    {
        string glyphValue = Glyph ?? string.Empty;
        bool isPuaGlyph = glyphValue.Length == 1 && glyphValue[0] >= '\uE000' && glyphValue[0] <= '\uF8FF';

        GlyphIcon.Visibility = isPuaGlyph ? Visibility.Visible : Visibility.Collapsed;
        GlyphIcon.Glyph = isPuaGlyph ? glyphValue : string.Empty;

        TextGlyphBlock.Visibility = !isPuaGlyph && !string.IsNullOrEmpty(glyphValue)
            ? Visibility.Visible
            : Visibility.Collapsed;
        TextGlyphBlock.Text = isPuaGlyph ? string.Empty : glyphValue;

        LabelTextBlock.Visibility = ShowLabel && !string.IsNullOrWhiteSpace(Label)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LabelTextBlock.Text = Label ?? string.Empty;

        RootButton.Width = ShowLabel ? double.NaN : 26;
        RootButton.Opacity = IsEnabled ? 1.0 : 0.38;
        RootButton.IsHitTestVisible = IsEnabled;
        ContentStack.Margin = ShowLabel ? new Thickness(7, 0, 7, 0) : new Thickness(0);

        bool hovered = _isHovered && IsEnabled;
        PrimarySurface.Visibility = IsPrimary && !hovered ? Visibility.Visible : Visibility.Collapsed;
        PrimaryHoverSurface.Visibility = IsPrimary && hovered ? Visibility.Visible : Visibility.Collapsed;
        ActiveSurface.Visibility = !IsPrimary && IsActive ? Visibility.Visible : Visibility.Collapsed;
        DestructiveHoverSurface.Visibility = !IsPrimary && !IsActive && IsDestructive && hovered ? Visibility.Visible : Visibility.Collapsed;
        HoverSurface.Visibility = !IsPrimary && !IsActive && !IsDestructive && hovered ? Visibility.Visible : Visibility.Collapsed;

        Brush? fgBrush = IsPrimary
            ? PrimaryForegroundBrush
            : ResolveThemeBrush(
                IsDestructive
                    ? "CmStatusDangerBrush"
                    : IsActive
                        ? "CmAccentHighlightBrush"
                        : "CmTextPrimaryBrush");

        if (fgBrush is not null)
        {
            GlyphIcon.Foreground = fgBrush;
            TextGlyphBlock.Foreground = fgBrush;
            LabelTextBlock.Foreground = fgBrush;
        }

        string tooltip = string.IsNullOrWhiteSpace(Shortcut)
            ? Label ?? string.Empty
            : $"{Label} ({Shortcut})";
        ToolTipService.SetToolTip(RootButton, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RootButton, tooltip);
    }

    private Brush? ResolveThemeBrush(string key)
        => CaixaMaker.App.Helpers.ThemeBrushHelper.ResolveBrush(this, key);

    private void OnRootButtonClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);

    private void OnRootPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        SyncVisualState();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        SyncVisualState();
    }
}
