using System.Numerics;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.App.Viewport3D;

/// <summary>
/// Constrói a cena 3D paramétrica completa da caixa com extrusão real das chapas MDF
/// (incluindo dentes Finger Joint, orelhas de dobradiça, rasgos passantes e furos circulares),
/// cinemática de abertura da tampa, vista explodida, projeção das gravações CNC e cotas 3D.
/// </summary>
public static class BoxScene3DBuilder
{
    private const float OverlayNormalEpsilon = 0.12f;
    private const int MaxLocalMeshCacheEntries = 24;

    private readonly record struct LocalPanelMeshKey(
        string ModelId,
        double WidthMm,
        double DepthMm,
        double HeightMm,
        double ThicknessMm,
        double FolgaMm,
        int VerticalJointCount,
        int FrontBottomJointCount,
        int SideBottomJointCount);

    private sealed record CachedLocalPanelMesh(
        SurfaceId SurfaceId,
        string PartId,
        string Label,
        Vector3 ExplodeNormal,
        bool FollowsLidHinge,
        Vector3 LocalCenter,
        Vector3 LocalMinBound,
        Vector3 LocalMaxBound,
        MeshTriangle3D[] LocalTriangles,
        EdgeSegment3D[] LocalCreases);

    private sealed record CachedLocalBoxMeshes(CachedLocalPanelMesh[] Panels);

    private static readonly Dictionary<LocalPanelMeshKey, LinkedListNode<(LocalPanelMeshKey Key, CachedLocalBoxMeshes Value)>> _cachedLocalPanelMeshes = new();
    private static readonly LinkedList<(LocalPanelMeshKey Key, CachedLocalBoxMeshes Value)> _localMeshLru = new();
    private static readonly object _localMeshCacheLock = new();

    internal static void ClearLocalMeshCache()
    {
        lock (_localMeshCacheLock)
        {
            _cachedLocalPanelMeshes.Clear();
            _localMeshLru.Clear();
        }
    }

    public static BoxScene3D BuildScene(
        BoxCalculator calc,
        IBoxModelDefinition model,
        BoxSheetGeometry2D sheetGeometry,
        SurfaceManager surfaceManager,
        double lidAngleDeg = 0.0,
        bool isExplodedView = false)
    {
        ArgumentNullException.ThrowIfNull(calc);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sheetGeometry);
        ArgumentNullException.ThrowIfNull(surfaceManager);

        float t = (float)calc.Thickness;
        float w = (float)calc.Width;
        float d = (float)calc.Depth;
        float h = (float)calc.Height;

        float explode = isExplodedView
            ? MathF.Max(24f, MathF.Min(w, MathF.Min(d, h)) * 0.25f)
            : 0f;

        double effectiveLidAngleDeg = model.SupportsLidArticulation
            ? Math.Clamp(lidAngleDeg, 0.0, 110.0)
            : 0.0;
        float lidAngleRad = (float)(effectiveLidAngleDeg * Math.PI / 180.0);
        bool rotateLid = MathF.Abs(lidAngleRad) > 1e-5f;

        // Pivô exato do eixo dos pinos na dobradiça traseira das orelhas laterais
        var baseHingeOrigin = new Vector3(0f, h + t * 0.5f, -(d * 0.5f + 3f * t));
        Vector3 lidHingeWorld = baseHingeOrigin + new Vector3(0f, explode * 1.15f, 0f);

        CachedLocalBoxMeshes localMeshes = GetOrBuildLocalPanelMeshes(calc, model, sheetGeometry);

        IReadOnlyDictionary<SurfaceId, IReadOnlyList<PersonalizationElement>> elementsBySurface =
            BuildVisibleElementsBySurface(surfaceManager);

        var panels = new List<ExtrudedPanel3D>(localMeshes.Panels.Length);
        var minBound = new Vector3(float.PositiveInfinity);
        var maxBound = new Vector3(float.NegativeInfinity);

