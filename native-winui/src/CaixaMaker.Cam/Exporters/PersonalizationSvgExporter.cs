using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.Cam.Exporters;

/// <summary>Modos de exportação SVG/ZIP disponíveis no motor CAM.</summary>
public enum PersonalizationExportMode
{
    FullBox,
    PanelCombined,
    PanelEngraveOnly,
    AllPanelsZip,
}

/// <summary>Resultado da geração do pacote ZIP multi-peça com manifesto JSON.</summary>
public sealed record ExportBundleResult(
    byte[] ZipBytes,
    IReadOnlyDictionary<string, string> Files,
    string ManifestJson);

/// <summary>
/// Exportador vetorial SVG multicamadas CNC (6 camadas semânticas),
/// gerando folha completa, peça individual corte+gravação, somente gravação e pacote ZIP.
/// </summary>
public sealed class PersonalizationSvgExporter
{
    private static readonly CncOperationType[] EngraveLayerOrder =
    [
        CncOperationType.EngraveRaster,
        CncOperationType.EngraveFill,
        CncOperationType.EngraveVector,
        CncOperationType.Mark,
    ];

    private static readonly CncOperationType[] AllLayerOrder =
    [
        CncOperationType.EngraveRaster,
        CncOperationType.EngraveFill,
        CncOperationType.EngraveVector,
        CncOperationType.Mark,
        CncOperationType.CutInternal,
        CncOperationType.CutExternal,
    ];

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    public BoxCalculator Calc => Manager.Calc;
    public BoxDrawer Drawer => Manager.Drawer;
    public IBoxModelDefinition BoxModel => Manager.BoxModel;
    public SurfaceMapper Surfaces => Manager.Surfaces;
    public SurfaceManager Manager { get; }

