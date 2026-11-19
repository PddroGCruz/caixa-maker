using CaixaMaker.App.Helpers;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace CaixaMaker.App.Controls.CadPrimitives;

/// <summary>
/// Linha compacta de Property Grid (28 px) em duas colunas (Rótulo | Controle de edição),
/// com suporte automático a <em>value scrubbing</em> horizontal no rótulo quando associada a um <see cref="CadNumericInput"/>.
/// </summary>
[ContentProperty(Name = nameof(EditorContent))]
public sealed partial class CadPropertyRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(CadPropertyRow),
        new PropertyMetadata("Propriedade", OnStateChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(CadPropertyRow),
        new PropertyMetadata(null, OnStateChanged));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth),
        typeof(double),
        typeof(CadPropertyRow),
        new PropertyMetadata(112.0, OnStateChanged));

    public static readonly DependencyProperty ShowBottomDividerProperty = DependencyProperty.Register(
        nameof(ShowBottomDivider),
        typeof(bool),
        typeof(CadPropertyRow),
        new PropertyMetadata(true, OnStateChanged));

    public static readonly DependencyProperty EditorContentProperty = DependencyProperty.Register(
        nameof(EditorContent),
        typeof(object),
        typeof(CadPropertyRow),
        new PropertyMetadata(null, OnStateChanged));

    private static readonly InputSystemCursor HorizontalResizeCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    private CadNumericInput? _associatedNumericInput;
    private bool _isScrubbingLabel;

    public CadPropertyRow()
    {
        InitializeComponent();
        SyncVisualState();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public bool ShowBottomDivider
    {
        get => (bool)GetValue(ShowBottomDividerProperty);
        set => SetValue(ShowBottomDividerProperty, value);
    }

    public object? EditorContent
    {
        get => GetValue(EditorContentProperty);
        set => SetValue(EditorContentProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadPropertyRow row)
        {
            row.SyncVisualState();
        }
    }

    private void SyncVisualState()
    {
        LabelTextBlock.Text = Label ?? string.Empty;
        LabelColumn.Width = new GridLength(Math.Max(48, LabelWidth));

        _associatedNumericInput = FindAssociatedNumericInput(EditorContent, depth: 0);

        bool hasDescription = !string.IsNullOrWhiteSpace(Description);
        InfoBadge.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(InfoBadge, hasDescription ? Description : null);

        string? labelTip = _associatedNumericInput is not null
            ? hasDescription
                ? $"{Description}\nArraste horizontalmente no rótulo para ajustar (Shift: fino · Ctrl: rápido)."
                : "Arraste horizontalmente no rótulo para ajustar (Shift: fino · Ctrl: rápido)"
            : hasDescription ? Description : null;
        ToolTipService.SetToolTip(LabelTouchArea, labelTip);

        BottomDivider.Visibility = ShowBottomDivider ? Visibility.Visible : Visibility.Collapsed;
        EditorPresenter.Content = EditorContent;
    }

    private static CadNumericInput? FindAssociatedNumericInput(object? element, int depth)
    {
        if (element is null || depth > 2)
        {
            return null;
        }

        if (element is CadNumericInput direct)
        {
            return direct;
        }

        if (element is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (FindAssociatedNumericInput(child, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }
        else if (element is Border border)
        {
            return FindAssociatedNumericInput(border.Child, depth + 1);
        }

        return null;
    }

    private void OnLabelPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_associatedNumericInput is null)
        {
            return;
        }

        ProtectedCursor = HorizontalResizeCursor;
        ApplyLabelScrubVisual(active: true);
    }

    private void OnLabelPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbingLabel)
        {
            ProtectedCursor = null;
            ApplyLabelScrubVisual(active: false);
        }
    }

    private void OnLabelPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_associatedNumericInput is null ||
            !e.GetCurrentPoint(LabelTouchArea).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isScrubbingLabel = true;
        ProtectedCursor = HorizontalResizeCursor;
        ApplyLabelScrubVisual(active: true);
        LabelTouchArea.CapturePointer(e.Pointer);
        _associatedNumericInput.BeginExternalScrub(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnLabelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbingLabel || _associatedNumericInput is null)
        {
            return;
        }

        _associatedNumericInput.UpdateExternalScrub(
            e.GetCurrentPoint(this).Position.X,
            fine: IsKeyDown(VirtualKey.Shift),
            fast: IsKeyDown(VirtualKey.Control));
        e.Handled = true;
    }

    private void OnLabelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbingLabel)
        {
            return;
        }

        _isScrubbingLabel = false;
        LabelTouchArea.ReleasePointerCapture(e.Pointer);
        _associatedNumericInput?.EndExternalScrub();
        ProtectedCursor = null;
        ApplyLabelScrubVisual(active: false);
        e.Handled = true;
    }

    private void OnLabelPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isScrubbingLabel)
        {
            _isScrubbingLabel = false;
            _associatedNumericInput?.EndExternalScrub();
            ProtectedCursor = null;
            ApplyLabelScrubVisual(active: false);
        }
    }

    private void ApplyLabelScrubVisual(bool active)
    {
        string key = active ? "CmAccentHighlightBrush" : "CmTextSecondaryBrush";
        if (ThemeBrushHelper.ResolveBrush(this, key) is Brush brush)
        {
            LabelTextBlock.Foreground = brush;
        }
    }

    private static bool IsKeyDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
}
