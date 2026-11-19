using System.Globalization;
using System.Text;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.Personalization;

/// <summary>
/// Resultado imutável da construção geométrica 1:1 em milímetros de uma primitiva paramétrica CNC.
/// </summary>
public sealed record ShapeGeometryResult
{
    public required GeometricShapeKind Kind { get; init; }
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }
    public required Rect2D BoundingBox { get; init; }
    public required BoundingBox2D TightBounds { get; init; }
    public required IReadOnlyList<Point2D> PrimaryContour { get; init; }
    public required IReadOnlyList<IReadOnlyList<Point2D>> Polylines { get; init; }
    public required string SvgPathData { get; init; }
    public required IReadOnlyList<VectorPathItem> VectorPaths { get; init; }
}

/// <summary>
/// Construtor geométrico determinístico 1:1 (mm) para primitivas paramétricas CNC
/// (<see cref="GeometricShapeKind.Rectangle"/>, <see cref="GeometricShapeKind.Circle"/>,
/// <see cref="GeometricShapeKind.Slot"/>, <see cref="GeometricShapeKind.Polygon"/> e
/// <see cref="GeometricShapeKind.Crosshair"/>) com discretização angular adaptativa a laser.
/// </summary>
public static class ShapeGeometryBuilder
{
    public const double MinDimensionMm = 0.5;
    public const double MaxChordalErrorMm = 0.02;
    public const int MinCircleSegments = 36;
    public const int MaxCircleSegments = 128;
    public const int MinPolygonSides = 3;
    public const int MaxPolygonSides = 24;
    public const double DefaultHatchSpacingMm = 0.8;

    /// <summary>
    /// Calcula o número de segmentos por circunferência completa garantindo erro cordal máximo &lt;= 0.02 mm,
    /// limitado ao intervalo [36, 128] e alinhado a múltiplo de 4 para preservar extremos cardeais exatos.
    /// </summary>
    public static int ComputeCircleSegments(double radiusMm, double maxChordalErrorMm = MaxChordalErrorMm)
    {
        double r = Math.Max(0.0, double.IsFinite(radiusMm) ? radiusMm : 0.0);
        double maxErr = Math.Max(1e-5, double.IsFinite(maxChordalErrorMm) ? maxChordalErrorMm : MaxChordalErrorMm);
        if (r <= maxErr)
        {
            return MinCircleSegments;
        }

        double cosHalf = Math.Clamp(1.0 - (maxErr / r), -1.0, 1.0);
        double halfAngle = Math.Acos(cosHalf);
        int raw = halfAngle > 1e-6
            ? (int)Math.Ceiling(Math.PI / halfAngle)
            : MaxCircleSegments;

        int clamped = Math.Clamp(raw, MinCircleSegments, MaxCircleSegments);
        int rem = clamped % 4;
        if (rem != 0)
        {
            clamped = Math.Min(MaxCircleSegments, clamped + (4 - rem));
        }

        return clamped;
    }

    /// <summary>
    /// Constrói a geometria vetorial 1:1 em milímetros para a especificação informada.
    /// </summary>
    public static ShapeGeometryResult Build(
        GeometricShapeSpec spec,
        OperationMode operationMode = OperationMode.Cut,
        CncOperationType? explicitOperation = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return spec.Kind switch
        {
            GeometricShapeKind.Rectangle => BuildRectangle(spec, operationMode, explicitOperation),
            GeometricShapeKind.Circle => BuildCircle(spec, operationMode, explicitOperation),
            GeometricShapeKind.Slot => BuildSlot(spec, operationMode, explicitOperation),
            GeometricShapeKind.Polygon => BuildPolygon(spec, operationMode, explicitOperation),
            GeometricShapeKind.Crosshair => BuildCrosshair(spec, operationMode, explicitOperation),
            _ => BuildRectangle(spec, operationMode, explicitOperation),
        };
    }

