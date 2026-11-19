namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Descreve uma violação de faixa ou invariante geométrica dos parâmetros da caixa.
/// </summary>
public sealed record BoxValidationIssue(string Code, string Parameter, string Message);

/// <summary>
/// Resultado consolidado da validação de <see cref="BoxParameters"/>.
/// </summary>
public sealed record BoxValidationResult(bool IsValid, IReadOnlyList<BoxValidationIssue> Issues)
{
    public static BoxValidationResult Success { get; } = new(true, Array.Empty<BoxValidationIssue>());

    /// <summary>Lança <see cref="ArgumentException"/> caso haja qualquer violação geométrica.</summary>
    public void ThrowIfInvalid()
    {
        if (IsValid || Issues.Count == 0)
        {
            return;
        }

        string combined = string.Join(" ", Issues.Select(issue => issue.Message));
        throw new ArgumentException(combined, Issues[0].Parameter);
    }
}

/// <summary>
/// Validador estrito de faixas numéricas e guardas geométricas dos parâmetros de caixa,
/// espelhando <c>BoxParametersRequest.validate_geometric_invariants</c> de <c>backend/server.py</c>.
/// </summary>
public static class BoxParametersValidator
{
    private const double MinSlotWidthMm = 0.1;
    private const double MinOuterMarginMm = 0.1;
    private const double MinAdjustedSegmentMm = 0.5;

    /// <summary>Valida limites numéricos e todas as guardas geométricas sem lançar exceção.</summary>
    public static BoxValidationResult Validate(BoxParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var issues = new List<BoxValidationIssue>();

        CheckFiniteAndRange(
            issues,
            "box_thickness",
            "Espessura",
            parameters.BoxThickness,
            BoxParameters.MinThickness,
            BoxParameters.MaxThickness);
        CheckFiniteAndRange(
            issues,
            "box_width",
            "Largura",
            parameters.BoxWidth,
            BoxParameters.MinWidth,
            BoxParameters.MaxWidth);
        CheckFiniteAndRange(
            issues,
            "box_depth",
            "Profundidade",
            parameters.BoxDepth,
            BoxParameters.MinDepth,
            BoxParameters.MaxDepth);
        CheckFiniteAndRange(
            issues,
            "box_height",
            "Altura",
            parameters.BoxHeight,
            BoxParameters.MinHeight,
            BoxParameters.MaxHeight);
        CheckFiniteAndRange(
            issues,
            "folga",
            "Folga",
            parameters.Folga,
            BoxParameters.MinFolga,
            BoxParameters.MaxFolga);
        CheckFiniteAndRange(
            issues,
            "target_finger_width_mm",
            "Largura alvo do dente",
            parameters.TargetFingerWidthMm,
            BoxParameters.MinTargetFingerWidthMm,
            BoxParameters.MaxTargetFingerWidthMm);
        if (parameters.FixedFingerCount < BoxParameters.MinFixedFingerCount ||
            parameters.FixedFingerCount > BoxParameters.MaxFixedFingerCount)
        {
            issues.Add(new BoxValidationIssue(
                "OUT_OF_RANGE",
                "fixed_finger_count",
                $"Quantidade fixa de espigas ({parameters.FixedFingerCount}) deve estar entre {BoxParameters.MinFixedFingerCount} e {BoxParameters.MaxFixedFingerCount}."));
        }

        if (issues.Count > 0)
        {
            return new BoxValidationResult(false, issues);
        }

        double t = parameters.BoxThickness;
        double w = parameters.BoxWidth;
        double d = parameters.BoxDepth;
        double h = parameters.BoxHeight;
        double folga = parameters.Folga;

        if (h <= t)
        {
            issues.Add(new BoxValidationIssue(
                "HEIGHT_BELOW_THICKNESS",
                "box_height",
                $"Altura ({h} mm) deve ser maior que a espessura da chapa ({t} mm)."));
        }

        if (w <= 2.0 * t)
        {
            issues.Add(new BoxValidationIssue(
                "WIDTH_BELOW_DOUBLE_THICKNESS",
                "box_width",
                $"Largura ({w} mm) deve ser maior que o dobro da espessura ({2.0 * t} mm)."));
        }

        if (d <= 2.0 * t)
        {
            issues.Add(new BoxValidationIssue(
                "DEPTH_BELOW_DOUBLE_THICKNESS",
                "box_depth",
                $"Profundidade ({d} mm) deve ser maior que o dobro da espessura ({2.0 * t} mm)."));
        }

        double slotWidth = t + folga;
        if (slotWidth < MinSlotWidthMm)
        {
            issues.Add(new BoxValidationIssue(
                "SLOT_WIDTH_TOO_SMALL",
                "folga",
                $"A folga negativa ({folga:+0.##;-0.##;0} mm) comprime o rasgo abaixo do mínimo seguro (espessura + folga = {slotWidth:F2} mm < 0.1 mm)."));
        }

        double outerMargin = t - folga / 2.0;
        if (outerMargin < MinOuterMarginMm)
        {
            issues.Add(new BoxValidationIssue(
                "OUTER_MARGIN_TOO_SMALL",
                "folga",
                $"A folga positiva ({folga:+0.##;-0.##;0} mm) reduz a margem entre o rasgo e a borda abaixo de 0.1 mm ({outerMargin:F2} mm)."));
        }

        if (h > t && w > 2.0 * t && d > 2.0 * t)
        {
            try
            {
                var nominalCalc = new BoxCalculator(parameters with { Folga = 0.0 });
                double verticalSegment = nominalCalc.GetVerticalJoints().ActualLength;
                double frontBottomSegment = nominalCalc.GetFrontBottomJoints().ActualLength;
                double sideBottomSegment = nominalCalc.GetSideBottomJoints().ActualLength;

                double minSegment = Math.Min(verticalSegment, Math.Min(frontBottomSegment, sideBottomSegment));
                if (minSegment - Math.Abs(folga) / 2.0 < MinAdjustedSegmentMm)
                {
                    issues.Add(new BoxValidationIssue(
                        "SEGMENT_TOO_SMALL_FOR_FOLGA",
                        "folga",
                        $"Folga ({folga:+0.##;-0.##;0} mm) é grande demais para o menor segmento de encaixe ({minSegment:F2} mm)."));
                }

                // Validação adicional do próprio BoxCalculator com folga aplicada (ex.: segmento de extremidade).
                _ = new BoxCalculator(parameters);
            }
            catch (ArgumentException ex)
            {
                issues.Add(new BoxValidationIssue("CALCULATOR_FOLGA_INVALID", "folga", ex.Message));
            }
        }

        return issues.Count == 0
            ? BoxValidationResult.Success
            : new BoxValidationResult(false, issues);
    }

