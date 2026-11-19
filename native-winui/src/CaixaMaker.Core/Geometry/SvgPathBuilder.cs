using System.Globalization;
using System.Text;

namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Constrói simultaneamente a string SVG <c>d</c> (com paridade textual exata ao Python)
/// e os polígonos discretizados <see cref="Polygon2D"/> em memória.
/// </summary>
internal sealed class SvgPathBuilder
{
    private const double VertexEpsilon = 1e-9;

    private readonly StringBuilder _svg = new();
    private readonly int _arcSegmentsPerQuarter;
    private readonly List<List<Point2D>> _subpaths = new();

    private List<Point2D>? _currentSubpath;
    private double _currentX;
    private double _currentY;
    private double _subpathStartX;
    private double _subpathStartY;
    private bool _hasDrawnInSubpath;

    public SvgPathBuilder(int arcSegmentsPerQuarter = 16)
    {
        if (arcSegmentsPerQuarter < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(arcSegmentsPerQuarter));
        }

        _arcSegmentsPerQuarter = arcSegmentsPerQuarter;
    }

    /// <summary>Retorna a string completa do atributo <c>d</c>.</summary>
    public string PathData => _svg.ToString();

    /// <summary>Adiciona texto bruto ao SVG sem alterar a geometria (apenas se necessário).</summary>
    public void AppendRawSvg(string raw) => _svg.Append(raw);

    /// <summary>Comando <c>M x,y </c> com formatação customizada opcional.</summary>
    public void MoveToAbs(double x, double y, string? svgX = null, string? svgY = null, bool spaceAfterComma = false)
    {
        FlushCurrentSubpathIfNonEmpty();

        string xText = svgX ?? PythonMath.FormatFloat(x);
        string yText = svgY ?? PythonMath.FormatFloat(y);
        _svg.Append(spaceAfterComma ? $"M {xText}, {yText} " : $"M {xText},{yText} ");

        _currentX = x;
        _currentY = y;
        _subpathStartX = x;
        _subpathStartY = y;
        _currentSubpath = new List<Point2D> { new(x, y) };
        _hasDrawnInSubpath = false;
    }

    /// <summary>Comando relativo <c>m dx, dy </c>.</summary>
    public void MoveBy(double dx, double dy, string? svgDx = null, string? svgDy = null, bool spaceAfterComma = true)
    {
        FlushCurrentSubpathIfNonEmpty();

        string dxText = svgDx ?? PythonMath.FormatFloat(dx);
        string dyText = svgDy ?? PythonMath.FormatFloat(dy);
        _svg.Append(spaceAfterComma ? $"m {dxText}, {dyText} " : $"m {dxText},{dyText} ");

        _currentX += dx;
        _currentY += dy;
        _subpathStartX = _currentX;
        _subpathStartY = _currentY;
        _currentSubpath = new List<Point2D> { new(_currentX, _currentY) };
        _hasDrawnInSubpath = false;
    }

    /// <summary>Segmento horizontal relativo <c>h dx </c>.</summary>
    public void HorizontalBy(double dx, string? svgDx = null)
    {
        string dxText = svgDx ?? PythonMath.FormatFloat(dx);
        _svg.Append($"h {dxText} ");
        _currentX += dx;
        AddVertex(_currentX, _currentY);
    }

    /// <summary>Segmento vertical relativo <c>v dy </c>.</summary>
    public void VerticalBy(double dy, string? svgDy = null)
    {
        string dyText = svgDy ?? PythonMath.FormatFloat(dy);
        _svg.Append($"v {dyText} ");
        _currentY += dy;
        AddVertex(_currentX, _currentY);
    }

    /// <summary>Segmento reto absoluto <c>L x,y </c>.</summary>
    public void LineToAbs(double x, double y, string? svgX = null, string? svgY = null)
    {
        string xText = svgX ?? PythonMath.FormatFloat(x);
        string yText = svgY ?? PythonMath.FormatFloat(y);
        _svg.Append($"L {xText},{yText} ");
        _currentX = x;
        _currentY = y;
        AddVertex(_currentX, _currentY);
    }

    /// <summary>
    /// Arco elíptico/circular relativo <c>a rx,ry 0 largeArc sweep dx,dy </c> e discretização
    /// exata conforme especificação W3C SVG Apêndice F.6.5.
    /// </summary>
    public void ArcBy(
        double rx,
        double ry,
        int xAxisRotation,
        int largeArcFlag,
        int sweepFlag,
        double dx,
        double dy,
        string? svgDy = null)
    {
        string rxText = PythonMath.FormatFloat(rx);
        string ryText = PythonMath.FormatFloat(ry);
        string dxText = PythonMath.FormatFloat(dx);
        string dyText = svgDy ?? PythonMath.FormatFloat(dy);
        _svg.Append(
            CultureInfo.InvariantCulture,
            $"a {rxText},{ryText} {xAxisRotation} {largeArcFlag} {sweepFlag} {dxText},{dyText} ");

        double x1 = _currentX;
        double y1 = _currentY;
        double x2 = x1 + dx;
        double y2 = y1 + dy;

        if ((dx == 0.0 && dy == 0.0) || rx == 0.0 || ry == 0.0)
        {
            _currentX = x2;
            _currentY = y2;
            AddVertex(x2, y2);
            return;
        }

        // Parametrização centro de arco SVG (xAxisRotation == 0 para nossos arcos circulares/elípticos)
        double r = Math.Abs(rx);
        double halfDx = -dx / 2.0;
        double halfDy = -dy / 2.0;
        double distSq = (halfDx * halfDx) + (halfDy * halfDy);
        double rSq = r * r;
        if (distSq > rSq)
        {
            r = Math.Sqrt(distSq);
            rSq = r * r;
        }

        double factorSq = Math.Max(0.0, (rSq - distSq) / distSq);
        double factor = Math.Sqrt(factorSq);
        if (largeArcFlag == sweepFlag)
        {
            factor = -factor;
        }

        double cxPrime = factor * halfDy;
        double cyPrime = factor * (-halfDx);

        double cx = cxPrime + ((x1 + x2) / 2.0);
        double cy = cyPrime + ((y1 + y2) / 2.0);

        double startAngle = Math.Atan2(y1 - cy, x1 - cx);
        double endAngle = Math.Atan2(y2 - cy, x2 - cx);
        double deltaAngle = endAngle - startAngle;

        if (sweepFlag == 0 && deltaAngle > 0.0)
        {
            deltaAngle -= 2.0 * Math.PI;
        }
        else if (sweepFlag == 1 && deltaAngle < 0.0)
        {
            deltaAngle += 2.0 * Math.PI;
        }

        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(deltaAngle) / (Math.PI / 2.0) * _arcSegmentsPerQuarter));
        double scaleY = Math.Abs(ry) / Math.Abs(rx);

        for (int i = 1; i < steps; i++)
        {
            double angle = startAngle + (deltaAngle * i / steps);
            double px = cx + r * Math.Cos(angle);
            double py = cy + r * scaleY * Math.Sin(angle);
            AddVertex(px, py);
        }

        _currentX = x2;
        _currentY = y2;
        AddVertex(x2, y2);
    }

    /// <summary>Fecha o subcaminho atual (<c>Z</c>).</summary>
    public void ClosePath()
    {
        _svg.Append('Z');
        _currentX = _subpathStartX;
        _currentY = _subpathStartY;
        FlushCurrentSubpathIfNonEmpty();
    }

    /// <summary>Extrai todos os polígonos fechados que tiveram segmentos desenhados.</summary>
    public IReadOnlyList<Polygon2D> BuildPolygons(bool closeImplicitly = true)
    {
        FlushCurrentSubpathIfNonEmpty();

        var result = new List<Polygon2D>(_subpaths.Count);
        foreach (List<Point2D> raw in _subpaths)
        {
            if (raw.Count < 3)
            {
                continue;
            }

            // Remove ponto final redundante igual ao inicial em polígonos fechados.
            int count = raw.Count;
            if (count > 3 && AreSamePoint(raw[0], raw[count - 1]))
            {
                raw.RemoveAt(count - 1);
            }

            result.Add(new Polygon2D(raw.ToArray(), isClosed: closeImplicitly));
        }

        return result;
    }

    private void AddVertex(double x, double y)
    {
        _currentSubpath ??= new List<Point2D> { new(_subpathStartX, _subpathStartY) };

        var next = new Point2D(x, y);
        if (_currentSubpath.Count == 0 || !AreSamePoint(_currentSubpath[^1], next))
        {
            _currentSubpath.Add(next);
            _hasDrawnInSubpath = true;
        }
    }

    private void FlushCurrentSubpathIfNonEmpty()
    {
        if (_currentSubpath is not null && _hasDrawnInSubpath && _currentSubpath.Count >= 3)
        {
            _subpaths.Add(_currentSubpath);
        }

        _currentSubpath = null;
        _hasDrawnInSubpath = false;
    }

    private static bool AreSamePoint(Point2D a, Point2D b)
        => Math.Abs(a.X - b.X) <= VertexEpsilon && Math.Abs(a.Y - b.Y) <= VertexEpsilon;
}
