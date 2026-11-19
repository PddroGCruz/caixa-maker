namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Parâmetros geométricos nominais em milímetros para geração de uma caixa paramétrica.
/// Espelha o contrato <c>BoxParametersRequest</c> do backend v1.2.0.
/// </summary>
public sealed record BoxParameters
{
    public const double DefaultThickness = 3.0;
    public const double DefaultWidth = 200.0;
    public const double DefaultDepth = 150.0;
    public const double DefaultHeight = 100.0;
    public const double DefaultFolga = 0.0;

    public const double MinThickness = 0.5;
    public const double MaxThickness = 20.0;
    public const double RecommendedMaxThickness = 10.0;
    public const double StepThickness = 0.5;

    public const double MinWidth = 10.0;
    public const double MaxWidth = 2000.0;
    public const double RecommendedMinWidth = 50.0;
    public const double RecommendedMaxWidth = 500.0;

    public const double MinDepth = 10.0;
    public const double MaxDepth = 2000.0;
    public const double RecommendedMinDepth = 50.0;
    public const double RecommendedMaxDepth = 500.0;

    public const double MinHeight = 5.0;
    public const double MaxHeight = 2000.0;
    public const double RecommendedMinHeight = 20.0;
    public const double RecommendedMaxHeight = 300.0;

    public const double StepDimension = 1.0;

    public const double MinFolga = -2.0;
    public const double MaxFolga = 2.0;
    public const double RecommendedMinFolga = -1.0;
    public const double RecommendedMaxFolga = 1.0;
    public const double StepFolga = 0.05;

    public const double CentimetersToMillimetersFactor = 10.0;

    /// <summary>Espessura da chapa em milímetros (<c>box_thickness</c>).</summary>
    public double BoxThickness { get; init; } = DefaultThickness;

    /// <summary>Largura nominal em milímetros (<c>box_width</c>).</summary>
    public double BoxWidth { get; init; } = DefaultWidth;

    /// <summary>Profundidade nominal em milímetros (<c>box_depth</c>).</summary>
    public double BoxDepth { get; init; } = DefaultDepth;

    /// <summary>Altura nominal em milímetros (<c>box_height</c>).</summary>
    public double BoxHeight { get; init; } = DefaultHeight;

    /// <summary>Compensação bilateral de folga/kerf em milímetros (<c>folga</c>).</summary>
    public double Folga { get; init; } = DefaultFolga;

    /// <summary>Retorna a configuração padrão de caixa (3 mm, 200 × 150 × 100 mm, folga 0 mm).</summary>
    public static BoxParameters Default { get; } = new();

    /// <summary>
    /// Cria parâmetros a partir de largura, profundidade e altura em centímetros (fator 10,
    /// conforme <c>cli.py</c> da v1.2.0) com espessura e folga em milímetros.
    /// </summary>
    public static BoxParameters FromCentimeters(
        double boxThicknessMm,
        double boxWidthCm,
        double boxDepthCm,
        double boxHeightCm,
        double folgaMm = DefaultFolga)
    {
        return new BoxParameters
        {
            BoxThickness = boxThicknessMm,
            BoxWidth = boxWidthCm * CentimetersToMillimetersFactor,
            BoxDepth = boxDepthCm * CentimetersToMillimetersFactor,
            BoxHeight = boxHeightCm * CentimetersToMillimetersFactor,
            Folga = folgaMm,
        };
    }

    /// <summary>
    /// Converte todas as dimensões (incluindo espessura) de centímetros para milímetros por fator 10.
    /// </summary>
    public static BoxParameters FromAllCentimeters(
        double boxThicknessCm,
        double boxWidthCm,
        double boxDepthCm,
        double boxHeightCm,
        double folgaMm = DefaultFolga)
    {
        return new BoxParameters
        {
            BoxThickness = boxThicknessCm * CentimetersToMillimetersFactor,
            BoxWidth = boxWidthCm * CentimetersToMillimetersFactor,
            BoxDepth = boxDepthCm * CentimetersToMillimetersFactor,
            BoxHeight = boxHeightCm * CentimetersToMillimetersFactor,
            Folga = folgaMm,
        };
    }

    /// <summary>Exporta os parâmetros usando exatamente as chaves snake_case do contrato legado.</summary>
    public IReadOnlyDictionary<string, double> ToInputDictionary()
    {
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["box_thickness"] = PythonMath.Round(BoxThickness, 4),
            ["box_width"] = PythonMath.Round(BoxWidth, 4),
            ["box_depth"] = PythonMath.Round(BoxDepth, 4),
            ["box_height"] = PythonMath.Round(BoxHeight, 4),
            ["folga"] = PythonMath.Round(Folga, 4),
        };
    }
}
