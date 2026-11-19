using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.BoxModels;

/// <summary>
/// Contrato extensível para modelos paramétricos de caixa no Caixa Maker.
/// Porta integralmente <c>BoxModelDefinition</c> de <c>box_models/base.py</c>.
/// </summary>
public interface IBoxModelDefinition
{
    string ModelId { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<string> Features { get; }
    bool HasLid { get; }
    bool HasHandles { get; }
    bool SupportsLidArticulation { get; }
    int TotalPanels { get; }
    IReadOnlyList<string> SurfaceIds { get; }
    IReadOnlyList<string> SupportedParameters { get; }
    IReadOnlyDictionary<string, double> DefaultParameters { get; }

    BoxCalculator CreateCalculator(
        double boxThickness,
        double boxWidth,
        double boxDepth,
        double boxHeight,
        double folga = 0.0);

    BoxCalculator CreateCalculator(BoxParameters parameters);

    BoxDrawer CreateDrawer(BoxCalculator calc);

    BoxLayoutMetrics GetLayoutMetrics(BoxCalculator calc, BoxDrawer drawer);

    JointsSummary BuildJointsSummary(BoxCalculator calc);

    ComputedDimensionsSummary BuildComputedDimensions(
        BoxCalculator calc,
        BoxDrawer drawer,
        BoxLayoutMetrics layout);

    IReadOnlyList<PartSummary> BuildPartsSummary(
        BoxCalculator calc,
        BoxDrawer drawer,
        BoxLayoutMetrics layout,
        JointsSummary joints,
        string folgaNote,
        double effectiveSlotWidth);

    BoxCalculationSummary BuildSummary(BoxCalculator calc, BoxDrawer? drawer = null);

    BoxCalculationSummary BuildSummary(BoxParameters parameters);

    BoxSheetGeometry2D BuildSheetGeometry(BoxCalculator calc, BoxDrawer? drawer = null);

    BoxSheetGeometry2D BuildSheetGeometry(BoxParameters parameters);

    string DrawBoxSvg(BoxCalculator calc, BoxDrawer? drawer = null);

    string DrawBoxSvg(BoxParameters parameters);

    void ExportSvg(BoxCalculator calc, string outputPath, BoxDrawer? drawer = null);

    BoxModelCatalogItem ToCatalogItem();
}
