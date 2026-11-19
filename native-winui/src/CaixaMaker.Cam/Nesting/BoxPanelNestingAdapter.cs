using CaixaMaker.Cam.Svg;
using CaixaMaker.Cam.Text;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Nesting;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.Cam.Nesting;

/// <summary>
/// Adaptador direto em memória (zero escrita em disco) que extrai todas as chapas da caixa paramétrica ativa
/// com seus encaixes <i>Finger Joint</i>, furos estruturais e personalizações CNC acopladas como <see cref="NestingPart"/>.
/// </summary>
public sealed class BoxPanelNestingAdapter
{
    public IReadOnlyList<NestingPart> ExtractParts(
        IBoxModelDefinition boxModel,
        BoxParameters parameters,
        SurfaceManager? surfaceManager = null,
        int boxCopies = 1,
        NestingRotationPolicy rotationPolicy = NestingRotationPolicy.Step90)
    {
        ArgumentNullException.ThrowIfNull(boxModel);
        ArgumentNullException.ThrowIfNull(parameters);

        BoxCalculator calc = boxModel.CreateCalculator(parameters);
        return ExtractParts(calc, boxModel, surfaceManager, boxCopies, rotationPolicy);
    }

    public IReadOnlyList<NestingPart> ExtractParts(
        BoxCalculator calc,
        IBoxModelDefinition boxModel,
        SurfaceManager? surfaceManager = null,
        int boxCopies = 1,
        NestingRotationPolicy rotationPolicy = NestingRotationPolicy.Step90)
    {
        ArgumentNullException.ThrowIfNull(calc);
        ArgumentNullException.ThrowIfNull(boxModel);

        BoxDrawer drawer = surfaceManager?.Drawer ?? boxModel.CreateDrawer(calc);
        BoxSheetGeometry2D sheet = boxModel.BuildSheetGeometry(calc, drawer);
        SurfaceManager effectiveManager = surfaceManager ?? new SurfaceManager(calc, boxModel: boxModel, drawer: drawer);
        IReadOnlyList<PersonalizationElement> allElements = effectiveManager.ListElements();
        TextVectorizer? lazyTextVectorizer = null;

        int qty = Math.Max(1, boxCopies);
        var parts = new List<NestingPart>(sheet.Panels.Count);

        foreach (PanelGeometry2D panel in sheet.Panels)
        {
            Polygon2D outerSheetPoly = panel.GetSheetOuterContour();
            (double X, double Y)[] outerContour = outerSheetPoly.Vertices
                .Select(v => (v.X, v.Y))
                .ToArray();

            var features = new List<NestingPolyline2D>();
            var rasters = new List<NestingRasterOverlay>();

            // 1. Furos estruturais retangulares/poligonais (rasgos de alça, etc.)
            foreach (Polygon2D hole in panel.GetSheetInternalHoles())
            {
                if (hole.Vertices.Count >= 3)
                {
                    features.Add(new NestingPolyline2D(
                        hole.Vertices.Select(v => (v.X, v.Y)).ToArray(),
                        IsClosed: true,
                        NestingSemanticLayer.CutInternal,
                        StrokeWidthMm: 0.03,
                        SourceLayerName: "CUT_INTERNAL"));
                }
            }

            // 2. Furos circulares estruturais (eixos da dobradiça na aba lateral)
            foreach (Circle2D circle in panel.GetSheetCircularHoles())
            {
                if (circle.Radius > 1e-4)
                {
                    IReadOnlyList<(double X, double Y)> pts = NestingImportHelper.DiscretizeCircle(
                        circle.CenterX,
                        circle.CenterY,
                        circle.Radius);
                    features.Add(new NestingPolyline2D(
                        pts,
                        IsClosed: true,
                        NestingSemanticLayer.CutInternal,
                        StrokeWidthMm: 0.03,
                        SourceLayerName: "CUT_INTERNAL"));
                }
            }

            // 3. Elementos de personalização da face mapeados em coordenadas exatas CNC
            if (Enum.TryParse<SurfaceId>(panel.PanelId, ignoreCase: true, out SurfaceId surfaceId) &&
                effectiveManager.Surfaces.TryGetSurface(surfaceId, out _))
            {
                foreach (PersonalizationElement el in allElements)
                {
                    if (!el.Visible || el.PanelId != surfaceId)
                    {
                        continue;
                    }

                    AppendPersonalizationElement(
                        el,
                        effectiveManager.Surfaces,
                        features,
                        rasters,
                        ref lazyTextVectorizer);
                }
            }

            parts.Add(new NestingPart(
                id: $"box_{panel.PanelId}",
                name: ResolvePanelDisplayName(panel.PanelId),
                outerContour: outerContour,
                internalFeatures: features,
                rasterOverlays: rasters,
                sourceTag: "Caixa",
                sourcePanelKey: panel.PanelId,
                quantity: qty,
                rotationPolicy: rotationPolicy,
                enabled: true));
        }

        return parts;
    }

