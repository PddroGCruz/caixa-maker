using System.Globalization;

namespace CaixaMaker.App.Input;

/// <summary>
/// Matemática pura e determinística para a trilha interativa de slider
/// e para o ajuste contínuo horizontal (<em>value scrubbing</em>) do <c>CadNumericInput</c>.
/// </summary>
public static class NumericInputInteractionMath
{
    public const double PixelsPerStep = 4.0;
    public const double FineStepMultiplier = 0.1;
    public const double FastStepMultiplier = 5.0;

    /// <summary>Calcula a razão normalizada <c>[0, 1]</c> de um valor dentro de <c>[minimum, maximum]</c>.</summary>
    public static double ComputeRatio(double value, double minimum, double maximum)
    {
        (double min, double max) = NormalizeRange(minimum, maximum);
        double span = max - min;
        if (span <= 1e-9 || !double.IsFinite(value))
        {
            return 0.0;
        }

        return Math.Clamp((value - min) / span, 0.0, 1.0);
    }

    /// <summary>Resolve o incremento efetivo considerando modificadores <c>Shift</c> (fino) e <c>Ctrl</c> (rápido).</summary>
    public static double ResolveEffectiveStep(double step, bool fine = false, bool fast = false)
    {
        double baseStep = step > 0.0 && double.IsFinite(step) ? step : 1.0;
        double multiplier = fast ? FastStepMultiplier : (fine ? FineStepMultiplier : 1.0);
        return baseStep * multiplier;
    }

    /// <summary>
    /// Converte uma razão proporcional <c>[0, 1]</c> sobre a trilha do slider em um valor válido,
    /// respeitando limites <c>[minimum, maximum]</c> e quantização pelo passo.
    /// </summary>
    public static double EvaluateSliderRatio(
        double ratio,
        double minimum,
        double maximum,
        double step,
        int decimalPlaces,
        bool fine = false)
    {
        (double min, double max) = NormalizeRange(minimum, maximum);
        double clampedRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 0.0;
        double raw = min + (max - min) * clampedRatio;

        double? effectiveStep = step > 0.0 && double.IsFinite(step)
            ? ResolveEffectiveStep(step, fine: fine, fast: false)
            : null;

        return SnapAndClamp(raw, min, max, effectiveStep, decimalPlaces);
    }

    /// <summary>
    /// Aplica um deslocamento discreto em passos (<paramref name="deltaSteps"/>) no valor inicial
    /// simulando <em>value scrubbing</em> horizontal com modificadores normal/fino/rápido.
    /// </summary>
    public static double EvaluateScrubDelta(
        double currentValue,
        double deltaSteps,
        double minimum,
        double maximum,
        double step,
        int decimalPlaces,
        bool fine = false,
        bool fast = false)
    {
        if (!double.IsFinite(deltaSteps))
        {
            return SnapAndClamp(currentValue, minimum, maximum, null, decimalPlaces);
        }

        (double min, double max) = NormalizeRange(minimum, maximum);
        double safeCurrent = double.IsFinite(currentValue) ? Math.Clamp(currentValue, min, max) : min;
        double effectiveStep = ResolveEffectiveStep(step, fine: fine, fast: fast);
        double snapStep = Math.Min(ResolveEffectiveStep(step, fine: false, fast: false), effectiveStep);
        double raw = safeCurrent + deltaSteps * effectiveStep;
        return SnapAndClamp(raw, min, max, snapStep, decimalPlaces);
    }

    /// <summary>Converte um deslocamento em pixels de arraste horizontal no novo valor ajustado.</summary>
    public static double EvaluateScrubPixels(
        double currentValue,
        double deltaPx,
        double minimum,
        double maximum,
        double step,
        int decimalPlaces,
        bool fine = false,
        bool fast = false)
    {
        double deltaSteps = double.IsFinite(deltaPx) ? deltaPx / PixelsPerStep : 0.0;
        return EvaluateScrubDelta(currentValue, deltaSteps, minimum, maximum, step, decimalPlaces, fine, fast);
    }