    /// <summary>
    /// Sincroniza imediatamente dimensões de bounding box, contornos e caminhos vetoriais de um elemento Shape.
    /// </summary>
    public static ShapeGeometryResult SynchronizeElement(
        PersonalizationElement element,
        bool preserveAnchorPosition = false)
    {
        ArgumentNullException.ThrowIfNull(element);

        GeometricShapeSpec spec = element.ShapeSpec ?? new GeometricShapeSpec();
        element.ShapeSpec ??= spec;

        Point2D prevCenter = element.CenterMm();
        double prevLeft = element.XMm;
        double prevTop = element.YMm;
        double prevRight = element.XMm + element.WidthMm;
        double prevBottom = element.YMm + element.HeightMm;

        OperationMode effectiveMode = element.OperationMode;
        ShapeGeometryResult built = Build(spec, effectiveMode, element.Operation);

        element.WidthMm = built.WidthMm;
        element.HeightMm = built.HeightMm;

        if (element is ShapePersonalizationElement shapeEl)
        {
            shapeEl.ApplyBuiltGeometry(built);
        }

        if (preserveAnchorPosition)
        {
            switch (element.Anchor)
            {
                case AnchorMode.Center:
                case AnchorMode.Relative:
                    element.XMm = PythonMath.Round(prevCenter.X - built.WidthMm * 0.5, 4);
                    element.YMm = PythonMath.Round(prevCenter.Y - built.HeightMm * 0.5, 4);
                    break;
                case AnchorMode.TopRight:
                    element.XMm = PythonMath.Round(prevRight - built.WidthMm, 4);
                    element.YMm = PythonMath.Round(prevTop, 4);
                    break;
                case AnchorMode.BottomLeft:
                    element.XMm = PythonMath.Round(prevLeft, 4);
                    element.YMm = PythonMath.Round(prevBottom - built.HeightMm, 4);
                    break;
                case AnchorMode.BottomRight:
                    element.XMm = PythonMath.Round(prevRight - built.WidthMm, 4);
                    element.YMm = PythonMath.Round(prevBottom - built.HeightMm, 4);
                    break;
                case AnchorMode.TopLeft:
                case AnchorMode.Absolute:
                default:
                    element.XMm = PythonMath.Round(prevLeft, 4);
                    element.YMm = PythonMath.Round(prevTop, 4);
                    break;
            }
        }

        return built;
    }

    private static ShapeGeometryResult BuildRectangle(
        GeometricShapeSpec spec,
        OperationMode operationMode,
        CncOperationType? explicitOperation)
    {
        double w = PythonMath.Round(Math.Max(MinDimensionMm, spec.WidthMm), 4);
        double h = PythonMath.Round(Math.Max(MinDimensionMm, spec.HeightMm), 4);
        double maxR = Math.Min(w, h) * 0.5;
        double r = PythonMath.Round(Math.Clamp(spec.CornerRadiusMm, 0.0, maxR), 4);

        List<Point2D> contour;
        if (r <= 1e-4)
        {
            contour =
            [
                new Point2D(0.0, 0.0),
                new Point2D(w, 0.0),
                new Point2D(w, h),
                new Point2D(0.0, h),
            ];
        }
        else
        {
            int fullSegs = ComputeCircleSegments(r);
            int quarterSteps = Math.Max(4, fullSegs / 4);
            contour = new List<Point2D>(quarterSteps * 4 + 4);

            // Quarto de arco superior-direito: centro (w - r, r), ângulos [-pi/2 .. 0]
            AppendArcPoints(contour, w - r, r, r, -Math.PI * 0.5, 0.0, quarterSteps);
            // Quarto de arco inferior-direito: centro (w - r, h - r), ângulos [0 .. pi/2]
            AppendArcPoints(contour, w - r, h - r, r, 0.0, Math.PI * 0.5, quarterSteps);
            // Quarto de arco inferior-esquerdo: centro (r, h - r), ângulos [pi/2 .. pi]
            AppendArcPoints(contour, r, h - r, r, Math.PI * 0.5, Math.PI, quarterSteps);
            // Quarto de arco superior-esquerdo: centro (r, r), ângulos [pi .. 3*pi/2]
            AppendArcPoints(contour, r, r, r, Math.PI, Math.PI * 1.5, quarterSteps);

            RemoveDuplicateClosingVertex(contour);
        }

        return FinalizeClosedShapeResult(
            GeometricShapeKind.Rectangle,
            w,
            h,
            contour,
            spec,
            operationMode,
            explicitOperation);
    }

    private static ShapeGeometryResult BuildCircle(
        GeometricShapeSpec spec,
        OperationMode operationMode,
        CncOperationType? explicitOperation)
    {
        double d = PythonMath.Round(Math.Max(MinDimensionMm, spec.DiameterMm), 4);
        double r = d * 0.5;
        int segments = ComputeCircleSegments(r);
        var contour = new List<Point2D>(segments);

        BuildCircleVertices(contour, r, r, r, segments);

        return FinalizeClosedShapeResult(
            GeometricShapeKind.Circle,
            d,
            d,
            contour,
            spec,
            operationMode,
            explicitOperation);
    }

