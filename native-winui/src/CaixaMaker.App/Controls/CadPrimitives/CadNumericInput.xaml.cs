using System.Globalization;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Input;
using CaixaMaker.Core.Geometry;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace CaixaMaker.App.Controls.CadPrimitives;

/// <summary>
/// Controle numérico compacto CAD (28 px) com fonte monoespaçada, avaliador de expressões,
/// trilha de slider interativa com thumb (8×8 px) na base e alça horizontal de <em>value scrubbing</em>.
/// </summary>
public sealed partial class CadNumericInput : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(CadNumericInput),
        new PropertyMetadata(0.0, OnValueDependencyChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(CadNumericInput),
        new PropertyMetadata(0.0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(CadNumericInput),
        new PropertyMetadata(1000.0, OnRangeChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step),
        typeof(double),
        typeof(CadNumericInput),
        new PropertyMetadata(1.0));

    public static readonly DependencyProperty DecimalPlacesProperty = DependencyProperty.Register(
        nameof(DecimalPlaces),
        typeof(int),
        typeof(CadNumericInput),
        new PropertyMetadata(1, OnRangeChanged));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit),
        typeof(string),
        typeof(CadNumericInput),
        new PropertyMetadata("mm", OnUnitChanged));

    public static readonly DependencyProperty ShowProgressBarProperty = DependencyProperty.Register(
        nameof(ShowProgressBar),
        typeof(bool),
        typeof(CadNumericInput),
        new PropertyMetadata(true, OnRangeChanged));

    private static readonly InputSystemCursor HorizontalResizeCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    private readonly NumericScrubSession _scrubSession = new();
    private bool _isEditing;
    private bool _isDraggingTrack;
    private bool _isScrubbing;
    private double _valueBeforeEdit;

    public event EventHandler<double>? ValueChanged;
    public event EventHandler? InteractionStarted;
    public event EventHandler? InteractionCompleted;

    public CadNumericInput()
    {
        InitializeComponent();
        SyncTextFromValue();
        SyncProgressVisual();
        SyncUnitVisual();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public int DecimalPlaces
    {
        get => (int)GetValue(DecimalPlacesProperty);
        set => SetValue(DecimalPlacesProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public bool ShowProgressBar
    {
        get => (bool)GetValue(ShowProgressBarProperty);
        set => SetValue(ShowProgressBarProperty, value);
    }

    /// <summary>Aplica programaticamente uma razão <c>[0, 1]</c> simulando o arraste da trilha de slider.</summary>
    public double SimulateSliderRatio(double ratio, bool fine = false)
    {
        SyncSessionParameters();
        double next = _scrubSession.SimulateSliderRatio(ratio, fine);
        ApplyEvaluatedValue(next);
        return Value;
    }

    /// <summary>Aplica programaticamente um incremento em passos simulando <em>value scrubbing</em> horizontal.</summary>
    public double SimulateScrubDelta(double deltaSteps, bool fine = false, bool fast = false)
    {
        SyncSessionParameters();
        double next = _scrubSession.SimulateScrubDelta(deltaSteps, fine, fast);
        ApplyEvaluatedValue(next);
        return Value;
    }

    internal void BeginExternalScrub(double startPointerX)
    {
        SyncSessionParameters();
        _isScrubbing = true;
        _scrubSession.BeginDrag(startPointerX, Value);
        UpdateScrubHighlight(active: true);
    }

    internal void UpdateExternalScrub(double currentPointerX, bool fine, bool fast)
    {
        if (!_isScrubbing) return;
        SyncSessionParameters();
        double next = _scrubSession.UpdateDragPixels(currentPointerX, fine, fast);
        ApplyEvaluatedValue(next);
    }

    internal void EndExternalScrub()
    {
        _isScrubbing = false;
        _scrubSession.EndDrag();
        UpdateScrubHighlight(active: false);
    }

    private void SyncSessionParameters()
    {
        _scrubSession.Minimum = Minimum;
        _scrubSession.Maximum = Maximum;
        _scrubSession.Step = Step;
        _scrubSession.DecimalPlaces = DecimalPlaces;
        _scrubSession.Value = Value;
    }

    private static void OnValueDependencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadNumericInput input)
        {
            if (!input._isEditing)
            {
                input.SyncTextFromValue();
            }
            input.SyncProgressVisual();
            input.ValueChanged?.Invoke(input, input.Value);
        }
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadNumericInput input)
        {
            if (!input._isEditing)
            {
                input.SyncTextFromValue();
            }
            input.SyncProgressVisual();
        }
    }

    private static void OnUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CadNumericInput input)
        {
            input.SyncUnitVisual();
        }
    }

    private void SyncUnitVisual()
    {
        bool hasUnit = !string.IsNullOrWhiteSpace(Unit);
        UnitTextBlock.Visibility = hasUnit ? Visibility.Visible : Visibility.Collapsed;
        UnitTextBlock.Text = Unit ?? string.Empty;
    }

    private void SyncTextFromValue()
    {
        int decimals = Math.Clamp(DecimalPlaces, 0, 4);
        string format = decimals == 0 ? "0" : "0." + new string('0', decimals);

        // Exibe fração extra se um ajuste fino tiver gerado subpasso exato (ex.: 0.5 em ângulo de passo 5°)
        if (decimals == 0 && Math.Abs(Value - Math.Round(Value)) > 1e-6)
        {
            format = "0.#";
        }

        ValueTextBox.Text = Value.ToString(format, CultureInfo.InvariantCulture);
    }

    private void SyncProgressVisual()
    {
        ProgressRow.Height = ShowProgressBar ? new GridLength(6) : new GridLength(0);
        ProgressTrackGrid.Visibility = ShowProgressBar ? Visibility.Visible : Visibility.Collapsed;
        OuterBorder.Height = ShowProgressBar ? 28 : 22;

        double trackWidth = ProgressTrackGrid.ActualWidth;
        if (trackWidth <= 0 || !ShowProgressBar)
        {
            return;
        }

        double usableWidth = Math.Max(1.0, trackWidth - 4.0);
        double ratio = NumericInputInteractionMath.ComputeRatio(Value, Minimum, Maximum);
        double fillWidth = Math.Round(usableWidth * ratio);
        ProgressFillBorder.Width = fillWidth;

        double thumbSpan = Math.Max(0.0, usableWidth - SliderThumb.Width);
        double thumbOffset = Math.Clamp(thumbSpan * ratio, 0.0, thumbSpan);
        SliderThumb.Margin = new Thickness(Math.Round(thumbOffset), 0, 0, 0);
    }

    private void ApplyEvaluatedValue(double evaluated)
    {
        _isEditing = false;
        FocusBorder.Visibility = Visibility.Collapsed;
        Value = evaluated;
        SyncTextFromValue();
        SyncProgressVisual();
    }

    private void SetClampedValue(double raw, double? stepToSnap = null)
    {
        if (double.IsNaN(raw) || double.IsInfinity(raw))
        {
            SyncTextFromValue();
            return;
        }

        double next = NumericInputInteractionMath.SnapAndClamp(raw, Minimum, Maximum, stepToSnap, DecimalPlaces);
        Value = next;
        SyncTextFromValue();
        SyncProgressVisual();
    }

    private void CommitPendingText()
    {
        if (CadMathExpressionEvaluator.TryEvaluate(ValueTextBox.Text, out double evaluated))
        {
            SetClampedValue(evaluated);
        }
        else
        {
            SyncTextFromValue();
        }
    }

    private void NudgeBy(double delta)
    {
        double baseVal = CadMathExpressionEvaluator.TryEvaluate(ValueTextBox.Text, out double evaluated)
            ? evaluated
            : Value;

        SetClampedValue(baseVal + delta);
    }

    private void OnTextBoxGotFocus(object sender, RoutedEventArgs e)
    {
        _isEditing = true;
        _valueBeforeEdit = Value;
        FocusBorder.Visibility = Visibility.Visible;
        ValueTextBox.SelectAll();
    }

    private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        _isEditing = false;
        FocusBorder.Visibility = Visibility.Collapsed;
        CommitPendingText();
    }

    private void OnTextBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        double step = Step > 0 ? Step : 1.0;

        switch (e.Key)
        {
            case VirtualKey.Enter:
                CommitPendingText();
                ValueTextBox.SelectAll();
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                Value = _valueBeforeEdit;
                SyncTextFromValue();
                ValueTextBox.SelectAll();
                e.Handled = true;
                break;

            case VirtualKey.Up:
                NudgeBy(step);
                e.Handled = true;
                break;

            case VirtualKey.Down:
                NudgeBy(-step);
                e.Handled = true;
                break;
        }
    }

    private void OnDecrementPressed(object sender, PointerRoutedEventArgs e)
    {
        double step = Step > 0 ? Step : 1.0;
        NudgeBy(-step);
        e.Handled = true;
    }

    private void OnIncrementPressed(object sender, PointerRoutedEventArgs e)
    {
        double step = Step > 0 ? Step : 1.0;
        NudgeBy(step);
        e.Handled = true;
    }

    private void OnStepPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border &&
            ThemeBrushHelper.ResolveBrush(this, "CmElevatedHoverSurfaceBrush") is Brush brush)
        {
            border.Background = brush;
        }
    }

    private void OnStepPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void OnProgressSizeChanged(object sender, SizeChangedEventArgs e) => SyncProgressVisual();

    private void OnProgressPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = HorizontalResizeCursor;
        SliderThumb.Width = 10;
        SliderThumb.Height = 10;
        SliderThumb.CornerRadius = new CornerRadius(5);
    }

    private void OnProgressPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingTrack && !_isScrubbing)
        {
            ProtectedCursor = null;
            SliderThumb.Width = 8;
            SliderThumb.Height = 8;
            SliderThumb.CornerRadius = new CornerRadius(4);
        }
    }

    private void OnProgressPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(ProgressTrackGrid).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDraggingTrack = true;
        ProtectedCursor = HorizontalResizeCursor;
        ProgressTrackGrid.CapturePointer(e.Pointer);
        InteractionStarted?.Invoke(this, EventArgs.Empty);
        ApplyTrackPosition(e.GetCurrentPoint(ProgressTrackGrid).Position.X);
        e.Handled = true;
    }

    private void OnProgressPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingTrack)
        {
            ApplyTrackPosition(e.GetCurrentPoint(ProgressTrackGrid).Position.X);
            e.Handled = true;
        }
    }

    private void OnProgressPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingTrack)
        {
            _isDraggingTrack = false;
            ProgressTrackGrid.ReleasePointerCapture(e.Pointer);
            ProtectedCursor = null;
            SliderThumb.Width = 8;
            SliderThumb.Height = 8;
            SliderThumb.CornerRadius = new CornerRadius(4);
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnProgressPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingTrack)
        {
            _isDraggingTrack = false;
            ProtectedCursor = null;
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyTrackPosition(double x)
    {
        double usableWidth = ProgressTrackGrid.ActualWidth - 4.0;
        if (usableWidth <= 0)
        {
            return;
        }

        double ratio = Math.Clamp((x - 2.0) / usableWidth, 0.0, 1.0);
        SimulateSliderRatio(ratio, fine: IsKeyDown(VirtualKey.Shift));
    }

    private void OnScrubPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = HorizontalResizeCursor;
        UpdateScrubHighlight(active: true);
    }

    private void OnScrubPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbing && !_isDraggingTrack)
        {
            ProtectedCursor = null;
            UpdateScrubHighlight(active: false);
        }
    }

    private void OnScrubPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(ScrubHandleBorder).Properties.IsLeftButtonPressed)
        {
            return;
        }

        InteractionStarted?.Invoke(this, EventArgs.Empty);
        BeginExternalScrub(e.GetCurrentPoint(this).Position.X);
        ProtectedCursor = HorizontalResizeCursor;
        ScrubHandleBorder.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnScrubPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isScrubbing)
        {
            UpdateExternalScrub(
                e.GetCurrentPoint(this).Position.X,
                fine: IsKeyDown(VirtualKey.Shift),
                fast: IsKeyDown(VirtualKey.Control));
            e.Handled = true;
        }
    }

    private void OnScrubPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isScrubbing)
        {
            ScrubHandleBorder.ReleasePointerCapture(e.Pointer);
            EndExternalScrub();
            ProtectedCursor = null;
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnScrubPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isScrubbing)
        {
            EndExternalScrub();
            ProtectedCursor = null;
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateScrubHighlight(bool active)
    {
        ScrubGripText.Opacity = active ? 1.0 : 0.65;
        string brushKey = active ? "CmAccentHighlightBrush" : "CmTextSecondaryBrush";
        if (ThemeBrushHelper.ResolveBrush(this, brushKey) is Brush brush)
        {
            ScrubGripText.Foreground = brush;
        }
    }

    private static bool IsKeyDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
}
