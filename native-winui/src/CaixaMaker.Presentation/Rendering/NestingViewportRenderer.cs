using System.Globalization;
using CaixaMaker.Core.Nesting;
using SkiaSharp;

namespace CaixaMaker.App.Rendering;

/// <summary>
/// Cache estático baseado em <see cref="SKPicture"/> da chapa de nesting selecionada,
/// preservando 60+ FPS durante pan e zoom sem reconstruir milhares de segmentos vetoriais por quadro.
/// </summary>
public sealed class NestingSheetStaticCache : IDisposable
{
    private SKPicture? _cachedPicture;
    private string? _cacheKey;

    public int RecordCount { get; private set; }
    public int CacheHitCount { get; private set; }

    public SKPicture GetOrRecord(string cacheKey, SKRect cullRect, Action<SKCanvas> drawAction)
    {
        if (_cachedPicture is not null && string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal))
        {
            CacheHitCount++;
            return _cachedPicture;
        }

        _cachedPicture?.Dispose();
        using var recorder = new SKPictureRecorder();
        SKCanvas recCanvas = recorder.BeginRecording(cullRect);
        drawAction(recCanvas);
        _cachedPicture = recorder.EndRecording();
        _cacheKey = cacheKey;
        RecordCount++;
        return _cachedPicture;
    }

    public void Invalidate()
    {
        _cachedPicture?.Dispose();
        _cachedPicture = null;
        _cacheKey = null;
    }

    public void Dispose() => Invalidate();
}