    private static ShapeGeometryResult BuildSlot(
        GeometricShapeSpec spec,
        OperationMode operationMode,
        CncOperationType? explicitOperation)
    {
        double rawW = Math.Max(MinDimensionMm, spec.WidthMm);
        double rawH = Math.Max(MinDimensionMm, spec.HeightMm);
        double lengthL = PythonMath.Round(Math.Max(rawW, rawH), 4);
        double widthW = PythonMath.Round(Math.Min(rawW, rawH), 4);
        double r = widthW * 0.5;

        if (lengthL - widthW <= 1e-4)
        {
            int circleSegs = ComputeCircleSegments(r);
            var circleContour = new List<Point2D>(circleSegs);
            BuildCircleVertices(circleContour, r, r, r, circleSegs);

            return FinalizeClosedShapeResult(
                GeometricShapeKind.Slot,
                widthW,
                widthW,
                circleContour,
                spec,
                operationMode,
                explicitOperation);
        }

        int fullSegs = ComputeCircleSegments(r);
        int halfSteps = Math.Max(8, fullSegs / 2);
        var contour = new List<Point2D>(halfSteps * 2 + 2);

        // Tampa semicircular direita: centro (L - r, r), de -pi/2 (topo) passando por 0 (extremo direito) até +pi/2 (base)
        AppendArcPoints(contour, lengthL - r, r, r, -Math.PI * 0.5, Math.PI * 0.5, halfSteps);
        // Tampa semicircular esquerda: centro (r, r), de +pi/2 (base) passando por pi (extremo esquerdo) até +3*pi/2 (topo)
        AppendArcPoints(contour, r, r, r, Math.PI * 0.5, Math.PI * 1.5, halfSteps);

        RemoveDuplicateClosingVertex(contour);

        return FinalizeClosedShapeResult(
            GeometricShapeKind.Slot,
            lengthL,
            widthW,
            contour,
            spec,
            operationMode,
            explicitOperation);
    }

    private static ShapeGeometryResult BuildPolygon(
        GeometricShapeSpec spec,
        OperationMode operationMode,
        CncOperationType? explicitOperation)
    {
        double d = PythonMath.Round(Math.Max(MinDimensionMm, spec.DiameterMm), 4);
        double r = d * 0.5;
        int sides = Math.Clamp(spec.Sides, MinPolygonSides, MaxPolygonSides);

        // Orientado com base horizontal plana (e topo plano quando Sides é par, ex.: sextavado M3/M4)
        double stepAngle = (2.0 * Math.PI) / sides;
        double startAngle = (Math.PI * 0.5) - (Math.PI / sides);

        var contour = new List<Point2D>(sides);
        for (int i = 0; i < sides; i++)
        {
            double theta = startAngle + (i * stepAngle);
            double x = PythonMath.Round(r + (r * Math.Cos(theta)), 4);
            double y = PythonMath.Round(r + (r * Math.Sin(theta)), 4);
            contour.Add(new Point2D(
                Math.Clamp(x, 0.0, d),
                Math.Clamp(y, 0.0, d)));
        }

        return FinalizeClosedShapeResult(
            GeometricShapeKind.Polygon,
            d,
            d,
            contour,
            spec,
            operationMode,
            explicitOperation);
    }

