namespace CaixaMaker.Core.Geometry;

/// <summary>Ponto bidimensional em milímetros.</summary>
public readonly record struct Point2D(double X, double Y)
{
    public Point2D Offset(double dx, double dy) => new(X + dx, Y + dy);
}

/// <summary>Caixa delimitadora alinhada aos eixos em milímetros.</summary>
public readonly record struct BoundingBox2D(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public Point2D Center => new((MinX + MaxX) / 2.0, (MinY + MaxY) / 2.0);

    public static BoundingBox2D FromPoints(IEnumerable<Point2D> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        bool any = false;
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (Point2D pt in points)
        {
            any = true;
            if (pt.X < minX) minX = pt.X;
            if (pt.Y < minY) minY = pt.Y;
            if (pt.X > maxX) maxX = pt.X;
            if (pt.Y > maxY) maxY = pt.Y;
        }

        if (!any)
        {
            return new BoundingBox2D(0.0, 0.0, 0.0, 0.0);
        }

        return new BoundingBox2D(minX, minY, maxX, maxY);
    }
}

/// <summary>Círculo bidimensional em milímetros (por exemplo, furo do pino de dobradiça).</summary>
public readonly record struct Circle2D(double CenterX, double CenterY, double Radius);

/// <summary>Polígono 2D fechado ou polilinha em milímetros para CAD, CAM, SAT e extrusão 3D.</summary>
public sealed record Polygon2D
{
    public IReadOnlyList<Point2D> Vertices { get; }
    public bool IsClosed { get; }
    public BoundingBox2D Bounds { get; }

    public Polygon2D(IReadOnlyList<Point2D> vertices, bool isClosed = true)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        Vertices = vertices;
        IsClosed = isClosed;
        Bounds = BoundingBox2D.FromPoints(vertices);
    }

    /// <summary>Área assinada pelo algoritmo Shoelace em mm² (positiva ou negativa conforme orientação).</summary>
    public double SignedAreaMm2
    {
        get
        {
            if (Vertices.Count < 3)
            {
                return 0.0;
            }

            double sum = 0.0;
            for (int i = 0; i < Vertices.Count; i++)
            {
                Point2D a = Vertices[i];
                Point2D b = Vertices[(i + 1) % Vertices.Count];
                sum += (a.X * b.Y) - (b.X * a.Y);
            }

            return sum / 2.0;
        }
    }

    /// <summary>Área absoluta em mm².</summary>
    public double AreaMm2 => Math.Abs(SignedAreaMm2);

    /// <summary>Perímetro euclidiano do polígono ou polilinha em milímetros.</summary>
    public double PerimeterMm
    {
        get
        {
            if (Vertices.Count < 2)
            {
                return 0.0;
            }

            double total = 0.0;
            for (int i = 0; i < Vertices.Count - 1; i++)
            {
                total += SegmentLength(Vertices[i], Vertices[i + 1]);
            }

            if (IsClosed && Vertices.Count > 2)
            {
                total += SegmentLength(Vertices[^1], Vertices[0]);
            }

            return total;
        }
    }

    private static double SegmentLength(Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Aplica uma transformação arbitrária a todos os vértices.</summary>
    public Polygon2D Transform(Func<Point2D, Point2D> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var transformed = new Point2D[Vertices.Count];
        for (int i = 0; i < Vertices.Count; i++)
        {
            transformed[i] = transform(Vertices[i]);
        }

        return new Polygon2D(transformed, IsClosed);
    }
}

/// <summary>Posição e eventual espelhamento horizontal de um painel na folha de corte SVG.</summary>
public sealed record PanelPlacement2D(
    string PanelId,
    double X,
    double Y,
    bool MirrorX = false,
    double MirrorSpanWidth = 0.0)
{
    /// <summary>Converte um ponto do sistema local do grupo SVG para coordenadas absolutas da folha.</summary>
    public Point2D ToSheetCoordinates(Point2D localPoint)
    {
        double localX = MirrorX ? MirrorSpanWidth - localPoint.X : localPoint.X;
        return new Point2D(X + localX, Y + localPoint.Y);
    }

    /// <summary>Retorna o atributo SVG <c>transform</c> compatível com <c>svgwrite</c>.</summary>
    public string ToSvgTransformAttribute(bool yIsIntegerLiteral = false)
    {
        string xText = PythonMath.FormatFloat(X);
        string yText = yIsIntegerLiteral && Y == Math.Truncate(Y)
            ? ((long)Y).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : PythonMath.FormatFloat(Y);

        if (!MirrorX)
        {
            return $"translate({xText},{yText})";
        }

        string mirrorText = PythonMath.FormatFloat(MirrorSpanWidth);
        return $"translate({xText},{yText}) translate({mirrorText},0) scale(-1,1)";
    }
}

/// <summary>
/// Representação geométrica 2D completa de uma chapa individual, unificando dados vetoriais SVG
/// e polígonos/furos prontos para extrusão 3D, planejamento CAM e validação SAT.
/// </summary>
public sealed record PanelGeometry2D
{
    public required string PanelId { get; init; }
    public required string SvgElementId { get; init; }
    public required Polygon2D OuterContour { get; init; }
    public required IReadOnlyList<Polygon2D> InternalHoles { get; init; }
    public required IReadOnlyList<Circle2D> CircularHoles { get; init; }
    public required PanelPlacement2D Placement { get; init; }
    public required string OutlinePathData { get; init; }
    public string? HolesPathData { get; init; }

