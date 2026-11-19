using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using CaixaMaker.App.Models;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using SkiaSharp;

namespace CaixaMaker.App.Rendering;

/// <summary>
/// Renderizador offscreen de miniaturas e pré-visualizações 3D isométricas reais
/// para os cartões do Catálogo de Modelos e para a lista de Projetos Salvos Recentes.
/// Reutiliza <see cref="BoxScene3DBuilder"/> e <see cref="OffscreenViewportRenderer"/>
/// com cache thread-safe por parâmetros, modelo, modo explodido e tema.
/// </summary>
public static class CatalogPreview3DService
{
    private sealed record CachedBitmapData(int Width, int Height, byte[] PremulBgraPixels);

    private static readonly ConcurrentDictionary<string, CachedBitmapData> PreviewCache =
        new(StringComparer.Ordinal);

    /// <summary>Remove todas as miniaturas 3D armazenadas em cache.</summary>
    public static void ClearCache() => PreviewCache.Clear();

    /// <summary>
    /// Gera uma pré-visualização 3D isométrica para um modelo do catálogo nos modos
    /// Montada (<paramref name="exploded"/> = <see langword="false"/>) ou Explodida (<see langword="true"/>).
    /// </summary>
    public static SKBitmap RenderModelPreviewBitmap(
        string modelId,
        bool exploded = false,
        bool darkTheme = true,
        int widthPx = 320,
        int heightPx = 224,
        BoxModelRegistry? registry = null)
    {
        BoxModelRegistry effectiveRegistry = registry ?? BoxModelRegistry.Default;
        IBoxModelDefinition model = ResolveModelOrDefault(effectiveRegistry, modelId);

        var parameters = new BoxParameters
        {
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 100.0,
            BoxThickness = 3.0,
            Folga = BoxParameters.DefaultFolga,
        };

        double lidAngle = model.SupportsLidArticulation ? 35.0 : 0.0;
        return RenderBoxThumbnailBitmap(
            model.ModelId,
            parameters,
            exploded: exploded,
            lidAngleDeg: lidAngle,
            darkTheme: darkTheme,
            widthPx: widthPx,
            heightPx: heightPx,
            registry: effectiveRegistry);
    }

    /// <summary>Retorna a pré-visualização 3D do catálogo codificada como PNG.</summary>
    public static byte[] RenderModelPreviewPng(
        string modelId,
        bool exploded = false,
        bool darkTheme = true,
        int widthPx = 320,
        int heightPx = 224,
        BoxModelRegistry? registry = null)
    {
        using SKBitmap bmp = RenderModelPreviewBitmap(modelId, exploded, darkTheme, widthPx, heightPx, registry);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Gera a miniatura 3D compacta (ex.: 48×34 px ou 2× HiDPI) refletindo as dimensões reais
    /// e o modelo paramétrico de um projeto salvo.
    /// </summary>
    public static SKBitmap RenderProjectThumbnailBitmap(
        ProjectSummary project,
        bool darkTheme = true,
        int widthPx = 96,
        int heightPx = 68,
        BoxModelRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        int targetW = Math.Clamp(widthPx, 24, 1280);
        int targetH = Math.Clamp(heightPx, 24, 960);

        if (project.ThumbnailPngBytes is { Length: > 0 })
        {
            using SKBitmap? decoded = SKBitmap.Decode(project.ThumbnailPngBytes);
            if (decoded is not null && decoded.Width > 0 && decoded.Height > 0)
            {
                if (decoded.Width == targetW && decoded.Height == targetH)
                {
                    return decoded.Copy();
                }

                var targetInfo = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
                SKBitmap? resized = decoded.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (resized is not null)
                {
                    return resized;
                }
            }
        }

        BoxModelRegistry effectiveRegistry = registry ?? BoxModelRegistry.Default;
        IBoxModelDefinition model = ResolveModelOrDefault(effectiveRegistry, project.ModelId);

        BoxParameters raw = project.BoxParameters?.ToBoxParameters() ?? BoxParameters.Default;
        BoxParameters safe = ClampSafeParameters(raw);

        bool exploded = project.ViewerState?.Exploded ?? false;
        double lidAngle = 0.0;
        if (model.SupportsLidArticulation)
        {
            double savedAngle = project.ViewerState?.LidAngle ?? 35.0;
            lidAngle = double.IsFinite(savedAngle) ? Math.Clamp(savedAngle, 0.0, 110.0) : 35.0;
        }

        return RenderBoxThumbnailBitmap(
            model.ModelId,
            safe,
            exploded: exploded,
            lidAngleDeg: lidAngle,
            darkTheme: darkTheme,
            widthPx: targetW,
            heightPx: targetH,
            registry: effectiveRegistry);
    }

    /// <summary>
    /// Renderiza a miniatura 3D oficial (<c>preview/thumbnail_3d.png</c>, padrão 320×220 px SSAA 2×)
    /// com todas as personalizações (textos, SVGs, imagens) projetadas nas chapas.
    /// </summary>
    public static byte[] RenderProjectSceneWithElementsPng(
        IBoxModelDefinition model,
        BoxParameters parameters,
        SurfaceManager surfaceManager,
        ProjectViewerState? viewerState = null,
        bool darkTheme = true,
        int widthPx = 320,
        int heightPx = 220)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(surfaceManager);

        int targetW = Math.Clamp(widthPx, 64, 1280);
        int targetH = Math.Clamp(heightPx, 64, 960);
        BoxParameters safe = ClampSafeParameters(parameters);

        bool exploded = viewerState?.Exploded ?? false;
        double lidAngle = model.SupportsLidArticulation
            ? Math.Clamp(double.IsFinite(viewerState?.LidAngle ?? 35.0) ? (viewerState?.LidAngle ?? 35.0) : 35.0, 0.0, 110.0)
            : 0.0;

        BoxCalculator calc = model.CreateCalculator(safe);
        BoxDrawer drawer = model.CreateDrawer(calc);
        BoxSheetGeometry2D sheet = model.BuildSheetGeometry(calc, drawer);

        BoxScene3D scene = BoxScene3DBuilder.BuildScene(
            calc,
            model,
            sheet,
            surfaceManager,
            lidAngleDeg: lidAngle,
            isExplodedView: exploded);

        Camera3DController camera = BuildPreviewCamera(model, exploded, lidAngle);
        return OffscreenViewportRenderer.RenderScene3DPng(
            scene,
            camera,
            surfaceManager,
            new RenderScene3DOptions
            {
                WidthPx = targetW,
                HeightPx = targetH,
                DarkTheme = darkTheme,
                ShowDimensions = false,
                ShowSurfaceGuides = false,
                SupersampleScale = 2.0f,
            });
    }