    private static ShapeGeometryResult BuildCrosshair(
        GeometricShapeSpec spec,
        OperationMode operationMode,
        CncOperationType? explicitOperation)
    {
        double d = PythonMath.Round(Math.Max(MinDimensionMm, spec.DiameterMm), 4);
        double r = d * 0.5;
        double refCircleDiameter = Math.Min(2.0, d * 0.25);
        double refRadius = refCircleDiameter * 0.5;

        Point2D[] horizontalAxis =
        [
            new Point2D(0.0, r),
            new Point2D(d, r),
        ];

        Point2D[] verticalAxis =
        [
            new Point2D(r, 0.0),
            new Point2D(r, d),
        ];

        int circleSegs = ComputeCircleSegments(refRadius);
        var centerCircle = new List<Point2D>(circleSegs);
        BuildCircleVertices(centerCircle, r, r, refRadius, circleSegs);

        string hPath = $"M 0 {Fmt(r)} L {Fmt(d)} {Fmt(r)}";
        string vPath = $"M {Fmt(r)} 0 L {Fmt(r)} {Fmt(d)}";
        string cPath = BuildClosedPolylinePathData(centerCircle);
        string combinedSvgPath = $"{hPath} {vPath} {cPath}";

        // Polígono convexo envolvente não-degenerado para validação SAT de contenção/colisão
        Point2D[] satEnvelope =
        [
            new Point2D(0.0, 0.0),
            new Point2D(d, 0.0),
            new Point2D(d, d),
            new Point2D(0.0, d),
        ];

        CncOperationType axesOp = explicitOperation switch
        {
            CncOperationType.EngraveVector => CncOperationType.EngraveVector,
            CncOperationType.EngraveFill => CncOperationType.EngraveFill,
            _ => CncOperationType.Mark,
        };

        CncOperationType circleOp = operationMode is OperationMode.Cut or OperationMode.Both
            ? CncOperationType.CutInternal
            : axesOp;

        var vectorPaths = new List<VectorPathItem>(3)
        {
            new VectorPathItem
            {
                D = hPath,
                Operation = axesOp,
                IsClosed = false,
                HasFill = false,
                HasStroke = true,
                LayerName = LayerNameForOperation(axesOp),
            },
            new VectorPathItem
            {
                D = vPath,
                Operation = axesOp,
                IsClosed = false,
                HasFill = false,
                HasStroke = true,
                LayerName = LayerNameForOperation(axesOp),
            },
            new VectorPathItem
            {
                D = cPath,
                Operation = circleOp,
                IsClosed = true,
                HasFill = false,
                HasStroke = true,
                LayerName = LayerNameForOperation(circleOp),
            },
        };

        return new ShapeGeometryResult
        {
            Kind = GeometricShapeKind.Crosshair,
            WidthMm = d,
            HeightMm = d,
            BoundingBox = new Rect2D(0.0, 0.0, d, d),
            TightBounds = new BoundingBox2D(0.0, 0.0, d, d),
            PrimaryContour = satEnvelope,
            Polylines = [horizontalAxis, verticalAxis, centerCircle],
            SvgPathData = combinedSvgPath,
            VectorPaths = vectorPaths,
        };
    }

    private static ShapeGeometryResult FinalizeClosedShapeResult(
        GeometricShapeKind kind,
        double widthMm,
        double heightMm,
        List<Point2D> contour,
        GeometricShapeSpec spec,
        OperationMode operationMode,
        CncOperationType? explicitOperation)
    {
        string contourPathData = BuildClosedPolylinePathData(contour);
        BoundingBox2D tight = contour.Count > 0
            ? BoundingBox2D.FromPoints(contour)
            : new BoundingBox2D(0.0, 0.0, widthMm, heightMm);

        var vectorPaths = new List<VectorPathItem>();

        if (operationMode == OperationMode.Cut &&
            explicitOperation is not (CncOperationType.EngraveVector or CncOperationType.EngraveFill or CncOperationType.Mark))
        {
            CncOperationType cutOp = explicitOperation == CncOperationType.CutExternal
                ? CncOperationType.CutExternal
                : CncOperationType.CutInternal;

            vectorPaths.Add(new VectorPathItem
            {
                D = contourPathData,
                Operation = cutOp,
                IsClosed = true,
                HasFill = false,
                HasStroke = true,
                LayerName = LayerNameForOperation(cutOp),
            });
        }
        else if (operationMode == OperationMode.Both)
        {
            bool emitFill = spec.FillHatch || explicitOperation == CncOperationType.EngraveFill;
            if (emitFill)
            {
                AppendHatchFillPaths(vectorPaths, contour, spec.HatchSpacingMm);
            }

            vectorPaths.Add(new VectorPathItem
            {
                D = contourPathData,
                Operation = CncOperationType.EngraveVector,
                IsClosed = true,
                HasFill = false,
                HasStroke = true,
                LayerName = "engrave_vector",
            });

            vectorPaths.Add(new VectorPathItem
            {
                D = contourPathData,
                Operation = CncOperationType.CutInternal,
                IsClosed = true,
                HasFill = false,
                HasStroke = true,
                LayerName = "cut_internal",
            });
        }
        else
        {
            // OperationMode.Engrave (ou explicitOperation de gravação/marcação)
            CncOperationType contourOp = explicitOperation switch
            {
                CncOperationType.Mark => CncOperationType.Mark,
                CncOperationType.CutInternal => CncOperationType.CutInternal,
                CncOperationType.CutExternal => CncOperationType.CutExternal,
                _ => CncOperationType.EngraveVector,
            };

            bool emitFillHatch = spec.FillHatch || explicitOperation == CncOperationType.EngraveFill;
            if (emitFillHatch)
            {
                AppendHatchFillPaths(vectorPaths, contour, spec.HatchSpacingMm);
            }

            if (explicitOperation != CncOperationType.EngraveFill || !emitFillHatch)
            {
                vectorPaths.Add(new VectorPathItem
                {
                    D = contourPathData,
                    Operation = contourOp,
                    IsClosed = true,
                    HasFill = false,
                    HasStroke = true,
                    LayerName = LayerNameForOperation(contourOp),
                });
            }
            else
            {
                // Mantém o contorno também visível em ENGRAVE_VECTOR quando em modo preenchido/hachurado
                vectorPaths.Add(new VectorPathItem
                {
                    D = contourPathData,
                    Operation = CncOperationType.EngraveVector,
                    IsClosed = true,
                    HasFill = false,
                    HasStroke = true,
                    LayerName = "engrave_vector",
                });
            }
        }

        return new ShapeGeometryResult
        {
            Kind = kind,
            WidthMm = widthMm,
            HeightMm = heightMm,
            BoundingBox = new Rect2D(0.0, 0.0, widthMm, heightMm),
            TightBounds = tight,
            PrimaryContour = contour,
            Polylines = [contour],
            SvgPathData = contourPathData,
            VectorPaths = vectorPaths,
        };
    }