    /// <summary>Caixa delimitadora no sistema local do painel (em mm).</summary>
    public BoundingBox2D LocalBounds => OuterContour.Bounds;

    /// <summary>Perímetro euclidiano do contorno externo da chapa (mm).</summary>
    public double OuterCutPerimeterMm => OuterContour.PerimeterMm;

    /// <summary>Perímetro total dos recortes internos retangulares e furos circulares da chapa (mm).</summary>
    public double InternalCutPerimeterMm
        => InternalHoles.Sum(hole => hole.PerimeterMm)
         + CircularHoles.Sum(circle => 2.0 * Math.PI * Math.Max(0.0, circle.Radius));

    /// <summary>Perímetro linear total de corte da chapa (<c>OuterCutPerimeterMm + InternalCutPerimeterMm</c>), em mm.</summary>
    public double TotalCutPerimeterMm => OuterCutPerimeterMm + InternalCutPerimeterMm;

    /// <summary>Caixa delimitadora posicionada na folha de corte SVG (em mm).</summary>
    public BoundingBox2D SheetBounds
        => BoundingBox2D.FromPoints(OuterContour.Vertices.Select(Placement.ToSheetCoordinates));

    /// <summary>Contorno externo na folha de corte SVG (com translação e espelhamento aplicados).</summary>
    public Polygon2D GetSheetOuterContour() => OuterContour.Transform(Placement.ToSheetCoordinates);

    /// <summary>Furos retangulares/poligonais posicionados na folha de corte SVG.</summary>
    public IReadOnlyList<Polygon2D> GetSheetInternalHoles()
        => InternalHoles.Select(hole => hole.Transform(Placement.ToSheetCoordinates)).ToArray();

    /// <summary>Furos circulares posicionados na folha de corte SVG.</summary>
    public IReadOnlyList<Circle2D> GetSheetCircularHoles()
        => CircularHoles
            .Select(c =>
            {
                Point2D center = Placement.ToSheetCoordinates(new Point2D(c.CenterX, c.CenterY));
                return new Circle2D(center.X, center.Y, c.Radius);
            })
            .ToArray();

    /// <summary>
    /// Retorna o contorno externo centrado em <c>(0, 0)</c> pronto para extrusão 3D
    /// (com opção <paramref name="flipY"/> para inverter o eixo Y da tela para +Y para cima).
    /// </summary>
    public Polygon2D GetCenteredOuterContour(bool flipY = true)
    {
        Point2D center = LocalBounds.Center;
        double ySign = flipY ? -1.0 : 1.0;
        return OuterContour.Transform(pt =>
        {
            double x = Placement.MirrorX ? MirrorCenteredX(pt.X, center.X) : pt.X - center.X;
            double y = (pt.Y - center.Y) * ySign;
            return new Point2D(x, y);
        });
    }

    /// <summary>Retorna os furos internos centrados em <c>(0, 0)</c> para extrusão 3D.</summary>
    public IReadOnlyList<Polygon2D> GetCenteredInternalHoles(bool flipY = true)
    {
        Point2D center = LocalBounds.Center;
        double ySign = flipY ? -1.0 : 1.0;
        return InternalHoles
            .Select(hole => hole.Transform(pt =>
            {
                double x = Placement.MirrorX ? MirrorCenteredX(pt.X, center.X) : pt.X - center.X;
                double y = (pt.Y - center.Y) * ySign;
                return new Point2D(x, y);
            }))
            .ToArray();
    }

    private static double MirrorCenteredX(double x, double centerX) => centerX - x;
}

/// <summary>
/// Métricas da folha de corte SVG e posições exatas dos painéis em milímetros.
/// Espelha <c>BoxDrawer.get_layout_metrics</c> e os layouts customizados de modelos.
/// </summary>
public sealed record BoxLayoutMetrics
{
    public required double CanvasWidth { get; init; }
    public required double CanvasHeight { get; init; }
    public required double SheetWidth { get; init; }
    public required double SheetHeight { get; init; }
    public double? LidDepth { get; init; }
    public required double SideWidth { get; init; }
    public required double Row1Height { get; init; }
    public required double Row2Height { get; init; }
    public required double Row3TopClearance { get; init; }
    public required double Row3Height { get; init; }
    public required IReadOnlyList<PanelPlacement2D> OrderedPanels { get; init; }
    public required IReadOnlyDictionary<string, PanelPlacement2D> Panels { get; init; }
}

/// <summary>Conjunto completo de peças geométricas de uma caixa no plano 2D.</summary>
public sealed record BoxSheetGeometry2D
{
    public required string ModelId { get; init; }
    public required BoxLayoutMetrics Layout { get; init; }
    public required IReadOnlyList<PanelGeometry2D> Panels { get; init; }
    public required IReadOnlyDictionary<string, PanelGeometry2D> PanelsById { get; init; }

    /// <summary>Soma do perímetro externo de todas as chapas da folha de corte (mm).</summary>
    public double OuterCutPerimeterMm => Panels.Sum(panel => panel.OuterCutPerimeterMm);

    /// <summary>Soma do perímetro de todos os recortes e furos internos da folha de corte (mm).</summary>
    public double InternalCutPerimeterMm => Panels.Sum(panel => panel.InternalCutPerimeterMm);

    /// <summary>Perímetro linear total de corte da folha completa (mm).</summary>
    public double TotalCutPerimeterMm => OuterCutPerimeterMm + InternalCutPerimeterMm;
}