/// <summary>
/// Renderizador SkiaSharp dedicado à visualização técnica em escala real da chapa otimizada do Nesting 2D,
/// com suporte a Tema Claro/Escuro, 6 layers semânticas CAM e cache vetorial <see cref="NestingSheetStaticCache"/>.
/// </summary>
public sealed class NestingViewportRenderer : IDisposable
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly SKTypeface LabelTypeface =
        SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        ?? SKTypeface.Default;

    private static readonly SKTypeface MonoTypeface =
        SKTypeface.FromFamilyName("Consolas", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        ?? SKTypeface.Default;

    private static readonly SKPaint HudBgDarkPaint = new() { Color = new SKColor(15, 23, 42, 220), IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint HudBgLightPaint = new() { Color = new SKColor(255, 255, 255, 232), IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint HudBorderDarkPaint = new() { Color = new SKColor(51, 65, 85, 220), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private static readonly SKPaint HudBorderLightPaint = new() { Color = new SKColor(203, 213, 225, 255), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private static readonly SKPaint HudTextDarkPaint = new() { Color = new SKColor(226, 232, 240), IsAntialias = true };
    private static readonly SKPaint HudTextLightPaint = new() { Color = new SKColor(30, 41, 59), IsAntialias = true };
    private static readonly SKFont HudFont = new(MonoTypeface, 11f);
    private static readonly SKFont EmptyTitleFont = new(LabelTypeface, 16f);
    private static readonly SKFont EmptySubFont = new(LabelTypeface, 12.5f);

    private readonly NestingSheetStaticCache _cache = new();

    public NestingSheetStaticCache StaticCache => _cache;

    public void RenderToCanvas(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        NestingSheetLayout? sheet,
        NestingSheetConfig config,
        bool isDarkTheme,
        bool showSheetBoundary = true,
        bool showUsableMargin = true,
        bool showPartLabels = true,
        double zoom = 1.0,
        double panX = 0.0,
        double panY = 0.0)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(config);

        SKColor canvasBg = isDarkTheme ? new SKColor(13, 17, 23) : new SKColor(226, 232, 240);
        canvas.Clear(canvasBg);

        if (widthPx <= 4 || heightPx <= 4)
        {
            return;
        }

        double sheetW = sheet?.SheetWidthMm ?? Math.Max(50.0, config.SheetWidthMm);
        double sheetH = sheet?.SheetHeightMm ?? Math.Max(50.0, config.SheetHeightMm);
        double marginMm = sheet?.MarginMm ?? config.MarginMm;

        const double padPx = 36.0;
        double availW = Math.Max(20.0, widthPx - (2.0 * padPx));
        double availH = Math.Max(20.0, heightPx - (2.0 * padPx) - 24.0);
        double baseScale = Math.Min(availW / sheetW, availH / sheetH);
        double effectiveScale = Math.Max(0.05, baseScale * Math.Clamp(zoom, 0.25, 20.0));

        float originX = (float)(((widthPx - (sheetW * effectiveScale)) * 0.5) + panX);
        float originY = (float)(((heightPx - 24.0 - (sheetH * effectiveScale)) * 0.5) + panY);

        string cacheKey = BuildCacheKey(sheet, sheetW, sheetH, marginMm, isDarkTheme, showSheetBoundary, showUsableMargin, showPartLabels);
        var cullRect = SKRect.Create(-20f, -20f, (float)sheetW + 40f, (float)sheetH + 40f);
        SKPicture picture = _cache.GetOrRecord(cacheKey, cullRect, rec =>
            RecordSheetVectorContent(rec, sheet, sheetW, sheetH, marginMm, isDarkTheme, showSheetBoundary, showUsableMargin, showPartLabels));

        canvas.Save();
        canvas.Translate(originX, originY);
        canvas.Scale((float)effectiveScale);
        canvas.DrawPicture(picture);
        canvas.Restore();

        // Dimensões de referência da chapa nas bordas em coordenadas de tela
        DrawSheetRulerBadges(canvas, originX, originY, (float)(sheetW * effectiveScale), (float)(sheetH * effectiveScale), sheetW, sheetH, isDarkTheme);

        if (sheet is null || sheet.Placements.Count == 0)
        {
            DrawEmptyOverlay(canvas, widthPx, heightPx, isDarkTheme);
        }

        DrawLegendBar(canvas, widthPx, heightPx, effectiveScale, isDarkTheme);
    }

    public byte[] RenderToPngBytes(
        NestingSheetLayout? sheet,
        NestingSheetConfig config,
        int widthPx,
        int heightPx,
        bool isDarkTheme,
        bool showSheetBoundary = true,
        bool showUsableMargin = true,
        bool showPartLabels = true,
        double zoom = 1.0,
        double panX = 0.0,
        double panY = 0.0)
    {
        int w = Math.Max(64, widthPx);
        int h = Math.Max(64, heightPx);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        RenderToCanvas(surface.Canvas, w, h, sheet, config, isDarkTheme, showSheetBoundary, showUsableMargin, showPartLabels, zoom, panX, panY);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    public void RenderToBitmap(
        SKBitmap targetBitmap,
        NestingSheetLayout? sheet,
        NestingSheetConfig config,
        bool isDarkTheme,
        bool showSheetBoundary = true,
        bool showUsableMargin = true,
        bool showPartLabels = true,
        double zoom = 1.0,
        double panX = 0.0,
        double panY = 0.0)
    {
        ArgumentNullException.ThrowIfNull(targetBitmap);
        using var canvas = new SKCanvas(targetBitmap);
        RenderToCanvas(canvas, targetBitmap.Width, targetBitmap.Height, sheet, config, isDarkTheme, showSheetBoundary, showUsableMargin, showPartLabels, zoom, panX, panY);
        canvas.Flush();
    }

    private static void RecordSheetVectorContent(
        SKCanvas canvas,
        NestingSheetLayout? sheet,
        double sheetW,
        double sheetH,
        double marginMm,
        bool isDark,
        bool showSheetBoundary,
        bool showUsableMargin,
        bool showPartLabels)
    {
        SKRect sheetRect = SKRect.Create(0f, 0f, (float)sheetW, (float)sheetH);

        // 1. Fundo do retalho / chapa MDF
        using (var sheetBgPaint = new SKPaint
        {
            Color = isDark ? new SKColor(27, 35, 48) : new SKColor(255, 255, 255),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        })
        {
            canvas.DrawRect(sheetRect, sheetBgPaint);
        }

        // 2. Grade milimétrica discreta (50 mm)
        double gridStep = sheetW > 1200 || sheetH > 1200 ? 100.0 : 50.0;
        using (var gridPaint = new SKPaint
        {
            Color = isDark ? new SKColor(51, 65, 85, 75) : new SKColor(203, 213, 225, 130),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            IsAntialias = false,
        })
        {
            for (double gx = gridStep; gx < sheetW; gx += gridStep)
            {
                canvas.DrawLine((float)gx, 0f, (float)gx, (float)sheetH, gridPaint);
            }

            for (double gy = gridStep; gy < sheetH; gy += gridStep)
            {
                canvas.DrawLine(0f, (float)gy, (float)sheetW, (float)gy, gridPaint);
            }
        }

        // 3. Margem útil tracejada
        if (showUsableMargin && marginMm > 0.0 && sheetW > 2.0 * marginMm && sheetH > 2.0 * marginMm)
        {
            using var dashEffect = SKPathEffect.CreateDash([4f, 3f], 0f);
            using var marginPaint = new SKPaint
            {
                Color = isDark ? new SKColor(100, 116, 139, 175) : new SKColor(100, 116, 139, 185),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0f,
                PathEffect = dashEffect,
                IsAntialias = true,
            };
            canvas.DrawRect(
                SKRect.Create((float)marginMm, (float)marginMm, (float)(sheetW - (2.0 * marginMm)), (float)(sheetH - (2.0 * marginMm))),
                marginPaint);
        }

        // 4. Borda externa do retalho
        if (showSheetBoundary)
        {
            using var borderPaint = new SKPaint
            {
                Color = isDark ? new SKColor(148, 163, 184) : new SKColor(71, 85, 105),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0f,
                IsAntialias = true,
            };
            canvas.DrawRect(sheetRect, borderPaint);
        }

        if (sheet is null || sheet.Placements.Count == 0)
        {
            return;
        }

        using var boxFillPaint = new SKPaint
        {
            Color = isDark ? new SKColor(34, 197, 94, 42) : new SKColor(22, 163, 74, 34),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var extFillPaint = new SKPaint
        {
            Color = isDark ? new SKColor(59, 130, 246, 46) : new SKColor(37, 99, 235, 34),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var cutExtPaint = new SKPaint
        {
            Color = isDark ? new SKColor(248, 250, 252) : new SKColor(15, 23, 42),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            IsAntialias = true,
        };
        using var cutIntPaint = new SKPaint
        {
            Color = isDark ? new SKColor(248, 113, 113) : new SKColor(183, 28, 28),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            IsAntialias = true,
        };
        using var engraveVecPaint = new SKPaint
        {
            Color = isDark ? new SKColor(56, 189, 248) : new SKColor(13, 71, 161),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            IsAntialias = true,
        };
        using var engraveFillStrokePaint = new SKPaint
        {
            Color = isDark ? new SKColor(192, 132, 252) : new SKColor(106, 27, 154),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            IsAntialias = true,
        };
        using var engraveFillSolidPaint = new SKPaint
        {
            Color = isDark ? new SKColor(192, 132, 252, 150) : new SKColor(106, 27, 154, 140),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var markPaint = new SKPaint
        {
            Color = isDark ? new SKColor(74, 222, 128) : new SKColor(27, 94, 32),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            IsAntialias = true,
        };
        using var labelTextPaint = new SKPaint
        {
            Color = isDark ? new SKColor(241, 245, 249, 225) : new SKColor(15, 23, 42, 225),
            IsAntialias = true,
        };

        foreach (PlacedNestingPart placed in sheet.Placements)
        {
            if (placed.TransformedOuterContour.Count < 3)
            {
                continue;
            }

            // Sobreposições raster embarcadas
            foreach (NestingRasterOverlay raster in placed.TransformedRasterOverlays)
            {
                if (raster.ImageBytes.Length == 0) continue;
                using SKBitmap? bmp = SKBitmap.Decode(raster.ImageBytes);
                if (bmp is null) continue;

                canvas.Save();
                float rcx = (float)(raster.X + (raster.WidthMm * 0.5));
                float rcy = (float)(raster.Y + (raster.HeightMm * 0.5));
                if (Math.Abs(raster.RotationDeg) > 1e-3)
                {
                    canvas.RotateDegrees((float)raster.RotationDeg, rcx, rcy);
                }

                canvas.DrawBitmap(bmp, SKRect.Create((float)raster.X, (float)raster.Y, (float)raster.WidthMm, (float)raster.HeightMm));
                canvas.Restore();
            }

            // Preenchimento translúcido do contorno externo (com recorte de furos CutInternal em EvenOdd)
            using (var fillPath = new SKPath { FillType = SKPathFillType.EvenOdd })
            {
                AppendPolygonToPath(fillPath, placed.TransformedOuterContour, isClosed: true);
                foreach (NestingPolyline2D feat in placed.TransformedInternalFeatures)
                {
                    if (feat.IsClosed && feat.Layer == NestingSemanticLayer.CutInternal && feat.Points.Count >= 3)
                    {
                        AppendPolygonToPath(fillPath, feat.Points, isClosed: true);
                    }
                }

                bool isBox = string.Equals(placed.Part.SourceTag, "Caixa", StringComparison.OrdinalIgnoreCase);
                canvas.DrawPath(fillPath, isBox ? boxFillPaint : extFillPaint);
            }

            // Agrupa subcaminhos compostos de EngraveFill por CompoundGroupId para preenchimento EvenOdd fiel
            var renderedFillGroups = new HashSet<string>(StringComparer.Ordinal);
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

            // Feições internas por layer semântica
            foreach (NestingPolyline2D feat in placed.TransformedInternalFeatures)
            {
                if (feat.Points.Count < 2) continue;

                if (feat.Layer == NestingSemanticLayer.EngraveFill)
                {
                    if (feat.IsClosed && feat.IsFilled && feat.Points.Count >= 3)
                    {
                        if (!string.IsNullOrWhiteSpace(feat.CompoundGroupId))
                        {
                            if (!renderedFillGroups.Add(feat.CompoundGroupId!))
                            {
                                continue;
                            }

                            using var compoundPath = new SKPath { FillType = SKPathFillType.EvenOdd };
                            foreach (NestingPolyline2D sub in compoundFillGroups[feat.CompoundGroupId!])
                            {
                                AppendPolygonToPath(compoundPath, sub.Points, isClosed: true);
                            }

                            canvas.DrawPath(compoundPath, engraveFillSolidPaint);
                        }
                        else
                        {
                            using var singleFillPath = new SKPath { FillType = SKPathFillType.EvenOdd };
                            AppendPolygonToPath(singleFillPath, feat.Points, isClosed: true);
                            canvas.DrawPath(singleFillPath, engraveFillSolidPaint);
                        }
                    }
                    else
                    {
                        using var strokePath = new SKPath();
                        AppendPolygonToPath(strokePath, feat.Points, feat.IsClosed);
                        canvas.DrawPath(strokePath, engraveFillStrokePaint);
                    }

                    continue;
                }

                using var path = new SKPath();
                AppendPolygonToPath(path, feat.Points, feat.IsClosed);

                switch (feat.Layer)
                {
                    case NestingSemanticLayer.CutInternal:
                        canvas.DrawPath(path, cutIntPaint);
                        break;
                    case NestingSemanticLayer.EngraveVector:
                        canvas.DrawPath(path, engraveVecPaint);
                        break;
                    case NestingSemanticLayer.Mark:
                        canvas.DrawPath(path, markPaint);
                        break;
                    default:
                        canvas.DrawPath(path, cutExtPaint);
                        break;
                }
            }

            // Contorno externo (CUT_EXTERNAL)
            using (var outerPath = new SKPath())
            {
                AppendPolygonToPath(outerPath, placed.TransformedOuterContour, isClosed: true);
                canvas.DrawPath(outerPath, cutExtPaint);
            }

            // Rótulo centralizado da peça
            if (showPartLabels && placed.BoundsWidthMm >= 22.0 && placed.BoundsHeightMm >= 14.0)
            {
                string label = Math.Abs(placed.RotationDeg) > 0.1
                    ? string.Format(PtBr, "{0} • {1:0}°", placed.Part.Name, placed.RotationDeg)
                    : placed.Part.Name;

                float fontSizeMm = (float)Math.Clamp(Math.Min(placed.BoundsWidthMm / Math.Max(6.0, label.Length * 0.72), placed.BoundsHeightMm * 0.24), 4.0, 14.0);
                using var partFont = new SKFont(LabelTypeface, fontSizeMm);
                float textW = partFont.MeasureText(label);
                if (textW <= placed.BoundsWidthMm * 0.92)
                {
                    float cx = (float)((placed.MinXMm + placed.MaxXMm) * 0.5);
                    float cy = (float)(((placed.MinYMm + placed.MaxYMm) * 0.5) + (fontSizeMm * 0.35));
                    canvas.DrawText(label, cx - (textW * 0.5f), cy, SKTextAlign.Left, partFont, labelTextPaint);
                }
            }
        }
    }

    private static void DrawSheetRulerBadges(
        SKCanvas canvas,
        float originX,
        float originY,
        float scaledW,
        float scaledH,
        double sheetW,
        double sheetH,
        bool isDark)
    {
        SKPaint textPaint = isDark ? HudTextDarkPaint : HudTextLightPaint;
        string topLabel = string.Format(PtBr, "{0:0.#} mm", sheetW);
        float topW = HudFont.MeasureText(topLabel);
        canvas.DrawText(topLabel, originX + ((scaledW - topW) * 0.5f), Math.Max(14f, originY - 8f), SKTextAlign.Left, HudFont, textPaint);

        string sideLabel = string.Format(PtBr, "{0:0.#} mm", sheetH);
        canvas.Save();
        float sideX = Math.Max(14f, originX - 10f);
        float sideY = originY + (scaledH * 0.5f);
        canvas.RotateDegrees(-90f, sideX, sideY);
        float sideW = HudFont.MeasureText(sideLabel);
        canvas.DrawText(sideLabel, sideX - (sideW * 0.5f), sideY, SKTextAlign.Left, HudFont, textPaint);
        canvas.Restore();
    }

    private static void DrawEmptyOverlay(SKCanvas canvas, int widthPx, int heightPx, bool isDark)
    {
        SKPaint bg = isDark ? HudBgDarkPaint : HudBgLightPaint;
        SKPaint border = isDark ? HudBorderDarkPaint : HudBorderLightPaint;
        SKPaint text = isDark ? HudTextDarkPaint : HudTextLightPaint;

        const float boxW = 360f;
        const float boxH = 74f;
        var rect = SKRect.Create((widthPx - boxW) * 0.5f, ((heightPx - 24f) - boxH) * 0.5f, boxW, boxH);
        canvas.DrawRoundRect(rect, 6f, 6f, bg);
        canvas.DrawRoundRect(rect, 6f, 6f, border);

        const string title = "Retalho pronto para otimização";
        const string subtitle = "Importe SVG/DXF ou clique em \"Otimizar Disposição\"";
        float tw = EmptyTitleFont.MeasureText(title);
        float sw = EmptySubFont.MeasureText(subtitle);
        canvas.DrawText(title, rect.MidX - (tw * 0.5f), rect.Top + 30f, SKTextAlign.Left, EmptyTitleFont, text);
        canvas.DrawText(subtitle, rect.MidX - (sw * 0.5f), rect.Top + 52f, SKTextAlign.Left, EmptySubFont, text);
    }

    private static void DrawLegendBar(SKCanvas canvas, int widthPx, int heightPx, double pxPerMm, bool isDark)
    {
        var barRect = SKRect.Create(0f, heightPx - 24f, widthPx, 24f);
        canvas.DrawRect(barRect, isDark ? HudBgDarkPaint : HudBgLightPaint);
        canvas.DrawLine(0f, heightPx - 24f, widthPx, heightPx - 24f, isDark ? HudBorderDarkPaint : HudBorderLightPaint);

        (string Label, SKColor Color)[] legend =
        [
            ("Corte Ext.", isDark ? new SKColor(248, 250, 252) : new SKColor(15, 23, 42)),
            ("Corte Int.", isDark ? new SKColor(248, 113, 113) : new SKColor(183, 28, 28)),
            ("Gravação", isDark ? new SKColor(56, 189, 248) : new SKColor(13, 71, 161)),
            ("Hachura", isDark ? new SKColor(192, 132, 252) : new SKColor(106, 27, 154)),
            ("Marcação", isDark ? new SKColor(74, 222, 128) : new SKColor(27, 94, 32)),
        ];

        SKPaint textPaint = isDark ? HudTextDarkPaint : HudTextLightPaint;
        float x = 12f;
        float yCenter = heightPx - 12f;

        foreach ((string lbl, SKColor col) in legend)
        {
            using var dot = new SKPaint { Color = col, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawRoundRect(SKRect.Create(x, yCenter - 4f, 8f, 8f), 2f, 2f, dot);
            x += 12f;
            canvas.DrawText(lbl, x, yCenter + 4f, SKTextAlign.Left, HudFont, textPaint);
            x += HudFont.MeasureText(lbl) + 14f;
        }

        string scaleInfo = string.Format(PtBr, "Escala: {0:0.00} px/mm • Roda: Zoom • Arraste: Pan", pxPerMm);
        float scaleW = HudFont.MeasureText(scaleInfo);
        if (widthPx - scaleW - 12f > x + 8f)
        {
            canvas.DrawText(scaleInfo, widthPx - scaleW - 12f, yCenter + 4f, SKTextAlign.Left, HudFont, textPaint);
        }
    }

    private static void AppendPolygonToPath(SKPath path, IReadOnlyList<(double X, double Y)> points, bool isClosed)
    {
        if (points.Count < 2)
        {
            return;
        }

        path.MoveTo((float)points[0].X, (float)points[0].Y);
        for (int i = 1; i < points.Count; i++)
        {
            path.LineTo((float)points[i].X, (float)points[i].Y);
        }

        if (isClosed)
        {
            path.Close();
        }
    }

    private static string BuildCacheKey(
        NestingSheetLayout? sheet,
        double sheetW,
        double sheetH,
        double marginMm,
        bool isDark,
        bool showBoundary,
        bool showMargin,
        bool showLabels)
    {
        int sheetHash = sheet is null ? 0 : RuntimeHelpersHash(sheet);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sheetHash}|{sheetW:0.##}|{sheetH:0.##}|{marginMm:0.##}|{isDark}|{showBoundary}|{showMargin}|{showLabels}");
    }

    private static int RuntimeHelpersHash(NestingSheetLayout sheet)
        => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(sheet);

    public void Dispose() => _cache.Dispose();
}
