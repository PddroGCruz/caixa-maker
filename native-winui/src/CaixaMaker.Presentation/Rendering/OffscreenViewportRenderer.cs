using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using SkiaSharp;

namespace CaixaMaker.App.Rendering;

/// <summary>Resultado do hit-test em coordenadas de tela (px) de uma cota dimensional 3D.</summary>
public readonly record struct DimensionCalloutHitResult(
    BoxDimensionAxis Axis,
    SKRect BadgeBoundsScreen,
    Vector2 CenterScreen,
    string Label);

/// <summary>Opções de renderização offscreen e interativa para a cena 3D.</summary>
public sealed class RenderScene3DOptions
{
    public int WidthPx { get; init; } = 800;
    public int HeightPx { get; init; } = 600;
    public bool DarkTheme { get; init; } = true;
    public bool ShowDimensions { get; init; } = true;
    public bool ShowSurfaceGuides { get; init; }
    public SurfaceId? ActiveSurfaceId { get; init; }
    public string? SelectedPartId { get; init; }
    public string? SelectedElementId { get; init; }
    public IReadOnlySet<string>? ErrorElementIds { get; init; }
    public double SafetyMarginMm { get; init; } = GeometryValidator.DefaultSafetyMarginMm;
    public BoxDimensionAxis? HighlightedDimensionAxis { get; init; }
    public float ScaleFactor { get; init; } = 1.0f;
    public float SupersampleScale { get; init; } = 1.0f;
    public bool TransparentBackground { get; init; }
}

/// <summary>Opções de renderização offscreen e interativa para o editor CAD 2D.</summary>
public sealed class RenderEditor2DOptions
{
    public int WidthPx { get; init; } = 640;
    public int HeightPx { get; init; } = 420;
    public bool DarkTheme { get; init; } = true;
    public bool DrawForbiddenZones { get; init; } = true;
    public bool DrawSafeArea { get; init; } = true;
    public double SafetyMarginMm { get; init; } = GeometryValidator.DefaultSafetyMarginMm;
    public string? SelectedElementId { get; init; }
    public IReadOnlyList<string>? SelectedElementIds { get; init; }
    public IReadOnlySet<string>? ErrorElementIds { get; init; }
    public bool ShowLiveDimensionBadge { get; init; }
}

/// <summary>Transformação de projeção em milímetros para pixels da folha completa 1:1 no canvas 2D.</summary>
public readonly record struct SheetViewTransform2D(
    double FitScalePxPerMm,
    double EffectiveScalePxPerMm,
    double OriginPxX,
    double OriginPxY);

/// <summary>Resultado do hit-test em pixels sobre uma chapa do plano de corte 2D completo.</summary>
public readonly record struct ManufacturingSheetPanelHit(
    string PanelId,
    string PartId,
    SurfaceId SurfaceId,
    BoundingBox2D SheetBounds);

/// <summary>Opções de renderização para o visualizador 2D do plano de corte completo (Sheet Nesting 1:1).</summary>
public sealed class RenderManufacturingSheet2DOptions
{
    public int WidthPx { get; init; } = 820;
    public int HeightPx { get; init; } = 420;
    public bool DarkTheme { get; init; } = true;
    public string? SelectedPartId { get; init; }
    public string? SelectedElementId { get; init; }
    public IReadOnlySet<string>? ErrorElementIds { get; init; }
    public Func<CncOperationType, bool>? IsLayerVisible { get; init; }
    public double LaserBedWidthMm { get; init; }
    public double LaserBedHeightMm { get; init; }
    public string? LaserBedLabel { get; init; }
    public bool ShowLaserBedOverlay { get; init; } = true;
    public bool ShowSheetDimensions { get; init; } = true;
    public double UserZoom { get; init; } = 1.0;
    public double PanPxX { get; init; }
    public double PanPxY { get; init; }
    public double CursorMmX { get; init; }
    public double CursorMmY { get; init; }
}

/// <summary>
/// Motor gráfico nativo via SkiaSharp + Z-Buffer 3D em memória, compartilhado entre
/// os controles WinUI 3 (<c>BoxViewer3DControl</c>, <c>Surface2DEditorControl</c>),
/// os testes unitários offscreen e o modo <c>--smoke-test</c>.
/// </summary>
public static class OffscreenViewportRenderer
{
    private sealed record OverlayTextureData(int Width, int Height, uint[] PremulBgraPixels);

    private sealed record CompiledPathCommand(
        SKPath Path,
        CncOperationType Operation,
        bool HasFill,
        bool HasStroke);

    private sealed class CompiledVectorEntry
    {
        public required int SourceCount { get; init; }
        public required CompiledPathCommand[] Commands { get; init; }
    }

    private sealed record Editor2DStaticKey(
        int WidthPx,
        int HeightPx,
        bool DarkTheme,
        double OriginPxX,
        double OriginPxY,
        double ScalePxPerMm,
        SurfaceId PanelId,
        double SurfaceWidthMm,
        double SurfaceHeightMm,
        bool DrawForbiddenZones,
        int ForbiddenZoneCount,
        bool DrawSafeArea,
        double SafetyMarginMm,
        int ContourHash,
        string StaticElementsSignature);

    private sealed class Editor2DStaticCache : IDisposable
    {
        public Editor2DStaticKey? Key;
        public SKBitmap? Bitmap;
        public SKCanvas? Canvas;
        public int PixelW;
        public int PixelH;

        public void EnsureSurface(int w, int h)
        {
            if (Bitmap is not null && Canvas is not null && PixelW == w && PixelH == h)
            {
                return;
            }

            Canvas?.Dispose();
            Bitmap?.Dispose();
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            Bitmap = new SKBitmap(info);
            Canvas = new SKCanvas(Bitmap);
            PixelW = w;
            PixelH = h;
            Key = null;
        }

        public void Dispose()
        {
            Canvas?.Dispose();
            Canvas = null;
            Bitmap?.Dispose();
            Bitmap = null;
            Key = null;
        }
    }

    private sealed record ManufacturingSheetStaticKey(
        int WidthPx,
        int HeightPx,
        bool DarkTheme,
        int SheetIdentityHash,
        int SurfaceMapperHash,
        double EffectiveScalePxPerMm,
        double OriginPxX,
        double OriginPxY,
        string SelectedPartId,
        string SelectedElementId,
        bool ShowLaserBedOverlay,
        double LaserBedWidthMm,
        double LaserBedHeightMm,
        string LaserBedLabel,
        bool ShowSheetDimensions,
        int LayerVisibilityMask,
        string VisibleElementsSignature);

    private sealed class ManufacturingSheetStaticCache : IDisposable
    {
        public ManufacturingSheetStaticKey? Key;
        public SKBitmap? Bitmap;
        public SKCanvas? Canvas;
        public int PixelW;
        public int PixelH;

        public void EnsureSurface(int w, int h)
        {
            if (Bitmap is not null && Canvas is not null && PixelW == w && PixelH == h)
            {
                return;
            }

            Canvas?.Dispose();
            Bitmap?.Dispose();
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            Bitmap = new SKBitmap(info);
            Canvas = new SKCanvas(Bitmap);
            PixelW = w;
            PixelH = h;
            Key = null;
        }

        public void Dispose()
        {
            Canvas?.Dispose();
            Canvas = null;
            Bitmap?.Dispose();
            Bitmap = null;
            Key = null;
        }
    }

    private sealed class CachedSheetPanelPaths
    {
        public required SKPath WoodPath { get; init; }
        public SKPath? InternalCutPath { get; init; }
        public required SKPath OuterCutPath { get; init; }
    }

    private sealed class CompiledSheetPanelsEntry
    {
        public readonly Dictionary<string, CachedSheetPanelPaths> Panels = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct MappedElementTransformKey(
        SurfaceId PanelId,
        double CenterXMm,
        double CenterYMm,
        double RotationDeg);

    private sealed class ZBufferScratchBuffers
    {
        public uint[] ColorBuffer = Array.Empty<uint>();
        public float[] ZBuffer = Array.Empty<float>();
        public byte[] IdBuffer = Array.Empty<byte>();
        public readonly List<RasterTriangle> Triangles = new(1024);
        public readonly List<RasterCreaseSegment> Creases = new(1024);
        public readonly List<OverlayPanelJob> OverlayJobs = new(8);

        public void EnsureCapacity(int pixelCount)
        {
            if (ColorBuffer.Length < pixelCount)
            {
                ColorBuffer = new uint[pixelCount];
                ZBuffer = new float[pixelCount];
                IdBuffer = new byte[pixelCount];
            }
        }
    }

    private readonly record struct RasterTriangle(
        float Ax,
        float Ay,
        float InvZa,
        float Bx,
        float By,
        float InvZb,
        float Cx,
        float Cy,
        float InvZc,
        float InvArea,
        float Tol,
        float Dw0dx,
        float Dw1dx,
        float Dw2dx,
        float DzDx,
        float SortInvZ,
        int MinX,
        int MaxX,
        int MinY,
        int MaxY,
        uint BgraColor,
        byte SurfaceTag);

    private static readonly Comparison<RasterTriangle> FrontToBackTriangleComparison =
        static (a, b) => b.SortInvZ.CompareTo(a.SortInvZ);

    private readonly record struct RasterCreaseSegment(
        float Ax,
        float Ay,
        float InvZa,
        float Bx,
        float By,
        float InvZb,
        int MinY,
        int MaxY,
        uint BaseBgra,
        float HalfWidthPx);

    private struct ScreenBounds
    {
        public int MinX;
        public int MaxX;
        public int MinY;
        public int MaxY;

        public void Reset()
        {
            MinX = int.MaxValue;
            MaxX = int.MinValue;
            MinY = int.MaxValue;
            MaxY = int.MinValue;
        }

        public void Include(int minX, int maxX, int minY, int maxY)
        {
            if (minX < MinX) MinX = minX;
            if (maxX > MaxX) MaxX = maxX;
            if (minY < MinY) MinY = minY;
            if (maxY > MaxY) MaxY = maxY;
        }

        public readonly bool IsValid => MinX <= MaxX && MinY <= MaxY;
    }