        foreach (CachedLocalPanelMesh cached in localMeshes.Panels)
        {
            float explodeDistance = ResolveExplodeDistance(cached.SurfaceId, explode);
            Vector3 explodeOffset = cached.ExplodeNormal * explodeDistance;
            bool applyLidRotation = cached.FollowsLidHinge && rotateLid;
            bool applyExplode = explodeOffset != Vector3.Zero;

            IReadOnlyList<MeshTriangle3D> triangles;
            IReadOnlyList<EdgeSegment3D> creases;
            Vector3 panelCenter;

            if (!applyLidRotation && !applyExplode)
            {
                triangles = cached.LocalTriangles;
                creases = cached.LocalCreases;
                panelCenter = cached.LocalCenter;
                minBound = Vector3.Min(minBound, cached.LocalMinBound);
                maxBound = Vector3.Max(maxBound, cached.LocalMaxBound);
            }
            else if (!applyLidRotation)
            {
                MeshTriangle3D[] srcTris = cached.LocalTriangles;
                var dstTris = new MeshTriangle3D[srcTris.Length];
                for (int i = 0; i < srcTris.Length; i++)
                {
                    MeshTriangle3D tri = srcTris[i];
                    dstTris[i] = new MeshTriangle3D(
                        tri.A + explodeOffset,
                        tri.B + explodeOffset,
                        tri.C + explodeOffset,
                        tri.Normal,
                        tri.Kind);
                }

                EdgeSegment3D[] srcCreases = cached.LocalCreases;
                var dstCreases = new EdgeSegment3D[srcCreases.Length];
                for (int i = 0; i < srcCreases.Length; i++)
                {
                    EdgeSegment3D edge = srcCreases[i];
                    dstCreases[i] = new EdgeSegment3D(
                        edge.Start + explodeOffset,
                        edge.End + explodeOffset,
                        edge.IsBoundaryCrease);
                }

                triangles = dstTris;
                creases = dstCreases;
                panelCenter = cached.LocalCenter + explodeOffset;
                minBound = Vector3.Min(minBound, cached.LocalMinBound + explodeOffset);
                maxBound = Vector3.Max(maxBound, cached.LocalMaxBound + explodeOffset);
            }
            else
            {
                MeshTriangle3D[] srcTris = cached.LocalTriangles;
                var dstTris = new MeshTriangle3D[srcTris.Length];
                for (int i = 0; i < srcTris.Length; i++)
                {
                    MeshTriangle3D tri = srcTris[i];
                    Vector3 a = RotateAroundHingeX(tri.A, baseHingeOrigin, lidAngleRad) + explodeOffset;
                    Vector3 b = RotateAroundHingeX(tri.B, baseHingeOrigin, lidAngleRad) + explodeOffset;
                    Vector3 c = RotateAroundHingeX(tri.C, baseHingeOrigin, lidAngleRad) + explodeOffset;
                    Vector3 n = Vector3.Normalize(RotateDirectionX(tri.Normal, lidAngleRad));
                    dstTris[i] = new MeshTriangle3D(a, b, c, n, tri.Kind);
                    ExpandBounds(ref minBound, ref maxBound, a, b, c);
                }

                EdgeSegment3D[] srcCreases = cached.LocalCreases;
                var dstCreases = new EdgeSegment3D[srcCreases.Length];
                for (int i = 0; i < srcCreases.Length; i++)
                {
                    EdgeSegment3D edge = srcCreases[i];
                    dstCreases[i] = new EdgeSegment3D(
                        RotateAroundHingeX(edge.Start, baseHingeOrigin, lidAngleRad) + explodeOffset,
                        RotateAroundHingeX(edge.End, baseHingeOrigin, lidAngleRad) + explodeOffset,
                        edge.IsBoundaryCrease);
                }

                triangles = dstTris;
                creases = dstCreases;
                panelCenter = RotateAroundHingeX(cached.LocalCenter, baseHingeOrigin, lidAngleRad) + explodeOffset;
            }

            // Overlay da superfície externa mapeada via SurfaceMapper.SurfaceTo3DLocal
            SurfaceOverlayQuad3D? overlay = null;
            if (surfaceManager.Surfaces.TryGetSurface(cached.SurfaceId, out SurfaceDefinition? surfDef) &&
                surfDef is not null)
            {
                elementsBySurface.TryGetValue(cached.SurfaceId, out IReadOnlyList<PersonalizationElement>? surfElements);
                overlay = BuildSurfaceOverlay(
                    surfaceManager.Surfaces,
                    surfDef,
                    cached.PartId,
                    cached.FollowsLidHinge,
                    baseHingeOrigin,
                    lidAngleRad,
                    explodeOffset,
                    surfElements);
            }

            panels.Add(new ExtrudedPanel3D(
                cached.SurfaceId,
                cached.PartId,
                cached.Label,
                panelCenter,
                cached.ExplodeNormal,
                cached.FollowsLidHinge,
                triangles,
                creases,
                overlay));
        }

