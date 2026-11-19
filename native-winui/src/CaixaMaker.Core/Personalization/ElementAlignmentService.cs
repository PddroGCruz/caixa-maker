using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.Personalization;

/// <summary>Modo de referência espacial utilizado para alinhamento em grupo ou individual.</summary>
public enum AlignmentReferenceMode
{
    SurfaceSafeArea,
    FirstSelected,
    LastSelected,
    SelectionBounds,
}

/// <summary>Operações suportadas de alinhamento nos 6 extremos/centros e distribuição uniforme.</summary>
public enum AlignmentAction
{
    AlignLeft,
    AlignCenterX,
    AlignRight,
    AlignTop,
    AlignCenterY,
    AlignBottom,
    DistributeHorizontal,
    DistributeVertical,
}

/// <summary>Limites reais alinhados aos eixos de um elemento ou referência no plano 2D da chapa (mm).</summary>
public readonly record struct ElementBounds2D(
    double MinX,
    double MaxX,
    double MinY,
    double MaxY)
{
    public double Width => Math.Max(0.0, MaxX - MinX);
    public double Height => Math.Max(0.0, MaxY - MinY);
    public double CenterX => (MinX + MaxX) * 0.5;
    public double CenterY => (MinY + MaxY) * 0.5;
}

/// <summary>Deslocamento e nova coordenada (Top-Left em mm) calculados para um elemento após alinhamento/distribuição.</summary>
public readonly record struct ElementAlignmentDelta(
    string ElementId,
    double DeltaXMm,
    double DeltaYMm,
    double NewXMm,
    double NewYMm);