    private const int MaxOverlayTexDim = 768;
    private const int MaxOverlayCacheEntries = 64;
    private static readonly ConcurrentDictionary<string, SKBitmap> RasterCache = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<IReadOnlyList<VectorPathItem>, CompiledVectorEntry> VectorPathCache = new();
    private static readonly object VectorPathCompileLock = new();
    private static readonly ConditionalWeakTable<BoxSheetGeometry2D, CompiledSheetPanelsEntry> PanelContourSkiaCache = new();
    private static readonly object PanelContourCompileLock = new();
    private static readonly ConditionalWeakTable<SurfaceMapper, ConcurrentDictionary<MappedElementTransformKey, PanelSvgTransformResult>> MappedSheetElementsCache = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, OverlayTextureData Value)>> SurfaceOverlayImageCache = new(StringComparer.Ordinal);
    private static readonly LinkedList<(string Key, OverlayTextureData Value)> SurfaceOverlayLru = new();
    private static readonly object OverlayCacheLock = new();
    private static readonly SKPaint SrcCopyPaint = new() { BlendMode = SKBlendMode.Src };
    private static readonly SKSamplingOptions DownsampleSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    [ThreadStatic]
    private static ZBufferScratchBuffers? t_zBufferScratch;
    private static readonly SKMaskFilter FloorShadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f);

    private static readonly Vector3 KeyLightDir = Vector3.Normalize(new Vector3(0.46f, 0.78f, 0.48f));
    private static readonly Vector3 FillLightDir = Vector3.Normalize(new Vector3(-0.56f, 0.36f, -0.46f));
    private static readonly SKTypeface MonoTypeface =
        SKTypeface.FromFamilyName("Cascadia Mono")
        ?? SKTypeface.FromFamilyName("Consolas")
        ?? SKTypeface.Default;
    private static readonly SKFont LiveBadgeFont = new(MonoTypeface, 10.5f) { Embolden = true };
    private static readonly SKPaint LiveBadgeBgPaint = new()
    {
        Style = SKPaintStyle.Fill,
        Color = new SKColor(0x0F, 0x17, 0x2A, 0xE6),
        IsAntialias = true,
    };
    private static readonly SKPaint LiveBadgeBorderPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(0x38, 0xBD, 0xF8),
        StrokeWidth = 1.25f,
        IsAntialias = true,
    };
    private static readonly SKPaint LiveBadgeTextPaint = new()
    {
        Color = new SKColor(0xE0, 0xF2, 0xFE),
        IsAntialias = true,
    };
    private static readonly SKPaint MarqueeFillPaint = new()
    {
        Style = SKPaintStyle.Fill,
        Color = new SKColor(0x38, 0xBD, 0xF8, 0x26),
        IsAntialias = true,
    };
    private static readonly SKPaint MarqueeBorderPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(0x38, 0xBD, 0xF8),
        StrokeWidth = 1.25f,
        PathEffect = SKPathEffect.CreateDash([5f, 3f], 0f),
        IsAntialias = true,
    };
    private static readonly SKPaint GroupBoundsBorderPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(0x38, 0xBD, 0xF8, 220),
        StrokeWidth = 1.25f,
        PathEffect = SKPathEffect.CreateDash([6f, 4f], 0f),
        IsAntialias = true,
    };
    private static readonly SKPaint MultiSelectedOutlinePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(0x38, 0xBD, 0xF8),
        StrokeWidth = 1.5f,
        IsAntialias = true,
    };
    private static readonly SKPaint AnchorOutlinePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(0x22, 0xC5, 0x5E),
        StrokeWidth = 2.2f,
        IsAntialias = true,
    };
    private static readonly SKPaint AnchorBadgeFillPaint = new()
    {
        Style = SKPaintStyle.Fill,
        Color = new SKColor(0x22, 0xC5, 0x5E),
        IsAntialias = true,
    };
    private static readonly SKPaint AnchorBadgeTextPaint = new()
    {
        Color = SKColors.White,
        IsAntialias = true,
    };
    private static readonly SKFont AnchorBadgeFont = new(MonoTypeface, 9.0f) { Embolden = true };

    // =========================================================================
    //  RENDERIZAÇÃO 3D (Z-BUFFER + GRAVAÇÕES CNC PROJETADAS + COTAS 3D)
    // =========================================================================

    public static SKBitmap RenderScene3DBitmap(
        BoxScene3D scene,
        Camera3DController camera,
        SurfaceManager surfaceManager,
        RenderScene3DOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(surfaceManager);
        ArgumentNullException.ThrowIfNull(options);

        int w = Math.Clamp(options.WidthPx, 64, 2560);
        int h = Math.Clamp(options.HeightPx, 64, 1920);

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        RenderScene3DToCanvas(canvas, w, h, scene, camera, surfaceManager, options);
        return bitmap;
    }

    public static byte[] RenderScene3DPng(
        BoxScene3D scene,
        Camera3DController camera,
        SurfaceManager surfaceManager,
        RenderScene3DOptions options)
    {
        using SKBitmap bmp = RenderScene3DBitmap(scene, camera, surfaceManager, options);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void RenderScene3DToCanvas(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        BoxScene3D scene,
        Camera3DController camera,
        SurfaceManager surfaceManager,
        RenderScene3DOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(surfaceManager);
        ArgumentNullException.ThrowIfNull(options);

        CameraFrame frame = camera.BuildFrame(scene, widthPx, heightPx);
        bool dark = options.DarkTheme;
        float dpiScale = Math.Clamp(float.IsFinite(options.ScaleFactor) ? options.ScaleFactor : 1.0f, 1.0f, 3.0f);

        // 1. Fundo em gradiente CAD suave + grade perspectiva no piso Y = 0
        if (!options.TransparentBackground)
        {
            Draw3DBackgroundAndFloorGrid(canvas, widthPx, heightPx, scene, frame, dark, dpiScale);
        }

        // 2. Scanline Z-Buffer Paralelo + Bordas/Gravações Depth-Tested + SSAA adaptativo
        RenderScene3DPanelsZBuffer(canvas, widthPx, heightPx, scene, frame, surfaceManager, options, dpiScale);

        // 3. Overlay de Cotas Técnicas 3D (vetorial nítido)
        if (options.ShowDimensions)
        {
            Draw3DDimensions(canvas, scene, frame, dark, options.HighlightedDimensionAxis, dpiScale);
        }
    }

    /// <summary>Projeta os retângulos exatos (em px de tela) dos badges de cota 3D para hit-test e editor inline.</summary>
    public static IReadOnlyList<DimensionCalloutHitResult> ProjectDimensionCalloutBounds(
        BoxScene3D scene,
        Camera3DController camera,
        int viewportWidth,
        int viewportHeight,
        bool showDimensions = true,
        float scaleFactor = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);

        if (!showDimensions || viewportWidth <= 0 || viewportHeight <= 0 || scene.Dimensions.Count == 0)
        {
            return Array.Empty<DimensionCalloutHitResult>();
        }

        float scale = Math.Clamp(float.IsFinite(scaleFactor) ? scaleFactor : 1.0f, 1.0f, 3.0f);
        CameraFrame frame = camera.BuildFrame(scene, viewportWidth, viewportHeight);
        using var titleFont = new SKFont(MonoTypeface, 11f * scale) { Embolden = true };
        using var subFont = new SKFont(MonoTypeface, 9f * scale);

        var results = new List<DimensionCalloutHitResult>(scene.Dimensions.Count);
        foreach (DimensionCallout3D dim in scene.Dimensions)
        {
            if (TryProjectDimensionCalloutGeometry(
                    dim,
                    frame,
                    titleFont,
                    subFont,
                    scale,
                    out _,
                    out _,
                    out _,
                    out _,
                    out Vector2 lp,
                    out SKRect rect))
            {
                results.Add(new DimensionCalloutHitResult(dim.Axis, rect, lp, dim.Label));
            }
        }

        return results;
    }

    /// <summary>Testa se o ponto em tela (<paramref name="screenX"/>, <paramref name="screenY"/>) está sobre um badge de cota 3D.</summary>
    public static DimensionCalloutHitResult? HitTestDimensionCallout(
        BoxScene3D scene,
        Camera3DController camera,
        int viewportWidth,
        int viewportHeight,
        float screenX,
        float screenY,
        bool showDimensions = true,
        float hitPaddingPx = 4f,
        float scaleFactor = 1.0f)
    {
        IReadOnlyList<DimensionCalloutHitResult> projected = ProjectDimensionCalloutBounds(
            scene,
            camera,
            viewportWidth,
            viewportHeight,
            showDimensions,
            scaleFactor);

        if (projected.Count == 0)
        {
            return null;
        }

        float scale = Math.Clamp(float.IsFinite(scaleFactor) ? scaleFactor : 1.0f, 1.0f, 3.0f);
        float pad = MathF.Max(0f, hitPaddingPx) * scale;
        DimensionCalloutHitResult? best = null;
        float bestDistSq = float.PositiveInfinity;

        foreach (DimensionCalloutHitResult item in projected)
        {
            SKRect r = item.BadgeBoundsScreen;
            if (screenX >= r.Left - pad && screenX <= r.Right + pad &&
                screenY >= r.Top - pad && screenY <= r.Bottom + pad)
            {
                float dx = screenX - item.CenterScreen.X;
                float dy = screenY - item.CenterScreen.Y;
                float distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = item;
                }
            }
        }

        return best;
    }

    public static SKBitmap RenderAxisGizmoBitmap(Camera3DController camera, int sizePx = 46, bool dark = true)
    {
        ArgumentNullException.ThrowIfNull(camera);
        int s = Math.Clamp(sizePx, 28, 160);
        var info = new SKImageInfo(s, s, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        RenderAxisGizmoToCanvas(canvas, s, camera, dark);
        return bitmap;
    }

    public static void RenderAxisGizmoToCanvas(SKCanvas canvas, int sizePx, Camera3DController camera, bool dark)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(camera);

        canvas.Clear(SKColors.Transparent);

        // Utiliza cena neutra apenas para derivar a base de rotação da câmera
        float yawRad = (float)(camera.YawDeg * Math.PI / 180.0);
        float pitchRad = (float)(camera.PitchDeg * Math.PI / 180.0);
        float cosP = MathF.Cos(pitchRad);
        float sinP = MathF.Sin(pitchRad);
        float cosY = MathF.Cos(yawRad);
        float sinY = MathF.Sin(yawRad);

        Vector3 offsetDir = Vector3.Normalize(new Vector3(cosP * sinY, sinP, cosP * cosY));
        Vector3 forward = -offsetDir;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        float cx = sizePx * 0.45f;
        float cy = sizePx * 0.56f;
        float armLen = sizePx * 0.31f;

        var axes = new (string Name, Vector3 Dir, SKColor Color)[]
        {
            ("X", Vector3.UnitX, new SKColor(0xEF, 0x53, 0x50)),
            ("Y", Vector3.UnitY, new SKColor(0x4C, 0xAF, 0x50)),
            ("Z", Vector3.UnitZ, new SKColor(0x3B, 0x82, 0xF6)),
        };

        var projected = axes
            .Select(a =>
            {
                float xScreen = Vector3.Dot(a.Dir, right);
                float yScreen = -Vector3.Dot(a.Dir, up);
                float depth = Vector3.Dot(a.Dir, forward);
                return (a.Name, Ex: cx + xScreen * armLen, Ey: cy + yScreen * armLen, Depth: depth, a.Color);
            })
            .OrderByDescending(x => x.Depth) // mais distante primeiro
            .ToArray();

        using var linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.1f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        };
        using var font = new SKFont(SKTypeface.Default, 9.5f) { Embolden = true };
        using var textPaint = new SKPaint { IsAntialias = true };

        foreach (var ax in projected)
        {
            linePaint.Color = ax.Color;
            canvas.DrawLine(cx, cy, ax.Ex, ax.Ey, linePaint);

            float dx = ax.Ex - cx;
            float dy = ax.Ey - cy;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            float lx = len > 1f ? ax.Ex + (dx / len) * 6.5f : ax.Ex;
            float ly = len > 1f ? ax.Ey + (dy / len) * 6.5f + 3.5f : ax.Ey + 3.5f;

            textPaint.Color = ax.Color;
            canvas.DrawText(ax.Name, lx, ly, SKTextAlign.Center, font, textPaint);
        }

        using var hubPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = dark ? new SKColor(0xD1, 0xD5, 0xDB) : new SKColor(0x4B, 0x55, 0x63),
            IsAntialias = true,
        };
        canvas.DrawCircle(cx, cy, 2.3f, hubPaint);
    }

    // =========================================================================
    //  RENDERIZAÇÃO 2D CAD (CHAPA REAL + ZONAS + ELEMENTOS CNC + GIZMO)
    // =========================================================================

    public static SKBitmap RenderEditor2DBitmap(
        SurfaceCanvas2DController controller,
        SurfaceDefinition surface,
        IReadOnlyList<PersonalizationElement> elements,
        SurfaceContour2D? panelContour,
        RenderEditor2DOptions options)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(options);

        int w = Math.Clamp(options.WidthPx, 64, 2560);
        int h = Math.Clamp(options.HeightPx, 64, 1920);
        controller.UpdateLayout(surface, w, h, panelContour);

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        RenderEditor2DToCanvas(canvas, w, h, controller, surface, elements, panelContour, options);
        return bitmap;
    }

    public static byte[] RenderEditor2DPng(
        SurfaceCanvas2DController controller,
        SurfaceDefinition surface,
        IReadOnlyList<PersonalizationElement> elements,
        SurfaceContour2D? panelContour,
        RenderEditor2DOptions options)
    {
        using SKBitmap bmp = RenderEditor2DBitmap(controller, surface, elements, panelContour, options);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void RenderEditor2DToCanvas(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        SurfaceCanvas2DController controller,
        SurfaceDefinition surface,
        IReadOnlyList<PersonalizationElement> elements,
        SurfaceContour2D? panelContour,
        RenderEditor2DOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(options);

        bool dark = options.DarkTheme;
        float rulerPx = (float)SurfaceCanvas2DController.RulerThicknessPx;
        float scale = (float)controller.EffectiveScalePxPerMm;
        float ox = (float)controller.OriginPxX;
        float oy = (float)controller.OriginPxY;

        PersonalizationElement[] panelElements = elements
            .Where(e => e.Visible && e.PanelId == surface.PanelId)
            .ToArray();

        int selectedIndex = -1;
        int earliestSelectedIndex = -1;
        IReadOnlyList<string>? multiSelectedIds = options.SelectedElementIds;
        if (multiSelectedIds is { Count: > 0 })
        {
            for (int i = 0; i < panelElements.Length; i++)
            {
                string pid = panelElements[i].Id;
                bool isHit = false;
                for (int k = 0; k < multiSelectedIds.Count; k++)
                {
                    if (string.Equals(pid, multiSelectedIds[k], StringComparison.Ordinal))
                    {
                        isHit = true;
                        break;
                    }
                }

                if (isHit)
                {
                    if (earliestSelectedIndex < 0)
                    {
                        earliestSelectedIndex = i;
                    }
                    if (selectedIndex < 0 &&
                        string.Equals(pid, multiSelectedIds[0], StringComparison.Ordinal))
                    {
                        selectedIndex = i;
                    }
                }
            }

            if (selectedIndex < 0)
            {
                selectedIndex = earliestSelectedIndex;
            }
        }
        else if (!string.IsNullOrEmpty(options.SelectedElementId))
        {
            for (int i = 0; i < panelElements.Length; i++)
            {
                if (string.Equals(panelElements[i].Id, options.SelectedElementId, StringComparison.Ordinal))
                {
                    selectedIndex = i;
                    earliestSelectedIndex = i;
                    break;
                }
            }
        }

        // Elementos atrás do primeiro elemento selecionado são estáticos durante o arraste (único ou em grupo) e ficam pré-compostos no cache.
        int staticCount = earliestSelectedIndex >= 0 ? earliestSelectedIndex : panelElements.Length;
        string staticElementsSig = BuildStaticElementsSignature(panelElements, staticCount, options.ErrorElementIds);
        int contourHash = ComputeContourCacheHash(panelContour);

        var staticKey = new Editor2DStaticKey(
            widthPx,
            heightPx,
            dark,
            controller.OriginPxX,
            controller.OriginPxY,
            controller.EffectiveScalePxPerMm,
            surface.PanelId,
            surface.WidthMm,
            surface.HeightMm,
            options.DrawForbiddenZones,
            surface.ForbiddenZones.Count,
            options.DrawSafeArea,
            options.SafetyMarginMm,
            contourHash,
            staticElementsSig);

        if (controller.StaticRenderCache is not Editor2DStaticCache staticCache)
        {
            controller.StaticRenderCache?.Dispose();
            staticCache = new Editor2DStaticCache();
            controller.StaticRenderCache = staticCache;
        }

        staticCache.EnsureSurface(widthPx, heightPx);
        if (!Equals(staticCache.Key, staticKey) && staticCache.Canvas is { } bgCanvas)
        {
            bgCanvas.Clear(dark ? new SKColor(0x11, 0x13, 0x17) : new SKColor(0xDF, 0xE4, 0xEC));

            bgCanvas.Save();
            bgCanvas.ClipRect(new SKRect(rulerPx, rulerPx, widthPx, heightPx));

            // 1. Grade de mesa de corte em milímetros no fundo (10 mm fina / 50 mm principal)
            Draw2DMillimeterGrid(bgCanvas, widthPx, heightPx, ox, oy, scale, dark);

            // 2. Chapa MDF real em escala (com dentes, orelhas, rasgos e furos circulares)
            Draw2DMdfPanelSheet(bgCanvas, ox, oy, scale, surface, panelContour, dark);

            bgCanvas.Save();
            bgCanvas.Translate(ox, oy);
            bgCanvas.Scale(scale, scale);

            // 3. Zonas proibidas estruturais (#EF4444)
            if (options.DrawForbiddenZones)
            {
                Draw2DForbiddenZones(bgCanvas, surface);
            }

            // 4. Área segura útil (#22C55E tracejado)
            if (options.DrawSafeArea)
            {
                Rect2D safe = surface.SafeAreaForMargin(options.SafetyMarginMm);
                using var safePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(0x22, 0xC5, 0x5E, 220),
                    StrokeWidth = 1.4f / scale,
                    PathEffect = SKPathEffect.CreateDash([6f / scale, 4f / scale], 0f),
                    IsAntialias = true,
                };
                bgCanvas.DrawRect((float)safe.XMm, (float)safe.YMm, (float)safe.WidthMm, (float)safe.HeightMm, safePaint);
            }

            // Elementos estáticos situados atrás do selecionado
            if (staticCount > 0)
            {
                DrawSurfaceElements(
                    bgCanvas,
                    new ArraySegment<PersonalizationElement>(panelElements, 0, staticCount),
                    woodRealisticColors: false,
                    options.ErrorElementIds,
                    options.SelectedElementId,
                    showSelectionOutlines: false,
                    strokeScaleAdjust: 1f / Math.Max(0.4f, scale * 0.45f));
            }

            bgCanvas.Restore();
            bgCanvas.Restore();

            // Réguas milimetradas estáticas (fundo, marcas e números)
            Draw2DMillimeterRulersStatic(bgCanvas, widthPx, heightPx, ox, oy, scale, dark);
            bgCanvas.Flush();
            staticCache.Key = staticKey;
            controller.StaticRenderCacheRebuildCount++;
        }

        if (staticCache.Bitmap is not null)
        {
            canvas.DrawBitmap(staticCache.Bitmap, 0f, 0f, SrcCopyPaint);
        }

        canvas.Save();
        canvas.ClipRect(new SKRect(rulerPx, rulerPx, widthPx, heightPx));

        canvas.Save();
        canvas.Translate(ox, oy);
        canvas.Scale(scale, scale);

        // Guias magnéticas de snap CAD (#00E5FF tracejado cruzando a chapa)
        if (controller.SnapGuideXActive || controller.SnapGuideYActive || controller.SnapGuideX.HasValue || controller.SnapGuideY.HasValue)
        {
            Rect2D safe = surface.SafeAreaForMargin(options.SafetyMarginMm);
            using var snapPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(0x00, 0xE5, 0xFF, 230),
                StrokeWidth = 1.2f / scale,
                PathEffect = SKPathEffect.CreateDash([5f / scale, 3f / scale], 0f),
                IsAntialias = true,
            };
            if (controller.SnapGuideXActive || controller.SnapGuideX.HasValue)
            {
                float gx = (float)(controller.SnapGuideX ?? safe.Center.X);
                canvas.DrawLine(gx, 0f, gx, (float)surface.HeightMm, snapPaint);
            }
            if (controller.SnapGuideYActive || controller.SnapGuideY.HasValue)
            {
                float gy = (float)(controller.SnapGuideY ?? safe.Center.Y);
                canvas.DrawLine(0f, gy, (float)surface.WidthMm, gy, snapPaint);
            }
        }

        // 5. Conteúdo gráfico dos elementos dinâmicos (selecionado e posteriores)
        if (staticCount < panelElements.Length)
        {
            DrawSurfaceElements(
                canvas,
                new ArraySegment<PersonalizationElement>(panelElements, staticCount, panelElements.Length - staticCount),
                woodRealisticColors: false,
                options.ErrorElementIds,
                options.SelectedElementId,
                showSelectionOutlines: false,
                strokeScaleAdjust: 1f / Math.Max(0.4f, scale * 0.45f));
        }

        // Destaca contorno de erro SAT dos elementos inválidos
        if (options.ErrorElementIds is { Count: > 0 })
        {
            foreach (PersonalizationElement el in panelElements)
            {
                if (!options.ErrorElementIds.Contains(el.Id)) continue;
                DrawRotatedElementBox(
                    canvas,
                    el,
                    new SKColor(0xEF, 0x44, 0x44),
                    1.6f / scale,
                    dashed: el.Id != options.SelectedElementId,
                    scale,
                    fillError: true);
            }
        }

        canvas.Restore();

        // 6. Gizmo CAD de seleção (única) OU seleção múltipla em grupo + caixa Marquee
        if (multiSelectedIds is { Count: > 1 })
        {
            DrawMultiSelectionOverlay2D(
                canvas,
                controller,
                panelElements,
                multiSelectedIds,
                widthPx,
                heightPx,
                controller.IsDraggingGroup || controller.IsDragging || options.ShowLiveDimensionBadge);
        }
        else
        {
            PersonalizationElement? selected = selectedIndex >= 0 ? panelElements[selectedIndex] : null;
            if (selected is not null)
            {
                bool hasError = options.ErrorElementIds?.Contains(selected.Id) == true;
                DrawSelectionGizmo2D(canvas, controller, selected, hasError);

                if (controller.IsDrawingShape || controller.IsDragging || options.ShowLiveDimensionBadge)
                {
                    DrawLiveDimensionBadge2D(canvas, controller, selected, widthPx, heightPx);
                }
            }
        }

        if (controller.IsMarqueeSelecting)
        {
            DrawMarqueeSelectionBox2D(canvas, controller);
        }

        canvas.Restore();

        // 7. Cursores milimetrados ao vivo sobre as réguas em cache
        Draw2DMillimeterRulerCursorLines(
            canvas,
            widthPx,
            heightPx,
            ox,
            oy,
            scale,
            dark,
            controller.LastPointerMmX,
            controller.LastPointerMmY);
    }

    private static int ComputeContourCacheHash(SurfaceContour2D? contour)
    {
        if (contour is null) return 0;
        var hc = new HashCode();
        hc.Add(RuntimeHelpers.GetHashCode(contour.OuterContour));
        hc.Add(contour.OuterContour.Vertices.Count);
        hc.Add(contour.InternalHoles.Count);
        hc.Add(contour.CircularHoles.Count);
        hc.Add(Math.Round(contour.Bounds.MinX, 3));
        hc.Add(Math.Round(contour.Bounds.MinY, 3));
        hc.Add(Math.Round(contour.Bounds.Width, 3));
        hc.Add(Math.Round(contour.Bounds.Height, 3));
        return hc.ToHashCode();
    }

    private static string BuildStaticElementsSignature(
        PersonalizationElement[] panelElements,
        int staticCount,
        IReadOnlySet<string>? errorElementIds)
    {
        if (staticCount <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(staticCount * 48);
        for (int i = 0; i < staticCount; i++)
        {
            PersonalizationElement el = panelElements[i];
            bool hasErr = errorElementIds?.Contains(el.Id) == true;
            sb.Append(el.Id).Append(':')
              .Append((int)el.Operation).Append(':')
              .Append(hasErr ? '1' : '0').Append(':')
              .Append(el.XMm.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(el.YMm.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(el.WidthMm.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(el.HeightMm.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(el.RotationDeg.ToString("R", CultureInfo.InvariantCulture));

            switch (el)
            {
                case TextPersonalizationElement txt:
                    sb.Append(",T:").Append(RuntimeHelpers.GetHashCode(txt.VectorPaths)).Append(':').Append(txt.VectorPaths.Count);
                    break;
                case VectorSvgPersonalizationElement svg:
                    sb.Append(",V:").Append(RuntimeHelpers.GetHashCode(svg.Paths)).Append(':').Append(svg.Paths.Count);
                    break;
                case RasterImagePersonalizationElement rst:
                    sb.Append(",R:").Append(rst.Vectorize ? '1' : '0').Append(':')
                      .Append(RuntimeHelpers.GetHashCode(rst.VectorPaths)).Append(':')
                      .Append(rst.ProcessedImageB64?.Length ?? 0).Append(':')
                      .Append(rst.ProcessedImageB64?.GetHashCode(StringComparison.Ordinal) ?? 0);
                    break;
                case ShapePersonalizationElement shp:
                    sb.Append(",S:").Append((int)shp.OperationMode).Append(':')
                      .Append(RuntimeHelpers.GetHashCode(shp.VectorPaths)).Append(':')
                      .Append(shp.VectorPaths.Count).Append(':')
                      .Append(shp.ShapeSpec?.GetSignature() ?? string.Empty);
                    break;
            }

            sb.Append(';');
        }

        return sb.ToString();
    }

    // =========================================================================
    //  PLANO DE CORTE COMPLETO 2D (SHEET NESTING 1:1 + GABARITO DE MESA LASER)
    // =========================================================================

    public static SheetViewTransform2D ComputeManufacturingSheetTransform(
        BoxSheetGeometry2D sheet,
        RenderManufacturingSheet2DOptions options)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(options);

        double rulerPx = SurfaceCanvas2DController.RulerThicknessPx;
        int widthPx = Math.Clamp(options.WidthPx, 64, 2560);
        int heightPx = Math.Clamp(options.HeightPx, 64, 1920);

        double spanW = Math.Max(1.0, sheet.Layout.SheetWidth);
        double spanH = Math.Max(1.0, sheet.Layout.SheetHeight);
        if (options.ShowLaserBedOverlay && options.LaserBedWidthMm > 1.0 && options.LaserBedHeightMm > 1.0)
        {
            spanW = Math.Max(spanW, options.LaserBedWidthMm);
            spanH = Math.Max(spanH, options.LaserBedHeightMm);
        }

        double availW = Math.Max(40.0, widthPx - rulerPx - 72.0);
        double availH = Math.Max(40.0, heightPx - rulerPx - 78.0);
        double fitScale = Math.Max(0.05, Math.Min(availW / spanW, availH / spanH));
        double zoom = Math.Clamp(options.UserZoom, 0.25, 12.0);
        double effScale = Math.Max(1e-4, fitScale * zoom);

        double ox = rulerPx + ((widthPx - rulerPx) - spanW * effScale) * 0.5 + options.PanPxX;
        double oy = rulerPx + ((heightPx - rulerPx) - spanH * effScale) * 0.5 + 4.0 + options.PanPxY;
        return new SheetViewTransform2D(fitScale, effScale, ox, oy);
    }

    public static SKBitmap RenderManufacturingSheet2DBitmap(
        BoxSheetGeometry2D sheet,
        SurfaceMapper? surfaceMapper,
        IReadOnlyList<PersonalizationElement> elements,
        RenderManufacturingSheet2DOptions options)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(options);

        int w = Math.Clamp(options.WidthPx, 64, 2560);
        int h = Math.Clamp(options.HeightPx, 64, 1920);

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        RenderManufacturingSheet2DToCanvas(canvas, w, h, sheet, surfaceMapper, elements, options);
        return bitmap;
    }

    public static byte[] RenderManufacturingSheet2DPng(
        BoxSheetGeometry2D sheet,
        SurfaceMapper? surfaceMapper,
        IReadOnlyList<PersonalizationElement> elements,
        RenderManufacturingSheet2DOptions options)
    {
        using SKBitmap bmp = RenderManufacturingSheet2DBitmap(sheet, surfaceMapper, elements, options);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void RenderManufacturingSheet2DToCanvas(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        BoxSheetGeometry2D sheet,
        SurfaceMapper? surfaceMapper,
        IReadOnlyList<PersonalizationElement> elements,
        RenderManufacturingSheet2DOptions options)
        => RenderManufacturingSheet2DToCanvas(
            canvas,
            widthPx,
            heightPx,
            sheet,
            surfaceMapper,
            elements,
            options,
            controller: null);

    public static void RenderManufacturingSheet2DToCanvas(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        BoxSheetGeometry2D sheet,
        SurfaceMapper? surfaceMapper,
        IReadOnlyList<PersonalizationElement> elements,
        RenderManufacturingSheet2DOptions options,
        SurfaceCanvas2DController? controller)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(options);

        bool dark = options.DarkTheme;
        SheetViewTransform2D tx = ComputeManufacturingSheetTransform(sheet, options);
        float scale = (float)tx.EffectiveScalePxPerMm;
        float ox = (float)tx.OriginPxX;
        float oy = (float)tx.OriginPxY;

        if (controller is not null)
        {
            int layerMask = ComputeLayerVisibilityMask(options.IsLayerVisible);
            string elementsSig = BuildManufacturingElementsSignature(
                surfaceMapper,
                elements,
                options);
            var staticKey = new ManufacturingSheetStaticKey(
                widthPx,
                heightPx,
                dark,
                ComputeSheetIdentityHash(sheet),
                surfaceMapper is null ? 0 : RuntimeHelpers.GetHashCode(surfaceMapper),
                tx.EffectiveScalePxPerMm,
                tx.OriginPxX,
                tx.OriginPxY,
                options.SelectedPartId ?? string.Empty,
                options.SelectedElementId ?? string.Empty,
                options.ShowLaserBedOverlay,
                options.LaserBedWidthMm,
                options.LaserBedHeightMm,
                options.LaserBedLabel ?? string.Empty,
                options.ShowSheetDimensions,
                layerMask,
                elementsSig);

            if (controller.StaticRenderCache is not ManufacturingSheetStaticCache sheetCache)
            {
                controller.StaticRenderCache?.Dispose();
                sheetCache = new ManufacturingSheetStaticCache();
                controller.StaticRenderCache = sheetCache;
            }

            sheetCache.EnsureSurface(widthPx, heightPx);
            if (!Equals(sheetCache.Key, staticKey) && sheetCache.Canvas is { } bgCanvas)
            {
                RenderManufacturingSheetStaticLayers(
                    bgCanvas,
                    widthPx,
                    heightPx,
                    sheet,
                    surfaceMapper,
                    elements,
                    options,
                    tx);
                bgCanvas.Flush();
                sheetCache.Key = staticKey;
            }

            if (sheetCache.Bitmap is not null)
            {
                canvas.DrawBitmap(sheetCache.Bitmap, 0f, 0f, SrcCopyPaint);
            }
        }
        else
        {
            RenderManufacturingSheetStaticLayers(
                canvas,
                widthPx,
                heightPx,
                sheet,
                surfaceMapper,
                elements,
                options,
                tx);
        }

        // Cursores milimetrados ao vivo sobre as réguas em cache
        Draw2DMillimeterRulerCursorLines(
            canvas,
            widthPx,
            heightPx,
            ox,
            oy,
            scale,
            dark,
            options.CursorMmX,
            options.CursorMmY);
    }

    private static void RenderManufacturingSheetStaticLayers(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        BoxSheetGeometry2D sheet,
        SurfaceMapper? surfaceMapper,
        IReadOnlyList<PersonalizationElement> elements,
        RenderManufacturingSheet2DOptions options,
        SheetViewTransform2D tx)
    {
        bool dark = options.DarkTheme;
        float rulerPx = (float)SurfaceCanvas2DController.RulerThicknessPx;
        float scale = (float)tx.EffectiveScalePxPerMm;
        float ox = (float)tx.OriginPxX;
        float oy = (float)tx.OriginPxY;

        canvas.Clear(dark ? new SKColor(0x11, 0x13, 0x17) : new SKColor(0xDF, 0xE4, 0xEC));

        canvas.Save();
        canvas.ClipRect(new SKRect(rulerPx, rulerPx, widthPx, heightPx));

        // 1. Grade de corte em milímetros no fundo
        Draw2DMillimeterGrid(canvas, widthPx, heightPx, ox, oy, scale, dark);

        float sheetW = (float)sheet.Layout.SheetWidth;
        float sheetH = (float)sheet.Layout.SheetHeight;

        // 2. Transformação em milímetros da folha completa (0..SheetWidth, 0..SheetHeight)
        canvas.Save();
        canvas.Translate(ox, oy);
        canvas.Scale(scale, scale);

        bool hasBedOverlay = options.ShowLaserBedOverlay && options.LaserBedWidthMm > 1.0 && options.LaserBedHeightMm > 1.0;

        // 2b. Área de fundo translúcida da mesa Laser/CNC (desenhada ANTES das chapas para não alterar a cor do MDF)
        if (hasBedOverlay)
        {
            using var bedBackdrop = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = dark ? new SKColor(0x0E, 0xA5, 0xE9, 14) : new SKColor(0x0E, 0xA5, 0xE9, 12),
            };
            canvas.DrawRect(0f, 0f, (float)options.LaserBedWidthMm, (float)options.LaserBedHeightMm, bedBackdrop);
        }

        // Quadro geral da folha de MDF (contorno discreto de referência)
        using (var sheetBorderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = dark ? new SKColor(0x47, 0x55, 0x69, 150) : new SKColor(0x64, 0x74, 0x8B, 140),
            StrokeWidth = 1.0f / scale,
            PathEffect = SKPathEffect.CreateDash([4f / scale, 3f / scale], 0f),
            IsAntialias = false,
        })
        {
            canvas.DrawRect(0f, 0f, sheetW, sheetH, sheetBorderPaint);
        }

        // 3. Todas as chapas (5 ou 6 painéis) exatamente posicionadas para corte CNC
        bool showCutExt = options.IsLayerVisible?.Invoke(CncOperationType.CutExternal) ?? true;
        bool showCutInt = options.IsLayerVisible?.Invoke(CncOperationType.CutInternal) ?? true;

        var panelFillBg = dark ? new SKColor(0xEF, 0xE0, 0xCA) : new SKColor(0xF5, 0xE6, 0xD3);
        var panelShadowColor = dark ? new SKColor(0x00, 0x00, 0x00, 88) : new SKColor(0x0F, 0x17, 0x2A, 38);
        var selectedTint = new SKColor(0x22, 0xC5, 0x5E, 46);
        var selectedStroke = new SKColor(0x22, 0xC5, 0x5E);
        var outerCutColor = SKColors.Black;
        var internalCutColor = ResolveCncLayerColor(CncOperationType.CutInternal);

        foreach (PanelGeometry2D panel in sheet.Panels)
        {
            bool isSelected = PanelMatchesSelection(panel.PanelId, options.SelectedPartId);
            DrawSheetPanel2D(
                canvas,
                sheet,
                panel,
                scale,
                panelFillBg,
                panelShadowColor,
                outerCutColor,
                internalCutColor,
                selectedTint,
                selectedStroke,
                isSelected,
                showCutExt,
                showCutInt);
        }

        // 4. Elementos de personalização CNC mapeados na posição exata (inclui Tampa 180°)
        if (surfaceMapper is not null)
        {
            DrawManufacturingSheetElements(canvas, surfaceMapper, elements, options, scale);
        }

        // 5. Moldura tracejada da área útil da mesa Laser/CNC + indicação de excesso
        if (hasBedOverlay)
        {
            DrawSheetLaserBedOverlayMm(
                canvas,
                sheetW,
                sheetH,
                (float)options.LaserBedWidthMm,
                (float)options.LaserBedHeightMm,
                scale,
                dark);
        }

        canvas.Restore();

        // 6. Rótulos discretos das chapas, badge da mesa e cotas globais da folha em espaço de tela
        DrawManufacturingSheetAnnotations(canvas, sheet, ox, oy, scale, dark, options);

        canvas.Restore();

        // 7. Réguas milimetradas graduadas estáticas (18 px)
        Draw2DMillimeterRulersStatic(canvas, widthPx, heightPx, ox, oy, scale, dark);
    }

    private static int ComputeSheetIdentityHash(BoxSheetGeometry2D sheet)
    {
        var hc = new HashCode();
        hc.Add(RuntimeHelpers.GetHashCode(sheet));
        hc.Add(Math.Round(sheet.Layout.SheetWidth, 3));
        hc.Add(Math.Round(sheet.Layout.SheetHeight, 3));
        hc.Add(sheet.Panels.Count);
        return hc.ToHashCode();
    }

    private static int ComputeLayerVisibilityMask(Func<CncOperationType, bool>? isLayerVisible)
    {
        if (isLayerVisible is null)
        {
            return 0b111111;
        }

        int mask = 0;
        if (isLayerVisible(CncOperationType.CutExternal)) mask |= 1 << 0;
        if (isLayerVisible(CncOperationType.CutInternal)) mask |= 1 << 1;
        if (isLayerVisible(CncOperationType.EngraveVector)) mask |= 1 << 2;
        if (isLayerVisible(CncOperationType.EngraveFill)) mask |= 1 << 3;
        if (isLayerVisible(CncOperationType.Mark)) mask |= 1 << 4;
        if (isLayerVisible(CncOperationType.EngraveRaster)) mask |= 1 << 5;
        return mask;
    }

    private static string BuildManufacturingElementsSignature(
        SurfaceMapper? surfaceMapper,
        IReadOnlyList<PersonalizationElement> elements,
        RenderManufacturingSheet2DOptions options)
    {
        if (surfaceMapper is null || elements.Count == 0)
        {
            return string.Empty;
        }

        var visible = new List<PersonalizationElement>(elements.Count);
        for (int i = 0; i < elements.Count; i++)
        {
            PersonalizationElement el = elements[i];
            if (!el.Visible || !surfaceMapper.TryGetSurface(el.PanelId, out _))
            {
                continue;
            }

            if (options.IsLayerVisible is not null && !options.IsLayerVisible(el.Operation))
            {
                continue;
            }

            visible.Add(el);
        }

        return visible.Count == 0
            ? string.Empty
            : BuildStaticElementsSignature(visible.ToArray(), visible.Count, options.ErrorElementIds);
    }

    public static ManufacturingSheetPanelHit? HitTestManufacturingSheetPanel(
        BoxSheetGeometry2D sheet,
        RenderManufacturingSheet2DOptions options,
        double pxX,
        double pxY)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(options);

        SheetViewTransform2D tx = ComputeManufacturingSheetTransform(sheet, options);
        if (tx.EffectiveScalePxPerMm <= 1e-6)
        {
            return null;
        }

        double mmX = (pxX - tx.OriginPxX) / tx.EffectiveScalePxPerMm;
        double mmY = (pxY - tx.OriginPxY) / tx.EffectiveScalePxPerMm;
        var pt = new Point2D(mmX, mmY);

        foreach (PanelGeometry2D panel in sheet.Panels)
        {
            BoundingBox2D sheetBounds = panel.SheetBounds;
            if (!BoundsContains(sheetBounds, pt))
            {
                continue;
            }

            Polygon2D outer = panel.GetSheetOuterContour();
            if (IsPointInPolygon(outer.Vertices, pt))
            {
                return new ManufacturingSheetPanelHit(
                    panel.PanelId,
                    MapPanelIdToPartId(panel.PanelId),
                    MapPanelIdToSurfaceId(panel.PanelId),
                    sheetBounds);
            }
        }

        // Fallback tolerante em bordas/dentes para cliques dentro da caixa delimitadora
        foreach (PanelGeometry2D panel in sheet.Panels)
        {
            BoundingBox2D sheetBounds = panel.SheetBounds;
            if (BoundsContains(sheetBounds, pt))
            {
                return new ManufacturingSheetPanelHit(
                    panel.PanelId,
                    MapPanelIdToPartId(panel.PanelId),
                    MapPanelIdToSurfaceId(panel.PanelId),
                    sheetBounds);
            }
        }

        return null;
    }

    private static bool BoundsContains(BoundingBox2D bounds, Point2D pt)
        => pt.X >= bounds.MinX && pt.X <= bounds.MaxX && pt.Y >= bounds.MinY && pt.Y <= bounds.MaxY;

    public static string MapPanelIdToPartId(string panelId) => panelId.ToLowerInvariant() switch
    {
        "front" or "back" => "front_back",
        "left" or "right" => "side",
        "bottom" => "bottom",
        "lid" => "lid",
        _ => panelId,
    };

    public static SurfaceId MapPanelIdToSurfaceId(string panelId) => panelId.ToLowerInvariant() switch
    {
        "back" => SurfaceId.Back,
        "left" => SurfaceId.Left,
        "right" => SurfaceId.Right,
        "bottom" => SurfaceId.Bottom,
        "lid" => SurfaceId.Lid,
        _ => SurfaceId.Front,
    };

    private static bool PanelMatchesSelection(string panelId, string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            return false;
        }

        return string.Equals(panelId, selectedId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(MapPanelIdToPartId(panelId), selectedId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPointInPolygon(IReadOnlyList<Point2D> polygon, Point2D pt)
    {
        if (polygon.Count < 3) return false;
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Point2D pi = polygon[i];
            Point2D pj = polygon[j];
            bool intersect = (pi.Y > pt.Y) != (pj.Y > pt.Y)
                && pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / (pj.Y - pi.Y + 1e-12) + pi.X;
            if (intersect)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static CachedSheetPanelPaths? GetOrCreateSheetPanelPaths(BoxSheetGeometry2D sheet, PanelGeometry2D panel)
    {
        CompiledSheetPanelsEntry entry = PanelContourSkiaCache.GetOrCreateValue(sheet);
        lock (PanelContourCompileLock)
        {
            if (entry.Panels.TryGetValue(panel.PanelId, out CachedSheetPanelPaths? existing))
            {
                return existing;
            }
        }

        Polygon2D outer = panel.GetSheetOuterContour();
        if (outer.Vertices.Count < 3)
        {
            return null;
        }

        IReadOnlyList<Polygon2D> internalHoles = panel.GetSheetInternalHoles();
        IReadOnlyList<Circle2D> circularHoles = panel.GetSheetCircularHoles();

        var woodPath = new SKPath { FillType = SKPathFillType.EvenOdd };
        AppendPolygonToPath(woodPath, outer);
        foreach (Polygon2D hole in internalHoles)
        {
            AppendPolygonToPath(woodPath, hole);
        }
        foreach (Circle2D circle in circularHoles)
        {
            woodPath.AddCircle((float)circle.CenterX, (float)circle.CenterY, (float)circle.Radius);
        }

        SKPath? intPath = null;
        if (internalHoles.Count > 0 || circularHoles.Count > 0)
        {
            intPath = new SKPath();
            foreach (Polygon2D hole in internalHoles)
            {
                AppendPolygonToPath(intPath, hole);
            }
            foreach (Circle2D circle in circularHoles)
            {
                intPath.AddCircle((float)circle.CenterX, (float)circle.CenterY, (float)circle.Radius);
            }
        }

        var outerPath = new SKPath();
        AppendPolygonToPath(outerPath, outer);

        var created = new CachedSheetPanelPaths
        {
            WoodPath = woodPath,
            InternalCutPath = intPath,
            OuterCutPath = outerPath,
        };

        lock (PanelContourCompileLock)
        {
            if (entry.Panels.TryGetValue(panel.PanelId, out CachedSheetPanelPaths? race))
            {
                woodPath.Dispose();
                intPath?.Dispose();
                outerPath.Dispose();
                return race;
            }

            entry.Panels[panel.PanelId] = created;
            return created;
        }
    }

    private static void DrawSheetPanel2D(
        SKCanvas canvas,
        BoxSheetGeometry2D sheet,
        PanelGeometry2D panel,
        float scale,
        SKColor fillColor,
        SKColor shadowColor,
        SKColor outerCutColor,
        SKColor internalCutColor,
        SKColor selectedTint,
        SKColor selectedStroke,
        bool isSelected,
        bool showCutExt,
        bool showCutInt)
    {
        CachedSheetPanelPaths? paths = GetOrCreateSheetPanelPaths(sheet, panel);
        if (paths is null)
        {
            return;
        }

        // Sombra sutil sob a peça na folha MDF
        canvas.Save();
        float shadowShift = 1.6f / MathF.Max(0.4f, scale);
        canvas.Translate(shadowShift, shadowShift * 1.4f);
        using (var shadowPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = shadowColor, IsAntialias = true })
        {
            canvas.DrawPath(paths.WoodPath, shadowPaint);
        }
        canvas.Restore();

        // Preenchimento MDF claro (garante leitura nítida das camadas CNC sobre madeira)
        using (var woodPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = fillColor, IsAntialias = true })
        {
            canvas.DrawPath(paths.WoodPath, woodPaint);
        }

        if (isSelected)
        {
            using var selFill = new SKPaint { Style = SKPaintStyle.Fill, Color = selectedTint, IsAntialias = true };
            canvas.DrawPath(paths.WoodPath, selFill);
        }

        // Recortes internos (CUT_INTERNAL #B71C1C)
        if (showCutInt && paths.InternalCutPath is not null)
        {
            using var intPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = internalCutColor,
                StrokeWidth = 1.3f / scale,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawPath(paths.InternalCutPath, intPaint);
        }

        // Contorno externo (CUT_OUTER #000000 ou destaque verde #22C55E quando selecionada)
        if (showCutExt || isSelected)
        {
            using var extPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = isSelected ? selectedStroke : outerCutColor,
                StrokeWidth = (isSelected ? 2.4f : 1.45f) / scale,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawPath(paths.OuterCutPath, extPaint);
        }
        else
        {
            using var faintPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(0x64, 0x74, 0x8B, 110),
                StrokeWidth = 0.9f / scale,
                PathEffect = SKPathEffect.CreateDash([3f / scale, 3f / scale], 0f),
                IsAntialias = true,
            };
            canvas.DrawPath(paths.OuterCutPath, faintPaint);
        }
    }

    private static void DrawManufacturingSheetElements(
        SKCanvas canvas,
        SurfaceMapper surfaceMapper,
        IReadOnlyList<PersonalizationElement> elements,
        RenderManufacturingSheet2DOptions options,
        float scale)
    {
        float strokeScaleAdjust = 1f / Math.Max(0.35f, scale * 0.45f);
        ConcurrentDictionary<MappedElementTransformKey, PanelSvgTransformResult> transformCache =
            MappedSheetElementsCache.GetOrCreateValue(surfaceMapper);

        foreach (PersonalizationElement el in elements)
        {
            if (!el.Visible || !surfaceMapper.TryGetSurface(el.PanelId, out _))
            {
                continue;
            }

            if (options.IsLayerVisible is not null && !options.IsLayerVisible(el.Operation))
            {
                continue;
            }

            Point2D center = el.CenterMm();
            var cacheKey = new MappedElementTransformKey(el.PanelId, center.X, center.Y, el.RotationDeg);
            if (!transformCache.TryGetValue(cacheKey, out PanelSvgTransformResult mapped))
            {
                mapped = surfaceMapper.SurfaceToPanelSvg(
                    el.PanelId,
                    center.X,
                    center.Y,
                    rotationDeg: el.RotationDeg,
                    originOffsetMm: null);
                transformCache.TryAdd(cacheKey, mapped);
            }

            bool hasError = options.ErrorElementIds?.Contains(el.Id) == true;
            bool isSelected = options.SelectedElementId == el.Id;

            canvas.Save();
            canvas.Translate((float)mapped.XMm, (float)mapped.YMm);
            if (Math.Abs(mapped.RotationDeg) > 1e-4)
            {
                canvas.RotateDegrees((float)mapped.RotationDeg);
            }
            canvas.Translate(-(float)(el.WidthMm * 0.5), -(float)(el.HeightMm * 0.5));

            DrawElementLocalContent(canvas, el, woodRealisticColors: false, hasError, strokeScaleAdjust);

            if (isSelected || hasError)
            {
                using var framePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = hasError ? new SKColor(0xEF, 0x44, 0x44) : new SKColor(0x16, 0xA3, 0x4A),
                    StrokeWidth = (isSelected ? 1.5f : 1.3f) / scale,
                    IsAntialias = true,
                };
                canvas.DrawRect(0f, 0f, (float)el.WidthMm, (float)el.HeightMm, framePaint);
            }

            canvas.Restore();
        }
    }

    private static void DrawSheetLaserBedOverlayMm(
        SKCanvas canvas,
        float sheetW,
        float sheetH,
        float bedW,
        float bedH,
        float scale,
        bool dark)
    {
        var bedRect = new SKRect(0f, 0f, bedW, bedH);
        SKColor cyanStroke = dark ? new SKColor(0x38, 0xBD, 0xF8, 225) : new SKColor(0x02, 0x84, 0xC7, 225);

        // Destaca faixas em que a folha ultrapassa a área útil da mesa
        if (sheetW > bedW + 1e-4f || sheetH > bedH + 1e-4f)
        {
            using var excessFill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(0xF5, 0x9E, 0x0B, 42),
            };
            using var excessBorder = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(0xF5, 0x9E, 0x0B, 220),
                StrokeWidth = 1.4f / scale,
                IsAntialias = true,
            };

            if (sheetW > bedW + 1e-4f)
            {
                var rightExcess = new SKRect(bedW, 0f, sheetW, sheetH);
                canvas.DrawRect(rightExcess, excessFill);
                canvas.DrawRect(rightExcess, excessBorder);
            }
            if (sheetH > bedH + 1e-4f)
            {
                var bottomExcess = new SKRect(0f, bedH, Math.Min(sheetW, bedW), sheetH);
                canvas.DrawRect(bottomExcess, excessFill);
                canvas.DrawRect(bottomExcess, excessBorder);
            }
        }

        using var bedStroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = cyanStroke,
            StrokeWidth = 1.6f / scale,
            PathEffect = SKPathEffect.CreateDash([7f / scale, 4f / scale], 0f),
            IsAntialias = true,
        };
        canvas.DrawRect(bedRect, bedStroke);
    }

    private static void DrawManufacturingSheetAnnotations(
        SKCanvas canvas,
        BoxSheetGeometry2D sheet,
        float ox,
        float oy,
        float scale,
        bool dark,
        RenderManufacturingSheet2DOptions options)
    {
        // 1. Etiquetas discretas nos cantos superiores esquerdos de cada chapa (não cobrem gravações centrais)
        using var tagFont = new SKFont(MonoTypeface, 9.0f) { Embolden = true };
        using var tagBgPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = dark ? new SKColor(0x16, 0x1A, 0x22, 200) : new SKColor(0xFF, 0xFF, 0xFF, 210),
            IsAntialias = true,
        };
        using var tagBorderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
        };
        using var tagTextPaint = new SKPaint { IsAntialias = true };

        foreach (PanelGeometry2D panel in sheet.Panels)
        {
            BoundingBox2D b = panel.SheetBounds;
            float leftPx = ox + (float)b.MinX * scale;
            float topPx = oy + (float)b.MinY * scale;
            float widthPx = (float)b.Width * scale;
            float heightPx = (float)b.Height * scale;
            if (widthPx < 36f || heightPx < 22f) continue;

            string label = FormatSheetPanelBadgeText(panel.PanelId);
            bool isSelected = PanelMatchesSelection(panel.PanelId, options.SelectedPartId);
            float textW = tagFont.MeasureText(label);
            var pill = new SKRect(leftPx + 4f, topPx + 4f, leftPx + 12f + textW, topPx + 18f);

            tagBorderPaint.Color = isSelected
                ? new SKColor(0x22, 0xC5, 0x5E)
                : (dark ? new SKColor(0x47, 0x55, 0x69, 175) : new SKColor(0x94, 0xA3, 0xB8, 185));
            tagTextPaint.Color = isSelected
                ? new SKColor(0x22, 0xC5, 0x5E)
                : (dark ? new SKColor(0xE2, 0xE8, 0xF0) : new SKColor(0x1E, 0x29, 0x3B));

            canvas.DrawRoundRect(pill, 2f, 2f, tagBgPaint);
            canvas.DrawRoundRect(pill, 2f, 2f, tagBorderPaint);
            canvas.DrawText(label, pill.Left + 4f, pill.Top + 10.3f, SKTextAlign.Left, tagFont, tagTextPaint);
        }

        float sLeft = ox;
        float sTop = oy;
        float sRight = ox + (float)sheet.Layout.SheetWidth * scale;
        float sBottom = oy + (float)sheet.Layout.SheetHeight * scale;

        // 2. Cotas totais externas da folha (Largura × Altura)
        if (options.ShowSheetDimensions)
        {
            using var dimFont = new SKFont(MonoTypeface, 9.5f) { Embolden = true };
            SKColor dimColor = dark ? new SKColor(0xCB, 0xD5, 0xE1) : new SKColor(0x33, 0x41, 0x55);
            SKColor dimBg = dark ? new SKColor(0x18, 0x1C, 0x24, 225) : new SKColor(0xFF, 0xFF, 0xFF, 230);
            using var linePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = dimColor, StrokeWidth = 1f, IsAntialias = true };
            using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = dimBg, IsAntialias = true };
            using var textPaint = new SKPaint { Color = dimColor, IsAntialias = true };

            // Cota horizontal superior (Largura da Folha)
            float dimY = sTop - 10f;
            if (dimY > SurfaceCanvas2DController.RulerThicknessPx + 8f && sRight - sLeft > 40f)
            {
                canvas.DrawLine(sLeft, sTop - 3f, sLeft, dimY - 3f, linePaint);
                canvas.DrawLine(sRight, sTop - 3f, sRight, dimY - 3f, linePaint);
                canvas.DrawLine(sLeft, dimY, sRight, dimY, linePaint);

                string wText = string.Format(CultureInfo.InvariantCulture, "{0:0.#} mm", sheet.Layout.SheetWidth);
                float wTextWidth = dimFont.MeasureText(wText);
                float midX = (sLeft + sRight) * 0.5f;
                var wRect = new SKRect(midX - wTextWidth * 0.5f - 4f, dimY - 7.5f, midX + wTextWidth * 0.5f + 4f, dimY + 5.5f);
                canvas.DrawRoundRect(wRect, 2f, 2f, bgPaint);
                canvas.DrawText(wText, midX, dimY + 3f, SKTextAlign.Center, dimFont, textPaint);
            }

            // Cota vertical à direita da folha (Altura da Folha)
            float dimX = sRight + 10f;
            if (dimX < options.WidthPx - 20f && sBottom - sTop > 36f)
            {
                canvas.DrawLine(sRight + 3f, sTop, dimX + 3f, sTop, linePaint);
                canvas.DrawLine(sRight + 3f, sBottom, dimX + 3f, sBottom, linePaint);
                canvas.DrawLine(dimX, sTop, dimX, sBottom, linePaint);

                string hText = string.Format(CultureInfo.InvariantCulture, "{0:0.#} mm", sheet.Layout.SheetHeight);
                float hTextWidth = dimFont.MeasureText(hText);
                float midY = (sTop + sBottom) * 0.5f;
                var hRect = new SKRect(dimX - 7f, midY - hTextWidth * 0.5f - 4f, dimX + 6f, midY + hTextWidth * 0.5f + 4f);
                canvas.DrawRoundRect(hRect, 2f, 2f, bgPaint);
                canvas.Save();
                canvas.Translate(dimX + 3.5f, midY);
                canvas.RotateDegrees(-90f);
                canvas.DrawText(hText, 0f, 0f, SKTextAlign.Center, dimFont, textPaint);
                canvas.Restore();
            }
        }

        // 3. Badge da mesa laser CNC no canto superior direito ou inferior da moldura ciano
        if (options.ShowLaserBedOverlay && options.LaserBedWidthMm > 1.0 && options.LaserBedHeightMm > 1.0)
        {
            float bedRight = ox + (float)options.LaserBedWidthMm * scale;
            float bedBottom = oy + (float)options.LaserBedHeightMm * scale;
            string bedText = string.IsNullOrWhiteSpace(options.LaserBedLabel)
                ? string.Format(CultureInfo.InvariantCulture, "MESA CNC: {0:0.#} × {1:0.#} mm", options.LaserBedWidthMm, options.LaserBedHeightMm)
                : $"MESA CNC: {options.LaserBedLabel}";

            using var bedFont = new SKFont(MonoTypeface, 9.0f) { Embolden = true };
            float bw = bedFont.MeasureText(bedText);
            float badgeRight = MathF.Min(bedRight - 4f, options.WidthPx - 8f);
            float badgeBottom = MathF.Min(bedBottom - 4f, options.HeightPx - 6f);
            var bedRect = new SKRect(badgeRight - bw - 10f, badgeBottom - 16f, badgeRight, badgeBottom);
            if (bedRect.Left > SurfaceCanvas2DController.RulerThicknessPx + 4f && bedRect.Top > SurfaceCanvas2DController.RulerThicknessPx + 4f)
            {
                using var bg = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = dark ? new SKColor(0x0B, 0x16, 0x24, 225) : new SKColor(0xEC, 0xFE, 0xFF, 235),
                    IsAntialias = true,
                };
                using var border = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = dark ? new SKColor(0x38, 0xBD, 0xF8, 220) : new SKColor(0x02, 0x84, 0xC7, 220),
                    StrokeWidth = 1f,
                    IsAntialias = true,
                };
                using var textP = new SKPaint
                {
                    Color = dark ? new SKColor(0x7D, 0xD3, 0xFC) : new SKColor(0x03, 0x69, 0xA1),
                    IsAntialias = true,
                };
                canvas.DrawRoundRect(bedRect, 2f, 2f, bg);
                canvas.DrawRoundRect(bedRect, 2f, 2f, border);
                canvas.DrawText(bedText, bedRect.Left + 5f, bedRect.Top + 11.2f, SKTextAlign.Left, bedFont, textP);
            }
        }
    }

    private static string FormatSheetPanelBadgeText(string panelId) => panelId.ToLowerInvariant() switch
    {
        "front" => "FRENTE",
        "back" => "TRÁS",
        "left" => "ESQ",
        "right" => "DIR",
        "bottom" => "BASE",
        "lid" => "TAMPA 180°",
        _ => panelId.ToUpperInvariant(),
    };

    // =========================================================================
    //  AUXILIARES PRIVADOS DO RENDERIZADOR 3D
    // =========================================================================

    private static void Draw3DBackgroundAndFloorGrid(
        SKCanvas canvas,
        int w,
        int h,
        BoxScene3D scene,
        CameraFrame frame,
        bool dark,
        float dpiScale)
    {
        canvas.Clear(dark ? new SKColor(0x11, 0x13, 0x17) : new SKColor(0xDF, 0xE4, 0xEC));

        float gridHalf = MathF.Max(280f, MathF.Max((float)scene.WidthMm, (float)scene.DepthMm) * 1.6f);
        float step = 10f;
        int majorEvery = 5;

        using var minorPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f * dpiScale,
            Color = dark ? new SKColor(0x1D, 0x23, 0x2C) : new SKColor(0xC9, 0xD1, 0xDC),
            IsAntialias = true,
        };
        using var majorPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.25f * dpiScale,
            Color = dark ? new SKColor(0x28, 0x30, 0x3D) : new SKColor(0xB4, 0xC0, 0xCE),
            IsAntialias = true,
        };
        using var axisXPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * dpiScale,
            Color = dark ? new SKColor(0x7D, 0x3B, 0x3F) : new SKColor(0xB8, 0x6A, 0x6E),
            IsAntialias = true,
        };
        using var axisZPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * dpiScale,
            Color = dark ? new SKColor(0x31, 0x51, 0x7A) : new SKColor(0x5E, 0x84, 0xB3),
            IsAntialias = true,
        };

        int maxLines = (int)MathF.Floor(gridHalf / step);
        for (int i = -maxLines; i <= maxLines; i++)
        {
            if (i == 0) continue;
            float coord = i * step;
            bool major = i % majorEvery == 0;
            SKPaint paint = major ? majorPaint : minorPaint;

            if (TryClipLineToNearPlane(frame, new Vector3(coord, -0.3f, -gridHalf), new Vector3(coord, -0.3f, gridHalf), out Vector2 p0, out Vector2 p1))
            {
                canvas.DrawLine(p0.X, p0.Y, p1.X, p1.Y, paint);
            }

            if (TryClipLineToNearPlane(frame, new Vector3(-gridHalf, -0.3f, coord), new Vector3(gridHalf, -0.3f, coord), out Vector2 q0, out Vector2 q1))
            {
                canvas.DrawLine(q0.X, q0.Y, q1.X, q1.Y, paint);
            }
        }

        // Eixos centrais X e Z destacados em tom técnico discreto sobre Y = 0
        if (TryClipLineToNearPlane(frame, new Vector3(-gridHalf, -0.25f, 0f), new Vector3(gridHalf, -0.25f, 0f), out Vector2 x0, out Vector2 x1))
        {
            canvas.DrawLine(x0.X, x0.Y, x1.X, x1.Y, axisXPaint);
        }

        if (TryClipLineToNearPlane(frame, new Vector3(0f, -0.25f, -gridHalf), new Vector3(0f, -0.25f, gridHalf), out Vector2 z0, out Vector2 z1))
        {
            canvas.DrawLine(z0.X, z0.Y, z1.X, z1.Y, axisZPaint);
        }

        // Sombra suave projetada no piso abaixo da caixa
        float sx = (float)scene.WidthMm * 0.56f;
        float sz = (float)scene.DepthMm * 0.56f;
        var floorQuad = new Vector3[]
        {
            new(-sx, -0.15f, -sz),
            new(sx, -0.15f, -sz),
            new(sx, -0.15f, sz),
            new(-sx, -0.15f, sz),
        };
        using var shadowPath = new SKPath();
        bool allValid = true;
        for (int i = 0; i < 4; i++)
        {
            if (!frame.TryProject(floorQuad[i], out Vector2 sp, out _))
            {
                allValid = false;
                break;
            }
            if (i == 0) shadowPath.MoveTo(sp.X, sp.Y);
            else shadowPath.LineTo(sp.X, sp.Y);
        }

        if (allValid)
        {
            shadowPath.Close();
            using var shadowPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = dark ? new SKColor(0x05, 0x06, 0x08, 95) : new SKColor(0x1E, 0x29, 0x3B, 34),
                MaskFilter = FloorShadowBlur,
                IsAntialias = true,
            };
            canvas.DrawPath(shadowPath, shadowPaint);
        }
    }

    private static bool TryClipLineToNearPlane(
        CameraFrame frame,
        Vector3 worldA,
        Vector3 worldB,
        out Vector2 screenA,
        out Vector2 screenB)
    {
        const float nearZ = 8f;
        Vector3 ra = worldA - frame.Eye;
        Vector3 rb = worldB - frame.Eye;
        float za = Vector3.Dot(ra, frame.Forward);
        float zb = Vector3.Dot(rb, frame.Forward);

        if (za < nearZ && zb < nearZ)
        {
            screenA = default;
            screenB = default;
            return false;
        }

        if (za < nearZ)
        {
            float t = (nearZ - za) / (zb - za);
            worldA = Vector3.Lerp(worldA, worldB, t);
        }
        else if (zb < nearZ)
        {
            float t = (nearZ - zb) / (za - zb);
            worldB = Vector3.Lerp(worldB, worldA, t);
        }

        frame.TryProject(worldA, out screenA, out _);
        frame.TryProject(worldB, out screenB, out _);
        return true;
    }

    private readonly record struct OverlayPanelJob(
        OverlayTextureData Texture,
        byte OuterTag,
        ScreenBounds Bounds,
        float I00,
        float I01,
        float I02,
        float I10,
        float I11,
        float I12,
        float I20,
        float I21,
        float I22);

    private static void RenderScene3DPanelsZBuffer(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        BoxScene3D scene,
        CameraFrame frame,
        SurfaceManager surfaceManager,
        RenderScene3DOptions options,
        float dpiScale)
    {
        if (widthPx <= 0 || heightPx <= 0 || scene.Panels.Count == 0)
        {
            return;
        }

        float requestedSs = Math.Clamp(
            float.IsFinite(options.SupersampleScale) ? options.SupersampleScale : 1.0f,
            1.0f,
            2.5f);
        float maxScaleX = 1920f / widthPx;
        float maxScaleY = 1200f / heightPx;
        float clampedSs = Math.Clamp(MathF.Min(requestedSs, MathF.Min(maxScaleX, maxScaleY)), 1.0f, 2.5f);

        int ssW = clampedSs <= 1.01f ? widthPx : Math.Max(widthPx, (int)MathF.Round(widthPx * clampedSs));
        int ssH = clampedSs <= 1.01f ? heightPx : Math.Max(heightPx, (int)MathF.Round(heightPx * clampedSs));
        float effectiveSs = (float)ssW / widthPx;

        CameraFrame ssFrame = effectiveSs <= 1.01f
            ? frame
            : new CameraFrame(
                frame.Eye,
                frame.Target,
                frame.Right,
                frame.Up,
                frame.Forward,
                ssW,
                ssH,
                frame.FocalLengthPx * effectiveSs);

        ZBufferScratchBuffers scratch = t_zBufferScratch ??= new ZBufferScratchBuffers();
        scratch.EnsureCapacity(ssW * ssH);
        scratch.Triangles.Clear();
        scratch.Creases.Clear();
        scratch.OverlayJobs.Clear();

        uint[] colorBuffer = scratch.ColorBuffer;
        float[] zBuffer = scratch.ZBuffer;
        byte[] idBuffer = scratch.IdBuffer;

        int panelCount = scene.Panels.Count;
        var outerBoundsPerPanel = new ScreenBounds[panelCount];
        var panelHighlightedFlags = new bool[panelCount];
        var panelSurfaceActiveFlags = new bool[panelCount];

        var globalBounds = default(ScreenBounds);
        globalBounds.Reset();

        float creaseHalfWidth = 0.525f * dpiScale * effectiveSs;
        int creasePadPx = (int)MathF.Ceiling(creaseHalfWidth + 1.5f);
        float clipMinX = -creasePadPx;
        float clipMaxX = ssW - 1 + creasePadPx;
        float clipMinY = -creasePadPx;
        float clipMaxY = ssH - 1 + creasePadPx;

        for (int p = 0; p < panelCount; p++)
        {
            ExtrudedPanel3D panel = scene.Panels[p];
            outerBoundsPerPanel[p].Reset();

            bool isPartHighlighted = !string.IsNullOrEmpty(options.SelectedPartId) &&
                                     string.Equals(options.SelectedPartId, panel.PartId, StringComparison.OrdinalIgnoreCase);
            bool isSurfaceActive = options.ShowSurfaceGuides &&
                                   options.ActiveSurfaceId == panel.SurfaceId;
            bool isSelected = isPartHighlighted || isSurfaceActive;

            panelHighlightedFlags[p] = isPartHighlighted;
            panelSurfaceActiveFlags[p] = isSurfaceActive;

            int tagBase = (Math.Min(p, 60) + 1) * 4;
            ScreenBounds panelOuterBounds = default;
            panelOuterBounds.Reset();

            foreach (MeshTriangle3D tri in panel.Triangles)
            {
                if (Vector3.Dot(tri.Normal, ssFrame.Eye - tri.A) <= 1e-4f)
                {
                    continue;
                }

                SKColor shaded = ShadePanelColor(tri.Normal, tri.Kind, isPartHighlighted, isSurfaceActive);
                uint bgra = (uint)shaded;
                byte surfaceTag = (byte)(tagBase + (int)tri.Kind);

                ProjectAndAddClippedTriangles(
                    tri.A,
                    tri.B,
                    tri.C,
                    ssFrame,
                    ssW,
                    ssH,
                    bgra,
                    surfaceTag,
                    tri.Kind == PanelMaterialKind.OuterFace,
                    scratch.Triangles,
                    ref globalBounds,
                    ref panelOuterBounds);
            }

            outerBoundsPerPanel[p] = panelOuterBounds;

            SKColor creaseColor = isSelected
                ? new SKColor(0x22, 0xC5, 0x5E, 235)
                : new SKColor(0x38, 0x26, 0x18, 170);
            uint creaseBgra = (uint)creaseColor;

            foreach (EdgeSegment3D edge in panel.CreaseEdges)
            {
                if (!TryClipSegmentToNearPlaneWithInvZ(
                        ssFrame,
                        edge.Start,
                        edge.End,
                        out Vector2 sa,
                        out float invZa,
                        out Vector2 sb,
                        out float invZb))
                {
                    continue;
                }

                if (!TryClipSegmentLiangBarsky(
                        sa.X, sa.Y, invZa,
                        sb.X, sb.Y, invZb,
                        clipMinX, clipMinY, clipMaxX, clipMaxY,
                        out sa, out invZa,
                        out sb, out invZb))
                {
                    continue;
                }

                int segMinX = (int)MathF.Floor(Math.Clamp(MathF.Min(sa.X, sb.X) - creasePadPx, 0f, ssW - 1f));
                int segMaxX = (int)MathF.Ceiling(Math.Clamp(MathF.Max(sa.X, sb.X) + creasePadPx, 0f, ssW - 1f));
                int segMinY = (int)MathF.Floor(Math.Clamp(MathF.Min(sa.Y, sb.Y) - creasePadPx, 0f, ssH - 1f));
                int segMaxY = (int)MathF.Ceiling(Math.Clamp(MathF.Max(sa.Y, sb.Y) + creasePadPx, 0f, ssH - 1f));

                if (segMinX > segMaxX || segMinY > segMaxY)
                {
                    continue;
                }

                globalBounds.Include(segMinX, segMaxX, segMinY, segMaxY);
                scratch.Creases.Add(new RasterCreaseSegment(
                    sa.X, sa.Y, invZa,
                    sb.X, sb.Y, invZb,
                    segMinY, segMaxY,
                    creaseBgra,
                    creaseHalfWidth));
            }
        }

        if (!globalBounds.IsValid)
        {
            return;
        }

        int clearStart = globalBounds.MinY * ssW;
        int clearLength = (globalBounds.MaxY - globalBounds.MinY + 1) * ssW;
        Array.Clear(colorBuffer, clearStart, clearLength);
        Array.Clear(zBuffer, clearStart, clearLength);
        Array.Clear(idBuffer, clearStart, clearLength);

        List<RasterTriangle> triangles = scratch.Triangles;
        int activeRows = globalBounds.MaxY - globalBounds.MinY + 1;
        bool allowParallel = Environment.ProcessorCount >= 2;

        // Pass 1: Ordenação Front-to-Back + Scanline Z-Buffer Paralelo por work-stealing (seguro para STA)
        if (triangles.Count > 0)
        {
            if (triangles.Count > 1)
            {
                triangles.Sort(FrontToBackTriangleComparison);
            }

            RunBandsParallel(
                globalBounds.MinY,
                globalBounds.MaxY + 1,
                16,
                allowParallel && activeRows >= 48 && triangles.Count >= 4,
                (bStart, bEnd) => RasterizeTriangleBand(bStart, bEnd, ssW, triangles, colorBuffer, zBuffer, idBuffer));
        }

        // Pass 2: Projeção perspectiva depth-tested das gravações CNC / guias na OuterFace visível
        IReadOnlyList<PersonalizationElement> allVisibleElements = surfaceManager.ListElements()
            .Where(e => e.Visible)
            .ToArray();

        List<OverlayPanelJob> overlayJobs = scratch.OverlayJobs;
        ScreenBounds ovUnionBounds = default;
        ovUnionBounds.Reset();

        for (int p = 0; p < panelCount; p++)
        {
            if (!outerBoundsPerPanel[p].IsValid)
            {
                continue;
            }

            ExtrudedPanel3D panel = scene.Panels[p];
            if (TryPrepareOverlayPanelJob(
                    panel,
                    p,
                    outerBoundsPerPanel[p],
                    ssFrame,
                    ssW,
                    ssH,
                    surfaceManager,
                    allVisibleElements,
                    options,
                    panelHighlightedFlags[p],
                    panelSurfaceActiveFlags[p],
                    out OverlayPanelJob job))
            {
                overlayJobs.Add(job);
                ovUnionBounds.Include(job.Bounds.MinX, job.Bounds.MaxX, job.Bounds.MinY, job.Bounds.MaxY);
            }
        }

        if (overlayJobs.Count > 0 && ovUnionBounds.IsValid)
        {
            int ovRows = ovUnionBounds.MaxY - ovUnionBounds.MinY + 1;
            RunBandsParallel(
                ovUnionBounds.MinY,
                ovUnionBounds.MaxY + 1,
                16,
                allowParallel && ovRows >= 48,
                (bStart, bEnd) =>
                {
                    for (int j = 0; j < overlayJobs.Count; j++)
                    {
                        OverlayPanelJob job = overlayJobs[j];
                        if (job.Bounds.MaxY < bStart || job.Bounds.MinY >= bEnd)
                        {
                            continue;
                        }

                        CompositeOverlayBand(bStart, bEnd, ssW, in job, colorBuffer, idBuffer);
                    }
                });
        }

        // Pass 3: Arestas CAD / bordas queimadas a laser com anti-aliasing e depth-test read-only
        List<RasterCreaseSegment> creases = scratch.Creases;
        if (creases.Count > 0)
        {
            RunBandsParallel(
                globalBounds.MinY,
                globalBounds.MaxY + 1,
                16,
                allowParallel && activeRows >= 48 && creases.Count >= 32,
                (bStart, bEnd) => RasterizeCreaseBand(bStart, bEnd, ssW, creases, colorBuffer, zBuffer));
        }

        // Pass 4: Compósito SSAA (downsample nítido sobre fundo vetorial 1x)
        int sliceHeight = globalBounds.MaxY - globalBounds.MinY + 1;
        var srcInfo = new SKImageInfo(ssW, sliceHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        GCHandle handle = GCHandle.Alloc(colorBuffer, GCHandleType.Pinned);
        SKImage? foregroundImg = null;
        try
        {
            IntPtr slicePtr = handle.AddrOfPinnedObject() + globalBounds.MinY * ssW * sizeof(uint);
            foregroundImg = SKImage.FromPixels(srcInfo, slicePtr, ssW * sizeof(uint));
            if (foregroundImg is not null)
            {
                var srcRect = new SKRect(globalBounds.MinX, 0f, globalBounds.MaxX + 1f, sliceHeight);
                float invSs = 1.0f / effectiveSs;
                var dstRect = new SKRect(
                    globalBounds.MinX * invSs,
                    globalBounds.MinY * invSs,
                    (globalBounds.MaxX + 1f) * invSs,
                    (globalBounds.MaxY + 1f) * invSs);

                SKSamplingOptions sampling = effectiveSs > 1.01f
                    ? DownsampleSampling
                    : new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
                canvas.DrawImage(foregroundImg, srcRect, dstRect, sampling);
                canvas.Flush();
            }
        }
        finally
        {
            foregroundImg?.Dispose();
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    private static void ProjectAndAddClippedTriangles(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        CameraFrame frame,
        int viewportW,
        int viewportH,
        uint bgraColor,
        byte surfaceTag,
        bool trackOuterBounds,
        List<RasterTriangle> output,
        ref ScreenBounds globalBounds,
        ref ScreenBounds panelOuterBounds)
    {
        const float NearZ = 2.0f;
        float za = Vector3.Dot(a - frame.Eye, frame.Forward);
        float zb = Vector3.Dot(b - frame.Eye, frame.Forward);
        float zc = Vector3.Dot(c - frame.Eye, frame.Forward);

        if (za > NearZ && zb > NearZ && zc > NearZ)
        {
            if (frame.TryProject(a, out Vector2 pa, out _) &&
                frame.TryProject(b, out Vector2 pb, out _) &&
                frame.TryProject(c, out Vector2 pc, out _))
            {
                TryAddRasterTriangle(
                    pa, 1f / za,
                    pb, 1f / zb,
                    pc, 1f / zc,
                    viewportW,
                    viewportH,
                    bgraColor,
                    surfaceTag,
                    trackOuterBounds,
                    output,
                    ref globalBounds,
                    ref panelOuterBounds);
            }
            return;
        }

        if (za <= NearZ && zb <= NearZ && zc <= NearZ)
        {
            return;
        }

        Span<Vector3> clipped = stackalloc Vector3[4];
        int clippedCount = 0;

        ReadOnlySpan<Vector3> inputVerts = [a, b, c];
        ReadOnlySpan<float> inputZ = [za, zb, zc];

        for (int i = 0; i < 3; i++)
        {
            int next = (i + 1) % 3;
            Vector3 v0 = inputVerts[i];
            Vector3 v1 = inputVerts[next];
            float z0 = inputZ[i];
            float z1 = inputZ[next];
            bool in0 = z0 > NearZ;
            bool in1 = z1 > NearZ;

            if (in0 && in1)
            {
                clipped[clippedCount++] = v1;
            }
            else if (in0 && !in1)
            {
                float t = (NearZ - z0) / (z1 - z0);
                clipped[clippedCount++] = Vector3.Lerp(v0, v1, t);
            }
            else if (!in0 && in1)
            {
                float t = (NearZ - z0) / (z1 - z0);
                clipped[clippedCount++] = Vector3.Lerp(v0, v1, t);
                clipped[clippedCount++] = v1;
            }
        }

        for (int i = 1; i + 1 < clippedCount; i++)
        {
            if (frame.TryProject(clipped[0], out Vector2 pa, out float pza) &&
                frame.TryProject(clipped[i], out Vector2 pb, out float pzb) &&
                frame.TryProject(clipped[i + 1], out Vector2 pc, out float pzc))
            {
                TryAddRasterTriangle(
                    pa, 1f / MathF.Max(NearZ, pza),
                    pb, 1f / MathF.Max(NearZ, pzb),
                    pc, 1f / MathF.Max(NearZ, pzc),
                    viewportW,
                    viewportH,
                    bgraColor,
                    surfaceTag,
                    trackOuterBounds,
                    output,
                    ref globalBounds,
                    ref panelOuterBounds);
            }
        }
    }

    private static void TryAddRasterTriangle(
        Vector2 pa,
        float invZa,
        Vector2 pb,
        float invZb,
        Vector2 pc,
        float invZc,
        int viewportW,
        int viewportH,
        uint bgraColor,
        byte surfaceTag,
        bool trackOuterBounds,
        List<RasterTriangle> output,
        ref ScreenBounds globalBounds,
        ref ScreenBounds panelOuterBounds)
    {
        // Backface e degenerado 2D estrito (faces frontais em base dextrógira têm area > 0 na tela +Y para baixo)
        float area = EdgeCross(pa.X, pa.Y, pb.X, pb.Y, pc.X, pc.Y);
        if (area <= 1e-4f || !float.IsFinite(area))
        {
            return;
        }

        float minXf = MathF.Min(pa.X, MathF.Min(pb.X, pc.X));
        float maxXf = MathF.Max(pa.X, MathF.Max(pb.X, pc.X));
        float minYf = MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y));
        float maxYf = MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y));

        if (!float.IsFinite(minXf) || !float.IsFinite(maxXf) ||
            !float.IsFinite(minYf) || !float.IsFinite(maxYf))
        {
            return;
        }

        if (maxXf < 0f || minXf >= viewportW || maxYf < 0f || minYf >= viewportH)
        {
            return;
        }

        int minX = (int)MathF.Floor(Math.Clamp(minXf, 0f, viewportW - 1f));
        int maxX = (int)MathF.Ceiling(Math.Clamp(maxXf, 0f, viewportW - 1f));
        int minY = (int)MathF.Floor(Math.Clamp(minYf, 0f, viewportH - 1f));
        int maxY = (int)MathF.Ceiling(Math.Clamp(maxYf, 0f, viewportH - 1f));
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        globalBounds.Include(minX, maxX, minY, maxY);
        if (trackOuterBounds)
        {
            panelOuterBounds.Include(minX, maxX, minY, maxY);
        }

        float invArea = 1.0f / area;
        float tol = -1e-4f * area;
        float dw0dx = pc.Y - pb.Y;
        float dw1dx = pa.Y - pc.Y;
        float dw2dx = pb.Y - pa.Y;
        float dzDx = (dw0dx * invZa + dw1dx * invZb + dw2dx * invZc) * invArea;
        float sortInvZ = MathF.Max(invZa, MathF.Max(invZb, invZc));

        output.Add(new RasterTriangle(
            pa.X, pa.Y, invZa,
            pb.X, pb.Y, invZb,
            pc.X, pc.Y, invZc,
            invArea,
            tol,
            dw0dx,
            dw1dx,
            dw2dx,
            dzDx,
            sortInvZ,
            minX, maxX, minY, maxY,
            bgraColor,
            surfaceTag));
    }

    private static void RasterizeTriangleBand(
        int yStart,
        int yEnd,
        int ssW,
        List<RasterTriangle> triangles,
        uint[] colorBuffer,
        float[] zBuffer,
        byte[] idBuffer)
    {
        int triCount = triangles.Count;
        for (int i = 0; i < triCount; i++)
        {
            RasterTriangle tri = triangles[i];
            if (tri.MaxY < yStart || tri.MinY >= yEnd)
            {
                continue;
            }

            int rowStart = Math.Max(yStart, tri.MinY);
            int rowEnd = Math.Min(yEnd - 1, tri.MaxY);
            int spanMaxDx = tri.MaxX - tri.MinX;
            float pxMin = tri.MinX + 0.5f;
            float tol = tri.Tol;
            float dw0dx = tri.Dw0dx;
            float dw1dx = tri.Dw1dx;
            float dw2dx = tri.Dw2dx;
            float dzDx = tri.DzDx;
            float invArea = tri.InvArea;
            uint bgra = tri.BgraColor;
            byte tag = tri.SurfaceTag;

            for (int y = rowStart; y <= rowEnd; y++)
            {
                float py = y + 0.5f;
                float w0Min = EdgeCross(tri.Bx, tri.By, tri.Cx, tri.Cy, pxMin, py);
                float w1Min = EdgeCross(tri.Cx, tri.Cy, tri.Ax, tri.Ay, pxMin, py);
                float w2Min = EdgeCross(tri.Ax, tri.Ay, tri.Bx, tri.By, pxMin, py);

                int xLeft = tri.MinX;
                int xRight = tri.MaxX;

                if (!NarrowEdgeSpan(w0Min, dw0dx, tol, tri.MinX, spanMaxDx, ref xLeft, ref xRight) ||
                    !NarrowEdgeSpan(w1Min, dw1dx, tol, tri.MinX, spanMaxDx, ref xLeft, ref xRight) ||
                    !NarrowEdgeSpan(w2Min, dw2dx, tol, tri.MinX, spanMaxDx, ref xLeft, ref xRight))
                {
                    continue;
                }

                if (xLeft > xRight)
                {
                    continue;
                }

                int dxStart = xLeft - tri.MinX;
                float w0 = w0Min + dxStart * dw0dx;
                float w1 = w1Min + dxStart * dw1dx;
                float w2 = w2Min + dxStart * dw2dx;

                while (xLeft <= xRight && (w0 < tol || w1 < tol || w2 < tol))
                {
                    xLeft++;
                    w0 += dw0dx;
                    w1 += dw1dx;
                    w2 += dw2dx;
                }

                if (xLeft > xRight)
                {
                    continue;
                }

                int dxEnd = xRight - tri.MinX;
                float w0End = w0Min + dxEnd * dw0dx;
                float w1End = w1Min + dxEnd * dw1dx;
                float w2End = w2Min + dxEnd * dw2dx;

                while (xRight > xLeft && (w0End < tol || w1End < tol || w2End < tol))
                {
                    xRight--;
                    w0End -= dw0dx;
                    w1End -= dw1dx;
                    w2End -= dw2dx;
                }

                int count = xRight - xLeft + 1;
                float invZ = (w0 * tri.InvZa + w1 * tri.InvZb + w2 * tri.InvZc) * invArea;
                int startIdx = y * ssW + xLeft;

                Span<float> zSpan = zBuffer.AsSpan(startIdx, count);
                Span<uint> cSpan = colorBuffer.AsSpan(startIdx, count);
                Span<byte> idSpan = idBuffer.AsSpan(startIdx, count);

                for (int k = 0; k < zSpan.Length; k++)
                {
                    if (invZ > zSpan[k])
                    {
                        zSpan[k] = invZ;
                        cSpan[k] = bgra;
                        idSpan[k] = tag;
                    }

                    invZ += dzDx;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NarrowEdgeSpan(
        float wAtMinX,
        float dwDx,
        float tol,
        int triMinX,
        int spanMaxDx,
        ref int xLeft,
        ref int xRight)
    {
        if (dwDx > 1e-9f)
        {
            float kMin = (tol - wAtMinX) / dwDx;
            if (!float.IsFinite(kMin) || kMin > spanMaxDx + 1f)
            {
                return false;
            }

            if (kMin > 0f)
            {
                int c = triMinX + (int)MathF.Ceiling(kMin - 1e-4f);
                if (c > xLeft)
                {
                    xLeft = c;
                }
            }
        }
        else if (dwDx < -1e-9f)
        {
            float kMax = (tol - wAtMinX) / dwDx;
            if (!float.IsFinite(kMax) || kMax < -1f)
            {
                return false;
            }

            if (kMax < spanMaxDx)
            {
                int c = triMinX + (int)MathF.Floor(kMax + 1e-4f);
                if (c < xRight)
                {
                    xRight = c;
                }
            }
        }
        else
        {
            float slack = MathF.Max(2e-3f, MathF.Abs(tol) * 1e-5f);
            if (wAtMinX < tol - slack)
            {
                return false;
            }
        }

        return true;
    }

    private static void RunBandsParallel(
        int minY,
        int maxYExclusive,
        int bandHeight,
        bool allowParallel,
        Action<int, int> bandAction)
    {
        int totalRows = maxYExclusive - minY;
        if (totalRows <= 0)
        {
            return;
        }

        if (!allowParallel || totalRows < 48 || Environment.ProcessorCount < 2)
        {
            bandAction(minY, maxYExclusive);
            return;
        }

        int bandCount = (totalRows + bandHeight - 1) / bandHeight;
        if (bandCount <= 1)
        {
            bandAction(minY, maxYExclusive);
            return;
        }

        int workerCount = Math.Min(Environment.ProcessorCount - 1, bandCount - 1);
        int nextBand = -1;
        int remainingWorkers = workerCount;
        Exception? firstError = null;

        void ProcessLoop()
        {
            try
            {
                while (true)
                {
                    int b = Interlocked.Increment(ref nextBand);
                    if (b >= bandCount)
                    {
                        break;
                    }

                    int bStart = minY + b * bandHeight;
                    int bEnd = Math.Min(maxYExclusive, bStart + bandHeight);
                    bandAction(bStart, bEnd);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref firstError, ex, null);
            }
        }

        for (int w = 0; w < workerCount; w++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    ProcessLoop();
                }
                finally
                {
                    Interlocked.Decrement(ref remainingWorkers);
                }
            }, null);
        }

        ProcessLoop();

        var spin = new SpinWait();
        while (Volatile.Read(ref remainingWorkers) > 0)
        {
            spin.SpinOnce(-1);
        }

        if (firstError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EdgeCross(float ax, float ay, float bx, float by, float px, float py)
        => (px - ax) * (by - ay) - (py - ay) * (bx - ax);

    private static bool TryPrepareOverlayPanelJob(
        ExtrudedPanel3D panel,
        int panelIndex,
        ScreenBounds outerBounds,
        CameraFrame ssFrame,
        int ssW,
        int ssH,
        SurfaceManager surfaceManager,
        IReadOnlyList<PersonalizationElement> allVisibleElements,
        RenderScene3DOptions options,
        bool isPartHighlighted,
        bool isSurfaceActive,
        out OverlayPanelJob job)
    {
        job = default;
        SurfaceOverlayQuad3D? quad = panel.OverlayQuad;
        if (quad is null)
        {
            return false;
        }

        if (!surfaceManager.Surfaces.TryGetSurface(panel.SurfaceId, out SurfaceDefinition? surfDef) || surfDef is null)
        {
            return false;
        }

        PersonalizationElement[] panelElements = allVisibleElements
            .Where(e => e.PanelId == panel.SurfaceId)
            .ToArray();

        bool showGuidesOrHighlight = (options.ShowSurfaceGuides && isSurfaceActive) || isPartHighlighted;
        bool hasVisualContent = panelElements.Length > 0 || showGuidesOrHighlight;
        if (!hasVisualContent)
        {
            return false;
        }

        ScreenBounds jobBounds = outerBounds;
        if (!showGuidesOrHighlight && panelElements.Length > 0)
        {
            float elMinX = float.PositiveInfinity;
            float elMaxX = float.NegativeInfinity;
            float elMinY = float.PositiveInfinity;
            float elMaxY = float.NegativeInfinity;
            bool fallbackToOuter = false;

            IReadOnlyList<ProjectedElement3D> projectedList = quad.ProjectedElements;
            for (int e = 0; e < panelElements.Length; e++)
            {
                string elId = panelElements[e].Id;
                ProjectedElement3D? match = null;
                for (int k = 0; k < projectedList.Count; k++)
                {
                    if (string.Equals(projectedList[k].ElementId, elId, StringComparison.Ordinal))
                    {
                        match = projectedList[k];
                        break;
                    }
                }

                if (match is null || match.Corners3D.Count == 0)
                {
                    fallbackToOuter = true;
                    break;
                }

                for (int c = 0; c < match.Corners3D.Count; c++)
                {
                    if (!ssFrame.TryProject(match.Corners3D[c], out Vector2 sc, out _) ||
                        !float.IsFinite(sc.X) ||
                        !float.IsFinite(sc.Y))
                    {
                        fallbackToOuter = true;
                        break;
                    }

                    if (sc.X < elMinX) elMinX = sc.X;
                    if (sc.X > elMaxX) elMaxX = sc.X;
                    if (sc.Y < elMinY) elMinY = sc.Y;
                    if (sc.Y > elMaxY) elMaxY = sc.Y;
                }

                if (fallbackToOuter)
                {
                    break;
                }
            }

            if (!fallbackToOuter && elMinX <= elMaxX && elMinY <= elMaxY)
            {
                int padPx = Math.Clamp((int)MathF.Ceiling(8f * (ssW / 640f)), 8, 24);
                elMinX -= padPx;
                elMaxX += padPx;
                elMinY -= padPx;
                elMaxY += padPx;

                if (elMaxX < outerBounds.MinX || elMinX > outerBounds.MaxX ||
                    elMaxY < outerBounds.MinY || elMinY > outerBounds.MaxY)
                {
                    return false;
                }

                int tMinX = Math.Max(outerBounds.MinX, (int)MathF.Floor(Math.Clamp(elMinX, 0f, ssW - 1f)));
                int tMaxX = Math.Min(outerBounds.MaxX, (int)MathF.Ceiling(Math.Clamp(elMaxX, 0f, ssW - 1f)));
                int tMinY = Math.Max(outerBounds.MinY, (int)MathF.Floor(Math.Clamp(elMinY, 0f, ssH - 1f)));
                int tMaxY = Math.Min(outerBounds.MaxY, (int)MathF.Ceiling(Math.Clamp(elMaxY, 0f, ssH - 1f)));
                if (tMinX > tMaxX || tMinY > tMaxY)
                {
                    return false;
                }

                jobBounds = new ScreenBounds
                {
                    MinX = tMinX,
                    MaxX = tMaxX,
                    MinY = tMinY,
                    MaxY = tMaxY,
                };
            }
        }

        // Homografia analítica direta do plano 3D para tela (robusta mesmo quando cantos saem atrás da câmera em zoom 5x)
        Vector3 planeOffset = quad.Normal * 0.12f;
        OverlayTextureData texData = GetOrCreateSurfaceOverlayTexture(
            surfDef,
            panelElements,
            options,
            isSurfaceActive,
            isPartHighlighted);

        if (!TryComputePlaneScreenToTex(
                quad,
                planeOffset,
                texData.Width,
                texData.Height,
                ssFrame,
                out float i00, out float i01, out float i02,
                out float i10, out float i11, out float i12,
                out float i20, out float i21, out float i22))
        {
            return false;
        }

        byte outerTag = (byte)((Math.Min(panelIndex, 60) + 1) * 4 + (int)PanelMaterialKind.OuterFace);
        job = new OverlayPanelJob(
            texData,
            outerTag,
            jobBounds,
            i00, i01, i02,
            i10, i11, i12,
            i20, i21, i22);
        return true;
    }

    private static void CompositeOverlayBand(
        int yStart,
        int yEnd,
        int ssW,
        in OverlayPanelJob job,
        uint[] colorBuffer,
        byte[] idBuffer)
    {
        int rowStart = Math.Max(yStart, job.Bounds.MinY);
        int rowEnd = Math.Min(yEnd - 1, job.Bounds.MaxY);
        int colStart = job.Bounds.MinX;
        int colEnd = job.Bounds.MaxX;

        int texW = job.Texture.Width;
        int texH = job.Texture.Height;
        uint[] texPixels = job.Texture.PremulBgraPixels;
        byte outerTag = job.OuterTag;

        for (int y = rowStart; y <= rowEnd; y++)
        {
            float py = y + 0.5f;
            int rowOffset = y * ssW;

            float uNum = job.I00 * (colStart + 0.5f) + job.I01 * py + job.I02;
            float vNum = job.I10 * (colStart + 0.5f) + job.I11 * py + job.I12;
            float wDen = job.I20 * (colStart + 0.5f) + job.I21 * py + job.I22;

            for (int x = colStart; x <= colEnd; x++)
            {
                int idx = rowOffset + x;
                if (idBuffer[idx] == outerTag && MathF.Abs(wDen) > 1e-9f)
                {
                    float invW = 1.0f / wDen;
                    float u = uNum * invW;
                    float v = vNum * invW;

                    if (u >= 0f && u < texW && v >= 0f && v < texH)
                    {
                        float fu = Math.Clamp(u - 0.5f, 0f, texW - 1.001f);
                        float fv = Math.Clamp(v - 0.5f, 0f, texH - 1.001f);
                        int fx = (int)(fu * 256f + 0.5f);
                        int fy = (int)(fv * 256f + 0.5f);

                        int x0 = fx >> 8;
                        int y0 = fy >> 8;
                        int x1 = Math.Min(x0 + 1, texW - 1);
                        int y1 = Math.Min(y0 + 1, texH - 1);

                        uint c00 = texPixels[y0 * texW + x0];
                        uint c10 = texPixels[y0 * texW + x1];
                        uint c01 = texPixels[y1 * texW + x0];
                        uint c11 = texPixels[y1 * texW + x1];

                        if ((c00 | c10 | c01 | c11) != 0u)
                        {
                            uint wx = (uint)(fx & 0xFF);
                            uint wy = (uint)(fy & 0xFF);
                            uint iwx = 256u - wx;
                            uint iwy = 256u - wy;

                            uint w00 = iwx * iwy;
                            uint w10 = wx * iwy;
                            uint w01 = iwx * wy;
                            uint w11 = wx * wy;

                            uint srcB = ((c00 & 0xFFu) * w00 + (c10 & 0xFFu) * w10 + (c01 & 0xFFu) * w01 + (c11 & 0xFFu) * w11 + 32768u) >> 16;
                            uint srcG = (((c00 >> 8) & 0xFFu) * w00 + ((c10 >> 8) & 0xFFu) * w10 + ((c01 >> 8) & 0xFFu) * w01 + ((c11 >> 8) & 0xFFu) * w11 + 32768u) >> 16;
                            uint srcR = (((c00 >> 16) & 0xFFu) * w00 + ((c10 >> 16) & 0xFFu) * w10 + ((c01 >> 16) & 0xFFu) * w01 + ((c11 >> 16) & 0xFFu) * w11 + 32768u) >> 16;
                            uint srcA = ((c00 >> 24) * w00 + (c10 >> 24) * w10 + (c01 >> 24) * w01 + (c11 >> 24) * w11 + 32768u) >> 16;

                            if (srcA > 0u)
                            {
                                if (srcA >= 255u)
                                {
                                    colorBuffer[idx] = 0xFF000000u | (Math.Min(255u, srcR) << 16) | (Math.Min(255u, srcG) << 8) | Math.Min(255u, srcB);
                                }
                                else
                                {
                                    uint dst = colorBuffer[idx];
                                    uint invA = 255u - srcA;
                                    uint dstB = dst & 0xFFu;
                                    uint dstG = (dst >> 8) & 0xFFu;
                                    uint dstR = (dst >> 16) & 0xFFu;

                                    uint outB = srcB + ((dstB * invA + 127u) / 255u);
                                    uint outG = srcG + ((dstG * invA + 127u) / 255u);
                                    uint outR = srcR + ((dstR * invA + 127u) / 255u);
                                    colorBuffer[idx] = 0xFF000000u
                                        | (Math.Min(255u, outR) << 16)
                                        | (Math.Min(255u, outG) << 8)
                                        | Math.Min(255u, outB);
                                }
                            }
                        }
                    }
                }

                uNum += job.I00;
                vNum += job.I10;
                wDen += job.I20;
            }
        }
    }

    private static bool TryClipSegmentToNearPlaneWithInvZ(
        CameraFrame frame,
        Vector3 worldA,
        Vector3 worldB,
        out Vector2 screenA,
        out float invZa,
        out Vector2 screenB,
        out float invZb)
    {
        const float NearZ = 2.0f;
        float za = Vector3.Dot(worldA - frame.Eye, frame.Forward);
        float zb = Vector3.Dot(worldB - frame.Eye, frame.Forward);

        if (za <= NearZ && zb <= NearZ)
        {
            screenA = default;
            screenB = default;
            invZa = 0f;
            invZb = 0f;
            return false;
        }

        if (za <= NearZ)
        {
            float t = (NearZ - za) / (zb - za);
            worldA = Vector3.Lerp(worldA, worldB, t);
            za = NearZ;
        }
        else if (zb <= NearZ)
        {
            float t = (NearZ - zb) / (za - zb);
            worldB = Vector3.Lerp(worldB, worldA, t);
            zb = NearZ;
        }

        invZa = 1.0f / za;
        invZb = 1.0f / zb;
        screenA = default;
        screenB = default;
        return frame.TryProject(worldA, out screenA, out _) &&
               frame.TryProject(worldB, out screenB, out _);
    }

    private static bool TryClipSegmentLiangBarsky(
        float x0,
        float y0,
        float invZ0,
        float x1,
        float y1,
        float invZ1,
        float minX,
        float minY,
        float maxX,
        float maxY,
        out Vector2 outA,
        out float outInvZa,
        out Vector2 outB,
        out float outInvZb)
    {
        if (!float.IsFinite(x0) || !float.IsFinite(y0) || !float.IsFinite(invZ0) ||
            !float.IsFinite(x1) || !float.IsFinite(y1) || !float.IsFinite(invZ1))
        {
            outA = default;
            outB = default;
            outInvZa = 0f;
            outInvZb = 0f;
            return false;
        }

        float dx = x1 - x0;
        float dy = y1 - y0;
        float dz = invZ1 - invZ0;
        float t0 = 0f;
        float t1 = 1f;

        if (!ClipTest(-dx, x0 - minX, ref t0, ref t1) ||
            !ClipTest(dx, maxX - x0, ref t0, ref t1) ||
            !ClipTest(-dy, y0 - minY, ref t0, ref t1) ||
            !ClipTest(dy, maxY - y0, ref t0, ref t1))
        {
            outA = default;
            outB = default;
            outInvZa = 0f;
            outInvZb = 0f;
            return false;
        }

        outA = new Vector2(x0 + t0 * dx, y0 + t0 * dy);
        outInvZa = invZ0 + t0 * dz;
        outB = new Vector2(x0 + t1 * dx, y0 + t1 * dy);
        outInvZb = invZ0 + t1 * dz;
        return float.IsFinite(outA.X) && float.IsFinite(outA.Y) &&
               float.IsFinite(outB.X) && float.IsFinite(outB.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool ClipTest(float p, float q, ref float t0, ref float t1)
        {
            if (MathF.Abs(p) <= 1e-12f)
            {
                return q >= 0f;
            }

            float r = q / p;
            if (p < 0f)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }

            return true;
        }
    }

    private static void RasterizeCreaseBand(
        int yStart,
        int yEnd,
        int ssW,
        List<RasterCreaseSegment> creases,
        uint[] colorBuffer,
        float[] zBuffer)
    {
        int count = creases.Count;
        for (int i = 0; i < count; i++)
        {
            RasterCreaseSegment seg = creases[i];
            if (seg.MaxY < yStart || seg.MinY >= yEnd)
            {
                continue;
            }

            float dx = seg.Bx - seg.Ax;
            float dy = seg.By - seg.Ay;
            float lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-6f)
            {
                continue;
            }

            float reach = seg.HalfWidthPx + 0.5f;
            float reachSq = reach * reach;
            float invLenSq = 1.0f / lenSq;

            uint baseA = seg.BaseBgra >> 24;
            uint baseR = (seg.BaseBgra >> 16) & 0xFFu;
            uint baseG = (seg.BaseBgra >> 8) & 0xFFu;
            uint baseB = seg.BaseBgra & 0xFFu;

            if (MathF.Abs(dy) >= MathF.Abs(dx))
            {
                int rowStart = Math.Max(yStart, seg.MinY);
                int rowEnd = Math.Min(yEnd - 1, seg.MaxY);
                float slopeX = dx / dy;
                int extraReach = (int)MathF.Ceiling(reach + MathF.Abs(slopeX) * 0.5f) + 1;

                for (int y = rowStart; y <= rowEnd; y++)
                {
                    float py = y + 0.5f;
                    float tRow = Math.Clamp((py - seg.Ay) / dy, 0f, 1f);
                    float cx = seg.Ax + tRow * dx;
                    int xMin = Math.Max(0, (int)MathF.Floor(cx) - extraReach);
                    int xMax = Math.Min(ssW - 1, (int)MathF.Ceiling(cx) + extraReach);
                    int rowOffset = y * ssW;

                    for (int x = xMin; x <= xMax; x++)
                    {
                        float px = x + 0.5f;
                        float tSeg = Math.Clamp(((px - seg.Ax) * dx + (py - seg.Ay) * dy) * invLenSq, 0f, 1f);
                        float qx = seg.Ax + tSeg * dx - px;
                        float qy = seg.Ay + tSeg * dy - py;
                        float distSq = qx * qx + qy * qy;
                        if (distSq >= reachSq)
                        {
                            continue;
                        }

                        int idx = rowOffset + x;
                        float invZ = seg.InvZa + (seg.InvZb - seg.InvZa) * tSeg;
                        float biasedInvZ = invZ / MathF.Max(0.1f, 1f - 0.35f * invZ);
                        if (biasedInvZ < zBuffer[idx])
                        {
                            continue;
                        }

                        float cov = Math.Clamp(reach - MathF.Sqrt(distSq), 0f, 1f);
                        BlendPremulPixelOver(ref colorBuffer[idx], baseR, baseG, baseB, baseA, cov);
                    }
                }
            }
            else
            {
                int xMin = Math.Max(0, (int)MathF.Floor(MathF.Min(seg.Ax, seg.Bx) - reach));
                int xMax = Math.Min(ssW - 1, (int)MathF.Ceiling(MathF.Max(seg.Ax, seg.Bx) + reach));
                float slopeY = dy / dx;
                int extraReach = (int)MathF.Ceiling(reach + MathF.Abs(slopeY) * 0.5f) + 1;

                for (int x = xMin; x <= xMax; x++)
                {
                    float px = x + 0.5f;
                    float tCol = Math.Clamp((px - seg.Ax) / dx, 0f, 1f);
                    float cy = seg.Ay + tCol * dy;
                    int rowStart = Math.Max(yStart, (int)MathF.Floor(cy) - extraReach);
                    int rowEnd = Math.Min(yEnd - 1, (int)MathF.Ceiling(cy) + extraReach);

                    for (int y = rowStart; y <= rowEnd; y++)
                    {
                        float py = y + 0.5f;
                        float tSeg = Math.Clamp(((px - seg.Ax) * dx + (py - seg.Ay) * dy) * invLenSq, 0f, 1f);
                        float qx = seg.Ax + tSeg * dx - px;
                        float qy = seg.Ay + tSeg * dy - py;
                        float distSq = qx * qx + qy * qy;
                        if (distSq >= reachSq)
                        {
                            continue;
                        }

                        int idx = y * ssW + x;
                        float invZ = seg.InvZa + (seg.InvZb - seg.InvZa) * tSeg;
                        float biasedInvZ = invZ / MathF.Max(0.1f, 1f - 0.35f * invZ);
                        if (biasedInvZ < zBuffer[idx])
                        {
                            continue;
                        }

                        float cov = Math.Clamp(reach - MathF.Sqrt(distSq), 0f, 1f);
                        BlendPremulPixelOver(ref colorBuffer[idx], baseR, baseG, baseB, baseA, cov);
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BlendPremulPixelOver(
        ref uint dstPixel,
        uint srcBaseR,
        uint srcBaseG,
        uint srcBaseB,
        uint srcBaseA,
        float coverage)
    {
        uint srcA = (uint)Math.Clamp((int)(srcBaseA * coverage + 0.5f), 0, 255);
        if (srcA == 0u)
        {
            return;
        }

        uint srcB = (srcBaseB * srcA + 127u) / 255u;
        uint srcG = (srcBaseG * srcA + 127u) / 255u;
        uint srcR = (srcBaseR * srcA + 127u) / 255u;

        uint dst = dstPixel;
        uint dstA = dst >> 24;
        if (dstA == 0u)
        {
            dstPixel = (srcA << 24) | (srcR << 16) | (srcG << 8) | srcB;
            return;
        }

        uint invA = 255u - srcA;
        uint dstB = dst & 0xFFu;
        uint dstG = (dst >> 8) & 0xFFu;
        uint dstR = (dst >> 16) & 0xFFu;

        uint outA = srcA + ((dstA * invA + 127u) / 255u);
        uint outB = srcB + ((dstB * invA + 127u) / 255u);
        uint outG = srcG + ((dstG * invA + 127u) / 255u);
        uint outR = srcR + ((dstR * invA + 127u) / 255u);

        dstPixel = (Math.Min(255u, outA) << 24)
            | (Math.Min(255u, outR) << 16)
            | (Math.Min(255u, outG) << 8)
            | Math.Min(255u, outB);
    }

    private static SKColor ShadePanelColor(
        Vector3 normal,
        PanelMaterialKind kind,
        bool isPartHighlighted,
        bool isSurfaceActive)
    {
        float ndl1 = MathF.Max(0f, Vector3.Dot(normal, KeyLightDir));
        float ndl2 = MathF.Max(0f, Vector3.Dot(normal, FillLightDir));
        float light = 0.44f + ndl1 * 0.48f + ndl2 * 0.22f;

        float baseR, baseG, baseB;
        if (kind == PanelMaterialKind.CutEdge)
        {
            // Borda queimada a laser (Finger Joints, orelhas e furos internos)
            baseR = 0x4A;
            baseG = 0x35;
            baseB = 0x25;
            light = 0.56f + ndl1 * 0.36f + ndl2 * 0.16f;
        }
        else if (isPartHighlighted)
        {
            baseR = 0xA5;
            baseG = 0xD6;
            baseB = 0xA7;
        }
        else if (isSurfaceActive)
        {
            baseR = 0xE3;
            baseG = 0xC9;
            baseB = 0xA3;
        }
        else if (kind == PanelMaterialKind.InnerFace)
        {
            baseR = 0xC6;
            baseG = 0xA0;
            baseB = 0x76;
            light *= 0.90f;
        }
        else
        {
            baseR = 0xD4;
            baseG = 0xB4;
            baseB = 0x8C;
        }

        byte r = (byte)Math.Clamp((int)MathF.Round(baseR * light), 0, 255);
        byte g = (byte)Math.Clamp((int)MathF.Round(baseG * light), 0, 255);
        byte b = (byte)Math.Clamp((int)MathF.Round(baseB * light), 0, 255);
        return new SKColor(r, g, b, 255);
    }

    private static OverlayTextureData GetOrCreateSurfaceOverlayTexture(
        SurfaceDefinition surface,
        IReadOnlyList<PersonalizationElement> panelElements,
        RenderScene3DOptions options,
        bool isSurfaceActive,
        bool isPartHighlighted)
    {
        string key = BuildOverlayCacheKey(surface, panelElements, options, isSurfaceActive, isPartHighlighted);
        lock (OverlayCacheLock)
        {
            if (SurfaceOverlayImageCache.TryGetValue(key, out LinkedListNode<(string Key, OverlayTextureData Value)>? cachedNode))
            {
                SurfaceOverlayLru.Remove(cachedNode);
                SurfaceOverlayLru.AddFirst(cachedNode);
                return cachedNode.Value.Value;
            }
        }

        double longest = Math.Max(surface.WidthMm, surface.HeightMm);
        double pxPerMm = Math.Clamp(MaxOverlayTexDim / Math.Max(1.0, longest), 1.5, 4.5);

        int texW = Math.Clamp((int)Math.Ceiling(surface.WidthMm * pxPerMm), 64, MaxOverlayTexDim);
        int texH = Math.Clamp((int)Math.Ceiling(surface.HeightMm * pxPerMm), 64, MaxOverlayTexDim);

        var info = new SKImageInfo(texW, texH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);

            float sx = (float)(texW / Math.Max(0.1, surface.WidthMm));
            float sy = (float)(texH / Math.Max(0.1, surface.HeightMm));

            canvas.Save();
            canvas.Scale(sx, sy);

            if ((options.ShowSurfaceGuides && isSurfaceActive) || isPartHighlighted)
            {
                Rect2D safe = surface.SafeAreaForMargin(options.SafetyMarginMm);
                using var safePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(0x2E, 0x7D, 0x32, 220),
                    StrokeWidth = 0.7f,
                    PathEffect = SKPathEffect.CreateDash([2.4f, 1.6f], 0f),
                    IsAntialias = true,
                };
                canvas.DrawRect((float)safe.XMm, (float)safe.YMm, (float)safe.WidthMm, (float)safe.HeightMm, safePaint);

                using var outerBorder = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(0x22, 0xC5, 0x5E, 235),
                    StrokeWidth = 1.1f,
                    IsAntialias = true,
                };
                canvas.DrawRect(0.55f, 0.55f, (float)surface.WidthMm - 1.1f, (float)surface.HeightMm - 1.1f, outerBorder);
            }

            DrawSurfaceElements(
                canvas,
                panelElements,
                woodRealisticColors: true,
                options.ErrorElementIds,
                options.SelectedElementId,
                showSelectionOutlines: options.ShowSurfaceGuides,
                strokeScaleAdjust: 0.75f);

            canvas.Restore();
        }

        uint[] premulPixels = MemoryMarshal.Cast<byte, uint>(bmp.GetPixelSpan()).ToArray();
        var created = new OverlayTextureData(texW, texH, premulPixels);
        lock (OverlayCacheLock)
        {
            if (SurfaceOverlayImageCache.TryGetValue(key, out LinkedListNode<(string Key, OverlayTextureData Value)>? existingNode))
            {
                SurfaceOverlayLru.Remove(existingNode);
                SurfaceOverlayLru.AddFirst(existingNode);
                return existingNode.Value.Value;
            }

            while (SurfaceOverlayImageCache.Count >= MaxOverlayCacheEntries && SurfaceOverlayLru.Last is { } oldest)
            {
                SurfaceOverlayLru.RemoveLast();
                SurfaceOverlayImageCache.Remove(oldest.Value.Key);
            }

            var newNode = new LinkedListNode<(string Key, OverlayTextureData Value)>((key, created));
            SurfaceOverlayLru.AddFirst(newNode);
            SurfaceOverlayImageCache[key] = newNode;
            return created;
        }
    }

    private static string BuildOverlayCacheKey(
        SurfaceDefinition surface,
        IReadOnlyList<PersonalizationElement> elements,
        RenderScene3DOptions options,
        bool isSurfaceActive,
        bool isPartHighlighted)
    {
        var sb = new StringBuilder(256);
        sb.Append(surface.PanelId).Append('|')
          .Append(surface.WidthMm.ToString("F2", CultureInfo.InvariantCulture)).Append('x')
          .Append(surface.HeightMm.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
          .Append(isSurfaceActive ? '1' : '0')
          .Append(isPartHighlighted ? '1' : '0')
          .Append(options.ShowSurfaceGuides ? '1' : '0').Append('|')
          .Append(options.SafetyMarginMm.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
          .Append(options.SelectedElementId ?? string.Empty);

        foreach (PersonalizationElement el in elements)
        {
            bool hasErr = options.ErrorElementIds?.Contains(el.Id) == true;
            sb.Append(';').Append(el.Id).Append(',')
              .Append((int)el.Operation).Append(',')
              .Append(hasErr ? '1' : '0').Append(',')
              .Append(el.XMm.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(el.YMm.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(el.WidthMm.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(el.HeightMm.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(el.RotationDeg.ToString("F1", CultureInfo.InvariantCulture));

            switch (el)
            {
                case TextPersonalizationElement txt:
                    sb.Append(",T:").Append(txt.Content).Append(':').Append(txt.FontFamily).Append(':')
                      .Append(txt.FontSizeMm.ToString("F2", CultureInfo.InvariantCulture)).Append(':')
                      .Append(txt.Bold ? 'B' : 'R').Append(':').Append(txt.VectorPaths.Count);
                    break;
                case VectorSvgPersonalizationElement svg:
                    sb.Append(",V:").Append(svg.Paths.Count).Append(':')
                      .Append(svg.BaseWidthMm.ToString("F2", CultureInfo.InvariantCulture));
                    break;
                case RasterImagePersonalizationElement rst:
                    sb.Append(",R:").Append(rst.Vectorize ? '1' : '0').Append(':')
                      .Append(rst.VectorPaths.Count).Append(':')
                      .Append(rst.ProcessedImageB64?.Length ?? 0).Append(':')
                      .Append(rst.ProcessedImageB64?.GetHashCode(StringComparison.Ordinal) ?? 0);
                    break;
                case ShapePersonalizationElement shp:
                    sb.Append(",S:").Append((int)shp.OperationMode).Append(':')
                      .Append(RuntimeHelpers.GetHashCode(shp.VectorPaths)).Append(':')
                      .Append(shp.VectorPaths.Count).Append(':')
                      .Append(shp.ShapeSpec?.GetSignature() ?? string.Empty);
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool TryComputePlaneScreenToTex(
        SurfaceOverlayQuad3D quad,
        Vector3 planeOffset,
        int texW,
        int texH,
        CameraFrame frame,
        out float i00, out float i01, out float i02,
        out float i10, out float i11, out float i12,
        out float i20, out float i21, out float i22)
    {
        if (texW <= 0 || texH <= 0)
        {
            i00 = i01 = i02 = i10 = i11 = i12 = i20 = i21 = i22 = 0f;
            return false;
        }

        Vector3 origin = (quad.TopLeft - planeOffset) - frame.Eye;
        Vector3 eU = (quad.TopRight - quad.TopLeft) / texW;
        Vector3 eV = (quad.BottomLeft - quad.TopLeft) / texH;

        double cx = frame.ViewportWidth * 0.5;
        double cy = frame.ViewportHeight * 0.5;
        double f = frame.FocalLengthPx;

        static void ProjectColumn(
            Vector3 v,
            CameraFrame cam,
            double centerX,
            double centerY,
            double focal,
            out double c0,
            out double c1,
            out double c2)
        {
            double fz = Vector3.Dot(v, cam.Forward);
            double rx = Vector3.Dot(v, cam.Right);
            double uy = Vector3.Dot(v, cam.Up);
            c0 = fz * centerX + rx * focal;
            c1 = fz * centerY - uy * focal;
            c2 = fz;
        }

        ProjectColumn(eU, frame, cx, cy, f, out double m00, out double m10, out double m20);
        ProjectColumn(eV, frame, cx, cy, f, out double m01, out double m11, out double m21);
        ProjectColumn(origin, frame, cx, cy, f, out double m02, out double m12, out double m22);

        double det = m00 * (m11 * m22 - m12 * m21)
                   - m01 * (m10 * m22 - m12 * m20)
                   + m02 * (m10 * m21 - m11 * m20);
        if (Math.Abs(det) <= 1e-12 || !double.IsFinite(det))
        {
            i00 = i01 = i02 = i10 = i11 = i12 = i20 = i21 = i22 = 0f;
            return false;
        }

        double invDet = 1.0 / det;
        i00 = (float)((m11 * m22 - m12 * m21) * invDet);
        i01 = (float)((m02 * m21 - m01 * m22) * invDet);
        i02 = (float)((m01 * m12 - m02 * m11) * invDet);
        i10 = (float)((m12 * m20 - m10 * m22) * invDet);
        i11 = (float)((m00 * m22 - m02 * m20) * invDet);
        i12 = (float)((m02 * m10 - m00 * m12) * invDet);
        i20 = (float)((m10 * m21 - m11 * m20) * invDet);
        i21 = (float)((m01 * m20 - m00 * m21) * invDet);
        i22 = (float)((m00 * m11 - m01 * m10) * invDet);

        return float.IsFinite(i00) && float.IsFinite(i01) && float.IsFinite(i02)
            && float.IsFinite(i10) && float.IsFinite(i11) && float.IsFinite(i12)
            && float.IsFinite(i20) && float.IsFinite(i21) && float.IsFinite(i22);
    }

    private static bool TryProjectDimensionCalloutGeometry(
        DimensionCallout3D dim,
        CameraFrame frame,
        SKFont titleFont,
        SKFont subFont,
        float dpiScale,
        out Vector2 startScreen,
        out Vector2 endScreen,
        out Vector2 extStartScreen,
        out Vector2 extEndScreen,
        out Vector2 labelScreen,
        out SKRect badgeRect)
    {
        if (!frame.TryProject(dim.Start, out startScreen, out _) ||
            !frame.TryProject(dim.End, out endScreen, out _) ||
            !frame.TryProject(dim.ExtStart, out extStartScreen, out _) ||
            !frame.TryProject(dim.ExtEnd, out extEndScreen, out _) ||
            !frame.TryProject(dim.LabelPos, out labelScreen, out _))
        {
            startScreen = default;
            endScreen = default;
            extStartScreen = default;
            extEndScreen = default;
            labelScreen = default;
            badgeRect = default;
            return false;
        }

        float wMain = titleFont.MeasureText(dim.Label);
        float wSub = string.IsNullOrEmpty(dim.Sublabel) ? 0f : subFont.MeasureText(dim.Sublabel);
        float boxW = MathF.Max(wMain, wSub) + 14f * dpiScale;
        float boxH = (string.IsNullOrEmpty(dim.Sublabel) ? 18f : 29f) * dpiScale;
        float logicalW = frame.ViewportWidth / dpiScale;
        float logicalH = frame.ViewportHeight / dpiScale;
        float marginX = (logicalW >= 220f ? 10.0f : 6.0f) * dpiScale;
        float padTop = (logicalH >= 220f ? 42.0f : 6.0f) * dpiScale;
        float padBottom = (logicalH >= 220f ? 44.0f : 6.0f) * dpiScale;

        float maxLeft = MathF.Max(marginX, frame.ViewportWidth - boxW - marginX);
        float maxTop = MathF.Max(padTop, frame.ViewportHeight - boxH - padBottom);
        float left = Math.Clamp(labelScreen.X - boxW * 0.5f, marginX, maxLeft);
        float top = Math.Clamp(labelScreen.Y - boxH * 0.5f, padTop, maxTop);

        badgeRect = SKRect.Create(left, top, boxW, boxH);
        labelScreen = new Vector2(badgeRect.MidX, badgeRect.MidY);
        return true;
    }

    private static void Draw3DDimensions(
        SKCanvas canvas,
        BoxScene3D scene,
        CameraFrame frame,
        bool dark,
        BoxDimensionAxis? highlightedAxis,
        float dpiScale)
    {
        SKColor accentColor = dark ? new SKColor(0x22, 0xC5, 0x5E) : new SKColor(0x16, 0xA3, 0x4A);
        SKColor extColor = dark ? new SKColor(0x22, 0xC5, 0x5E, 185) : new SKColor(0x16, 0xA3, 0x4A, 195);
        SKColor pillBg = dark ? new SKColor(0x14, 0x18, 0x1F, 240) : new SKColor(0xFF, 0xFF, 0xFF, 244);
        SKColor pillActiveBg = dark ? new SKColor(0x16, 0x2B, 0x1F, 250) : new SKColor(0xEC, 0xFD, 0xF5, 250);
        SKColor glowColor = dark ? new SKColor(0x22, 0xC5, 0x5E, 72) : new SKColor(0x16, 0xA3, 0x4A, 58);
        SKColor textColor = dark ? new SKColor(0xEC, 0xEF, 0xF4) : new SKColor(0x18, 0x1D, 0x25);
        SKColor subColor = dark ? new SKColor(0xA0, 0xAA, 0xB8) : new SKColor(0x4B, 0x55, 0x63);

        using var linePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = accentColor, StrokeWidth = 1.35f * dpiScale, IsAntialias = true };
        using var lineActivePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = accentColor, StrokeWidth = 1.85f * dpiScale, IsAntialias = true };
        using var arrowFillPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = accentColor, IsAntialias = true };
        using var dashPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = extColor,
            StrokeWidth = 1.0f * dpiScale,
            PathEffect = SKPathEffect.CreateDash([4f * dpiScale, 3f * dpiScale], 0f),
            IsAntialias = true,
        };
        using var pillFillPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = pillBg, IsAntialias = true };
        using var pillActiveFillPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = pillActiveBg, IsAntialias = true };
        using var pillStrokePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = accentColor, StrokeWidth = 1.0f * dpiScale, IsAntialias = true };
        using var pillActiveStrokePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = accentColor, StrokeWidth = 1.65f * dpiScale, IsAntialias = true };
        using var pillGlowPaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = glowColor, StrokeWidth = 2.2f * dpiScale, IsAntialias = true };
        using var titleFont = new SKFont(MonoTypeface, 11f * dpiScale) { Embolden = true };
        using var subFont = new SKFont(MonoTypeface, 9f * dpiScale);
        using var titlePaint = new SKPaint { Color = textColor, IsAntialias = true };
        using var subPaint = new SKPaint { Color = subColor, IsAntialias = true };

        foreach (DimensionCallout3D dim in scene.Dimensions)
        {
            if (!TryProjectDimensionCalloutGeometry(
                    dim,
                    frame,
                    titleFont,
                    subFont,
                    dpiScale,
                    out Vector2 s,
                    out Vector2 e,
                    out Vector2 es,
                    out Vector2 ee,
                    out Vector2 lp,
                    out SKRect rect))
            {
                continue;
            }

            bool isHighlighted = highlightedAxis.HasValue && dim.Axis == highlightedAxis.Value;
            SKPaint activeLinePaint = isHighlighted ? lineActivePaint : linePaint;

            canvas.DrawLine(es.X, es.Y, s.X, s.Y, dashPaint);
            canvas.DrawLine(ee.X, ee.Y, e.X, e.Y, dashPaint);
            canvas.DrawLine(s.X, s.Y, e.X, e.Y, activeLinePaint);

            Vector2 delta = e - s;
            float len = delta.Length();
            if (len > 4f * dpiScale)
            {
                Vector2 dir = delta / len;
                DrawDimensionEndMarker(canvas, s, dir, dpiScale, arrowFillPaint, activeLinePaint);
                DrawDimensionEndMarker(canvas, e, -dir, dpiScale, arrowFillPaint, activeLinePaint);
            }

            float radius = 2f * dpiScale;
            if (isHighlighted)
            {
                var glowRect = new SKRect(rect.Left - 1.5f * dpiScale, rect.Top - 1.5f * dpiScale, rect.Right + 1.5f * dpiScale, rect.Bottom + 1.5f * dpiScale);
                canvas.DrawRoundRect(glowRect, 3f * dpiScale, 3f * dpiScale, pillGlowPaint);
                canvas.DrawRoundRect(rect, radius, radius, pillActiveFillPaint);
                canvas.DrawRoundRect(rect, radius, radius, pillActiveStrokePaint);
                canvas.DrawCircle(rect.Right - 4.5f * dpiScale, rect.Top + 4.5f * dpiScale, 1.8f * dpiScale, arrowFillPaint);
            }
            else
            {
                canvas.DrawRoundRect(rect, radius, radius, pillFillPaint);
                canvas.DrawRoundRect(rect, radius, radius, pillStrokePaint);
            }

            if (string.IsNullOrEmpty(dim.Sublabel))
            {
                canvas.DrawText(dim.Label, lp.X, lp.Y + 4f * dpiScale, SKTextAlign.Center, titleFont, titlePaint);
            }
            else
            {
                canvas.DrawText(dim.Label, lp.X, rect.Top + 11.5f * dpiScale, SKTextAlign.Center, titleFont, titlePaint);
                canvas.DrawText(dim.Sublabel, lp.X, rect.Top + 23.5f * dpiScale, SKTextAlign.Center, subFont, subPaint);
            }
        }
    }

    private static void DrawDimensionEndMarker(
        SKCanvas canvas,
        Vector2 tip,
        Vector2 inwardDir,
        float dpiScale,
        SKPaint fillPaint,
        SKPaint tickPaint)
    {
        var perp = new Vector2(-inwardDir.Y, inwardDir.X);
        Vector2 tickA = tip - perp * (3.5f * dpiScale);
        Vector2 tickB = tip + perp * (3.5f * dpiScale);
        canvas.DrawLine(tickA.X, tickA.Y, tickB.X, tickB.Y, tickPaint);

        Vector2 baseCenter = tip + inwardDir * (6.0f * dpiScale);
        Vector2 w1 = baseCenter + perp * (2.3f * dpiScale);
        Vector2 w2 = baseCenter - perp * (2.3f * dpiScale);
        using var arrow = new SKPath();
        arrow.MoveTo(tip.X, tip.Y);
        arrow.LineTo(w1.X, w1.Y);
        arrow.LineTo(w2.X, w2.Y);
        arrow.Close();
        canvas.DrawPath(arrow, fillPaint);
    }

    // =========================================================================
    //  AUXILIARES PRIVADOS DO EDITOR CAD 2D E COMPARTILHADOS
    // =========================================================================

    private static void Draw2DMillimeterGrid(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        float ox,
        float oy,
        float scale,
        bool dark)
    {
        float minorStepMm = scale * 10f < 8f ? 20f : 10f;
        float minorPx = minorStepMm * scale;
        if (minorPx < 6f) return;

        float startPx = (float)SurfaceCanvas2DController.RulerThicknessPx;

        using var minorPaint = new SKPaint
        {
            Color = dark ? new SKColor(0x1D, 0x23, 0x2C) : new SKColor(0xC9, 0xD1, 0xDC),
            StrokeWidth = 1f,
            IsAntialias = false,
        };
        using var majorPaint = new SKPaint
        {
            Color = dark ? new SKColor(0x28, 0x30, 0x3D) : new SKColor(0xB4, 0xC0, 0xCE),
            StrokeWidth = 1.2f,
            IsAntialias = false,
        };
        using var originPaint = new SKPaint
        {
            Color = dark ? new SKColor(0x2B, 0x44, 0x3A) : new SKColor(0x9A, 0xB8, 0xA8),
            StrokeWidth = 1.2f,
            IsAntialias = false,
        };

        int startIdxX = (int)MathF.Floor((startPx - ox) / minorPx) - 1;
        int endIdxX = (int)MathF.Ceiling((widthPx - ox) / minorPx) + 1;
        for (int ix = startIdxX; ix <= endIdxX; ix++)
        {
            float px = ox + ix * minorPx;
            if (px < startPx || px > widthPx) continue;
            SKPaint paint = ix == 0 ? originPaint : (ix % 5 == 0 ? majorPaint : minorPaint);
            canvas.DrawLine(px, startPx, px, heightPx, paint);
        }

        int startIdxY = (int)MathF.Floor((startPx - oy) / minorPx) - 1;
        int endIdxY = (int)MathF.Ceiling((heightPx - oy) / minorPx) + 1;
        for (int iy = startIdxY; iy <= endIdxY; iy++)
        {
            float py = oy + iy * minorPx;
            if (py < startPx || py > heightPx) continue;
            SKPaint paint = iy == 0 ? originPaint : (iy % 5 == 0 ? majorPaint : minorPaint);
            canvas.DrawLine(startPx, py, widthPx, py, paint);
        }
    }

    private static void Draw2DMillimeterRulers(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        float ox,
        float oy,
        float scale,
        bool dark,
        double cursorMmX,
        double cursorMmY)
    {
        Draw2DMillimeterRulersStatic(canvas, widthPx, heightPx, ox, oy, scale, dark);
        Draw2DMillimeterRulerCursorLines(canvas, widthPx, heightPx, ox, oy, scale, dark, cursorMmX, cursorMmY);
    }

    private static void Draw2DMillimeterRulersStatic(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        float ox,
        float oy,
        float scale,
        bool dark)
    {
        float r = (float)SurfaceCanvas2DController.RulerThicknessPx;
        if (widthPx <= r + 4 || heightPx <= r + 4 || scale <= 1e-4f) return;

        SKColor bg = dark ? new SKColor(0x1E, 0x23, 0x2B) : new SKColor(0xE4, 0xE9, 0xF0);
        SKColor cornerBg = dark ? new SKColor(0x18, 0x1C, 0x22) : new SKColor(0xDA, 0xE0, 0xE8);
        SKColor border = dark ? new SKColor(0x2A, 0x30, 0x3A) : new SKColor(0xC8, 0xD0, 0xDC);
        SKColor tickMinor = dark ? new SKColor(0x47, 0x55, 0x69) : new SKColor(0x94, 0xA3, 0xB8);
        SKColor tickMajor = dark ? new SKColor(0x88, 0x95, 0xA7) : new SKColor(0x4B, 0x55, 0x63);
        SKColor textCol = dark ? new SKColor(0xA0, 0xAA, 0xB8) : new SKColor(0x37, 0x41, 0x51);

        using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = bg };
        using var cornerPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = cornerBg };
        using var borderPaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = border, StrokeWidth = 1f };
        using var minorTickPaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = tickMinor, StrokeWidth = 1f };
        using var majorTickPaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = tickMajor, StrokeWidth = 1.1f };
        using var labelFont = new SKFont(MonoTypeface, 8.2f);
        using var cornerFont = new SKFont(MonoTypeface, 7.6f) { Embolden = true };
        using var textPaint = new SKPaint { Color = textCol, IsAntialias = true };

        // Fundos das réguas superior e esquerda
        canvas.DrawRect(r, 0, widthPx - r, r, bgPaint);
        canvas.DrawRect(0, r, r, heightPx - r, bgPaint);

        // Escolha adaptativa do passo principal numérico (10, 20, 50, 100 ou 200 mm)
        float[] candidates = [10f, 20f, 50f, 100f, 200f];
        float majorMm = 200f;
        foreach (float c in candidates)
        {
            if (c * scale >= 42f)
            {
                majorMm = c;
                break;
            }
        }

        int subDivs = (majorMm / 5f) * scale >= 4.5f ? 5 : ((majorMm / 2f) * scale >= 4.5f ? 2 : 1);
        float subMm = majorMm / subDivs;

        // Régua superior (eixo X em mm)
        canvas.Save();
        canvas.ClipRect(new SKRect(r, 0, widthPx, r));
        float minMmX = (r - ox) / scale;
        float maxMmX = (widthPx - ox) / scale;
        int startSubX = (int)MathF.Floor(minMmX / subMm) - 1;
        int endSubX = (int)MathF.Ceiling(maxMmX / subMm) + 1;

        for (int ix = startSubX; ix <= endSubX; ix++)
        {
            float mm = ix * subMm;
            float px = ox + mm * scale;
            bool isMajor = ix % subDivs == 0;
            if (isMajor)
            {
                canvas.DrawLine(px, r - 6f, px, r, majorTickPaint);
                string lbl = ((int)MathF.Round(mm)).ToString(CultureInfo.InvariantCulture);
                canvas.DrawText(lbl, px + 2.5f, 9.5f, SKTextAlign.Left, labelFont, textPaint);
            }
            else
            {
                float tickTop = (subDivs == 2 || ix % 2 == 0) ? r - 4f : r - 2.5f;
                canvas.DrawLine(px, tickTop, px, r, minorTickPaint);
            }
        }
        canvas.Restore();

        // Régua esquerda (eixo Y em mm)
        canvas.Save();
        canvas.ClipRect(new SKRect(0, r, r, heightPx));
        float minMmY = (r - oy) / scale;
        float maxMmY = (heightPx - oy) / scale;
        int startSubY = (int)MathF.Floor(minMmY / subMm) - 1;
        int endSubY = (int)MathF.Ceiling(maxMmY / subMm) + 1;

        for (int iy = startSubY; iy <= endSubY; iy++)
        {
            float mm = iy * subMm;
            float py = oy + mm * scale;
            bool isMajor = iy % subDivs == 0;
            if (isMajor)
            {
                canvas.DrawLine(r - 6f, py, r, py, majorTickPaint);
                string lbl = ((int)MathF.Round(mm)).ToString(CultureInfo.InvariantCulture);
                canvas.Save();
                canvas.Translate(9.5f, py - 2.5f);
                canvas.RotateDegrees(-90f);
                canvas.DrawText(lbl, 0f, 0f, SKTextAlign.Left, labelFont, textPaint);
                canvas.Restore();
            }
            else
            {
                float tickLeft = (subDivs == 2 || iy % 2 == 0) ? r - 4f : r - 2.5f;
                canvas.DrawLine(tickLeft, py, r, py, minorTickPaint);
            }
        }
        canvas.Restore();

        // Canto superior esquerdo ("mm") e linhas divisórias de 1 px
        canvas.DrawRect(0, 0, r, r, cornerPaint);
        canvas.DrawText("mm", r * 0.5f, r * 0.5f + 2.8f, SKTextAlign.Center, cornerFont, textPaint);
        canvas.DrawLine(r, 0, r, heightPx, borderPaint);
        canvas.DrawLine(0, r, widthPx, r, borderPaint);
    }

    private static void Draw2DMillimeterRulerCursorLines(
        SKCanvas canvas,
        int widthPx,
        int heightPx,
        float ox,
        float oy,
        float scale,
        bool dark,
        double cursorMmX,
        double cursorMmY)
    {
        float r = (float)SurfaceCanvas2DController.RulerThicknessPx;
        if (widthPx <= r + 4 || heightPx <= r + 4 || scale <= 1e-4f) return;

        SKColor cursorCol = dark ? new SKColor(0x22, 0xC5, 0x5E) : new SKColor(0x16, 0xA3, 0x4A);
        using var cursorPaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = cursorCol, StrokeWidth = 1.2f };

        float curPxX = ox + (float)cursorMmX * scale;
        if (curPxX >= r && curPxX <= widthPx)
        {
            canvas.DrawLine(curPxX, 0f, curPxX, r, cursorPaint);
        }

        float curPxY = oy + (float)cursorMmY * scale;
        if (curPxY >= r && curPxY <= heightPx)
        {
            canvas.DrawLine(0f, curPxY, r, curPxY, cursorPaint);
        }
    }

    private static void Draw2DMdfPanelSheet(
        SKCanvas canvas,
        float ox,
        float oy,
        float scale,
        SurfaceDefinition surface,
        SurfaceContour2D? contour,
        bool dark)
    {
        using var sheetPath = new SKPath { FillType = SKPathFillType.EvenOdd };

        if (contour is not null && contour.OuterContour.Vertices.Count >= 3)
        {
            AppendPolygonToPath(sheetPath, contour.OuterContour);
            foreach (Polygon2D hole in contour.InternalHoles)
            {
                AppendPolygonToPath(sheetPath, hole);
            }
            foreach (Circle2D circle in contour.CircularHoles)
            {
                sheetPath.AddCircle((float)circle.CenterX, (float)circle.CenterY, (float)circle.Radius);
            }
        }
        else
        {
            sheetPath.AddRect(new SKRect(0f, 0f, (float)surface.WidthMm, (float)surface.HeightMm));
        }

        canvas.Save();
        canvas.Translate(ox, oy);
        canvas.Scale(scale, scale);

        // Sombra suave da chapa MDF sobre a mesa
        canvas.Save();
        canvas.Translate(1.5f / Math.Max(0.4f, scale * 0.5f), 2.5f / Math.Max(0.4f, scale * 0.5f));
        using (var dropShadow = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = dark ? new SKColor(0x00, 0x00, 0x00, 90) : new SKColor(0x0F, 0x17, 0x2A, 38),
            IsAntialias = true,
        })
        {
            canvas.DrawPath(sheetPath, dropShadow);
        }
        canvas.Restore();

        // Preenchimento MDF claro (preserva contraste exato de todas as 6 camadas CNC)
        using (var woodFill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = dark ? new SKColor(0xEF, 0xE0, 0xCA) : new SKColor(0xF5, 0xE6, 0xD3),
            IsAntialias = true,
        })
        {
            canvas.DrawPath(sheetPath, woodFill);
        }

        // Borda de corte externo/interno da chapa
        using (var cutStroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0x5D, 0x40, 0x37),
            StrokeWidth = 1.6f / scale,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        })
        {
            canvas.DrawPath(sheetPath, cutStroke);
        }

        canvas.Restore();
    }

    private static void Draw2DForbiddenZones(SKCanvas canvas, SurfaceDefinition surface)
    {
        float strokeScale = 1f / MathF.Max(1f, MathF.Min((float)surface.WidthMm, (float)surface.HeightMm) * 0.01f);
        float lw = Math.Clamp(0.35f / strokeScale, 0.2f, 0.55f);

        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xEF, 0x44, 0x44, 42),
            IsAntialias = true,
        };
        using var borderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0xEF, 0x44, 0x44, 170),
            StrokeWidth = lw,
            PathEffect = SKPathEffect.CreateDash([1.6f, 1.2f], 0f),
            IsAntialias = true,
        };
        using var hatchPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0xEF, 0x44, 0x44, 65),
            StrokeWidth = lw * 0.7f,
            IsAntialias = true,
        };

        foreach (ForbiddenZone zone in surface.ForbiddenZones)
        {
            var r = new SKRect(
                (float)zone.RectMm.XMm,
                (float)zone.RectMm.YMm,
                (float)zone.RectMm.Right,
                (float)zone.RectMm.Bottom);

            canvas.DrawRect(r, fillPaint);

            // Hachura diagonal 45° sutil dentro da zona restrita
            canvas.Save();
            canvas.ClipRect(r);
            float step = MathF.Max(2.5f, MathF.Min(r.Width, r.Height) * 0.25f);
            step = Math.Min(step, 5f);
            float span = r.Height;
            for (float x = r.Left - span; x < r.Right + span; x += step)
            {
                canvas.DrawLine(x, r.Bottom, x + span, r.Top, hatchPaint);
            }
            canvas.Restore();

            canvas.DrawRect(r, borderPaint);
        }
    }

    private static void DrawSurfaceElements(
        SKCanvas canvas,
        IReadOnlyList<PersonalizationElement> elements,
        bool woodRealisticColors,
        IReadOnlySet<string>? errorElementIds,
        string? selectedElementId,
        bool showSelectionOutlines,
        float strokeScaleAdjust)
    {
        foreach (PersonalizationElement el in elements)
        {
            bool isSelected = selectedElementId == el.Id;
            bool hasError = errorElementIds?.Contains(el.Id) == true;

            float cx = (float)(el.XMm + el.WidthMm * 0.5);
            float cy = (float)(el.YMm + el.HeightMm * 0.5);

            canvas.Save();
            canvas.Translate(cx, cy);
            if (Math.Abs(el.RotationDeg) > 1e-4)
            {
                canvas.RotateDegrees((float)el.RotationDeg);
            }
            canvas.Translate(-(float)(el.WidthMm * 0.5), -(float)(el.HeightMm * 0.5));

            DrawElementLocalContent(canvas, el, woodRealisticColors, hasError, strokeScaleAdjust);

            if (showSelectionOutlines && (isSelected || hasError))
            {
                using var selPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = hasError ? new SKColor(0xEF, 0x44, 0x44) : new SKColor(0x16, 0xA3, 0x4A),
                    StrokeWidth = hasError ? 0.85f : 0.75f,
                    IsAntialias = true,
                };
                if (!isSelected)
                {
                    selPaint.PathEffect = SKPathEffect.CreateDash([1.6f, 1.2f], 0f);
                }
                canvas.DrawRect(0f, 0f, (float)el.WidthMm, (float)el.HeightMm, selPaint);
            }

            canvas.Restore();
        }
    }

    private static void DrawElementLocalContent(
        SKCanvas canvas,
        PersonalizationElement el,
        bool woodRealisticColors,
        bool hasError,
        float strokeScaleAdjust)
    {
        (IReadOnlyList<VectorPathItem>? paths, double baseW, double baseH) = el switch
        {
            TextPersonalizationElement txt => (txt.VectorPaths, Math.Max(0.01, txt.BaseWidthMm), Math.Max(0.01, txt.BaseHeightMm)),
            VectorSvgPersonalizationElement svg => (svg.Paths, Math.Max(0.01, svg.BaseWidthMm), Math.Max(0.01, svg.BaseHeightMm)),
            RasterImagePersonalizationElement rst => (rst.Vectorize ? rst.VectorPaths : null, Math.Max(0.01, rst.BaseWidthMm), Math.Max(0.01, rst.BaseHeightMm)),
            ShapePersonalizationElement shp => (shp.VectorPaths, Math.Max(0.01, shp.BaseWidthMm), Math.Max(0.01, shp.BaseHeightMm)),
            _ => (null, Math.Max(0.01, el.WidthMm), Math.Max(0.01, el.HeightMm)),
        };

        float sx = (float)(el.WidthMm / baseW);
        float sy = (float)(el.HeightMm / baseH);

        canvas.Save();
        canvas.Scale(sx, sy);

        if (el is RasterImagePersonalizationElement raster && (!raster.Vectorize || raster.VectorPaths.Count == 0))
        {
            DrawRasterBitmapToCanvas(canvas, raster, (float)baseW, (float)baseH, woodRealisticColors);
        }

        if (paths is { Count: > 0 })
        {
            if (el is ShapePersonalizationElement shapeEl &&
                shapeEl.ShapeSpec?.Kind != GeometricShapeKind.Crosshair &&
                (shapeEl.OperationMode is OperationMode.Cut or OperationMode.Both || shapeEl.Operation == CncOperationType.CutInternal))
            {
                DrawShapeCutoutRecessTint(canvas, paths, hasError, woodRealisticColors);
            }

            float avgScale = MathF.Max(0.05f, (MathF.Abs(sx) + MathF.Abs(sy)) * 0.5f);
            float strokeMm = MathF.Max(0.22f, 0.42f * strokeScaleAdjust / avgScale);
            DrawVectorPathsToCanvas(canvas, paths, el.Operation, hasError, woodRealisticColors, strokeMm);
        }
        else if (el is TextPersonalizationElement txtFallback)
        {
            SKColor fallbackColor = ResolveCncOrWoodColor(el.Operation, hasError, woodRealisticColors);
            using var font = new SKFont(SKTypeface.Default, (float)Math.Max(2.0, txtFallback.FontSizeMm))
            {
                Embolden = txtFallback.Bold,
            };
            using var txtPaint = new SKPaint { Color = fallbackColor, IsAntialias = true };
            canvas.DrawText(txtFallback.Content, 0f, (float)Math.Max(2.0, txtFallback.FontSizeMm), SKTextAlign.Left, font, txtPaint);
        }

        canvas.Restore();
    }

    public static void InvalidateElementVectorCache(PersonalizationElement? element)
    {
        IReadOnlyList<VectorPathItem>? paths = element switch
        {
            TextPersonalizationElement txt => txt.VectorPaths,
            VectorSvgPersonalizationElement svg => svg.Paths,
            RasterImagePersonalizationElement rst => rst.VectorPaths,
            ShapePersonalizationElement shp => shp.VectorPaths,
            _ => null,
        };

        if (paths is null)
        {
            return;
        }

        lock (VectorPathCompileLock)
        {
            if (VectorPathCache.TryGetValue(paths, out CompiledVectorEntry? entry))
            {
                VectorPathCache.Remove(paths);
                foreach (CompiledPathCommand cmd in entry.Commands)
                {
                    cmd.Path.Dispose();
                }
            }
        }
    }

    private static void DrawShapeCutoutRecessTint(
        SKCanvas canvas,
        IReadOnlyList<VectorPathItem> paths,
        bool hasError,
        bool woodRealisticColors)
    {
        CompiledPathCommand[] commands = GetOrCompileVectorCommands(paths);
        if (commands.Length == 0)
        {
            return;
        }

        SKColor recessColor = hasError
            ? new SKColor(0xEF, 0x44, 0x44, 42)
            : (woodRealisticColors
                ? new SKColor(0x1E, 0x11, 0x07, 175)
                : new SKColor(0xB7, 0x1C, 0x1C, 28));

        using var tintPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = recessColor,
            IsAntialias = true,
        };

        for (int i = 0; i < commands.Length; i++)
        {
            CompiledPathCommand cmd = commands[i];
            if (cmd.Operation is CncOperationType.CutInternal or CncOperationType.CutExternal)
            {
                canvas.DrawPath(cmd.Path, tintPaint);
            }
        }
    }

    private static CompiledPathCommand[] GetOrCompileVectorCommands(IReadOnlyList<VectorPathItem> paths)
    {
        if (VectorPathCache.TryGetValue(paths, out CompiledVectorEntry? cached) &&
            cached.SourceCount == paths.Count)
        {
            return cached.Commands;
        }

        lock (VectorPathCompileLock)
        {
            if (VectorPathCache.TryGetValue(paths, out cached) &&
                cached.SourceCount == paths.Count)
            {
                return cached.Commands;
            }

            var commands = new List<CompiledPathCommand>(paths.Count);
            SKPath? pendingStrokeBatch = null;
            CncOperationType pendingStrokeOp = default;

            void FlushPendingStroke()
            {
                if (pendingStrokeBatch is not null)
                {
                    commands.Add(new CompiledPathCommand(pendingStrokeBatch, pendingStrokeOp, HasFill: false, HasStroke: true));
                    pendingStrokeBatch = null;
                }
            }

            foreach (VectorPathItem item in paths)
            {
                if (string.IsNullOrWhiteSpace(item.D))
                {
                    continue;
                }

                SKPath? parsed = SKPath.ParseSvgPathData(item.D);
                if (parsed is null)
                {
                    continue;
                }

                if (item.HasFill)
                {
                    FlushPendingStroke();
                    parsed.FillType = SKPathFillType.EvenOdd;
                    commands.Add(new CompiledPathCommand(parsed, item.Operation, HasFill: true, HasStroke: item.HasStroke));
                }
                else
                {
                    // Combina sequências contíguas de contornos puros (ou linhas Hatch) da mesma camada CAM
                    if (pendingStrokeBatch is not null && pendingStrokeOp == item.Operation)
                    {
                        pendingStrokeBatch.AddPath(parsed);
                        parsed.Dispose();
                    }
                    else
                    {
                        FlushPendingStroke();
                        pendingStrokeBatch = parsed;
                        pendingStrokeOp = item.Operation;
                    }
                }
            }

            FlushPendingStroke();

            var entry = new CompiledVectorEntry
            {
                SourceCount = paths.Count,
                Commands = commands.ToArray(),
            };
            VectorPathCache.AddOrUpdate(paths, entry);
            return entry.Commands;
        }
    }

    private static void DrawVectorPathsToCanvas(
        SKCanvas canvas,
        IReadOnlyList<VectorPathItem> paths,
        CncOperationType fallbackOp,
        bool hasError,
        bool woodRealisticColors,
        float strokeWidthMm)
    {
        _ = fallbackOp;
        CompiledPathCommand[] commands = GetOrCompileVectorCommands(paths);
        if (commands.Length == 0)
        {
            return;
        }

        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidthMm,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        for (int i = 0; i < commands.Length; i++)
        {
            CompiledPathCommand cmd = commands[i];
            SKColor color = ResolveCncOrWoodColor(cmd.Operation, hasError, woodRealisticColors);
            if (cmd.HasFill)
            {
                fillPaint.Color = color;
                canvas.DrawPath(cmd.Path, fillPaint);
            }

            if (cmd.HasStroke || !cmd.HasFill)
            {
                strokePaint.Color = color;
                canvas.DrawPath(cmd.Path, strokePaint);
            }
        }
    }

    private static void DrawRasterBitmapToCanvas(
        SKCanvas canvas,
        RasterImagePersonalizationElement raster,
        float targetW,
        float targetH,
        bool woodRealisticColors)
    {
        SKBitmap? bmp = GetOrDecodeRasterBitmap(raster);
        if (bmp is not null)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = woodRealisticColors
                    ? new SKColor(0x52, 0x32, 0x1A, 225)
                    : SKColors.White,
            };
            if (woodRealisticColors)
            {
                paint.BlendMode = SKBlendMode.Multiply;
            }

            canvas.DrawBitmap(bmp, new SKRect(0f, 0f, targetW, targetH), paint);
            return;
        }

        using var placeholder = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0x37, 0x47, 0x4F, 65),
        };
        canvas.DrawRect(0f, 0f, targetW, targetH, placeholder);
    }

    private static SKBitmap? GetOrDecodeRasterBitmap(RasterImagePersonalizationElement raster)
    {
        if (string.IsNullOrWhiteSpace(raster.ProcessedImageB64))
        {
            return null;
        }

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raster.ProcessedImageB64));
        string key = $"{raster.Id}:{Convert.ToHexString(hashBytes)}";

        if (RasterCache.TryGetValue(key, out SKBitmap? cached))
        {
            return cached;
        }

        try
        {
            byte[] pngBytes = Convert.FromBase64String(raster.ProcessedImageB64);
            SKBitmap? decoded = SKBitmap.Decode(pngBytes);
            if (decoded is null) return null;

            if (RasterCache.Count > 32)
            {
                RasterCache.Clear();
            }

            RasterCache[key] = decoded;
            return decoded;
        }
        catch
        {
            return null;
        }
    }

    private static void DrawRotatedElementBox(
        SKCanvas canvas,
        PersonalizationElement el,
        SKColor color,
        float strokeWidthMm,
        bool dashed,
        float scalePxPerMm,
        bool fillError)
    {
        float cx = (float)(el.XMm + el.WidthMm * 0.5);
        float cy = (float)(el.YMm + el.HeightMm * 0.5);

        canvas.Save();
        canvas.Translate(cx, cy);
        if (Math.Abs(el.RotationDeg) > 1e-4)
        {
            canvas.RotateDegrees((float)el.RotationDeg);
        }

        var rect = new SKRect(
            -(float)(el.WidthMm * 0.5),
            -(float)(el.HeightMm * 0.5),
            (float)(el.WidthMm * 0.5),
            (float)(el.HeightMm * 0.5));

        if (fillError)
        {
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0xEF, 0x44, 0x44, 30) };
            canvas.DrawRect(rect, fill);
        }

        using var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = color,
            StrokeWidth = strokeWidthMm,
            PathEffect = dashed ? SKPathEffect.CreateDash([4f / scalePxPerMm, 3f / scalePxPerMm], 0f) : null,
            IsAntialias = true,
        };
        canvas.DrawRect(rect, stroke);
        canvas.Restore();
    }

    private static void DrawSelectionGizmo2D(
        SKCanvas canvas,
        SurfaceCanvas2DController controller,
        PersonalizationElement selected,
        bool hasError)
    {
        IReadOnlyDictionary<Editor2DHandleType, Point2D> handles = controller.GetHandlesMm(selected);
        Point2D centerMm = selected.CenterMm();
        (double cxPx, double cyPx) = controller.MmToPx(centerMm.X, centerMm.Y);

        float scale = (float)controller.EffectiveScalePxPerMm;
        float boxW = (float)(selected.WidthMm * scale);
        float boxH = (float)(selected.HeightMm * scale);

        SKColor gizmoColor = hasError ? new SKColor(0xEF, 0x44, 0x44) : new SKColor(0x16, 0xA3, 0x4A);

        canvas.Save();
        canvas.Translate((float)cxPx, (float)cyPx);
        if (Math.Abs(selected.RotationDeg) > 1e-4)
        {
            canvas.RotateDegrees((float)selected.RotationDeg);
        }

        // 1. Quadro envolvente #16A34A + haste da alça de rotação superior
        float halfW = boxW * 0.5f;
        float halfH = boxH * 0.5f;
        float rotDist = (float)SurfaceCanvas2DController.RotateHandleOffsetPx;

        using var boxPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = gizmoColor,
            StrokeWidth = 1.8f,
            IsAntialias = true,
        };
        canvas.DrawRect(-halfW, -halfH, boxW, boxH, boxPaint);
        canvas.DrawLine(0f, -halfH, 0f, -halfH - rotDist, boxPaint);
        canvas.Restore();

        // 2. As 8 alças quadradas de redimensionamento e 1 alça circular de rotação
        using var handleFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
        using var handleStroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = gizmoColor, StrokeWidth = 1.8f, IsAntialias = true };
        using var rotFill = new SKPaint { Style = SKPaintStyle.Fill, Color = gizmoColor, IsAntialias = true };
        using var rotIconStroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.White,
            StrokeWidth = 1.5f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        };

        const float halfHandle = 4.0f;
        foreach ((Editor2DHandleType type, Point2D posMm) in handles)
        {
            (double px, double py) = controller.MmToPx(posMm.X, posMm.Y);
            float x = (float)px;
            float y = (float)py;

            if (type == Editor2DHandleType.Rotate)
            {
                canvas.DrawCircle(x, y, 7.2f, rotFill);
                using var arcRect = new SKPath();
                arcRect.AddArc(new SKRect(x - 3.6f, y - 3.6f, x + 3.6f, y + 3.6f), -60f, 250f);
                canvas.DrawPath(arcRect, rotIconStroke);
                continue;
            }

            var hRect = new SKRect(x - halfHandle, y - halfHandle, x + halfHandle, y + halfHandle);
            canvas.DrawRect(hRect, handleFill);
            canvas.DrawRect(hRect, handleStroke);
        }
    }

    public static string FormatLiveDimensionBadgeText(PersonalizationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        Point2D center = element.CenterMm();
        if (element is ShapePersonalizationElement { ShapeSpec: { } spec })
        {
            return spec.Kind switch
            {
                GeometricShapeKind.Rectangle => string.Format(
                    CultureInfo.InvariantCulture,
                    "W: {0:0.0} × H: {1:0.0} mm",
                    spec.WidthMm,
                    spec.HeightMm),
                GeometricShapeKind.Circle => string.Format(
                    CultureInfo.InvariantCulture,
                    "Ø {0:0.0} mm ({1:0.0}, {2:0.0})",
                    spec.DiameterMm,
                    center.X,
                    center.Y),
                GeometricShapeKind.Slot => string.Format(
                    CultureInfo.InvariantCulture,
                    "Slot {0:0.0} × {1:0.0} mm",
                    Math.Max(spec.WidthMm, spec.HeightMm),
                    Math.Min(spec.WidthMm, spec.HeightMm)),
                GeometricShapeKind.Polygon => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} lados • Ø {1:0.0} mm",
                    spec.Sides,
                    spec.DiameterMm),
                GeometricShapeKind.Crosshair => string.Format(
                    CultureInfo.InvariantCulture,
                    "Centro ({0:0.0}, {1:0.0}) mm",
                    center.X,
                    center.Y),
                _ => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.0} × {1:0.0} mm ({2:0.0}, {3:0.0})",
                    element.WidthMm,
                    element.HeightMm,
                    center.X,
                    center.Y),
            };
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0} × {1:0.0} mm ({2:0.0}, {3:0.0})",
            element.WidthMm,
            element.HeightMm,
            center.X,
            center.Y);
    }

    private static void DrawLiveDimensionBadge2D(
        SKCanvas canvas,
        SurfaceCanvas2DController controller,
        PersonalizationElement element,
        int widthPx,
        int heightPx)
    {
        string text = FormatLiveDimensionBadgeText(element);
        Point2D center = element.CenterMm();
        (double cxPx, double cyPx) = controller.MmToPx(center.X, center.Y);
        float scale = (float)controller.EffectiveScalePxPerMm;
        float elemHalfH = (float)(element.HeightMm * 0.5 * scale);

        float textWidth = LiveBadgeFont.MeasureText(text);
        const float padX = 8f;
        const float badgeH = 22f;
        float badgeW = MathF.Max(60f, textWidth + padX * 2f);
        float rulerPx = (float)SurfaceCanvas2DController.RulerThicknessPx;

        float bx = (float)cxPx - badgeW * 0.5f;
        float by = (float)cyPx + elemHalfH + 10f;
        if (by + badgeH > heightPx - 6f)
        {
            by = (float)cyPx - elemHalfH - badgeH - 10f;
        }

        bx = Math.Clamp(bx, rulerPx + 6f, MathF.Max(rulerPx + 6f, widthPx - badgeW - 6f));
        by = Math.Clamp(by, rulerPx + 6f, MathF.Max(rulerPx + 6f, heightPx - badgeH - 6f));

        var rect = new SKRect(bx, by, bx + badgeW, by + badgeH);
        canvas.DrawRoundRect(rect, 4f, 4f, LiveBadgeBgPaint);
        canvas.DrawRoundRect(rect, 4f, 4f, LiveBadgeBorderPaint);
        canvas.DrawText(text, rect.MidX, rect.Top + 14.8f, SKTextAlign.Center, LiveBadgeFont, LiveBadgeTextPaint);
    }

    public static string FormatGroupLiveDimensionBadgeText(int count, double dxMm, double dyMm)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0} itens • Δ({1:+0.0;-0.0;0.0}, {2:+0.0;-0.0;0.0}) mm",
            count,
            dxMm,
            dyMm);

    private static void DrawMarqueeSelectionBox2D(SKCanvas canvas, SurfaceCanvas2DController controller)
    {
        (double x1, double y1) = controller.MmToPx(controller.MarqueeStartMm.X, controller.MarqueeStartMm.Y);
        (double x2, double y2) = controller.MmToPx(controller.MarqueeCurrentMm.X, controller.MarqueeCurrentMm.Y);
        float left = (float)Math.Min(x1, x2);
        float top = (float)Math.Min(y1, y2);
        float right = (float)Math.Max(x1, x2);
        float bottom = (float)Math.Max(y1, y2);
        var rect = new SKRect(left, top, right, bottom);
        canvas.DrawRect(rect, MarqueeFillPaint);
        canvas.DrawRect(rect, MarqueeBorderPaint);
    }

    private static void DrawMultiSelectionOverlay2D(
        SKCanvas canvas,
        SurfaceCanvas2DController controller,
        PersonalizationElement[] panelElements,
        IReadOnlyList<string> selectedIds,
        int widthPx,
        int heightPx,
        bool showGroupBadge)
    {
        float scale = (float)controller.EffectiveScalePxPerMm;
        string? anchorId = selectedIds[0];

        float groupMinX = float.PositiveInfinity;
        float groupMinY = float.PositiveInfinity;
        float groupMaxX = float.NegativeInfinity;
        float groupMaxY = float.NegativeInfinity;
        int visibleSelectedCount = 0;

        for (int i = 0; i < panelElements.Length; i++)
        {
            PersonalizationElement el = panelElements[i];
            bool isSelected = false;
            for (int k = 0; k < selectedIds.Count; k++)
            {
                if (string.Equals(el.Id, selectedIds[k], StringComparison.Ordinal))
                {
                    isSelected = true;
                    break;
                }
            }

            if (!isSelected)
            {
                continue;
            }

            visibleSelectedCount++;
            bool isAnchor = string.Equals(el.Id, anchorId, StringComparison.Ordinal);

            Point2D centerMm = el.CenterMm();
            (double cxPx, double cyPx) = controller.MmToPx(centerMm.X, centerMm.Y);
            float boxW = (float)(el.WidthMm * scale);
            float boxH = (float)(el.HeightMm * scale);
            float halfW = boxW * 0.5f;
            float halfH = boxH * 0.5f;

            canvas.Save();
            canvas.Translate((float)cxPx, (float)cyPx);
            if (Math.Abs(el.RotationDeg) > 1e-4)
            {
                canvas.RotateDegrees((float)el.RotationDeg);
            }
            canvas.DrawRect(-halfW, -halfH, boxW, boxH, isAnchor ? AnchorOutlinePaint : MultiSelectedOutlinePaint);
            canvas.Restore();

            IReadOnlyList<Point2D> corners = el.Corners2D();
            float elMinX = float.PositiveInfinity;
            float elMinY = float.PositiveInfinity;
            for (int c = 0; c < corners.Count; c++)
            {
                (double px, double py) = controller.MmToPx(corners[c].X, corners[c].Y);
                float fx = (float)px;
                float fy = (float)py;
                if (fx < elMinX) elMinX = fx;
                if (fy < elMinY) elMinY = fy;
                if (fx < groupMinX) groupMinX = fx;
                if (fx > groupMaxX) groupMaxX = fx;
                if (fy < groupMinY) groupMinY = fy;
                if (fy > groupMaxY) groupMaxY = fy;
            }

            if (isAnchor)
            {
                float pillX = elMinX;
                float pillY = MathF.Max((float)SurfaceCanvas2DController.RulerThicknessPx + 2f, elMinY - 15f);
                var pillRect = new SKRect(pillX, pillY, pillX + 22f, pillY + 14f);
                canvas.DrawRoundRect(pillRect, 2.5f, 2.5f, AnchorBadgeFillPaint);
                canvas.DrawText("1º", pillRect.MidX, pillRect.Top + 10.2f, SKTextAlign.Center, AnchorBadgeFont, AnchorBadgeTextPaint);
            }
        }

        if (visibleSelectedCount <= 0 || groupMinX > groupMaxX)
        {
            return;
        }

        const float pad = 4f;
        var groupRect = new SKRect(groupMinX - pad, groupMinY - pad, groupMaxX + pad, groupMaxY + pad);
        canvas.DrawRect(groupRect, GroupBoundsBorderPaint);

        if (showGroupBadge)
        {
            string badgeText = FormatGroupLiveDimensionBadgeText(
                visibleSelectedCount,
                controller.GroupDragDeltaXMm,
                controller.GroupDragDeltaYMm);
            float textWidth = LiveBadgeFont.MeasureText(badgeText);
            const float padX = 8f;
            const float badgeH = 22f;
            float badgeW = MathF.Max(80f, textWidth + padX * 2f);
            float rulerPx = (float)SurfaceCanvas2DController.RulerThicknessPx;

            float bx = groupRect.MidX - badgeW * 0.5f;
            float by = groupRect.Bottom + 8f;
            if (by + badgeH > heightPx - 6f)
            {
                by = groupRect.Top - badgeH - 8f;
            }

            bx = Math.Clamp(bx, rulerPx + 6f, MathF.Max(rulerPx + 6f, widthPx - badgeW - 6f));
            by = Math.Clamp(by, rulerPx + 6f, MathF.Max(rulerPx + 6f, heightPx - badgeH - 6f));

            var rect = new SKRect(bx, by, bx + badgeW, by + badgeH);
            canvas.DrawRoundRect(rect, 4f, 4f, LiveBadgeBgPaint);
            canvas.DrawRoundRect(rect, 4f, 4f, LiveBadgeBorderPaint);
            canvas.DrawText(badgeText, rect.MidX, rect.Top + 14.8f, SKTextAlign.Center, LiveBadgeFont, LiveBadgeTextPaint);
        }
    }

    private static void AppendPolygonToPath(SKPath path, Polygon2D polygon)
    {
        if (polygon.Vertices.Count < 3) return;
        path.MoveTo((float)polygon.Vertices[0].X, (float)polygon.Vertices[0].Y);
        for (int i = 1; i < polygon.Vertices.Count; i++)
        {
            path.LineTo((float)polygon.Vertices[i].X, (float)polygon.Vertices[i].Y);
        }
        path.Close();
    }

    /// <summary>Retorna exatamente as cores das 6 camadas CNC exigidas pelo padrão CAM.</summary>
    public static SKColor ResolveCncLayerColor(CncOperationType operation) => operation switch
    {
        CncOperationType.CutInternal => new SKColor(0xB7, 0x1C, 0x1C),
        CncOperationType.CutExternal => new SKColor(0x00, 0x00, 0x00),
        CncOperationType.EngraveVector => new SKColor(0x0D, 0x47, 0xA1),
        CncOperationType.EngraveFill => new SKColor(0x6A, 0x1B, 0x9A),
        CncOperationType.Mark => new SKColor(0x1B, 0x5E, 0x20),
        _ => new SKColor(0x37, 0x47, 0x4F),
    };

    private static SKColor ResolveCncOrWoodColor(CncOperationType op, bool hasError, bool woodRealisticColors)
    {
        if (hasError)
        {
            return new SKColor(0xD3, 0x2F, 0x2F);
        }

        if (woodRealisticColors)
        {
            return op == CncOperationType.Mark
                ? new SKColor(0x4E, 0x34, 0x2E)
                : new SKColor(0x2B, 0x17, 0x08);
        }

        return ResolveCncLayerColor(op);
    }
}
