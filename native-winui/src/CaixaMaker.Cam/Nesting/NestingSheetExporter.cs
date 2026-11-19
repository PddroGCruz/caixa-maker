using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.Nesting;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.Cam.Nesting;

/// <summary>
/// Exportador 1:1 em milímetros para planos de corte gerados pelo <see cref="TrueShapeNestingEngine"/>,
/// preservando as 6 layers semânticas CAM (<c>CUT_EXTERNAL</c>, <c>CUT_INTERNAL</c>, <c>ENGRAVE_VECTOR</c>,
/// <c>ENGRAVE_FILL</c>, <c>MARK</c> e <c>ENGRAVE_RASTER</c>) nos formatos SVG, DXF R2000 (<c>AC1015</c>), PDF 1.4 e .ZIP.
/// </summary>
public sealed class NestingSheetExporter
{
    private static readonly NestingSemanticLayer[] LayerOrder =
    [
        NestingSemanticLayer.EngraveRaster,
        NestingSemanticLayer.EngraveFill,
        NestingSemanticLayer.EngraveVector,
        NestingSemanticLayer.Mark,
        NestingSemanticLayer.CutInternal,
        NestingSemanticLayer.CutExternal,
    ];

    public string ExportSheetToSvg(NestingSheetLayout sheet, bool includeSheetBoundary = true)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        string wStr = Fmt(sheet.SheetWidthMm);
        string hStr = Fmt(sheet.SheetHeightMm);

        var layerBuffers = LayerOrder.ToDictionary(l => l, _ => new StringBuilder());

        foreach (PlacedNestingPart placed in sheet.Placements)
        {
            string partTag = Esc(placed.Part.Id);

            // Contorno externo (CUT_EXTERNAL)
            if (placed.TransformedOuterContour.Count >= 3)
            {
                CncLayerSpec extSpec = CncLayerSpecs.CutExternal;
                string d = BuildPolyPathData(placed.TransformedOuterContour, isClosed: true);
                layerBuffers[NestingSemanticLayer.CutExternal].Append(CultureInfo.InvariantCulture,
                    $"<g id=\"part_{partTag}_{placed.InstanceIndex}\" data-part-id=\"{partTag}\" data-instance=\"{placed.InstanceIndex}\"><path d=\"{d}\" fill=\"none\" stroke=\"{extSpec.Stroke}\" stroke-width=\"{extSpec.StrokeWidth}\" /></g>");
            }

            // Feições internas acopladas (agrupando subcaminhos compostos de EngraveFill em um único <path fill-rule="evenodd">)
            var emittedCompoundGroups = new HashSet<string>(StringComparer.Ordinal);
            var compoundFillGroups = new Dictionary<string, List<NestingPolyline2D>>(StringComparer.Ordinal);
            foreach (NestingPolyline2D feat in placed.TransformedInternalFeatures)
            {
                if (feat.Layer == NestingSemanticLayer.EngraveFill &&
                    feat.IsClosed &&
                    feat.IsFilled &&
                    feat.Points.Count >= 3 &&
                    !string.IsNullOrWhiteSpace(feat.CompoundGroupId))
                {
                    if (!compoundFillGroups.TryGetValue(feat.CompoundGroupId!, out List<NestingPolyline2D>? group))
                    {
                        group = new List<NestingPolyline2D>();
                        compoundFillGroups[feat.CompoundGroupId!] = group;
                    }

                    group.Add(feat);
                }
            }

            foreach (NestingPolyline2D feat in placed.TransformedInternalFeatures)
            {
                if (feat.Points.Count < 2 || !layerBuffers.TryGetValue(feat.Layer, out StringBuilder? target))
                {
                    continue;
                }

                if (feat.Layer == NestingSemanticLayer.EngraveFill)
                {
                    CncLayerSpec fillSpec = CncLayerSpecs.EngraveFill;
                    if (feat.IsClosed && feat.IsFilled && feat.Points.Count >= 3)
                    {
                        if (!string.IsNullOrWhiteSpace(feat.CompoundGroupId))
                        {
                            if (!emittedCompoundGroups.Add(feat.CompoundGroupId!))
                            {
                                continue;
                            }

                            string combinedD = BuildCompoundPolyPathData(compoundFillGroups[feat.CompoundGroupId!]);
                            target.Append(CultureInfo.InvariantCulture,
                                $"<path d=\"{combinedD}\" fill=\"{fillSpec.Fill}\" stroke=\"none\" fill-rule=\"evenodd\" />");
                        }
                        else
                        {
                            string d = BuildPolyPathData(feat.Points, isClosed: true);
                            target.Append(CultureInfo.InvariantCulture,
                                $"<path d=\"{d}\" fill=\"{fillSpec.Fill}\" stroke=\"none\" fill-rule=\"evenodd\" />");
                        }
                    }
                    else
                    {
                        string d = BuildPolyPathData(feat.Points, feat.IsClosed);
                        target.Append(CultureInfo.InvariantCulture,
                            $"<path d=\"{d}\" fill=\"none\" stroke=\"{fillSpec.Fill}\" stroke-width=\"0.1mm\" />");
                    }
                }
                else
                {
                    string d = BuildPolyPathData(feat.Points, feat.IsClosed);
                    CncLayerSpec spec = GetLayerSpec(feat.Layer);
                    target.Append(CultureInfo.InvariantCulture,
                        $"<path d=\"{d}\" fill=\"none\" stroke=\"{spec.Stroke}\" stroke-width=\"{spec.StrokeWidth}\" />");
                }
            }

            // Sobreposições raster acopladas
            foreach (NestingRasterOverlay raster in placed.TransformedRasterOverlays)
            {
                if (raster.ImageBytes.Length == 0)
                {
                    continue;
                }

                string b64 = Convert.ToBase64String(raster.ImageBytes);
                double cx = raster.X + (raster.WidthMm * 0.5);
                double cy = raster.Y + (raster.HeightMm * 0.5);
                string transform = Math.Abs(raster.RotationDeg) > 1e-4
                    ? string.Format(CultureInfo.InvariantCulture, "rotate({0},{1},{2})", Fmt(raster.RotationDeg), Fmt(cx), Fmt(cy))
                    : string.Empty;

                string transformAttr = string.IsNullOrEmpty(transform) ? string.Empty : $" transform=\"{transform}\"";
                layerBuffers[NestingSemanticLayer.EngraveRaster].Append(CultureInfo.InvariantCulture,
                    $"<image href=\"data:image/png;base64,{b64}\" x=\"{Fmt(raster.X)}\" y=\"{Fmt(raster.Y)}\" width=\"{Fmt(raster.WidthMm)}\" height=\"{Fmt(raster.HeightMm)}\" preserveAspectRatio=\"none\"{transformAttr} />");
            }
        }

