using System.Globalization;
using System.Security;
using System.Text;

namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Geometria de um rasgo com compensação de folga centrada no rasgo nominal.
/// </summary>
public readonly record struct SlotGeometry(
    double SlotLength,
    double SlotWidth,
    double OffsetLength,
    double OffsetWidth);

/// <summary>
/// Desenhador vetorial paramétrico e gerador de geometria 2D/SVG 1:1 em milímetros.
/// Porta integralmente <c>BoxDrawer</c> de <c>models.py</c> sem dependências externas de SVG.
/// </summary>
public sealed class BoxDrawer
{
    public const double IncrementedMargin = 10.0;
    public const string StrokeColor = "black";
    public const string StrokeWidth = "0.03mm";
    public const string FillStyle = "none";
    public const double StartingCoordinate = 5.0;
    public const int StartingCoordinateInt = 5;
    public const double HoleFactor = 0.80;
    public const double HingePinOverhangFactor = 1.0;

    private readonly int _arcSegmentsPerQuarter;

    public BoxCalculator Calc { get; }

    public BoxDrawer(BoxCalculator calculator, int arcSegmentsPerQuarter = 16)
    {
        Calc = calculator ?? throw new ArgumentNullException(nameof(calculator));
        if (arcSegmentsPerQuarter < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(arcSegmentsPerQuarter));
        }

