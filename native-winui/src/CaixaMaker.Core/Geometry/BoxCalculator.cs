namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Resultado da discretização de uma aresta em dentes e vãos de finger joint.
/// </summary>
public readonly record struct JointDivision(int Count, double ActualLength);

/// <summary>
/// Núcleo matemático paramétrico da caixa com compensação bilateral de folga/kerf.
/// Porta integralmente <c>BoxCalculator</c> de <c>models.py</c> mantendo paridade exata.
/// </summary>
public sealed class BoxCalculator
{
    /// <summary>Fração da altura com dentes verticais (<c>JOINT_SPAN = 0.75</c>).</summary>
    public const double JointSpan = 0.75;

    /// <summary>Fração total da altura sem dentes (<c>1 - JOINT_SPAN = 0.25</c>).</summary>
    public const double JointlessSpan = 1.0 - JointSpan;

    /// <summary>Proporção-alvo padrão por segmento de junta (<c>TARGET_PROPORTION = 0.1</c>).</summary>
    public const double TargetProportion = 0.1;

    /// <summary>Constante estrutural herdada do módulo Python (<c>MIN_JOINT_LENGTH = 5.0</c>).</summary>
    public const double MinJointLength = 5.0;

    /// <summary>Comprimento mínimo padrão utilizado em <see cref="GetJointLength"/> (<c>min_length = 3.0</c>).</summary>
    public const double DefaultGetJointMinLength = 3.0;

    /// <summary>Espessura da chapa em milímetros.</summary>
    public double Thickness { get; }

    /// <summary>Largura nominal em milímetros.</summary>
    public double Width { get; }

    /// <summary>Profundidade nominal em milímetros.</summary>
    public double Depth { get; }

    /// <summary>Altura nominal em milímetros.</summary>
    public double Height { get; }

    /// <summary>Folga / kerf bilateral em milímetros.</summary>
    public double Folga { get; }

    /// <summary>Altura reduzida (<c>height - thickness</c>).</summary>
    public double DecreasedHeight { get; }

    /// <summary>Largura externa incrementada (<c>width + 2 * thickness</c>).</summary>
    public double IncreasedWidth { get; }

    /// <summary>Profundidade externa incrementada (<c>depth + 2 * thickness</c>).</summary>
    public double IncreasedDepth { get; }

    /// <summary>Largura interna livre (<c>width - 2 * thickness</c>).</summary>
    public double DecreasedWidth => Width - 2.0 * Thickness;

    /// <summary>Profundidade interna livre (<c>depth - 2 * thickness</c>).</summary>
    public double DecreasedDepth => Depth - 2.0 * Thickness;

    /// <summary>
    /// Inicializa uma instância de <see cref="BoxCalculator"/> e valida a folga sobre a geometria.
    /// </summary>
    public BoxCalculator(
        double boxThickness,
        double boxWidth,
        double boxDepth,
        double boxHeight,
        double folga = 0.0)
    {
        if (!double.IsFinite(boxThickness)
            || !double.IsFinite(boxWidth)
            || !double.IsFinite(boxDepth)
            || !double.IsFinite(boxHeight)
            || !double.IsFinite(folga))
        {
            throw new ArgumentException("Todos os parâmetros geométricos devem ser números finitos.");
        }

        Thickness = boxThickness;
        Width = boxWidth;
        Depth = boxDepth;
        Height = boxHeight;
        Folga = folga;

        DecreasedHeight = Height - Thickness;
        IncreasedWidth = Width + 2.0 * Thickness;
        IncreasedDepth = Depth + 2.0 * Thickness;

        ValidateFolga();
    }

    /// <summary>Inicializa uma instância de <see cref="BoxCalculator"/> a partir de <see cref="BoxParameters"/>.</summary>
    public BoxCalculator(BoxParameters parameters)
        : this(
            (parameters ?? throw new ArgumentNullException(nameof(parameters))).BoxThickness,
            parameters.BoxWidth,
            parameters.BoxDepth,
            parameters.BoxHeight,
            parameters.Folga)
    {
    }