        var sb = new StringBuilder(8192);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" width=\"{wStr}mm\" height=\"{hStr}mm\" viewBox=\"0 0 {wStr} {hStr}\">");
        sb.Append("<defs />");

        if (includeSheetBoundary)
        {
            sb.Append("<g id=\"layer_sheet_bounds\" data-layer=\"sheet_bounds\" inkscape:label=\"Limites do Retalho (Guia)\" inkscape:groupmode=\"layer\" stroke=\"#94A3B8\" stroke-width=\"0.2mm\" fill=\"none\">");
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"0\" y=\"0\" width=\"{wStr}\" height=\"{hStr}\" fill=\"none\" stroke=\"#94A3B8\" stroke-width=\"0.2mm\" />");
            if (sheet.MarginMm > 0.0 && sheet.SheetWidthMm > 2.0 * sheet.MarginMm && sheet.SheetHeightMm > 2.0 * sheet.MarginMm)
            {
                string mStr = Fmt(sheet.MarginMm);
                string uwStr = Fmt(sheet.SheetWidthMm - (2.0 * sheet.MarginMm));
                string uhStr = Fmt(sheet.SheetHeightMm - (2.0 * sheet.MarginMm));
                sb.Append(CultureInfo.InvariantCulture,
                    $"<rect x=\"{mStr}\" y=\"{mStr}\" width=\"{uwStr}\" height=\"{uhStr}\" fill=\"none\" stroke=\"#64748B\" stroke-width=\"0.12mm\" stroke-dasharray=\"3,2\" />");
            }

            sb.Append("</g>");
        }

        foreach (NestingSemanticLayer layer in LayerOrder)
        {
            CncLayerSpec spec = GetLayerSpec(layer);
            sb.Append(CultureInfo.InvariantCulture,
                $"<g id=\"{Esc(spec.Id)}\" inkscape:label=\"{Esc(spec.Label)}\" inkscape:groupmode=\"layer\" stroke=\"{Esc(spec.Stroke)}\" stroke-width=\"{Esc(spec.StrokeWidth)}\" fill=\"{Esc(spec.Fill)}\">");
            sb.Append(layerBuffers[layer]);
            sb.Append("</g>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    public string ExportSheetToDxf(NestingSheetLayout sheet, bool includeSheetBoundary = true)
    {
        string svg = ExportSheetToSvg(sheet, includeSheetBoundary);
        return VectorConverter.SvgToDxf(svg);
    }

    public byte[] ExportSheetToPdf(NestingSheetLayout sheet, bool includeSheetBoundary = true)
    {
        string svg = ExportSheetToSvg(sheet, includeSheetBoundary);
        return VectorConverter.SvgToPdf(svg);
    }

    public void ExportResultToZip(
        NestingResult result,
        string outputZipPath,
        bool includeSheetBoundary = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(outputZipPath))
        {
            throw new ArgumentException("Caminho do arquivo ZIP de destino não informado.", nameof(outputZipPath));
        }

        string? dir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var fs = new FileStream(outputZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        foreach (NestingSheetLayout sheet in result.Sheets)
        {
            string prefix = string.Format(CultureInfo.InvariantCulture, "chapa_{0:D2}", sheet.SheetIndex);
            string svg = ExportSheetToSvg(sheet, includeSheetBoundary);
            string dxf = VectorConverter.SvgToDxf(svg);
            byte[] pdf = VectorConverter.SvgToPdf(svg);

            WriteArchiveBytes(archive, $"{prefix}.svg", utf8NoBom.GetBytes(svg));
            WriteArchiveBytes(archive, $"{prefix}.dxf", Encoding.ASCII.GetBytes(dxf));
            WriteArchiveBytes(archive, $"{prefix}.pdf", pdf);
        }

        string report = BuildTextReport(result);
        WriteArchiveBytes(archive, "relatorio_nesting.txt", utf8NoBom.GetBytes(report));
    }

    public static string BuildTextReport(NestingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder(1024);
        sb.AppendLine("Caixa Maker — Relatório Técnico de Aproveitamento (Nesting 2D 1:1)");
        sb.AppendLine("=====================================================================");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Retalho Configurado: {result.Config.SheetWidthMm:0.##} x {result.Config.SheetHeightMm:0.##} mm");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Margem de Segurança: {result.Config.MarginMm:0.##} mm | Espaçamento: {result.Config.PartSpacingMm:0.##} mm | Encaixe em Furos: {result.Config.AllowNestingInHoles}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Estratégia Vencedora: {result.WinningStrategy} | Tempo: {result.Elapsed.TotalMilliseconds:0.#} ms");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Peças Solicitadas: {result.TotalRequestedInstances} | Alocadas: {result.TotalPlacedInstances} | Chapas Utilizadas: {result.Sheets.Count} | Aproveitamento Global: {result.OverallUtilizationPercent:0.00}%");
        sb.AppendLine();

        foreach (NestingSheetLayout sheet in result.Sheets)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"[Chapa {sheet.SheetIndex:D2}] {sheet.Placements.Count} peça(s) | Aproveitamento: {sheet.UtilizationPercent:0.00}% | Bloco usado: {sheet.UsedBoundsWidthMm:0.##} x {sheet.UsedBoundsHeightMm:0.##} mm");
            foreach (PlacedNestingPart p in sheet.Placements)
            {
                string holeFlag = p.PlacedInsideHole ? " (dentro de furo)" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  - {p.Part.Name} [#{p.InstanceIndex}] ({p.Part.SourceTag}): X={p.OffsetXMm:0.##} mm, Y={p.OffsetYMm:0.##} mm, Rot={p.RotationDeg:0.#}°, Tam={p.BoundsWidthMm:0.##}x{p.BoundsHeightMm:0.##} mm{holeFlag}");
            }

            sb.AppendLine();
        }

        if (result.UnplacedParts.Count > 0)
        {
            sb.AppendLine("Peças Não Alocadas:");
            foreach (NestingUnplacedPart u in result.UnplacedParts)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {u.PartName} [#{u.InstanceIndex}]: {u.Reason}");
            }
        }

        return sb.ToString();
    }

    private static void WriteArchiveBytes(ZipArchive archive, string entryName, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    private static CncLayerSpec GetLayerSpec(NestingSemanticLayer layer) => layer switch
    {
        NestingSemanticLayer.CutExternal => CncLayerSpecs.CutExternal,
        NestingSemanticLayer.CutInternal => CncLayerSpecs.CutInternal,
        NestingSemanticLayer.EngraveFill => CncLayerSpecs.EngraveFill,
        NestingSemanticLayer.Mark => CncLayerSpecs.Mark,
        NestingSemanticLayer.EngraveRaster => CncLayerSpecs.EngraveRaster,
        _ => CncLayerSpecs.EngraveVector,
    };

    private static string BuildCompoundPolyPathData(IReadOnlyList<NestingPolyline2D> subpaths)
    {
        var sb = new StringBuilder(subpaths.Count * 128);
        for (int i = 0; i < subpaths.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(BuildPolyPathData(subpaths[i].Points, isClosed: true));
        }

        return sb.ToString();
    }

    private static string BuildPolyPathData(IReadOnlyList<(double X, double Y)> points, bool isClosed)
    {
        var sb = new StringBuilder(points.Count * 16);
        sb.Append(CultureInfo.InvariantCulture, $"M {Fmt(points[0].X)} {Fmt(points[0].Y)}");
        for (int i = 1; i < points.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $" L {Fmt(points[i].X)} {Fmt(points[i].Y)}");
        }

        if (isClosed)
        {
            sb.Append(" Z");
        }

        return sb.ToString();
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
