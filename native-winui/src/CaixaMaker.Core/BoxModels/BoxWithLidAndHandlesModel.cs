using System.Globalization;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.BoxModels;

/// <summary>
/// Modelo tradicional completo com tampa articulada, orelhas laterais e 6 chapas SVG.
/// </summary>
public sealed class BoxWithLidAndHandlesModel : BoxModelDefinitionBase
{
    public const string Id = "box_with_lid_and_handles";

    public override string ModelId => Id;
    public override string DisplayName => "Caixa com Tampa e Alças";

    public override string Description =>
        "Modelo completo com tampa articulada e alças laterais, indicado para "
        + "caixas fechadas e aplicações que exigem transporte ou armazenamento protegido.";

    public override IReadOnlyList<string> Features { get; } =
    [
        "Base com rasgos passantes",
        "Quatro painéis laterais",
        "Tampa articulada",
        "Alças laterais com eixo",
    ];

    public override bool HasLid => true;
    public override bool HasHandles => true;
    public override bool SupportsLidArticulation => true;
    public override int TotalPanels => 6;

    public override IReadOnlyList<string> SurfaceIds { get; } =
    [
        "front",
        "back",
        "left",
        "right",
        "lid",
        "bottom",
    ];

    public override BoxLayoutMetrics GetLayoutMetrics(BoxCalculator calc, BoxDrawer drawer)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        return drawer.GetLayoutMetrics();
    }

    public override IReadOnlyList<PartSummary> BuildPartsSummary(
        BoxCalculator calc,
        BoxDrawer drawer,
        BoxLayoutMetrics layout,
        JointsSummary joints,
        string folgaNote,
        double effectiveSlotWidth)
    {
        ArgumentNullException.ThrowIfNull(calc);
        ArgumentNullException.ThrowIfNull(layout);

        double t = calc.Thickness;
        double decreasedWidth = calc.Width - 2.0 * t;
        double lidDepth = layout.LidDepth ?? (calc.IncreasedDepth + 2.5 * t);

        string lidNotes = string.Format(
            CultureInfo.InvariantCulture,
            "Cobertura superior {0:F1} mm com pescoço traseiro {1:F1} mm e 2 pinos laterais ({2:F1} × {3:F1} mm, sobrando {4:F1} mm para fora de cada alça) articulados nos furos das laterais.",
            calc.IncreasedWidth,
            decreasedWidth,
            (1.0 + BoxDrawer.HingePinOverhangFactor) * t,
            t,
            BoxDrawer.HingePinOverhangFactor * t);

        var lidPart = new PartSummary
        {
            Id = "lid",
            Name = "Tampa articulada",
            Quantity = 1,
            WidthMm = PythonMath.Round(calc.IncreasedWidth, 3),
            HeightMm = PythonMath.Round(lidDepth, 3),
            ThicknessMm = PythonMath.Round(t, 3),
            Notes = lidNotes,
        };

        return
        [
            CreateFrontBackPart(calc, joints, folgaNote),
            CreateSidePart(calc, joints, effectiveSlotWidth),
            CreateBottomPart(calc, effectiveSlotWidth),
            lidPart,
        ];
    }
}
