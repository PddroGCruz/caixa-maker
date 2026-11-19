using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.BoxModels;

/// <summary>Metadados de catálogo de um modelo paramétrico de caixa.</summary>
public sealed record BoxModelCatalogItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Features { get; init; }
    public required bool HasLid { get; init; }
    public required bool HasHandles { get; init; }
    public required bool SupportsLidArticulation { get; init; }
    public required int TotalPanels { get; init; }
    public required IReadOnlyList<string> SurfaceIds { get; init; }
    public required IReadOnlyList<string> SupportedParameters { get; init; }
    public required IReadOnlyDictionary<string, double> DefaultParameters { get; init; }
}

/// <summary>Especificação numérica de um conjunto de encaixes finger joint.</summary>
public sealed record JointSpec
{
    public required int Count { get; init; }
    public required double SegmentLengthMm { get; init; }
    public required double TabLengthMm { get; init; }
    public required double SlotLengthMm { get; init; }
    public required int TabCount { get; init; }
    public required int SlotCount { get; init; }
    public required double AvailableSpanMm { get; init; }
    public bool WasReducedByLimit { get; init; }
}

/// <summary>Agrupamento dos três grupos de encaixe da caixa.</summary>
public sealed record JointsSummary
{
    public required JointSpec Vertical { get; init; }
    public required JointSpec FrontBottom { get; init; }
    public required JointSpec SideBottom { get; init; }
}

/// <summary>Resumo dimensional e de fabricação de uma chapa ou par de chapas.</summary>
public sealed record PartSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Quantity { get; init; }
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }
    public required double ThicknessMm { get; init; }
    public required string Notes { get; init; }

    /// <summary>Área retangular estimada para corte desta peça (considerando quantidade), em cm².</summary>
    public double EstimatedAreaCm2 => (WidthMm * HeightMm * Quantity) / 100.0;
}

/// <summary>Dimensões derivadas calculadas em milímetros.</summary>
public sealed record ComputedDimensionsSummary
{
    public required double IncreasedWidth { get; init; }
    public required double IncreasedDepth { get; init; }
    public required double DecreasedHeight { get; init; }
    public required double InnerWidth { get; init; }
    public required double InnerDepth { get; init; }
    public required double InnerHeight { get; init; }
    public required double JointlessSpan { get; init; }
    public required double VerticalJointSpan { get; init; }
    public required double OuterWidthWithFlange { get; init; }
    public required double OuterDepthWithHinge { get; init; }
    public required double TotalHeightNominal { get; init; }
    public required double TotalHeightWithLid { get; init; }
    public required double FolgaMm { get; init; }
    public required double EffectiveSlotWidthMm { get; init; }
    public double? HingeHoleRadius { get; init; }
    public double? LidWidth { get; init; }
    public double? LidDepth { get; init; }
    public double VerticalFingerWidthMm { get; init; }
    public double BaseFrontFingerWidthMm { get; init; }
    public double BaseSideFingerWidthMm { get; init; }
    public int VerticalFingerCount { get; init; }
    public int BaseFrontFingerCount { get; init; }
    public int BaseSideFingerCount { get; init; }
}

/// <summary>Dimensões totais da folha SVG gerada em milímetros.</summary>
public sealed record SvgCanvasSummary
{
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }
    public required double SingleColumnWidthMm { get; init; }
}

/// <summary>
/// Resumo dimensional completo da caixa para inspeção, preview, exportação e validação CNC.
/// Espelha integralmente <c>BoxCalculationSummary</c> de <c>backend/server.py</c> e o cálculo de área de corte.
/// </summary>
public sealed record BoxCalculationSummary
{
    public required string ModelId { get; init; }
    public required string ModelName { get; init; }
    public required IReadOnlyDictionary<string, double> InputParameters { get; init; }
    public required ComputedDimensionsSummary ComputedDimensions { get; init; }
    public required JointsSummary Joints { get; init; }
    public required IReadOnlyList<PartSummary> Parts { get; init; }
    public required SvgCanvasSummary SvgCanvas { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Soma das áreas das peças em cm² (<c>(width_mm * height_mm * quantity) / 100</c>),
    /// idêntico ao painel direito de modelagem da v1.2.0.
    /// </summary>
    public double EstimatedCuttingAreaCm2 { get; init; }

    /// <summary>Perímetro linear total do contorno externo de todas as peças, em milímetros.</summary>
    public double OuterCutPerimeterMm { get; init; }

    /// <summary>Perímetro linear total dos recortes e furos internos de todas as peças, em milímetros.</summary>
    public double InternalCutPerimeterMm { get; init; }

    /// <summary>Perímetro linear total de corte (contornos externos + recortes/furos internos), em milímetros.</summary>
    public double TotalCutPerimeterMm { get; init; }

    /// <summary>Perímetro linear total de corte em metros (<c>TotalCutPerimeterMm / 1000</c>).</summary>
    public double TotalCutPerimeterMeters => PythonMath.Round(TotalCutPerimeterMm / 1000.0, 3);
}