        DimensionCallout3D[] dimensions = BuildDimensionCallouts(w, d, h, t);

        return new BoxScene3D(
            model.ModelId,
            w,
            d,
            h,
            t,
            effectiveLidAngleDeg,
            isExplodedView,
            lidHingeWorld,
            panels,
            dimensions,
            minBound,
            maxBound);
    }

    private static CachedLocalBoxMeshes GetOrBuildLocalPanelMeshes(
        BoxCalculator calc,
        IBoxModelDefinition model,
        BoxSheetGeometry2D sheetGeometry)
    {
        var key = new LocalPanelMeshKey(
            model.ModelId,
            calc.Width,
            calc.Depth,
            calc.Height,
            calc.Thickness,
            calc.Folga,
            calc.GetVerticalJoints().Count,
            calc.GetFrontBottomJoints().Count,
            calc.GetSideBottomJoints().Count);

        lock (_localMeshCacheLock)
        {
            if (_cachedLocalPanelMeshes.TryGetValue(key, out LinkedListNode<(LocalPanelMeshKey Key, CachedLocalBoxMeshes Value)>? node))
            {
                _localMeshLru.Remove(node);
                _localMeshLru.AddFirst(node);
                return node.Value.Value;
            }
        }

        float t = (float)calc.Thickness;
        var builtPanels = new CachedLocalPanelMesh[sheetGeometry.Panels.Count];

        for (int p = 0; p < sheetGeometry.Panels.Count; p++)
        {
            PanelGeometry2D panel2D = sheetGeometry.Panels[p];
            PanelBasis basis = ResolvePanelBasis(panel2D.PanelId, calc, model);
            TriangulatedPolygon2D tri2D = PolygonTriangulator2D.Triangulate(
                panel2D.OuterContour,
                panel2D.InternalHoles,
                panel2D.CircularHoles,
                basis.MapToPlane2D);

            Vector3 ToLocalWorldPoint(Vector2 uv, float extrusionDepth)
                => basis.Origin
                 + basis.UAxis * uv.X
                 + basis.VAxis * uv.Y
                 + basis.ExtrudeDir * extrusionDepth;

            Vector3 outerNormal = Vector3.Normalize(basis.ExtrudeDir);
            Vector3 innerNormal = -outerNormal;

            int estimatedTriangles = tri2D.Triangles.Count * 2
                + tri2D.OuterLoop.Count * 2
                + tri2D.HoleLoops.Sum(l => l.Count * 2);
            var triangles = new List<MeshTriangle3D>(estimatedTriangles);
            var creases = new List<EdgeSegment3D>(tri2D.OuterLoop.Count * 3 + tri2D.HoleLoops.Sum(l => l.Count * 3));
            var pMin = new Vector3(float.PositiveInfinity);
            var pMax = new Vector3(float.NegativeInfinity);

            // Tampas externa (w = t) e interna (w = 0)
            foreach ((Vector2 a2, Vector2 b2, Vector2 c2) in tri2D.Triangles)
            {
                Vector3 oa = ToLocalWorldPoint(a2, t);
                Vector3 ob = ToLocalWorldPoint(b2, t);
                Vector3 oc = ToLocalWorldPoint(c2, t);
                triangles.Add(new MeshTriangle3D(oa, ob, oc, outerNormal, PanelMaterialKind.OuterFace));

                Vector3 ia = ToLocalWorldPoint(a2, 0f);
                Vector3 ib = ToLocalWorldPoint(b2, 0f);
                Vector3 ic = ToLocalWorldPoint(c2, 0f);
                triangles.Add(new MeshTriangle3D(ia, ic, ib, innerNormal, PanelMaterialKind.InnerFace));

                ExpandBounds(ref pMin, ref pMax, oa, ob, oc);
                ExpandBounds(ref pMin, ref pMax, ia, ib, ic);
            }

            // Paredes laterais de corte a laser do contorno externo (CCW) e furos internos (CW)
            AppendSideWalls(tri2D.OuterLoop, t, ToLocalWorldPoint, static dir => Vector3.Normalize(dir), triangles, creases);
            foreach (IReadOnlyList<Vector2> holeLoop in tri2D.HoleLoops)
            {
                AppendSideWalls(holeLoop, t, ToLocalWorldPoint, static dir => Vector3.Normalize(dir), triangles, creases);
            }

            Vector3 localCenter = ComputeAverageCenter(tri2D.OuterLoop, t * 0.5f, ToLocalWorldPoint);

            builtPanels[p] = new CachedLocalPanelMesh(
                basis.SurfaceId,
                basis.PartId,
                basis.Label,
                basis.ExplodeNormal,
                basis.FollowsLidHinge,
                localCenter,
                pMin,
                pMax,
                triangles.ToArray(),
                creases.ToArray());
        }

        var created = new CachedLocalBoxMeshes(builtPanels);
        lock (_localMeshCacheLock)
        {
            if (_cachedLocalPanelMeshes.TryGetValue(key, out LinkedListNode<(LocalPanelMeshKey Key, CachedLocalBoxMeshes Value)>? existing))
            {
                _localMeshLru.Remove(existing);
                _localMeshLru.AddFirst(existing);
                return existing.Value.Value;
            }

            while (_cachedLocalPanelMeshes.Count >= MaxLocalMeshCacheEntries && _localMeshLru.Last is { } oldest)
            {
                _localMeshLru.RemoveLast();
                _cachedLocalPanelMeshes.Remove(oldest.Value.Key);
            }

            var newNode = new LinkedListNode<(LocalPanelMeshKey Key, CachedLocalBoxMeshes Value)>((key, created));
            _localMeshLru.AddFirst(newNode);
            _cachedLocalPanelMeshes[key] = newNode;
            return created;
        }
    }

    /// <summary>
    /// Reconstrói apenas os overlays de personalização sobre uma cena 3D estrutural MDF já triangulada,
    /// eliminando o custo de <see cref="PolygonTriangulator2D.Triangulate"/> durante arrastos 2D no modo Split.
    /// </summary>
    public static BoxScene3D RebuildSurfaceOverlays(
        BoxScene3D existingScene,
        IBoxModelDefinition model,
        SurfaceManager surfaceManager)
    {
        ArgumentNullException.ThrowIfNull(existingScene);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(surfaceManager);

        float t = (float)existingScene.ThicknessMm;
        float w = (float)existingScene.WidthMm;
        float d = (float)existingScene.DepthMm;
        float h = (float)existingScene.HeightMm;

        float explode = existingScene.IsExplodedView
            ? MathF.Max(24f, MathF.Min(w, MathF.Min(d, h)) * 0.25f)
            : 0f;
        double effectiveLidAngleDeg = model.SupportsLidArticulation
            ? Math.Clamp(existingScene.LidAngleDeg, 0.0, 110.0)
            : 0.0;
        float lidAngleRad = (float)(effectiveLidAngleDeg * Math.PI / 180.0);
        var baseHingeOrigin = new Vector3(0f, h + t * 0.5f, -(d * 0.5f + 3f * t));

        IReadOnlyDictionary<SurfaceId, IReadOnlyList<PersonalizationElement>> elementsBySurface =
            BuildVisibleElementsBySurface(surfaceManager);

        var updatedPanels = new List<ExtrudedPanel3D>(existingScene.Panels.Count);
        foreach (ExtrudedPanel3D panel in existingScene.Panels)
        {
            float explodeDistance = ResolveExplodeDistance(panel.SurfaceId, explode);
            Vector3 explodeOffset = panel.ExplodeNormal * explodeDistance;

            SurfaceOverlayQuad3D? overlay = null;
            if (surfaceManager.Surfaces.TryGetSurface(panel.SurfaceId, out SurfaceDefinition? surfDef) &&
                surfDef is not null)
            {
                elementsBySurface.TryGetValue(panel.SurfaceId, out IReadOnlyList<PersonalizationElement>? surfElements);
                overlay = BuildSurfaceOverlay(
                    surfaceManager.Surfaces,
                    surfDef,
                    panel.PartId,
                    panel.FollowsLidHinge,
                    baseHingeOrigin,
                    lidAngleRad,
                    explodeOffset,
                    surfElements);
            }

            updatedPanels.Add(panel with { OverlayQuad = overlay });
        }

        return existingScene with { Panels = updatedPanels };
    }

    private static Dictionary<SurfaceId, IReadOnlyList<PersonalizationElement>> BuildVisibleElementsBySurface(
        SurfaceManager surfaceManager)
        => surfaceManager.ListElements()
            .Where(e => e.Visible)
            .GroupBy(e => e.PanelId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PersonalizationElement>)g.ToArray());

    private static float ResolveExplodeDistance(SurfaceId surfaceId, float explode)
        => surfaceId == SurfaceId.Lid ? explode * 1.15f
            : surfaceId == SurfaceId.Bottom ? explode * 0.75f
            : explode;

    /// <summary>
    /// Transforma coordenadas 2D de uma superfície via <see cref="SurfaceMapper.SurfaceTo3DLocal"/>
    /// aplicando cinemática da tampa articulada e deslocamento da vista explodida.
    /// </summary>
    public static Vector3 TransformSurfacePointToWorld(
        SurfaceMapper mapper,
        SurfaceId surfaceId,
        double surfaceXMm,
        double surfaceYMm,
        double lidAngleDeg = 0.0,
        bool isExplodedView = false,
        float normalOffsetMm = 0f)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        SurfaceDefinition surf = mapper.GetSurface(surfaceId);
        Point3D localPt = mapper.SurfaceTo3DLocal(surfaceId, surfaceXMm, surfaceYMm);
        Vector3 world = new((float)localPt.X, (float)localPt.Y, (float)localPt.Z);
        Vector3 normal = Vector3.Normalize(new Vector3(
            (float)surf.Normal3D[0],
            (float)surf.Normal3D[1],
            (float)surf.Normal3D[2]));

        world += normal * normalOffsetMm;

        double t = mapper.Calc.Thickness;
        double d = mapper.Calc.Depth;
        double w = mapper.Calc.Width;
        double h = mapper.Calc.Height;

        float explode = isExplodedView
            ? MathF.Max(24f, MathF.Min((float)w, MathF.Min((float)d, (float)h)) * 0.25f)
            : 0f;

        if (surfaceId == SurfaceId.Lid)
        {
            double angleRad = Math.Clamp(lidAngleDeg, 0.0, 110.0) * Math.PI / 180.0;
            var hingeOrigin = new Vector3(0f, (float)(h + t * 0.5), -(float)(d * 0.5 + 3.0 * t));
            world = RotateAroundHingeX(world, hingeOrigin, (float)angleRad);
            explode *= 1.15f;
        }
        else if (surfaceId == SurfaceId.Bottom)
        {
            explode *= 0.75f;
        }

        return world + normal * explode;
    }

    private static SurfaceOverlayQuad3D BuildSurfaceOverlay(
        SurfaceMapper mapper,
        SurfaceDefinition surf,
        string partId,
        bool followsLidHinge,
        Vector3 hingeOrigin,
        float lidAngleRad,
        Vector3 explodeOffset,
        IReadOnlyList<PersonalizationElement>? surfElements)
    {
        Vector3 surfNormal = Vector3.Normalize(new Vector3(
            (float)surf.Normal3D[0],
            (float)surf.Normal3D[1],
            (float)surf.Normal3D[2]));

        Vector3 MapPoint(double xMm, double yMm)
        {
            Point3D local = mapper.SurfaceTo3DLocal(surf.PanelId, xMm, yMm);
            var pt = new Vector3((float)local.X, (float)local.Y, (float)local.Z) + surfNormal * OverlayNormalEpsilon;
            if (followsLidHinge && MathF.Abs(lidAngleRad) > 1e-5f)
            {
                pt = RotateAroundHingeX(pt, hingeOrigin, lidAngleRad);
            }

            return pt + explodeOffset;
        }

        Vector3 tl = MapPoint(0.0, 0.0);
        Vector3 tr = MapPoint(surf.WidthMm, 0.0);
        Vector3 br = MapPoint(surf.WidthMm, surf.HeightMm);
        Vector3 bl = MapPoint(0.0, surf.HeightMm);

        Vector3 rotatedNormal = followsLidHinge && MathF.Abs(lidAngleRad) > 1e-5f
            ? Vector3.Normalize(RotateDirectionX(surfNormal, lidAngleRad))
            : surfNormal;

        var projectedElements = new List<ProjectedElement3D>(surfElements?.Count ?? 0);
        if (surfElements is not null)
        {
            foreach (PersonalizationElement el in surfElements)
            {
                IReadOnlyList<Point2D> corners = el.Corners2D();
                Vector3[] worldCorners = corners
                    .Select(c => MapPoint(c.X, c.Y))
                    .ToArray();
                projectedElements.Add(new ProjectedElement3D(el.Id, worldCorners));
            }
        }

        return new SurfaceOverlayQuad3D(
            surf.PanelId,
            partId,
            surf.WidthMm,
            surf.HeightMm,
            tl,
            tr,
            br,
            bl,
            rotatedNormal,
            projectedElements);
    }

    private static void AppendSideWalls(
        IReadOnlyList<Vector2> loop,
        float thickness,
        Func<Vector2, float, Vector3> toWorldPoint,
        Func<Vector3, Vector3> transformDir,
        List<MeshTriangle3D> triangles,
        List<EdgeSegment3D> creases)
    {
        int n = loop.Count;
        if (n < 3) return;

        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = loop[i];
            Vector2 p1 = loop[(i + 1) % n];

            Vector3 i0 = toWorldPoint(p0, 0f);
            Vector3 i1 = toWorldPoint(p1, 0f);
            Vector3 o0 = toWorldPoint(p0, thickness);
            Vector3 o1 = toWorldPoint(p1, thickness);

            Vector3 edgeVec = i1 - i0;
            Vector3 extrudeVec = o0 - i0;
            Vector3 wallNormal = Vector3.Cross(edgeVec, extrudeVec);
            float nLen = wallNormal.Length();
            if (nLen <= 1e-6f) continue;

            wallNormal /= nLen;

            // Dois triângulos da face lateral cortada a laser
            triangles.Add(new MeshTriangle3D(i0, i1, o1, wallNormal, PanelMaterialKind.CutEdge));
            triangles.Add(new MeshTriangle3D(i0, o1, o0, wallNormal, PanelMaterialKind.CutEdge));

            // Arestas de contorno externo/interno para nitidez do Finger Joint
            creases.Add(new EdgeSegment3D(o0, o1, IsBoundaryCrease: true));
            creases.Add(new EdgeSegment3D(i0, i1, IsBoundaryCrease: false));

            // Só adiciona aresta vertical nos cantos agudos (pula segmentos suaves de arco circular)
            Vector2 prev = loop[(i - 1 + n) % n];
            Vector2 dPrev = Vector2.Normalize(p0 - prev);
            Vector2 dCurr = Vector2.Normalize(p1 - p0);
            if (Vector2.Dot(dPrev, dCurr) < 0.94f)
            {
                creases.Add(new EdgeSegment3D(i0, o0, IsBoundaryCrease: true));
            }
        }
    }

    private static Vector3 ComputeAverageCenter(
        IReadOnlyList<Vector2> loop,
        float midDepth,
        Func<Vector2, float, Vector3> toWorldPoint)
    {
        if (loop.Count == 0)
        {
            return toWorldPoint(Vector2.Zero, midDepth);
        }

        Vector2 min = new(float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity);
        foreach (Vector2 p in loop)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        return toWorldPoint((min + max) * 0.5f, midDepth);
    }

    private static Vector3 RotateAroundHingeX(Vector3 p, Vector3 hingeOrigin, float thetaRad)
    {
        Vector3 rel = p - hingeOrigin;
        Vector3 rotated = RotateDirectionX(rel, thetaRad);
        return hingeOrigin + rotated;
    }

    private static Vector3 RotateDirectionX(Vector3 v, float thetaRad)
    {
        // Abertura positiva thetaRad rotaciona o plano +Z (frontal) para cima (+Y) ao redor do eixo X traseiro.
        float c = MathF.Cos(thetaRad);
        float s = MathF.Sin(thetaRad);
        return new Vector3(
            v.X,
            v.Y * c + v.Z * s,
            -v.Y * s + v.Z * c);
    }

    private static DimensionCallout3D[] BuildDimensionCallouts(float w, float d, float h, float t)
    {
        float hw = w * 0.5f;
        float hd = d * 0.5f;
        float offset = MathF.Max(18f, t * 3.5f);
        float lineZ = hd + t + offset;
        float lineX = hw + t + offset;

        return
        [
            new DimensionCallout3D(
                new Vector3(-hw, 0f, lineZ),
                new Vector3(hw, 0f, lineZ),
                new Vector3(-hw, 0f, hd),
                new Vector3(hw, 0f, hd),
                new Vector3(0f, 0f, lineZ + 6f),
                $"Largura: {w:0} mm",
                $"Base total: {w + 2f * t:0} mm",
                BoxDimensionAxis.WidthX),

            new DimensionCallout3D(
                new Vector3(lineX, 0f, -hd),
                new Vector3(lineX, 0f, hd),
                new Vector3(hw, 0f, -hd),
                new Vector3(hw, 0f, hd),
                new Vector3(lineX + 6f, 0f, 0f),
                $"Profundidade: {d:0} mm",
                $"Paredes: {d + 2f * t:0} mm",
                BoxDimensionAxis.DepthZ),

            new DimensionCallout3D(
                new Vector3(-(hw + t + offset), 0f, hd + t),
                new Vector3(-(hw + t + offset), h, hd + t),
                new Vector3(-(hw + t), 0f, hd + t),
                new Vector3(-(hw + t), h, hd + t),
                new Vector3(-(hw + t + offset + 6f), h * 0.5f, hd + t),
                $"Altura: {h:0} mm",
                null,
                BoxDimensionAxis.HeightY),
        ];
    }

    private static PanelBasis ResolvePanelBasis(string panelId, BoxCalculator calc, IBoxModelDefinition model)
    {
        float s = (float)BoxDrawer.StartingCoordinate;
        float t = (float)calc.Thickness;
        float w = (float)calc.Width;
        float d = (float)calc.Depth;
        float h = (float)calc.Height;
        float hw = w * 0.5f;
        float hd = d * 0.5f;

        return panelId switch
        {
            "front" => new PanelBasis(
                SurfaceId.Front,
                "front_back",
                "Frente (+Z)",
                new Vector3(0f, 0f, hd - t),
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector3.UnitZ,
                FollowsLidHinge: false,
                pt => new Vector2((float)(pt.X - s) - hw, h - (float)(pt.Y - s))),

            "back" => new PanelBasis(
                SurfaceId.Back,
                "front_back",
                "Traseira (-Z)",
                new Vector3(0f, 0f, -hd + t),
                -Vector3.UnitX,
                Vector3.UnitY,
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                FollowsLidHinge: false,
                pt => new Vector2((float)(pt.X - s) - hw, h - (float)(pt.Y - s))),

            "right" => new PanelBasis(
                SurfaceId.Right,
                "side",
                "Lateral Direita (+X)",
                new Vector3(hw - t, 0f, 0f),
                -Vector3.UnitZ,
                Vector3.UnitY,
                Vector3.UnitX,
                Vector3.UnitX,
                FollowsLidHinge: false,
                pt => new Vector2((float)(pt.X - s) - (hd + t), h - (float)(pt.Y - s))),

            "left" => new PanelBasis(
                SurfaceId.Left,
                "side",
                "Lateral Esquerda (-X)",
                new Vector3(-hw + t, 0f, 0f),
                Vector3.UnitZ,
                Vector3.UnitY,
                -Vector3.UnitX,
                -Vector3.UnitX,
                FollowsLidHinge: false,
                pt => new Vector2((hd + t) - (float)(pt.X - s), h - (float)(pt.Y - s))),

            "bottom" => new PanelBasis(
                SurfaceId.Bottom,
                "bottom",
                "Base (-Y)",
                new Vector3(0f, t, 0f),
                Vector3.UnitX,
                Vector3.UnitZ,
                -Vector3.UnitY,
                -Vector3.UnitY,
                FollowsLidHinge: false,
                pt => new Vector2((float)(pt.X - s) - (hw + t), (float)(pt.Y - s) - (hd + t))),

            "lid" => new PanelBasis(
                SurfaceId.Lid,
                "lid",
                "Tampa (+Y)",
                new Vector3(0f, h, 0f),
                Vector3.UnitX,
                -Vector3.UnitZ,
                Vector3.UnitY,
                Vector3.UnitY,
                FollowsLidHinge: model.SupportsLidArticulation,
                pt => new Vector2((float)(pt.X - s) - (hw + t), (float)(pt.Y - s) - (hd + t))),

            _ => throw new ArgumentException($"Painel desconhecido: {panelId}", nameof(panelId)),
        };
    }

    private static void ExpandBounds(ref Vector3 min, ref Vector3 max, Vector3 a, Vector3 b, Vector3 c)
    {
        min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
        max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
    }

    private readonly record struct PanelBasis(
        SurfaceId SurfaceId,
        string PartId,
        string Label,
        Vector3 Origin,
        Vector3 UAxis,
        Vector3 VAxis,
        Vector3 ExtrudeDir,
        Vector3 ExplodeNormal,
        bool FollowsLidHinge,
        Func<Point2D, Vector2> MapToPlane2D);
}