    /// <summary>Valida os parâmetros e lança <see cref="ArgumentException"/> caso haja violações.</summary>
    public static void ValidateAndThrow(BoxParameters parameters)
    {
        Validate(parameters).ThrowIfInvalid();
    }

    /// <summary>Valida estritamente os parâmetros e instancia o <see cref="BoxCalculator"/>.</summary>
    public static BoxCalculator ValidateAndCreateCalculator(BoxParameters parameters)
    {
        ValidateAndThrow(parameters);
        return new BoxCalculator(parameters);
    }

    /// <summary>
    /// Verifica avisos não bloqueantes quando algum parâmetro ultrapassa as faixas recomendadas de interface.
    /// </summary>
    public static IReadOnlyList<BoxValidationIssue> GetRecommendedRangeAdvisories(BoxParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var advisories = new List<BoxValidationIssue>();

        if (parameters.BoxThickness > BoxParameters.RecommendedMaxThickness)
        {
            advisories.Add(new BoxValidationIssue(
                "THICKNESS_ABOVE_RECOMMENDED",
                "box_thickness",
                $"Espessura ({parameters.BoxThickness:F1} mm) acima da faixa recomendada para corte a laser padrão ({BoxParameters.RecommendedMaxThickness:F1} mm)."));
        }

        if (parameters.BoxWidth < BoxParameters.RecommendedMinWidth || parameters.BoxWidth > BoxParameters.RecommendedMaxWidth)
        {
            advisories.Add(new BoxValidationIssue(
                "WIDTH_OUTSIDE_RECOMMENDED",
                "box_width",
                $"Largura ({parameters.BoxWidth:F1} mm) fora da faixa recomendada de mesa ({BoxParameters.RecommendedMinWidth:F0}–{BoxParameters.RecommendedMaxWidth:F0} mm)."));
        }

        if (parameters.BoxDepth < BoxParameters.RecommendedMinDepth || parameters.BoxDepth > BoxParameters.RecommendedMaxDepth)
        {
            advisories.Add(new BoxValidationIssue(
                "DEPTH_OUTSIDE_RECOMMENDED",
                "box_depth",
                $"Profundidade ({parameters.BoxDepth:F1} mm) fora da faixa recomendada de mesa ({BoxParameters.RecommendedMinDepth:F0}–{BoxParameters.RecommendedMaxDepth:F0} mm)."));
        }

        if (parameters.BoxHeight < BoxParameters.RecommendedMinHeight || parameters.BoxHeight > BoxParameters.RecommendedMaxHeight)
        {
            advisories.Add(new BoxValidationIssue(
                "HEIGHT_OUTSIDE_RECOMMENDED",
                "box_height",
                $"Altura ({parameters.BoxHeight:F1} mm) fora da faixa recomendada ({BoxParameters.RecommendedMinHeight:F0}–{BoxParameters.RecommendedMaxHeight:F0} mm)."));
        }

        if (parameters.Folga < BoxParameters.RecommendedMinFolga || parameters.Folga > BoxParameters.RecommendedMaxFolga)
        {
            advisories.Add(new BoxValidationIssue(
                "FOLGA_OUTSIDE_RECOMMENDED",
                "folga",
                $"Folga ({parameters.Folga:+0.##;-0.##;0} mm) fora da faixa recomendada ({BoxParameters.RecommendedMinFolga:+0.#} a {BoxParameters.RecommendedMaxFolga:+0.#} mm)."));
        }

        return advisories;
    }

    private static void CheckFiniteAndRange(
        List<BoxValidationIssue> issues,
        string parameterName,
        string displayName,
        double value,
        double min,
        double max)
    {
        if (!double.IsFinite(value))
        {
            issues.Add(new BoxValidationIssue(
                "NON_FINITE",
                parameterName,
                $"{displayName} deve ser um número finito válido."));
            return;
        }

        if (value < min || value > max)
        {
            issues.Add(new BoxValidationIssue(
                "OUT_OF_RANGE",
                parameterName,
                $"{displayName} ({value} mm) deve estar entre {min} e {max} mm."));
        }
    }
}