    /// <summary>
    /// Valida invariantes geométricas da compensação bilateral de folga, espelhando <c>validate_folga</c>.
    /// </summary>
    public void ValidateFolga()
    {
        if (CalcSlotWidth() <= 0.0)
        {
            throw new ArgumentException(
                $"Folga negativa ({Folga} mm) é grande demais para a espessura ({Thickness} mm): a largura do rasgo resultante seria {CalcSlotWidth()} mm.");
        }

        if (Thickness - Folga / 2.0 <= 0.0)
        {
            throw new ArgumentException(
                $"Folga positiva ({Folga} mm) deixa material residual negativo ou nulo na margem do rasgo para espessura ({Thickness} mm).");
        }

        ReadOnlySpan<double> spans =
        [
            CalcHeightJointSpan(),
            Width - 2.0 * Thickness,
            Depth - 2.0 * Thickness,
        ];

        foreach (double span in spans)
        {
            if (span <= 0.0)
            {
                continue;
            }

            JointDivision division = GetJointLength(span);
            if (CalcTabLength(division.ActualLength) <= 0.0 || CalcSlotLength(division.ActualLength) <= 0.0)
            {
                throw new ArgumentException(
                    $"Folga ({Folga} mm) incompatível com o comprimento nominal do dente ({division.ActualLength:F2} mm).");
            }

            if (division.Count >= 3 && (division.ActualLength + Folga / 4.0) <= 0.0)
            {
                throw new ArgumentException(
                    $"Folga ({Folga} mm) elimina segmento de extremidade (passo {division.ActualLength:F2} mm).");
            }
        }
    }

    /// <summary>Retorna a largura efetiva dos rasgos (<c>thickness + folga</c>).</summary>
    public double CalcSlotWidth() => Thickness + Folga;

    /// <summary>Retorna o comprimento efetivo de uma espiga/dente (<c>nominalLength - folga / 2.0</c>).</summary>
    public double CalcTabLength(double nominalLength) => nominalLength - Folga / 2.0;

    /// <summary>Retorna o comprimento efetivo de um rasgo/vão (<c>nominalLength + folga / 2.0</c>).</summary>
    public double CalcSlotLength(double nominalLength) => nominalLength + Folga / 2.0;

    /// <summary>Retorna o comprimento total dedicado aos dentes verticais (<c>0.75 * decreased_height</c>).</summary>
    public double CalcHeightJointSpan() => JointSpan * DecreasedHeight;

    /// <summary>Retorna cada margem vertical sem dentes (<c>(decreased_height * 0.25) / 2</c>).</summary>
    public double CalcJointlessSpan() => (DecreasedHeight * JointlessSpan) / 2.0;

    /// <summary>
    /// Calcula a quantidade ímpar de divisões e o passo nominal ao longo de um trecho disponível,
    /// utilizando o mesmo arredondamento bancário half-to-even do Python 3.
    /// </summary>
    public JointDivision GetJointLength(
        double availableLength,
        double minLength = DefaultGetJointMinLength,
        double targetProportion = TargetProportion)
    {
        if (!double.IsFinite(availableLength) || availableLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableLength), availableLength, "O vão disponível deve ser positivo e finito.");
        }

        double idealLength = availableLength * targetProportion;
        if (idealLength < minLength)
        {
            idealLength = minLength;
        }

        int numJoints = PythonMath.RoundHalfToEven(availableLength / idealLength);
        if (numJoints < 1)
        {
            numJoints = 1;
        }

        if (numJoints % 2 == 0)
        {
            numJoints += 1;
        }

        double actualLength = availableLength / numJoints;
        return new JointDivision(numJoints, actualLength);
    }

    /// <summary>Calcula as divisões das juntas verticais (front/back ↔ laterais).</summary>
    public JointDivision GetVerticalJoints() => GetJointLength(CalcHeightJointSpan());

    /// <summary>Calcula as divisões da borda inferior frontal/traseira com a base.</summary>
    public JointDivision GetFrontBottomJoints() => GetJointLength(Width - 2.0 * Thickness);

    /// <summary>Calcula as divisões da borda inferior lateral com a base.</summary>
    public JointDivision GetSideBottomJoints() => GetJointLength(Depth - 2.0 * Thickness);
}
