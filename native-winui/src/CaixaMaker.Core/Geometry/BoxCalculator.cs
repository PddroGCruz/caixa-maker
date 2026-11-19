namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Resultado da discretização de uma aresta em dentes e vãos de finger joint.
/// </summary>
public readonly record struct JointDivision(int Count, double ActualLength, bool WasReducedByLimit = false);

/// <summary>
/// Núcleo matemático paramétrico da caixa com compensação bilateral de folga/kerf
/// e dimensionamento adaptativo unificado de encaixes Finger Joint (<see cref="FingerJointSizingMode"/>).
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

    /// <summary>Modo de dimensionamento dos encaixes Finger Joint.</summary>
    public FingerJointSizingMode FingerJointMode { get; }

    /// <summary>Largura-alvo desejada dos dentes em milímetros (modo <see cref="FingerJointSizingMode.TargetSizeMm"/>).</summary>
    public double TargetFingerWidthMm { get; }

    /// <summary>Quantidade-base de espigas por aresta (modo <see cref="FingerJointSizingMode.FixedCount"/>).</summary>
    public int FixedFingerCount { get; }

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
        double folga = 0.0,
        FingerJointSizingMode fingerJointMode = BoxParameters.DefaultFingerJointMode,
        double targetFingerWidthMm = BoxParameters.DefaultTargetFingerWidthMm,
        int fixedFingerCount = BoxParameters.DefaultFixedFingerCount)
    {
        if (!double.IsFinite(boxThickness)
            || !double.IsFinite(boxWidth)
            || !double.IsFinite(boxDepth)
            || !double.IsFinite(boxHeight)
            || !double.IsFinite(folga)
            || !double.IsFinite(targetFingerWidthMm))
        {
            throw new ArgumentException("Todos os parâmetros geométricos devem ser números finitos.");
        }

        Thickness = boxThickness;
        Width = boxWidth;
        Depth = boxDepth;
        Height = boxHeight;
        Folga = folga;
        FingerJointMode = fingerJointMode;
        TargetFingerWidthMm = Math.Clamp(targetFingerWidthMm, BoxParameters.MinTargetFingerWidthMm, BoxParameters.MaxTargetFingerWidthMm);
        FixedFingerCount = Math.Clamp(fixedFingerCount, BoxParameters.MinFixedFingerCount, BoxParameters.MaxFixedFingerCount);

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
            parameters.Folga,
            parameters.FingerJointMode,
            parameters.TargetFingerWidthMm,
            parameters.FixedFingerCount)
    {
    }

    /// <summary>
    /// Calcula a largura-alvo automática de dente proporcional à espessura da chapa
    /// (<c>Math.Clamp(2.2 * thicknessMm, 7.5, 16.0)</c>).
    /// </summary>
    public static double ComputeAutoTargetFingerWidthMm(double thicknessMm)
        => Math.Clamp(2.2 * thicknessMm, 7.5, 16.0);

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

        (double Span, bool IsBase)[] edges =
        [
            (CalcHeightJointSpan(), false),
            (Width - 2.0 * Thickness, true),
            (Depth - 2.0 * Thickness, true),
        ];

        foreach ((double span, bool isBase) in edges)
        {
            if (span <= 0.0)
            {
                continue;
            }

            JointDivision division = GetAdaptiveJointDivision(span, isBase);
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
    /// Calcula a quantidade ímpar de divisões e o passo nominal pelo algoritmo proporcional legado
    /// (<c>targetProportion = 0.1</c>, arredondamento bancário half-to-even).
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

    /// <summary>
    /// Calcula de forma determinística a discretização ímpar (<c>2*k + 1</c>) de uma aresta útil
    /// conforme <see cref="FingerJointMode"/> (<c>Auto</c>, <c>TargetSizeMm</c> ou <c>FixedCount</c>),
    /// mantendo simetria perfeita em relação ao centro da chapa e respeitando o mínimo estrutural (<c>&gt;= 3,0 mm</c>).
    /// </summary>
    public JointDivision GetAdaptiveJointDivision(double availableLength, bool isBaseEdge = false)
    {
        if (!double.IsFinite(availableLength) || availableLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableLength),
                availableLength,
                "O vão disponível deve ser positivo e finito.");
        }

        const double minStructuralFingerMm = DefaultGetJointMinLength; // 3.0 mm
        double minSafeSegment = Math.Max(minStructuralFingerMm, (Math.Abs(Folga) / 2.0) + 0.5);

        if (FingerJointMode == FingerJointSizingMode.FixedCount)
        {
            int requestedTabs = Math.Clamp(
                FixedFingerCount,
                BoxParameters.MinFixedFingerCount,
                BoxParameters.MaxFixedFingerCount);
            int requestedCount = (2 * requestedTabs) + 1;
            int count = requestedCount;

            while (count > 3 && (availableLength / count) < minSafeSegment)
            {
                count -= 2;
            }

            bool wasReduced = count < requestedCount;
            return new JointDivision(count, availableLength / count, wasReduced);
        }

        double targetMm = FingerJointMode == FingerJointSizingMode.TargetSizeMm
            ? Math.Clamp(TargetFingerWidthMm, BoxParameters.MinTargetFingerWidthMm, BoxParameters.MaxTargetFingerWidthMm)
            : ComputeAutoTargetFingerWidthMm(Thickness);

        // Na base, prefere ao menos 2 espigas (5 segmentos).
        // Nas verticais em modo Auto, também prefere ao menos 5 segmentos (2 espigas) sempre que a largura resultante
        // atingir >= Math.Max(4.0, Thickness), evitando efeito pivô em caixas pequenas; 3 segmentos ficam para caixas rasas.
        int minOddCount;
        if (isBaseEdge)
        {
            minOddCount = (availableLength / 5.0) >= minSafeSegment ? 5 : 3;
        }
        else if (FingerJointMode == FingerJointSizingMode.Auto)
        {
            double preferredVerticalMinMm = Math.Max(minSafeSegment, Math.Max(4.0, Thickness));
            minOddCount = (availableLength / 5.0) >= preferredVerticalMinMm ? 5 : 3;
        }
        else
        {
            minOddCount = 3;
        }

        int maxAllowed = (int)Math.Floor(availableLength / minSafeSegment);
        if (maxAllowed % 2 == 0)
        {
            maxAllowed -= 1;
        }

        if (maxAllowed < minOddCount)
        {
            int fallbackCount = Math.Max(3, maxAllowed);
            return new JointDivision(fallbackCount, availableLength / fallbackCount, WasReducedByLimit: false);
        }

        double rawIdealCount = availableLength / targetMm;
        int floorOdd = Math.Max(minOddCount, ((int)Math.Floor(rawIdealCount)) | 1);
        if (floorOdd > rawIdealCount && floorOdd - 2 >= minOddCount)
        {
            floorOdd -= 2;
        }

        int lowerOdd = Math.Clamp(floorOdd, minOddCount, maxAllowed);
        int upperOdd = Math.Clamp(lowerOdd + 2, minOddCount, maxAllowed);

        double lowerDiff = Math.Abs((availableLength / lowerOdd) - targetMm);
        double upperDiff = Math.Abs((availableLength / upperOdd) - targetMm);

        int bestCount = upperDiff <= lowerDiff ? upperOdd : lowerOdd;
        return new JointDivision(bestCount, availableLength / bestCount, WasReducedByLimit: false);
    }

    /// <summary>Calcula as divisões das juntas verticais (front/back ↔ laterais).</summary>
    public JointDivision GetVerticalJoints() => GetAdaptiveJointDivision(CalcHeightJointSpan(), isBaseEdge: false);

    /// <summary>Calcula as divisões da borda inferior frontal/traseira com a base.</summary>
    public JointDivision GetFrontBottomJoints() => GetAdaptiveJointDivision(Width - 2.0 * Thickness, isBaseEdge: true);

    /// <summary>Calcula as divisões da borda inferior lateral com a base.</summary>
    public JointDivision GetSideBottomJoints() => GetAdaptiveJointDivision(Depth - 2.0 * Thickness, isBaseEdge: true);
}