    private static void AppendPersonalizationElement(
        PersonalizationElement element,
        SurfaceMapper mapper,
        List<NestingPolyline2D> features,
        List<NestingRasterOverlay> rasters,
        ref TextVectorizer? lazyTextVectorizer)
    {
        Point2D center = element.CenterMm();
        PanelSvgTransformResult mapped = mapper.SurfaceToPanelSvg(
            element.PanelId,
            center.X,
            center.Y,
            rotationDeg: element.RotationDeg,
            originOffsetMm: null);

        double baseW = Math.Max(1e-4, GetElementBaseWidth(element));
        double baseH = Math.Max(1e-4, GetElementBaseHeight(element));
        double sx = element.WidthMm / baseW;
        double sy = element.HeightMm / baseH;
        double halfW = element.WidthMm * 0.5;
        double halfH = element.HeightMm * 0.5;

        AffineMatrix2D matrix = AffineMatrix2D
            .CreateTranslation(mapped.XMm, mapped.YMm)
            .Multiply(AffineMatrix2D.CreateRotation(mapped.RotationDeg))
            .Multiply(AffineMatrix2D.CreateTranslation(-halfW, -halfH))
            .Multiply(AffineMatrix2D.CreateScale(sx, sy));

        if (element is RasterImagePersonalizationElement raster && (!raster.Vectorize || raster.VectorPaths.Count == 0))
        {
            if (!string.IsNullOrWhiteSpace(raster.ProcessedImageB64))
            {
                try
                {
                    byte[] pngBytes = Convert.FromBase64String(raster.ProcessedImageB64);
                    Point2D transformedCenter = matrix.Transform(baseW * 0.5, baseH * 0.5);
                    rasters.Add(new NestingRasterOverlay(
                        transformedCenter.X - (element.WidthMm * 0.5),
                        transformedCenter.Y - (element.HeightMm * 0.5),
                        element.WidthMm,
                        element.HeightMm,
                        mapped.RotationDeg,
                        pngBytes,
                        element.Id));
                }
                catch (FormatException)
                {
                    // Ignora base64 inválido sem interromper outras chapas
                }
            }

            return;
        }

        if (element is ShapePersonalizationElement shapeEl && shapeEl.VectorPaths.Count == 0)
        {
            shapeEl.RebuildGeometry();
        }

        IReadOnlyList<VectorPathItem> items;
        if (element is TextPersonalizationElement textEl)
        {
            if (textEl.VectorPaths.Count > 0)
            {
                items = textEl.VectorPaths;
            }
            else
            {
                lazyTextVectorizer ??= new TextVectorizer();
                TextVectorizationResult vec = lazyTextVectorizer.Vectorize(
                    textEl.Content,
                    textEl.FontFamily,
                    textEl.FontPath,
                    textEl.FontSizeMm,
                    textEl.Bold,
                    textEl.Italic,
                    textEl.HorizontalAlign,
                    textEl.LetterSpacingMm,
                    textEl.LineSpacing,
                    textEl.RenderMode,
                    textEl.HatchSpacingMm);
                items = vec.Paths;
            }
        }
        else
        {
            items = element switch
            {
                VectorSvgPersonalizationElement svg => svg.Paths,
                RasterImagePersonalizationElement img => img.VectorPaths,
                ShapePersonalizationElement shape => shape.VectorPaths,
                _ => Array.Empty<VectorPathItem>(),
            };
        }

        int elementFeatureStart = features.Count;
        int itemIdx = 0;
        foreach (VectorPathItem item in items)
        {
            itemIdx++;
            if (string.IsNullOrWhiteSpace(item.D))
            {
                continue;
            }

            CncOperationType op = ResolveItemOperation(item, element);
            NestingSemanticLayer layer = NestingImportHelper.ResolveCncOperationLayer(op);
            double strokeW = layer is NestingSemanticLayer.CutInternal or NestingSemanticLayer.CutExternal ? 0.03 : 0.1;
            bool isFilled = layer == NestingSemanticLayer.EngraveFill && item.HasFill;
            string? compoundGroupId = isFilled ? $"el_{element.Id}_p{itemIdx}" : null;

            List<SvgPathSegment> segs = SvgDocumentParser.ParsePathData(item.D);
            IReadOnlyList<SvgSubpath> subpaths = SvgDocumentParser.SplitSubpaths(segs);

            foreach (SvgSubpath sub in subpaths)
            {
                List<(double X, double Y)> pts = NestingImportHelper.FlattenSvgSubpath(sub, matrix);
                if (pts.Count >= 2)
                {
                    bool isClosed = sub.IsClosed && pts.Count >= 3;
                    features.Add(new NestingPolyline2D(
                        pts,
                        IsClosed: isClosed,
                        Layer: layer,
                        StrokeWidthMm: strokeW,
                        SourceLayerName: layer.ToString(),
                        CompoundGroupId: isClosed ? compoundGroupId : null,
                        IsFilled: isFilled && isClosed));
                }
            }
        }

        NestingImportHelper.MergeConcentricFilledEngraveGroups(features, startIndex: elementFeatureStart);
    }