        _arcSegmentsPerQuarter = arcSegmentsPerQuarter;
    }

    /// <summary>
    /// Calcula as dimensões da folha SVG e origens padrão (modelo completo com 6 chapas, tampa e alças),
    /// espelhando <c>BoxDrawer.get_layout_metrics</c>.
    /// </summary>
    public BoxLayoutMetrics GetLayoutMetrics()
    {
        double thickness = Calc.Thickness;
        double lidDepth = Calc.IncreasedDepth + 2.5 * thickness;
        double sideWidth = Calc.IncreasedDepth + 4.0 * thickness;

        double canvasWidth = CalculateCanvasSize(
            Math.Max(Calc.IncreasedWidth, sideWidth),
            Calc.IncreasedDepth);
        double row1Height = lidDepth + IncrementedMargin;
        double row2Height = Calc.Height + IncrementedMargin;
        double row3TopClearance = 2.5 * thickness;
        double row3Height = Calc.Height + row3TopClearance + IncrementedMargin;
        double canvasHeight = row1Height + row2Height + row3Height;

        double centralizeBottom = (canvasWidth - Calc.IncreasedWidth - 2.0 * StartingCoordinate) / 2.0;
        double centralizeFront = (canvasWidth - Calc.Width - 2.0 * StartingCoordinate) / 2.0;
        double centralizeSide = (canvasWidth - sideWidth - 2.0 * StartingCoordinate) / 2.0;

        double row2Y = row1Height;
        double row3Y = row1Height + row2Height + row3TopClearance;

        double sideMirrorSpan = (2.0 * StartingCoordinate) + Calc.IncreasedDepth + (4.0 * thickness);

        var orderedPanels = new PanelPlacement2D[]
        {
            new("bottom", centralizeBottom, 0.0),
            new("lid", canvasWidth + centralizeBottom, 0.0),
            new("front", centralizeFront, row2Y),
            new("back", canvasWidth + centralizeFront, row2Y),
            new("right", centralizeSide, row3Y),
            new("left", canvasWidth + centralizeSide, row3Y, MirrorX: true, MirrorSpanWidth: sideMirrorSpan),
        };

        var panelMap = orderedPanels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);

        return new BoxLayoutMetrics
        {
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
            SheetWidth = 2.0 * canvasWidth,
            SheetHeight = canvasHeight,
            LidDepth = lidDepth,
            SideWidth = sideWidth,
            Row1Height = row1Height,
            Row2Height = row2Height,
            Row3TopClearance = row3TopClearance,
            Row3Height = row3Height,
            OrderedPanels = orderedPanels,
            Panels = panelMap,
        };
    }

    internal double CalculateCanvasSize(double widthOrHeight, double depth)
    {
        return widthOrHeight > depth
            ? widthOrHeight + IncrementedMargin
            : depth + (4.0 * Calc.Thickness) + IncrementedMargin;
    }

    /// <summary>
    /// Retorna o comprimento individual de um segmento de finger joint mantendo invariante o vão total.
    /// </summary>
    public double GetJointSegmentLength(int index, int numJoints, double nominalLength, double folga = 0.0)
    {
        if (folga == 0.0 || numJoints <= 1)
        {
            return nominalLength;
        }

        double halfFolga = folga / 2.0;
        if (index % 2 == 1)
        {
            return PythonMath.Round(nominalLength - halfFolga, 6);
        }

        if (index == 0 || index == numJoints - 1)
        {
            return PythonMath.Round(nominalLength + halfFolga / 2.0, 6);
        }

        return PythonMath.Round(nominalLength + halfFolga, 6);
    }

    /// <summary>
    /// Retorna dimensões e deslocamentos centrados para desenho de um rasgo com compensação de folga.
    /// </summary>
    public SlotGeometry GetSlotGeometry(double jointLength, double thickness, double folga = 0.0)
    {
        if (folga == 0.0)
        {
            return new SlotGeometry(jointLength, thickness, 0.0, 0.0);
        }

        return new SlotGeometry(
            PythonMath.Round(jointLength + folga / 2.0, 6),
            PythonMath.Round(thickness + folga, 6),
            PythonMath.Round(folga / 4.0, 6),
            PythonMath.Round(folga / 2.0, 6));
    }

    /// <summary>Constrói a geometria 2D completa e os caminhos SVG de um painel individual.</summary>
    public PanelGeometry2D BuildPanelGeometry(
        string panelId,
        double xPosition,
        double yPosition,
        bool hasHingeEar = true)
    {
        bool mirrorX = string.Equals(panelId, "left", StringComparison.Ordinal);
        double mirrorSpan = mirrorX ? ComputeSideMirrorSpan(hasHingeEar) : 0.0;
        var placement = new PanelPlacement2D(panelId, xPosition, yPosition, mirrorX, mirrorSpan);

        return panelId switch
        {
            "bottom" => BuildBottomPanel(placement),
            "lid" => BuildLidPanel(placement),
            "front" or "back" => BuildFrontPanel(placement),
            "right" or "left" => BuildSidePanel(placement, hasHingeEar),
            _ => throw new ArgumentException($"Painel desconhecido: {panelId}", nameof(panelId)),
        };
    }

    /// <summary>Constrói a geometria 2D de um painel usando as coordenadas de um layout calculado.</summary>
    public PanelGeometry2D BuildPanelGeometry(
        string panelId,
        BoxLayoutMetrics layout,
        bool hasHingeEar = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!layout.Panels.TryGetValue(panelId, out PanelPlacement2D? placement))
        {
            throw new ArgumentException($"Painel '{panelId}' não encontrado no layout fornecido.", nameof(panelId));
        }

        return panelId switch
        {
            "bottom" => BuildBottomPanel(placement),
            "lid" => BuildLidPanel(placement),
            "front" or "back" => BuildFrontPanel(placement),
            "right" or "left" => BuildSidePanel(placement, hasHingeEar),
            _ => throw new ArgumentException($"Painel desconhecido: {panelId}", nameof(panelId)),
        };
    }

    /// <summary>Constrói todas as peças 2D da caixa em memória para o layout especificado (ou o padrão).</summary>
    public BoxSheetGeometry2D BuildSheetGeometry(
        BoxLayoutMetrics? customLayout = null,
        bool hasHingeEar = true,
        string modelId = "box_with_lid_and_handles")
    {
        BoxLayoutMetrics layout = customLayout ?? GetLayoutMetrics();
        var panels = new List<PanelGeometry2D>(layout.OrderedPanels.Count);

        foreach (PanelPlacement2D placement in layout.OrderedPanels)
        {
            panels.Add(BuildPanelGeometry(placement.PanelId, layout, hasHingeEar));
        }

        return new BoxSheetGeometry2D
        {
            ModelId = modelId,
            Layout = layout,
            Panels = panels,
            PanelsById = panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal),
        };
    }

    /// <summary>Gera o documento SVG completo 1:1 em milímetros em memória.</summary>
    public string DrawBoxSvg(
        BoxLayoutMetrics? customLayout = null,
        bool hasHingeEar = true,
        string modelId = "box_with_lid_and_handles")
    {
        BoxSheetGeometry2D sheet = BuildSheetGeometry(customLayout, hasHingeEar, modelId);
        return RenderSheetSvg(sheet);
    }

    /// <summary>Salva o documento SVG completo 1:1 em milímetros em disco (UTF-8).</summary>
    public void SaveBoxSvg(
        string outputPath,
        BoxLayoutMetrics? customLayout = null,
        bool hasHingeEar = true,
        string modelId = "box_with_lid_and_handles")
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("O caminho de saída não pode estar vazio.", nameof(outputPath));
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string svgContent = DrawBoxSvg(customLayout, hasHingeEar, modelId);
        File.WriteAllText(outputPath, svgContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Renderiza um <see cref="BoxSheetGeometry2D"/> pré-construído no formato XML/SVG exato do <c>svgwrite</c>.</summary>
    public static string RenderSheetSvg(BoxSheetGeometry2D sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        string widthAttr = PythonMath.FormatFloat(sheet.Layout.SheetWidth);
        string heightAttr = PythonMath.FormatFloat(sheet.Layout.SheetHeight);

        var sb = new StringBuilder(16_384);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg baseProfile=\"full\" height=\"{heightAttr}mm\" version=\"1.1\" viewBox=\"0 0 {widthAttr} {heightAttr}\" width=\"{widthAttr}mm\" xmlns=\"http://www.w3.org/2000/svg\" xmlns:ev=\"http://www.w3.org/2001/xml-events\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><defs />");

        foreach (PanelGeometry2D panel in sheet.Panels)
        {
            AppendPanelSvg(sb, panel);
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    internal double ComputeSideMirrorSpan(bool hasHingeEar)
        => (2.0 * StartingCoordinate) + Calc.IncreasedDepth + (hasHingeEar ? 4.0 * Calc.Thickness : 0.0);

    private PanelGeometry2D BuildFrontPanel(PanelPlacement2D placement)
    {
        double t = Calc.Thickness;
        double folga = Calc.Folga;
        double frontWidth = Calc.Width;
        double jointlessSpan = Calc.CalcJointlessSpan();
        double verticalSpan = Calc.CalcHeightJointSpan();
        double decreasedWidth = frontWidth - 2.0 * t;

        JointDivision vertical = Calc.GetJointLength(verticalSpan);
        JointDivision horizontal = Calc.GetJointLength(decreasedWidth);

        var path = new SvgPathBuilder(_arcSegmentsPerQuarter);
        path.MoveToAbs(StartingCoordinate + t, StartingCoordinate, svgY: StartingCoordinateInt.ToString(CultureInfo.InvariantCulture));
        path.HorizontalBy(decreasedWidth);
        path.VerticalBy(jointlessSpan);
        AppendVerticalJoints(path, vertical.Count, vertical.ActualLength, t, direction: 0, folga);
        path.VerticalBy(jointlessSpan);
        AppendHorizontalJoints(path, horizontal.Count, horizontal.ActualLength, t, folga);
        path.VerticalBy(-jointlessSpan, $"-{PythonMath.FormatFloat(jointlessSpan)}");
        AppendVerticalJoints(path, vertical.Count, vertical.ActualLength, t, direction: 1, folga);
        path.ClosePath();

        Polygon2D outer = path.BuildPolygons().Single();

        return new PanelGeometry2D
        {
            PanelId = placement.PanelId,
            SvgElementId = "front",
            OuterContour = outer,
            InternalHoles = Array.Empty<Polygon2D>(),
            CircularHoles = Array.Empty<Circle2D>(),
            Placement = placement,
            OutlinePathData = path.PathData,
            HolesPathData = null,
        };
    }

    private PanelGeometry2D BuildSidePanel(PanelPlacement2D placement, bool hasHingeEar)
    {
        double t = Calc.Thickness;
        double folga = Calc.Folga;
        double depth = Calc.IncreasedDepth;
        double decreasedDepth = Calc.Depth;
        double decreasedHeight = Calc.DecreasedHeight;
        double jointlessSpan = Calc.CalcJointlessSpan();
        double verticalSpan = Calc.CalcHeightJointSpan();

        JointDivision vertical = Calc.GetJointLength(verticalSpan);
        JointDivision horizontal = Calc.GetJointLength(decreasedDepth - 2.0 * t);

        var outline = new SvgPathBuilder(_arcSegmentsPerQuarter);
        string startInt = StartingCoordinateInt.ToString(CultureInfo.InvariantCulture);
        outline.MoveToAbs(StartingCoordinate, StartingCoordinate, svgX: startInt, svgY: startInt);
        outline.HorizontalBy(depth);

        if (hasHingeEar)
        {
            outline.VerticalBy(-t / 2.0);
            outline.ArcBy(2.0 * t, 2.0 * t, 0, 0, 1, 4.0 * t, 0.0, svgDy: "0");
        }

        outline.LineToAbs(StartingCoordinate + depth, StartingCoordinate + decreasedHeight);
        outline.HorizontalBy(-2.0 * t);
        AppendHorizontalJoints(outline, horizontal.Count, horizontal.ActualLength, t, folga);
        outline.HorizontalBy(-2.0 * t);
        outline.ClosePath();

        var holes = new SvgPathBuilder(_arcSegmentsPerQuarter);
        holes.MoveToAbs(
            StartingCoordinate + depth - t,
            StartingCoordinate + jointlessSpan,
            spaceAfterComma: true);
        AppendVerticalHoles(holes, vertical.Count, vertical.ActualLength, t, inversor: 0, folga);
        holes.MoveToAbs(
            StartingCoordinate + 2.0 * t,
            StartingCoordinate + jointlessSpan,
            spaceAfterComma: true);
        AppendVerticalHoles(holes, vertical.Count, vertical.ActualLength, t, inversor: 0, folga);

        IReadOnlyList<Circle2D> circularHoles = hasHingeEar
            ? new[]
            {
                new Circle2D(
                    StartingCoordinate + depth + 2.0 * t,
                    StartingCoordinate - (t / 2.0),
                    t * HoleFactor),
            }
            : Array.Empty<Circle2D>();

        double mirrorSpan = placement.MirrorX
            ? (placement.MirrorSpanWidth > 0.0 ? placement.MirrorSpanWidth : ComputeSideMirrorSpan(hasHingeEar))
            : 0.0;
        PanelPlacement2D normalizedPlacement = placement.MirrorX
            ? placement with { MirrorSpanWidth = mirrorSpan }
            : placement;

        return new PanelGeometry2D
        {
            PanelId = placement.PanelId,
            SvgElementId = "side_panel",
            OuterContour = outline.BuildPolygons().Single(),
            InternalHoles = holes.BuildPolygons(),
            CircularHoles = circularHoles,
            Placement = normalizedPlacement,
            OutlinePathData = outline.PathData,
            HolesPathData = holes.PathData,
        };
    }

    private PanelGeometry2D BuildBottomPanel(PanelPlacement2D placement)
    {
        double standardWidth = Calc.Width;
        double standardDepth = Calc.Depth;
        double t = Calc.Thickness;
        double folga = Calc.Folga;

        JointDivision horizontal = Calc.GetJointLength(standardWidth - 2.0 * t);
        JointDivision vertical = Calc.GetJointLength(standardDepth - 2.0 * t);

        var outline = new SvgPathBuilder(_arcSegmentsPerQuarter);
        outline.MoveToAbs(
            StartingCoordinate + t,
            StartingCoordinate,
            svgY: StartingCoordinateInt.ToString(CultureInfo.InvariantCulture));
        outline.HorizontalBy(standardWidth);
        outline.ArcBy(t, t, 0, 0, 1, t, t);
        outline.VerticalBy(standardDepth);
        outline.ArcBy(t, t, 0, 0, 1, -t, t);
        outline.HorizontalBy(-standardWidth);
        outline.ArcBy(t, t, 0, 0, 1, -t, -t);
        outline.VerticalBy(-standardDepth);
        outline.ArcBy(t, t, 0, 0, 1, t, -t);

        var holes = new SvgPathBuilder(_arcSegmentsPerQuarter);
        holes.MoveToAbs(StartingCoordinate + 2.0 * t, StartingCoordinate + 2.0 * t);
        AppendHorizontalHoles(holes, horizontal.Count, horizontal.ActualLength, t, inversor: 0, folga);

        holes.MoveToAbs(
            StartingCoordinate + 2.0 * t,
            StartingCoordinate + standardDepth + t,
            spaceAfterComma: true);
        AppendHorizontalHoles(holes, horizontal.Count, horizontal.ActualLength, t, inversor: 0, folga);

        holes.MoveToAbs(StartingCoordinate + standardWidth + t, StartingCoordinate + 2.0 * t);
        AppendVerticalHoles(holes, vertical.Count, vertical.ActualLength, t, inversor: 0, folga);

        holes.MoveToAbs(StartingCoordinate + 2.0 * t, StartingCoordinate + 2.0 * t);
        AppendVerticalHoles(holes, vertical.Count, vertical.ActualLength, t, inversor: 0, folga);

        return new PanelGeometry2D
        {
            PanelId = placement.PanelId,
            SvgElementId = "bottom",
            OuterContour = outline.BuildPolygons(closeImplicitly: true).Single(),
            InternalHoles = holes.BuildPolygons(),
            CircularHoles = Array.Empty<Circle2D>(),
            Placement = placement,
            OutlinePathData = outline.PathData,
            HolesPathData = holes.PathData,
        };
    }

    private PanelGeometry2D BuildLidPanel(PanelPlacement2D placement)
    {
        double standardWidth = Calc.Width;
        double standardDepth = Calc.Depth;
        double t = Calc.Thickness;
        double sideStraightDepth = standardDepth - 0.5 * t;
        double pinOverhang = HingePinOverhangFactor * t;
        double pinExtension = t + pinOverhang;
        double totalAxleWidth = standardWidth + 2.0 * pinOverhang;

        var outline = new SvgPathBuilder(_arcSegmentsPerQuarter);
        outline.MoveToAbs(
            StartingCoordinate + t,
            StartingCoordinate,
            svgY: StartingCoordinateInt.ToString(CultureInfo.InvariantCulture));
        outline.HorizontalBy(standardWidth);
        outline.ArcBy(t, t, 0, 0, 1, t, t);
        outline.VerticalBy(sideStraightDepth);
        outline.ArcBy(t, t, 0, 0, 1, -t, t);
        outline.HorizontalBy(-t);
        outline.VerticalBy(2.0 * t);
        outline.HorizontalBy(pinExtension);
        outline.VerticalBy(t);
        outline.HorizontalBy(-totalAxleWidth);
        outline.VerticalBy(-t);
        outline.HorizontalBy(pinExtension);
        outline.VerticalBy(-2.0 * t);
        outline.HorizontalBy(-t);
        outline.ArcBy(t, t, 0, 0, 1, -t, -t);
        outline.VerticalBy(-sideStraightDepth);
        outline.ArcBy(t, t, 0, 0, 1, t, -t);
        outline.ClosePath();

        return new PanelGeometry2D
        {
            PanelId = placement.PanelId,
            SvgElementId = "lid",
            OuterContour = outline.BuildPolygons().Single(),
            InternalHoles = Array.Empty<Polygon2D>(),
            CircularHoles = Array.Empty<Circle2D>(),
            Placement = placement,
            OutlinePathData = outline.PathData,
            HolesPathData = null,
        };
    }

    private void AppendVerticalJoints(
        SvgPathBuilder builder,
        int numVerticalJoints,
        double verticalJointLength,
        double thickness,
        int direction,
        double folga)
    {
        int factor = direction == 0 ? 1 : -1;
        for (int i = 0; i < numVerticalJoints; i++)
        {
            double segLen = GetJointSegmentLength(i, numVerticalJoints, verticalJointLength, folga);
            if (i % 2 == 0)
            {
                builder.VerticalBy(factor * segLen);
            }
            else
            {
                builder.HorizontalBy(factor * thickness);
                builder.VerticalBy(factor * segLen);
                builder.HorizontalBy(-factor * thickness);
            }
        }
    }

    private void AppendHorizontalJoints(
        SvgPathBuilder builder,
        int numHorizontalJoints,
        double horizontalJointLength,
        double thickness,
        double folga)
    {
        for (int j = 0; j < numHorizontalJoints; j++)
        {
            double segLen = GetJointSegmentLength(j, numHorizontalJoints, horizontalJointLength, folga);
            if (j % 2 == 0)
            {
                builder.HorizontalBy(-segLen, $"-{PythonMath.FormatFloat(segLen)}");
            }
            else
            {
                builder.VerticalBy(thickness);
                builder.HorizontalBy(-segLen, $"-{PythonMath.FormatFloat(segLen)}");
                builder.VerticalBy(-thickness, $"-{PythonMath.FormatFloat(thickness)}");
            }
        }
    }

    private void AppendVerticalHoles(
        SvgPathBuilder builder,
        int numJoints,
        double jointLength,
        double thickness,
        int inversor,
        double folga)
    {
        int factor = inversor == 0 ? 1 : -1;
        SlotGeometry slot = GetSlotGeometry(jointLength, thickness, folga);

        for (int i = 0; i < numJoints; i++)
        {
            if (i % 2 == 0)
            {
                builder.MoveBy(0.0, factor * jointLength, svgDx: "0");
            }
            else
            {
                if (folga != 0.0)
                {
                    builder.MoveBy(factor * slot.OffsetWidth, factor * (-slot.OffsetLength));
                }

                builder.VerticalBy(factor * slot.SlotLength);
                builder.HorizontalBy(-factor * slot.SlotWidth);
                builder.VerticalBy(-factor * slot.SlotLength);
                builder.HorizontalBy(factor * slot.SlotWidth);

                if (folga != 0.0)
                {
                    builder.MoveBy(factor * (-slot.OffsetWidth), factor * (jointLength + slot.OffsetLength));
                }
                else
                {
                    builder.MoveBy(0.0, factor * jointLength, svgDx: "0");
                }
            }
        }
    }

    private void AppendHorizontalHoles(
        SvgPathBuilder builder,
        int numJoints,
        double jointLength,
        double thickness,
        int inversor,
        double folga)
    {
        int factor = inversor == 0 ? 1 : -1;
        SlotGeometry slot = GetSlotGeometry(jointLength, thickness, folga);

        for (int i = 0; i < numJoints; i++)
        {
            if (i % 2 == 0)
            {
                builder.MoveBy(factor * jointLength, 0.0, svgDy: "0");
            }
            else
            {
                if (folga != 0.0)
                {
                    builder.MoveBy(factor * (-slot.OffsetLength), factor * slot.OffsetWidth);
                }

                builder.HorizontalBy(factor * slot.SlotLength);
                builder.VerticalBy(-factor * slot.SlotWidth);
                builder.HorizontalBy(-factor * slot.SlotLength);
                builder.VerticalBy(factor * slot.SlotWidth);

                if (folga != 0.0)
                {
                    builder.MoveBy(factor * (jointLength + slot.OffsetLength), factor * (-slot.OffsetWidth));
                }
                else
                {
                    builder.MoveBy(factor * jointLength, 0.0, svgDy: "0");
                }
            }
        }
    }

    private static void AppendPanelSvg(StringBuilder sb, PanelGeometry2D panel)
    {
        if (string.Equals(panel.PanelId, "bottom", StringComparison.Ordinal))
        {
            string transform = panel.Placement.ToSvgTransformAttribute(yIsIntegerLiteral: true);
            AppendPathElement(sb, panel.OutlinePathData, transform);
            if (!string.IsNullOrEmpty(panel.HolesPathData))
            {
                AppendPathElement(sb, panel.HolesPathData, transform);
            }

            return;
        }

        bool yIsLiteralZero = string.Equals(panel.PanelId, "lid", StringComparison.Ordinal)
                              && panel.Placement.Y == 0.0;
        string groupTransform = panel.Placement.ToSvgTransformAttribute(yIsIntegerLiteral: yIsLiteralZero);
        sb.Append(CultureInfo.InvariantCulture, $"<g id=\"{EscapeXml(panel.SvgElementId)}\" transform=\"{EscapeXml(groupTransform)}\">");

        if (string.Equals(panel.SvgElementId, "side_panel", StringComparison.Ordinal))
        {
            foreach (Circle2D circle in panel.CircularHoles)
            {
                string cx = PythonMath.FormatFloat(circle.CenterX);
                string cy = PythonMath.FormatFloat(circle.CenterY);
                string r = PythonMath.FormatFloat(circle.Radius);
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle cx=\"{cx}\" cy=\"{cy}\" fill=\"{FillStyle}\" r=\"{r}\" stroke=\"{StrokeColor}\" stroke-width=\"{StrokeWidth}\" />");
            }
        }

        AppendPathElement(sb, panel.OutlinePathData, transform: null);
        if (!string.IsNullOrEmpty(panel.HolesPathData))
        {
            AppendPathElement(sb, panel.HolesPathData, transform: null);
        }

        sb.Append("</g>");
    }

    private static void AppendPathElement(StringBuilder sb, string d, string? transform)
    {
        if (string.IsNullOrEmpty(transform))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<path d=\"{EscapeXml(d)}\" fill=\"{FillStyle}\" stroke=\"{StrokeColor}\" stroke-width=\"{StrokeWidth}\" />");
            return;
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"<path d=\"{EscapeXml(d)}\" fill=\"{FillStyle}\" stroke=\"{StrokeColor}\" stroke-width=\"{StrokeWidth}\" transform=\"{EscapeXml(transform)}\" />");
    }

    private static string EscapeXml(string value)
        => SecurityElement.Escape(value) ?? string.Empty;
}