    public PersonalizationSvgExporter(SurfaceManager manager)
    {
        Manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public PersonalizationSvgExporter(
        BoxCalculator calc,
        BoxDrawer? drawer = null,
        IBoxModelDefinition? boxModel = null)
        : this(new SurfaceManager(calc, boxModel: boxModel, drawer: drawer))
    {
    }

    public string ExportFullBoxSvg(
        IEnumerable<PersonalizationElement>? elements = null,
        string? outputPath = null)
    {
        IEnumerable<PersonalizationElement> elementList = elements ?? Manager.ListElements();
        BoxSheetGeometry2D sheet = BoxModel.BuildSheetGeometry(Calc, Drawer);

        var layerContents = InitLayerBuffers(includeCutLayers: true);

        foreach (PanelGeometry2D panel in sheet.Panels)
        {
            AppendCutPanelGeometry(panel, layerContents);
        }

        foreach (PersonalizationElement el in elementList)
        {
            if (!el.Visible || !Surfaces.TryGetSurface(el.PanelId, out _))
            {
                continue;
            }

            AppendElementToLayers(el, layerContents, originOffsetMm: null, usePanelCncOrientation: true);
        }

        string xml = AssembleSvgDocument(sheet.Layout.SheetWidth, sheet.Layout.SheetHeight, includeCutLayers: true, layerContents);
        WriteTextIfRequested(outputPath, xml);
        return xml;
    }

    public string ExportPanelCombinedSvg(
        SurfaceId panelId,
        IEnumerable<PersonalizationElement>? elements = null,
        string? outputPath = null)
    {
        SurfaceDefinition surface = Surfaces.GetSurface(panelId);
        IEnumerable<PersonalizationElement> elementList = elements ?? Manager.ListElements(panelId);

        (double canvasW, double canvasH, double drawX, double drawY, Point2D cellOrigin) =
            GetSinglePanelCanvasGeometry(surface);

        string panelKey = SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(panelId);
        PanelGeometry2D panelGeom = Drawer.BuildPanelGeometry(
            panelKey,
            drawX,
            drawY,
            hasHingeEar: BoxModel.HasHandles);

        var layerContents = InitLayerBuffers(includeCutLayers: true);
        AppendCutPanelGeometry(panelGeom, layerContents);

        foreach (PersonalizationElement el in elementList)
        {
            if (!el.Visible || el.PanelId != panelId)
            {
                continue;
            }

            AppendElementToLayers(el, layerContents, originOffsetMm: cellOrigin, usePanelCncOrientation: true);
        }

        string xml = AssembleSvgDocument(canvasW, canvasH, includeCutLayers: true, layerContents);
        WriteTextIfRequested(outputPath, xml);
        return xml;
    }

    public string ExportPanelEngraveOnlySvg(
        SurfaceId panelId,
        IEnumerable<PersonalizationElement>? elements = null,
        bool applyCncOrientation = false,
        string? outputPath = null)
    {
        SurfaceDefinition surface = Surfaces.GetSurface(panelId);
        IEnumerable<PersonalizationElement> elementList = elements ?? Manager.ListElements(panelId);

        var layerContents = InitLayerBuffers(includeCutLayers: false);

        foreach (PersonalizationElement el in elementList)
        {
            if (!el.Visible || el.PanelId != panelId)
            {
                continue;
            }

            if (applyCncOrientation)
            {
                Point2D center = el.CenterMm();
                double cxSvg;
                double cySvg;
                double rotSvg;
                if (Math.Abs(surface.CncRotationDeg - 180.0) < 1e-6)
                {
                    cxSvg = surface.WidthMm - center.X;
                    cySvg = surface.HeightMm - center.Y;
                    rotSvg = NormalizeAngle(el.RotationDeg + 180.0);
                }
                else
                {
                    cxSvg = center.X;
                    cySvg = center.Y;
                    rotSvg = NormalizeAngle(el.RotationDeg);
                }

                string transform = BuildElementTransformAttribute(el, cxSvg, cySvg, rotSvg);
                AppendTransformedElement(el, transform, layerContents);
            }
            else
            {
                AppendElementToLayers(el, layerContents, originOffsetMm: new Point2D(0.0, 0.0), usePanelCncOrientation: false);
            }
        }

        string xml = AssembleSvgDocument(surface.WidthMm, surface.HeightMm, includeCutLayers: false, layerContents);
        WriteTextIfRequested(outputPath, xml);
        return xml;
    }

    public ExportBundleResult ExportAllPanelsBundle(
        IEnumerable<PersonalizationElement>? elements = null,
        bool includeEngraveOnly = true,
        bool includeFullSheet = true,
        string projectPrefix = "caixa-maker",
        string? outputZipPath = null)
    {
        List<PersonalizationElement> elemList = (elements ?? Manager.ListElements()).ToList();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (includeFullSheet)
        {
            files[$"{projectPrefix}_folha_completa.svg"] = ExportFullBoxSvg(elemList);
        }

        var panelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SurfaceDefinition surf in Surfaces.ListSurfaces())
        {
            string pid = SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(surf.PanelId);
            panelCounts[pid] = elemList.Count(e => e.PanelId == surf.PanelId);

            files[$"pecas_corte_e_gravacao/{projectPrefix}_{pid}_completo.svg"] =
                ExportPanelCombinedSvg(surf.PanelId, elemList);

            if (includeEngraveOnly)
            {
                files[$"pecas_somente_gravacao/{projectPrefix}_{pid}_gravacao.svg"] =
                    ExportPanelEngraveOnlySvg(surf.PanelId, elemList);
            }
        }

        var layerSpecsManifest = AllLayerOrder
            .Select(op => CncLayerSpecs.Get(op))
            .ToDictionary(
                spec => spec.Id.Replace("layer_", string.Empty, StringComparison.Ordinal),
                spec => new
                {
                    id = spec.Id,
                    label = spec.Label,
                    stroke = spec.Stroke,
                    stroke_width = spec.StrokeWidth,
                    fill = spec.Fill,
                });

        var manifestObj = new
        {
            project_prefix = projectPrefix,
            box_parameters = new
            {
                width_mm = Calc.Width,
                depth_mm = Calc.Depth,
                height_mm = Calc.Height,
                thickness_mm = Calc.Thickness,
                folga_mm = Calc.Folga,
            },
            layers = layerSpecsManifest,
            elements_per_panel = panelCounts,
            total_elements = elemList.Count,
            files = files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
        };

        string manifestJson = JsonSerializer.Serialize(manifestObj, ManifestJsonOptions);
        files["manifest.json"] = manifestJson;

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string name, string content) in files)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using Stream entryStream = entry.Open();
                    byte[] utf8 = new UTF8Encoding(false).GetBytes(content);
                    entryStream.Write(utf8, 0, utf8.Length);
                }
            }

            zipBytes = ms.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(outputZipPath))
        {
            string? dir = Path.GetDirectoryName(outputZipPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(outputZipPath, zipBytes);
        }

        return new ExportBundleResult(zipBytes, files, manifestJson);
    }

    private (double CanvasW, double CanvasH, double DrawX, double DrawY, Point2D CellOriginOffsetMm)
        GetSinglePanelCanvasGeometry(SurfaceDefinition surface)
    {
        const double pad = BoxDrawer.StartingCoordinate;
        double t = Calc.Thickness;
        SurfaceId pid = surface.PanelId;

        if (pid is SurfaceId.Bottom or SurfaceId.Front or SurfaceId.Back)
        {
            return (
                (2.0 * pad) + surface.WidthMm,
                (2.0 * pad) + surface.HeightMm,
                0.0,
                0.0,
                new Point2D(pad, pad));
        }

        if (pid is SurfaceId.Left or SurfaceId.Right)
        {
            double topClearance = BoxModel.HasHandles ? 2.5 * t : 0.0;
            double earExtraW = BoxModel.HasHandles ? 4.0 * t : 0.0;
            double sideTotalW = Calc.IncreasedDepth + earExtraW;
            return (
                (2.0 * pad) + sideTotalW,
                (2.0 * pad) + topClearance + surface.HeightMm,
                0.0,
                topClearance,
                new Point2D(pad, pad + topClearance));
        }

        // LID: inclui tampa + pescoço/eixo da dobradiça traseira (D + 2.5*t)
        double lidTotalH = Calc.IncreasedDepth + (2.5 * t);
        return (
            (2.0 * pad) + surface.WidthMm,
            (2.0 * pad) + lidTotalH,
            0.0,
            0.0,
            new Point2D(pad, pad));
    }

    private void AppendElementToLayers(
        PersonalizationElement element,
        Dictionary<CncOperationType, StringBuilder> layerContents,
        Point2D? originOffsetMm,
        bool usePanelCncOrientation)
    {
        Point2D center = element.CenterMm();
        double cxSvg;
        double cySvg;
        double rotSvg;

        if (usePanelCncOrientation)
        {
            PanelSvgTransformResult mapped = Surfaces.SurfaceToPanelSvg(
                element.PanelId,
                center.X,
                center.Y,
                rotationDeg: element.RotationDeg,
                originOffsetMm: originOffsetMm);
            cxSvg = mapped.XMm;
            cySvg = mapped.YMm;
            rotSvg = mapped.RotationDeg;
        }
        else
        {
            double ox = originOffsetMm?.X ?? 0.0;
            double oy = originOffsetMm?.Y ?? 0.0;
            cxSvg = ox + center.X;
            cySvg = oy + center.Y;
            rotSvg = NormalizeAngle(element.RotationDeg);
        }

        string transformAttr = BuildElementTransformAttribute(element, cxSvg, cySvg, rotSvg);
        AppendTransformedElement(element, transformAttr, layerContents);
    }

    private static string BuildElementTransformAttribute(
        PersonalizationElement element,
        double cxSvg,
        double cySvg,
        double rotSvg)
    {
        double baseW = Math.Max(1e-4, GetElementBaseWidth(element));
        double baseH = Math.Max(1e-4, GetElementBaseHeight(element));
        double sx = element.WidthMm / baseW;
        double sy = element.HeightMm / baseH;
        double halfW = element.WidthMm * 0.5;
        double halfH = element.HeightMm * 0.5;

        return string.Format(
            CultureInfo.InvariantCulture,
            "translate({0},{1}) rotate({2}) translate({3},{4}) scale({5:0.######},{6:0.######})",
            Fmt(cxSvg),
            Fmt(cySvg),
            Fmt(rotSvg),
            Fmt(-halfW),
            Fmt(-halfH),
            sx,
            sy);
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

    private static void AppendTransformedElement(
        PersonalizationElement element,
        string transformAttr,
        Dictionary<CncOperationType, StringBuilder> layerContents)
    {
        string panelApi = SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(element.PanelId);
        string typeApi = SnakeCaseEnumJsonConverter<ElementType>.ToApiString(element.ElementType);

        if (element is RasterImagePersonalizationElement raster && (!raster.Vectorize || raster.VectorPaths.Count == 0))
        {
            if (string.IsNullOrWhiteSpace(raster.ProcessedImageB64))
            {
                return;
            }

            StringBuilder rasterLayer = layerContents[CncOperationType.EngraveRaster];
            rasterLayer.Append(CultureInfo.InvariantCulture,
                $"<g id=\"elem_{Esc(element.Id)}\" data-element-id=\"{Esc(element.Id)}\" data-element-type=\"{Esc(typeApi)}\" data-panel-id=\"{Esc(panelApi)}\" transform=\"{Esc(transformAttr)}\">");
            rasterLayer.Append(CultureInfo.InvariantCulture,
                $"<image href=\"data:image/png;base64,{Esc(raster.ProcessedImageB64)}\" x=\"0\" y=\"0\" width=\"{Fmt(raster.BaseWidthMm)}\" height=\"{Fmt(raster.BaseHeightMm)}\" preserveAspectRatio=\"none\" />");
            rasterLayer.Append("</g>");
            return;
        }

        if (element is ShapePersonalizationElement shapeEl && shapeEl.VectorPaths.Count == 0)
        {
            shapeEl.RebuildGeometry();
        }

        IReadOnlyList<VectorPathItem> items = element switch
        {
            TextPersonalizationElement text => text.VectorPaths,
            VectorSvgPersonalizationElement svg => svg.Paths,
            RasterImagePersonalizationElement img => img.VectorPaths,
            ShapePersonalizationElement shape => shape.VectorPaths,
            _ => Array.Empty<VectorPathItem>(),
        };

        if (items.Count == 0)
        {
            return;
        }

        var grouped = new Dictionary<CncOperationType, List<VectorPathItem>>();
        foreach (VectorPathItem item in items)
        {
            CncOperationType layerOp = ResolveItemLayerOperation(item, element.Operation, element);
            if (!grouped.TryGetValue(layerOp, out List<VectorPathItem>? list))
            {
                list = new List<VectorPathItem>();
                grouped[layerOp] = list;
            }

            list.Add(item);
        }

        foreach ((CncOperationType op, List<VectorPathItem> groupItems) in grouped)
        {
            if (!layerContents.TryGetValue(op, out StringBuilder? layerSb))
            {
                continue;
            }

            CncLayerSpec spec = CncLayerSpecs.Get(op);
            string suffix = op switch
            {
                CncOperationType.CutInternal => "cut_internal",
                CncOperationType.CutExternal => "cut_external",
                CncOperationType.EngraveFill => "engrave_fill",
                CncOperationType.Mark => "mark",
                _ => "engrave_vector",
            };

            layerSb.Append(CultureInfo.InvariantCulture,
                $"<g id=\"elem_{Esc(element.Id)}_{suffix}\" data-element-id=\"{Esc(element.Id)}\" data-element-type=\"{Esc(typeApi)}\" data-panel-id=\"{Esc(panelApi)}\" transform=\"{Esc(transformAttr)}\">");

            foreach (VectorPathItem item in groupItems)
            {
                if (op == CncOperationType.EngraveFill)
                {
                    if (item.HasFill)
                    {
                        layerSb.Append(CultureInfo.InvariantCulture,
                            $"<path d=\"{Esc(item.D)}\" fill=\"{Esc(spec.Fill)}\" stroke=\"none\" fill-rule=\"evenodd\" />");
                    }
                    else
                    {
                        layerSb.Append(CultureInfo.InvariantCulture,
                            $"<path d=\"{Esc(item.D)}\" fill=\"none\" stroke=\"{Esc(spec.Fill)}\" stroke-width=\"0.1mm\" />");
                    }
                }
                else
                {
                    layerSb.Append(CultureInfo.InvariantCulture,
                        $"<path d=\"{Esc(item.D)}\" fill=\"none\" stroke=\"{Esc(spec.Stroke)}\" stroke-width=\"{Esc(spec.StrokeWidth)}\" />");
                }
            }

            layerSb.Append("</g>");
        }
    }

    private static CncOperationType ResolveItemLayerOperation(
        VectorPathItem item,
        CncOperationType elementOp,
        PersonalizationElement element)
    {
        if (element is ShapePersonalizationElement)
        {
            return item.Operation;
        }

        if (item.Operation == CncOperationType.CutInternal || elementOp == CncOperationType.CutInternal)
        {
            return CncOperationType.CutInternal;
        }

        if (item.Operation == CncOperationType.Mark || elementOp == CncOperationType.Mark)
        {
            return CncOperationType.Mark;
        }

        if (item.Operation == CncOperationType.EngraveFill || elementOp == CncOperationType.EngraveFill)
        {
            return CncOperationType.EngraveFill;
        }

        return CncOperationType.EngraveVector;
    }

    private static void AppendCutPanelGeometry(
        PanelGeometry2D panel,
        Dictionary<CncOperationType, StringBuilder> layerContents)
    {
        bool yIsLiteralZero = string.Equals(panel.PanelId, "lid", StringComparison.Ordinal) && panel.Placement.Y == 0.0;
        bool yIsIntLiteral = string.Equals(panel.PanelId, "bottom", StringComparison.Ordinal) || yIsLiteralZero;
        string transform = panel.Placement.ToSvgTransformAttribute(yIsIntegerLiteral: yIsIntLiteral);

        CncLayerSpec extSpec = CncLayerSpecs.CutExternal;
        StringBuilder extSb = layerContents[CncOperationType.CutExternal];
        extSb.Append(CultureInfo.InvariantCulture,
            $"<g id=\"{Esc(panel.SvgElementId)}_cut_external\" data-layer=\"cut_external\" transform=\"{Esc(transform)}\">");
        extSb.Append(CultureInfo.InvariantCulture,
            $"<path d=\"{Esc(panel.OutlinePathData)}\" fill=\"none\" stroke=\"{Esc(extSpec.Stroke)}\" stroke-width=\"{Esc(extSpec.StrokeWidth)}\" />");
        extSb.Append("</g>");

        bool hasCircular = string.Equals(panel.SvgElementId, "side_panel", StringComparison.Ordinal) && panel.CircularHoles.Count > 0;
        bool hasHolesPath = !string.IsNullOrEmpty(panel.HolesPathData);
        if (!hasCircular && !hasHolesPath)
        {
            return;
        }

        CncLayerSpec intSpec = CncLayerSpecs.CutInternal;
        StringBuilder intSb = layerContents[CncOperationType.CutInternal];
        intSb.Append(CultureInfo.InvariantCulture,
            $"<g id=\"{Esc(panel.SvgElementId)}_cut_internal\" data-layer=\"cut_internal\" transform=\"{Esc(transform)}\">");

        if (hasCircular)
        {
            foreach (Circle2D circle in panel.CircularHoles)
            {
                intSb.Append(CultureInfo.InvariantCulture,
                    $"<circle cx=\"{PythonMath.FormatFloat(circle.CenterX)}\" cy=\"{PythonMath.FormatFloat(circle.CenterY)}\" r=\"{PythonMath.FormatFloat(circle.Radius)}\" fill=\"none\" stroke=\"{Esc(intSpec.Stroke)}\" stroke-width=\"{Esc(intSpec.StrokeWidth)}\" />");
            }
        }

        if (hasHolesPath)
        {
            intSb.Append(CultureInfo.InvariantCulture,
                $"<path d=\"{Esc(panel.HolesPathData!)}\" fill=\"none\" stroke=\"{Esc(intSpec.Stroke)}\" stroke-width=\"{Esc(intSpec.StrokeWidth)}\" />");
        }

        intSb.Append("</g>");
    }

    private static Dictionary<CncOperationType, StringBuilder> InitLayerBuffers(bool includeCutLayers)
    {
        CncOperationType[] ops = includeCutLayers ? AllLayerOrder : EngraveLayerOrder;
        return ops.ToDictionary(op => op, _ => new StringBuilder());
    }

    private static string AssembleSvgDocument(
        double widthMm,
        double heightMm,
        bool includeCutLayers,
        Dictionary<CncOperationType, StringBuilder> layerContents)
    {
        string wStr = Fmt(widthMm);
        string hStr = Fmt(heightMm);
        var sb = new StringBuilder(4096);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" width=\"{wStr}mm\" height=\"{hStr}mm\" viewBox=\"0 0 {wStr} {hStr}\">");
        sb.Append("<defs />");

        CncOperationType[] ops = includeCutLayers ? AllLayerOrder : EngraveLayerOrder;
        foreach (CncOperationType op in ops)
        {
            CncLayerSpec spec = CncLayerSpecs.Get(op);
            sb.Append(CultureInfo.InvariantCulture,
                $"<g id=\"{Esc(spec.Id)}\" inkscape:label=\"{Esc(spec.Label)}\" inkscape:groupmode=\"layer\" stroke=\"{Esc(spec.Stroke)}\" stroke-width=\"{Esc(spec.StrokeWidth)}\" fill=\"{Esc(spec.Fill)}\">");
            sb.Append(layerContents[op]);
            sb.Append("</g>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void WriteTextIfRequested(string? outputPath, string content)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static double NormalizeAngle(double angleDeg)
    {
        double mod = angleDeg % 360.0;
        return mod < 0.0 ? mod + 360.0 : mod;
    }

    private static string Fmt(double value)
    {
        if (Math.Abs(value) < 5e-5)
        {
            return "0";
        }

        string s = value.ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static string Esc(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