    /// <summary>Arredonda ao passo opcional (relativo ao mínimo), limita ao intervalo e aplica casas decimais.</summary>
    public static double SnapAndClamp(
        double rawValue,
        double minimum,
        double maximum,
        double? stepToSnap,
        int decimalPlaces)
    {
        (double min, double max) = NormalizeRange(minimum, maximum);
        if (!double.IsFinite(rawValue))
        {
            return min;
        }

        double candidate = rawValue;
        int decimals = Math.Clamp(decimalPlaces, 0, 4);

        if (stepToSnap is > 1e-9 and < double.PositiveInfinity)
        {
            double s = stepToSnap.Value;
            candidate = Math.Round((candidate - min) / s, MidpointRounding.AwayFromZero) * s + min;
            decimals = Math.Clamp(Math.Max(decimals, CountSignificantDecimals(s)), 0, 4);
        }

        double clamped = Math.Clamp(candidate, min, max);
        double rounded = Math.Round(clamped, decimals, MidpointRounding.AwayFromZero);

        // Garante que o arredondamento final nunca ultrapasse [min, max]
        return Math.Clamp(rounded, min, max);
    }

    private static (double Min, double Max) NormalizeRange(double minimum, double maximum)
    {
        double safeMin = double.IsFinite(minimum) ? minimum : 0.0;
        double safeMax = double.IsFinite(maximum) ? maximum : safeMin;
        return safeMin <= safeMax ? (safeMin, safeMax) : (safeMax, safeMin);
    }

    private static int CountSignificantDecimals(double step)
    {
        string text = step.ToString("0.####", CultureInfo.InvariantCulture);
        int dotIdx = text.IndexOf('.');
        return dotIdx >= 0 ? text.Length - dotIdx - 1 : 0;
    }
}

/// <summary>
/// Controlador com estado para simular e executar slider proporcional e arraste incremental de <em>value scrubbing</em>.
/// </summary>
public sealed class NumericScrubSession
{
    private double _lastPointerX;
    private double _accumulatedValue;
    private bool _isDragging;

    public double Value { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100.0;
    public double Step { get; set; } = 1.0;
    public int DecimalPlaces { get; set; } = 1;

    public bool IsDragging => _isDragging;

    public double SimulateSliderRatio(double ratio, bool fine = false)
    {
        Value = NumericInputInteractionMath.EvaluateSliderRatio(ratio, Minimum, Maximum, Step, DecimalPlaces, fine);
        return Value;
    }

    public double SimulateScrubDelta(double deltaSteps, bool fine = false, bool fast = false)
    {
        Value = NumericInputInteractionMath.EvaluateScrubDelta(Value, deltaSteps, Minimum, Maximum, Step, DecimalPlaces, fine, fast);
        return Value;
    }

    public void BeginDrag(double startX, double initialValue)
    {
        _isDragging = true;
        _lastPointerX = startX;
        double min = Math.Min(Minimum, Maximum);
        double max = Math.Max(Minimum, Maximum);
        _accumulatedValue = double.IsFinite(initialValue) ? Math.Clamp(initialValue, min, max) : min;
        Value = _accumulatedValue;
    }

    public double UpdateDragPixels(double currentX, bool fine = false, bool fast = false)
    {
        if (!_isDragging)
        {
            return Value;
        }

        double dx = currentX - _lastPointerX;
        _lastPointerX = currentX;

        double min = Math.Min(Minimum, Maximum);
        double max = Math.Max(Minimum, Maximum);
        double effectiveStep = NumericInputInteractionMath.ResolveEffectiveStep(Step, fine, fast);
        double snapStep = Math.Min(NumericInputInteractionMath.ResolveEffectiveStep(Step, fine: false, fast: false), effectiveStep);

        _accumulatedValue = Math.Clamp(
            _accumulatedValue + (dx / NumericInputInteractionMath.PixelsPerStep) * effectiveStep,
            min,
            max);

        Value = NumericInputInteractionMath.SnapAndClamp(_accumulatedValue, min, max, snapStep, DecimalPlaces);
        return Value;
    }

    public void EndDrag() => _isDragging = false;
}