    /// <summary>
    /// Renderiza a miniatura 2D oficial do plano de corte completo (<c>preview/sheet_2d.png</c>, padrão 360×240 px).
    /// </summary>
    public static byte[] RenderManufacturingSheetThumbnailPng(
        IBoxModelDefinition model,
        BoxParameters parameters,
        SurfaceManager surfaceManager,
        bool darkTheme = true,
        int widthPx = 360,
        int heightPx = 240)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(surfaceManager);

        int targetW = Math.Clamp(widthPx, 64, 1280);
        int targetH = Math.Clamp(heightPx, 64, 960);
        BoxParameters safe = ClampSafeParameters(parameters);

        BoxCalculator calc = model.CreateCalculator(safe);
        BoxDrawer drawer = model.CreateDrawer(calc);
        BoxSheetGeometry2D sheet = model.BuildSheetGeometry(calc, drawer);

        return OffscreenViewportRenderer.RenderManufacturingSheet2DPng(
            sheet,
            surfaceManager.Surfaces,
            surfaceManager.ListElements(),
            new RenderManufacturingSheet2DOptions
            {
                WidthPx = targetW,
                HeightPx = targetH,
                DarkTheme = darkTheme,
                ShowLaserBedOverlay = false,
                ShowSheetDimensions = false,
            });
    }

    private static Camera3DController BuildPreviewCamera(
        IBoxModelDefinition model,
        bool exploded,
        double effectiveLidAngle)
    {
        var camera = new Camera3DController();
        camera.ApplyPreset(CameraPreset.Isometric);
        double zoomFactor = exploded
            ? 0.94
            : model.SupportsLidArticulation && effectiveLidAngle > 5.0
                ? 1.04
                : 1.12;
        camera.ZoomBy(zoomFactor);
        return camera;
    }

    /// <summary>Retorna a miniatura 3D de um projeto salvo codificada como PNG.</summary>
    public static byte[] RenderProjectThumbnailPng(
        ProjectSummary project,
        bool darkTheme = true,
        int widthPx = 96,
        int heightPx = 68,
        BoxModelRegistry? registry = null)
    {
        using SKBitmap bmp = RenderProjectThumbnailBitmap(project, darkTheme, widthPx, heightPx, registry);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Renderiza qualquer combinação paramétrica de caixa em miniatura 3D isométrica limpa
    /// (com supersampling automático em miniaturas pequenas).
    /// </summary>
    public static SKBitmap RenderBoxThumbnailBitmap(
        string modelId,
        BoxParameters parameters,
        bool exploded = false,
        double lidAngleDeg = 35.0,
        bool darkTheme = true,
        int widthPx = 96,
        int heightPx = 68,
        BoxModelRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        BoxModelRegistry effectiveRegistry = registry ?? BoxModelRegistry.Default;
        IBoxModelDefinition model = ResolveModelOrDefault(effectiveRegistry, modelId);
        BoxParameters safe = ClampSafeParameters(parameters);

        int targetW = Math.Clamp(widthPx, 24, 1280);
        int targetH = Math.Clamp(heightPx, 24, 960);
        double effectiveLidAngle = model.SupportsLidArticulation
            ? Math.Clamp(double.IsFinite(lidAngleDeg) ? lidAngleDeg : 35.0, 0.0, 110.0)
            : 0.0;

        string cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{model.ModelId}|{safe.BoxWidth:F2}|{safe.BoxDepth:F2}|{safe.BoxHeight:F2}|{safe.BoxThickness:F2}|{safe.Folga:F3}|{exploded}|{effectiveLidAngle:F1}|{darkTheme}|{targetW}x{targetH}");

        CachedBitmapData cached = PreviewCache.GetOrAdd(cacheKey, _ =>
        {
            int renderW = targetW < 180 ? Math.Max(64, targetW * 2) : targetW;
            int renderH = targetH < 140 ? Math.Max(64, targetH * 2) : targetH;

            BoxCalculator calc = model.CreateCalculator(safe);
            BoxDrawer drawer = model.CreateDrawer(calc);
            BoxSheetGeometry2D sheet = model.BuildSheetGeometry(calc, drawer);
            var surfaceManager = new SurfaceManager(calc, model, drawer: drawer);

            BoxScene3D scene = BoxScene3DBuilder.BuildScene(
                calc,
                model,
                sheet,
                surfaceManager,
                lidAngleDeg: effectiveLidAngle,
                isExplodedView: exploded);

            Camera3DController camera = BuildPreviewCamera(model, exploded, effectiveLidAngle);

            using SKBitmap rendered = OffscreenViewportRenderer.RenderScene3DBitmap(
                scene,
                camera,
                surfaceManager,
                new RenderScene3DOptions
                {
                    WidthPx = renderW,
                    HeightPx = renderH,
                    DarkTheme = darkTheme,
                    ShowDimensions = false,
                    ShowSurfaceGuides = false,
                    SupersampleScale = 2.0f,
                });

            if (renderW == targetW && renderH == targetH)
            {
                return new CachedBitmapData(targetW, targetH, rendered.GetPixelSpan().ToArray());
            }

            var targetInfo = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKBitmap? downsampled = rendered.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.Mitchell));
            SKBitmap finalBmp = downsampled ?? rendered;
            return new CachedBitmapData(targetW, targetH, finalBmp.GetPixelSpan().ToArray());
        });

        var resultInfo = new SKImageInfo(cached.Width, cached.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var resultBitmap = new SKBitmap(resultInfo);
        Marshal.Copy(cached.PremulBgraPixels, 0, resultBitmap.GetPixels(), cached.PremulBgraPixels.Length);
        return resultBitmap;
    }

    private static IBoxModelDefinition ResolveModelOrDefault(BoxModelRegistry registry, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && registry.Contains(modelId))
        {
            return registry.Get(modelId);
        }

        return registry.Get(BoxWithLidAndHandlesModel.Id);
    }

    private static BoxParameters ClampSafeParameters(BoxParameters p)
    {
        double t = double.IsFinite(p.BoxThickness)
            ? Math.Clamp(p.BoxThickness, BoxParameters.MinThickness, BoxParameters.MaxThickness)
            : BoxParameters.DefaultThickness;
        double w = double.IsFinite(p.BoxWidth)
            ? Math.Clamp(p.BoxWidth, BoxParameters.MinWidth, BoxParameters.MaxWidth)
            : BoxParameters.DefaultWidth;
        double d = double.IsFinite(p.BoxDepth)
            ? Math.Clamp(p.BoxDepth, BoxParameters.MinDepth, BoxParameters.MaxDepth)
            : BoxParameters.DefaultDepth;
        double h = double.IsFinite(p.BoxHeight)
            ? Math.Clamp(p.BoxHeight, BoxParameters.MinHeight, BoxParameters.MaxHeight)
            : BoxParameters.DefaultHeight;
        double folga = double.IsFinite(p.Folga)
            ? Math.Clamp(p.Folga, BoxParameters.MinFolga, BoxParameters.MaxFolga)
            : BoxParameters.DefaultFolga;

        // Garante consistência mínima para espessura vs. dimensões ao gerar miniatura de projetos antigos
        w = Math.Max(w, t * 4.0 + 10.0);
        d = Math.Max(d, t * 4.0 + 10.0);
        h = Math.Max(h, t * 2.5 + 5.0);

        return new BoxParameters
        {
            BoxWidth = w,
            BoxDepth = d,
            BoxHeight = h,
            BoxThickness = t,
            Folga = folga,
        };
    }
}