    private static CncOperationType ResolveItemOperation(VectorPathItem item, PersonalizationElement element)
    {
        if (element is ShapePersonalizationElement)
        {
            return item.Operation;
        }

        if (item.Operation == CncOperationType.CutInternal || element.Operation == CncOperationType.CutInternal)
        {
            return CncOperationType.CutInternal;
        }

        if (item.Operation == CncOperationType.Mark || element.Operation == CncOperationType.Mark)
        {
            return CncOperationType.Mark;
        }

        if (item.Operation == CncOperationType.EngraveFill || element.Operation == CncOperationType.EngraveFill)
        {
            return CncOperationType.EngraveFill;
        }

        return CncOperationType.EngraveVector;
    }

    private static double GetElementBaseWidth(PersonalizationElement element) => element switch
    {
        TextPersonalizationElement t => t.BaseWidthMm,
        VectorSvgPersonalizationElement v => v.BaseWidthMm,
        RasterImagePersonalizationElement r => r.BaseWidthMm,
        ShapePersonalizationElement s => s.BaseWidthMm,
        _ => element.WidthMm,
    };

    private static double GetElementBaseHeight(PersonalizationElement element) => element switch
    {
        TextPersonalizationElement t => t.BaseHeightMm,
        VectorSvgPersonalizationElement v => v.BaseHeightMm,
        RasterImagePersonalizationElement r => r.BaseHeightMm,
        ShapePersonalizationElement s => s.BaseHeightMm,
        _ => element.HeightMm,
    };

    private static string ResolvePanelDisplayName(string panelId) => panelId.ToLowerInvariant() switch
    {
        "front" => "Frente",
        "back" => "Traseira",
        "left" => "Lateral Esquerda",
        "right" => "Lateral Direita",
        "lid" => "Tampa",
        "bottom" => "Fundo",
        _ => panelId,
    };
}
