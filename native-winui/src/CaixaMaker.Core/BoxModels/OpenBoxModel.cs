using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.BoxModels;

/// <summary>
/// Modelo aberto composto apenas pela base e quatro painéis laterais sem tampa ou alças (5 chapas).
/// </summary>
public sealed class OpenBoxModel : BoxModelDefinitionBase
{
    public const string Id = "open_box";

    public override string ModelId => Id;
    public override string DisplayName => "Caixa Aberta";

    public override string Description =>
        "Modelo aberto composto por uma base e quatro painéis laterais, sem tampa ou "
        + "alças, indicado para armazenamento, organização e diferentes aplicações de fabricação.";

    public override IReadOnlyList<string> Features { get; } =
    [
        "Base com rasgos passantes",
        "Quatro painéis laterais",
        "Abertura superior livre",
        "Encaixes Finger Joint",
    ];

    public override bool HasLid => false;
    public override bool HasHandles => false;
    public override bool SupportsLidArticulation => false;
    public override int TotalPanels => 5;

    public override IReadOnlyList<string> SurfaceIds { get; } =
    [
        "front",
        "back",
        "left",
        "right",
        "bottom",
    ];

    /// <summary>
    /// Organiza exclusivamente as 5 chapas (base + 4 paredes) sem espaço de tampa ou orelhas.
    /// </summary>
    public override BoxLayoutMetrics GetLayoutMetrics(BoxCalculator calc, BoxDrawer drawer)
    {
        ArgumentNullException.ThrowIfNull(calc);

        const double margin = BoxDrawer.IncrementedMargin;
        const double start = BoxDrawer.StartingCoordinate;
        double sideWidth = calc.IncreasedDepth;

        double colWidth = Math.Max(calc.IncreasedWidth, sideWidth) + margin;
        double sheetWidth = 2.0 * colWidth;

        double row1Height = calc.IncreasedDepth + margin;
        double row2Height = calc.Height + margin;
        double row3Height = calc.Height + margin;
        double sheetHeight = row1Height + row2Height + row3Height;

        double centralizeBottom = (sheetWidth - calc.IncreasedWidth - 2.0 * start) / 2.0;
        double centralizeFront = (colWidth - calc.Width - 2.0 * start) / 2.0;
        double centralizeSide = (colWidth - sideWidth - 2.0 * start) / 2.0;

        double row2Y = row1Height;
        double row3Y = row1Height + row2Height;
        double sideMirrorSpan = (2.0 * start) + sideWidth;

        var orderedPanels = new PanelPlacement2D[]
        {
            new("bottom", centralizeBottom, 0.0),
            new("front", centralizeFront, row2Y),
            new("back", colWidth + centralizeFront, row2Y),
            new("right", centralizeSide, row3Y),
            new("left", colWidth + centralizeSide, row3Y, MirrorX: true, MirrorSpanWidth: sideMirrorSpan),
        };

        var panelMap = orderedPanels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);

        return new BoxLayoutMetrics
        {
            CanvasWidth = colWidth,
            CanvasHeight = sheetHeight,
            SheetWidth = sheetWidth,
            SheetHeight = sheetHeight,
            LidDepth = null,
            SideWidth = sideWidth,
            Row1Height = row1Height,
            Row2Height = row2Height,
            Row3TopClearance = 0.0,
            Row3Height = row3Height,
            OrderedPanels = orderedPanels,
            Panels = panelMap,
        };
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

        return
        [
            CreateFrontBackPart(calc, joints, folgaNote),
            CreateSidePart(calc, joints, effectiveSlotWidth),
            CreateBottomPart(calc, effectiveSlotWidth),
        ];
    }
}