    /// <summary>
    /// Gera linhas de preenchimento em hachura a 45° analítica para um polígono fechado convexo na layer ENGRAVE_FILL.
    /// </summary>
    public static List<string> GenerateConvexHatch45Paths(
        IReadOnlyList<Point2D> convexPolygon,
        double hatchSpacingMm = DefaultHatchSpacingMm)
    {
        var segments = new List<string>();
        if (convexPolygon is null || convexPolygon.Count < 3)
        {
            return segments;
        }

        double spacing = Math.Clamp(double.IsFinite(hatchSpacingMm) ? hatchSpacingMm : DefaultHatchSpacingMm, 0.1, 10.0);
        double invSqrt2 = 1.0 / Math.Sqrt(2.0);

        double minProj = double.PositiveInfinity;
        double maxProj = double.NegativeInfinity;
        for (int i = 0; i < convexPolygon.Count; i++)
        {
            Point2D p = convexPolygon[i];
            double proj = (p.X + p.Y) * invSqrt2;
            if (proj < minProj) minProj = proj;
            if (proj > maxProj) maxProj = proj;
        }

        if (!double.IsFinite(minProj) || !double.IsFinite(maxProj) || (maxProj - minProj) <= spacing * 0.5)
        {
            return segments;
        }

        int n = convexPolygon.Count;
        for (double d = minProj + spacing * 0.5; d < maxProj - 1e-5; d += spacing)
        {
            double minT = double.PositiveInfinity;
            double maxT = double.NegativeInfinity;
            Point2D pStart = default;
            Point2D pEnd = default;
            int hits = 0;

            for (int i = 0; i < n; i++)
            {
                Point2D a = convexPolygon[i];
                Point2D b = convexPolygon[(i + 1) % n];
                double da = ((a.X + a.Y) * invSqrt2) - d;
                double db = ((b.X + b.Y) * invSqrt2) - d;

                if ((da <= 0.0 && db >= 0.0) || (da >= 0.0 && db <= 0.0))
                {
                    double denom = db - da;
                    double alpha = Math.Abs(denom) > 1e-12 ? Math.Clamp(-da / denom, 0.0, 1.0) : 0.0;
                    double ix = a.X + alpha * (b.X - a.X);
                    double iy = a.Y + alpha * (b.Y - a.Y);
                    double t = (ix - iy) * invSqrt2;

                    if (t < minT)
                    {
                        minT = t;
                        pStart = new Point2D(ix, iy);
                    }

                    if (t > maxT)
                    {
                        maxT = t;
                        pEnd = new Point2D(ix, iy);
                    }

                    hits++;
                }
            }

            if (hits >= 2 && (maxT - minT) > 0.04)
            {
                segments.Add($"M {Fmt(pStart.X)} {Fmt(pStart.Y)} L {Fmt(pEnd.X)} {Fmt(pEnd.Y)}");
            }
        }

        return segments;
    }