/// <summary>
/// Motor determinístico de alinhamento e distribuição CAD 1:1 (estilo Inkscape) para elementos
/// em coordenadas locais da chapa, respeitando rotação, AnchorMode e área útil CNC.
/// </summary>
public static class ElementAlignmentService
{
    public static ElementBounds2D GetElementBounds(PersonalizationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        double rotMod = Math.Abs(element.RotationDeg % 360.0);
        if (rotMod < 1e-9 || Math.Abs(rotMod - 360.0) < 1e-9)
        {
            return new ElementBounds2D(
                element.XMm,
                element.XMm + element.WidthMm,
                element.YMm,
                element.YMm + element.HeightMm);
        }

        if (element is ShapePersonalizationElement { ShapeSpec.Kind: GeometricShapeKind.Circle } circleEl)
        {
            Point2D center = circleEl.CenterMm();
            double rx = circleEl.WidthMm * 0.5;
            double ry = circleEl.HeightMm * 0.5;
            return new ElementBounds2D(center.X - rx, center.X + rx, center.Y - ry, center.Y + ry);
        }

        IReadOnlyList<Point2D> poly = element is ShapePersonalizationElement { ShapeSpec.Kind: GeometricShapeKind.Slot or GeometricShapeKind.Polygon } shapeEl
            ? shapeEl.GetCollisionPolygon2D()
            : element.Corners2D();

        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;

        for (int i = 0; i < poly.Count; i++)
        {
            Point2D p = poly[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        if (minX > maxX)
        {
            return new ElementBounds2D(
                element.XMm,
                element.XMm + element.WidthMm,
                element.YMm,
                element.YMm + element.HeightMm);
        }

        return new ElementBounds2D(minX, maxX, minY, maxY);
    }

    public static IReadOnlyList<ElementAlignmentDelta> ComputeAlignedPositions(
        IReadOnlyList<PersonalizationElement> orderedElements,
        SurfaceDefinition surface,
        AlignmentAction action,
        AlignmentReferenceMode referenceMode,
        double safetyMarginMm = GeometryValidator.DefaultSafetyMarginMm)
    {
        ArgumentNullException.ThrowIfNull(orderedElements);
        ArgumentNullException.ThrowIfNull(surface);

        if (orderedElements.Count == 0)
        {
            return [];
        }

        if (action is AlignmentAction.DistributeHorizontal or AlignmentAction.DistributeVertical)
        {
            return ComputeDistribution(orderedElements, action == AlignmentAction.DistributeHorizontal);
        }

        var bounds = new ElementBounds2D[orderedElements.Count];
        for (int i = 0; i < orderedElements.Count; i++)
        {
            bounds[i] = GetElementBounds(orderedElements[i]);
        }

        ElementBounds2D referenceBounds;
        string? fixedAnchorId = null;

        switch (referenceMode)
        {
            case AlignmentReferenceMode.SurfaceSafeArea:
            {
                Rect2D safe = surface.SafeAreaForMargin(safetyMarginMm);
                referenceBounds = new ElementBounds2D(safe.XMm, safe.Right, safe.YMm, safe.Bottom);
                break;
            }

            case AlignmentReferenceMode.FirstSelected:
                if (orderedElements.Count < 2)
                {
                    return [];
                }
                fixedAnchorId = orderedElements[0].Id;
                referenceBounds = bounds[0];
                break;

            case AlignmentReferenceMode.LastSelected:
                if (orderedElements.Count < 2)
                {
                    return [];
                }
                fixedAnchorId = orderedElements[^1].Id;
                referenceBounds = bounds[^1];
                break;

            case AlignmentReferenceMode.SelectionBounds:
                if (orderedElements.Count < 2)
                {
                    return [];
                }
                referenceBounds = ComputeUnionBounds(bounds);
                break;

            default:
                return [];
        }

        var deltas = new List<ElementAlignmentDelta>(orderedElements.Count);
        for (int i = 0; i < orderedElements.Count; i++)
        {
            PersonalizationElement el = orderedElements[i];
            if (el.Locked || string.Equals(el.Id, fixedAnchorId, StringComparison.Ordinal))
            {
                continue;
            }

            ElementBounds2D b = bounds[i];
            double dx = 0.0;
            double dy = 0.0;

            switch (action)
            {
                case AlignmentAction.AlignLeft:
                    dx = referenceBounds.MinX - b.MinX;
                    break;
                case AlignmentAction.AlignCenterX:
                    dx = referenceBounds.CenterX - b.CenterX;
                    break;
                case AlignmentAction.AlignRight:
                    dx = referenceBounds.MaxX - b.MaxX;
                    break;
                case AlignmentAction.AlignTop:
                    dy = referenceBounds.MinY - b.MinY;
                    break;
                case AlignmentAction.AlignCenterY:
                    dy = referenceBounds.CenterY - b.CenterY;
                    break;
                case AlignmentAction.AlignBottom:
                    dy = referenceBounds.MaxY - b.MaxY;
                    break;
            }

            dx = PythonMath.Round(dx, 4);
            dy = PythonMath.Round(dy, 4);
            if (Math.Abs(dx) > 1e-4 || Math.Abs(dy) > 1e-4)
            {
                deltas.Add(new ElementAlignmentDelta(
                    el.Id,
                    dx,
                    dy,
                    PythonMath.Round(el.XMm + dx, 4),
                    PythonMath.Round(el.YMm + dy, 4)));
            }
        }

        return deltas;
    }

    private static IReadOnlyList<ElementAlignmentDelta> ComputeDistribution(
        IReadOnlyList<PersonalizationElement> orderedElements,
        bool horizontal)
    {
        if (orderedElements.Count < 3)
        {
            return [];
        }

        var items = new (PersonalizationElement El, ElementBounds2D Bounds, int OrigIndex)[orderedElements.Count];
        for (int i = 0; i < orderedElements.Count; i++)
        {
            PersonalizationElement el = orderedElements[i];
            items[i] = (el, GetElementBounds(el), i);
        }

        Array.Sort(items, (a, b) =>
        {
            double ca = horizontal ? a.Bounds.CenterX : a.Bounds.CenterY;
            double cb = horizontal ? b.Bounds.CenterX : b.Bounds.CenterY;
            int cmp = ca.CompareTo(cb);
            if (cmp != 0) return cmp;

            double ma = horizontal ? a.Bounds.MinX : a.Bounds.MinY;
            double mb = horizontal ? b.Bounds.MinX : b.Bounds.MinY;
            cmp = ma.CompareTo(mb);
            return cmp != 0 ? cmp : a.OrigIndex.CompareTo(b.OrigIndex);
        });

        double minCenter = horizontal ? items[0].Bounds.CenterX : items[0].Bounds.CenterY;
        double maxCenter = horizontal ? items[^1].Bounds.CenterX : items[^1].Bounds.CenterY;
        double step = (maxCenter - minCenter) / (items.Length - 1);

        var deltas = new List<ElementAlignmentDelta>(items.Length - 2);
        for (int i = 1; i < items.Length - 1; i++)
        {
            PersonalizationElement el = items[i].El;
            if (el.Locked)
            {
                continue;
            }

            ElementBounds2D b = items[i].Bounds;
            double targetCenter = minCenter + i * step;
            double dx = horizontal ? PythonMath.Round(targetCenter - b.CenterX, 4) : 0.0;
            double dy = horizontal ? 0.0 : PythonMath.Round(targetCenter - b.CenterY, 4);

            if (Math.Abs(dx) > 1e-4 || Math.Abs(dy) > 1e-4)
            {
                deltas.Add(new ElementAlignmentDelta(
                    el.Id,
                    dx,
                    dy,
                    PythonMath.Round(el.XMm + dx, 4),
                    PythonMath.Round(el.YMm + dy, 4)));
            }
        }

        return deltas;
    }

    private static ElementBounds2D ComputeUnionBounds(ElementBounds2D[] bounds)
    {
        double minX = bounds[0].MinX;
        double maxX = bounds[0].MaxX;
        double minY = bounds[0].MinY;
        double maxY = bounds[0].MaxY;

        for (int i = 1; i < bounds.Length; i++)
        {
            ElementBounds2D b = bounds[i];
            if (b.MinX < minX) minX = b.MinX;
            if (b.MaxX > maxX) maxX = b.MaxX;
            if (b.MinY < minY) minY = b.MinY;
            if (b.MaxY > maxY) maxY = b.MaxY;
        }

        return new ElementBounds2D(minX, maxX, minY, maxY);
    }
}