    private static void AppendHatchFillPaths(
        List<VectorPathItem> target,
        IReadOnlyList<Point2D> contour,
        double hatchSpacingMm)
    {
        // Contorno fechado de preenchimento + hachura 45° na layer ENGRAVE_FILL
        List<string> hatchLines = GenerateConvexHatch45Paths(contour, hatchSpacingMm);
        if (hatchLines.Count > 0)
        {
            string combinedHatchD = string.Join(' ', hatchLines);
            target.Add(new VectorPathItem
            {
                D = combinedHatchD,
                Operation = CncOperationType.EngraveFill,
                IsClosed = false,
                HasFill = false,
                HasStroke = true,
                LayerName = "engrave_fill",
            });
        }
        else
        {
            target.Add(new VectorPathItem
            {
                D = BuildClosedPolylinePathData(contour),
                Operation = CncOperationType.EngraveFill,
                IsClosed = true,
                HasFill = true,
                HasStroke = false,
                LayerName = "engrave_fill",
            });
        }
    }

    private static void BuildCircleVertices(
        List<Point2D> output,
        double cx,
        double cy,
        double radius,
        int segments)
    {
        int n = Math.Max(4, segments);
        double step = (2.0 * Math.PI) / n;
        for (int i = 0; i < n; i++)
        {
            double theta = i * step;
            (double cosA, double sinA) = CosSinExactCardinals(theta);
            double x = PythonMath.Round(cx + (radius * cosA), 4);
            double y = PythonMath.Round(cy + (radius * sinA), 4);
            output.Add(new Point2D(x, y));
        }
    }

    private static void AppendArcPoints(
        List<Point2D> output,
        double cx,
        double cy,
        double radius,
        double startRad,
        double endRad,
        int steps)
    {
        int n = Math.Max(2, steps);
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            double theta = startRad + t * (endRad - startRad);
            (double cosA, double sinA) = CosSinExactCardinals(theta);
            var pt = new Point2D(
                PythonMath.Round(cx + (radius * cosA), 4),
                PythonMath.Round(cy + (radius * sinA), 4));

            if (output.Count == 0 || DistanceSq(output[^1], pt) > 1e-8)
            {
                output.Add(pt);
            }
        }
    }

    private static (double Cos, double Sin) CosSinExactCardinals(double theta)
    {
        double c = Math.Cos(theta);
        double s = Math.Sin(theta);
        if (Math.Abs(c) < 1e-12) c = 0.0;
        else if (Math.Abs(c - 1.0) < 1e-12) c = 1.0;
        else if (Math.Abs(c + 1.0) < 1e-12) c = -1.0;

        if (Math.Abs(s) < 1e-12) s = 0.0;
        else if (Math.Abs(s - 1.0) < 1e-12) s = 1.0;
        else if (Math.Abs(s + 1.0) < 1e-12) s = -1.0;

        return (c, s);
    }

    private static void RemoveDuplicateClosingVertex(List<Point2D> contour)
    {
        while (contour.Count > 3 && DistanceSq(contour[0], contour[^1]) <= 1e-8)
        {
            contour.RemoveAt(contour.Count - 1);
        }
    }

    private static double DistanceSq(Point2D a, Point2D b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static string BuildClosedPolylinePathData(IReadOnlyList<Point2D> points)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(points.Count * 16);
        sb.Append("M ").Append(Fmt(points[0].X)).Append(' ').Append(Fmt(points[0].Y));
        for (int i = 1; i < points.Count; i++)
        {
            sb.Append(" L ").Append(Fmt(points[i].X)).Append(' ').Append(Fmt(points[i].Y));
        }

        sb.Append(" Z");
        return sb.ToString();
    }

    private static string LayerNameForOperation(CncOperationType op) => op switch
    {
        CncOperationType.CutInternal => "cut_internal",
        CncOperationType.CutExternal => "cut_external",
        CncOperationType.EngraveFill => "engrave_fill",
        CncOperationType.Mark => "mark",
        CncOperationType.EngraveRaster => "engrave_raster",
        _ => "engrave_vector",
    };

    private static string Fmt(double value)
    {
        if (Math.Abs(value) < 5e-5)
        {
            return "0";
        }

        string s = value.ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
