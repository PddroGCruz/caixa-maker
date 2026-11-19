using System.Numerics;
using CaixaMaker.Core.BoxModels;
using SkiaSharp;

namespace CaixaMaker.App.Rendering;

/// <summary>
/// Renderizador minimalista e arquitetônico da Splash Screen v2.0.1 (680×380 lógicos / 1360×760 em 2× SSAA).
/// Combina fundo grafite-ardósia uniforme (#0B0E14 → #111620), iluminação radial difusa no centro-esquerda (#16A34A14)
/// e marca d'água geométrica isométrica discreta remetendo exclusivamente ao cubo oficial do Caixa Maker.
/// </summary>
public static class SplashArtworkRenderer
{
    public static readonly SKColor BackgroundStartColor = new(0x0B, 0x0E, 0x14);
    public static readonly SKColor BackgroundEndColor = new(0x11, 0x16, 0x20);
    public static readonly SKColor LeftAmbientGlowColor = new(0x16, 0xA3, 0x4A, 0x14);
    public static readonly SKColor WatermarkNeutralColor = new(0xFF, 0xFF, 0xFF, 0x07);
    public static readonly SKColor WatermarkEmeraldColor = new(0x22, 0xC5, 0x5E, 0x0A);

    public static SKBitmap RenderHeroArtworkBitmap(
        int widthPx = 1360,
        int heightPx = 760,
        BoxModelRegistry? _ = null)
    {
        int w = Math.Clamp(widthPx, 320, 2720);
        int h = Math.Clamp(heightPx, 180, 1520);
        float scale = w / 680f;

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);

        DrawMinimalSlateBackdrop(canvas, w, h);
        DrawIsometricCubeGeometricWatermark(canvas, w, h, scale);

        return bitmap;
    }

    public static byte[] RenderHeroArtworkPng(
        int widthPx = 1360,
        int heightPx = 760,
        BoxModelRegistry? registry = null)
    {
        using SKBitmap bitmap = RenderHeroArtworkBitmap(widthPx, heightPx, registry);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void DrawMinimalSlateBackdrop(SKCanvas canvas, int w, int h)
    {
        using (var bgShader = SKShader.CreateLinearGradient(
                   new SKPoint(0f, 0f),
                   new SKPoint(w, h),
                   [BackgroundStartColor, BackgroundEndColor],
                   [0.0f, 1.0f],
                   SKShaderTileMode.Clamp))
        using (var bgPaint = new SKPaint { Shader = bgShader, IsAntialias = true })
        {
            canvas.DrawRect(0f, 0f, w, h, bgPaint);
        }

        // Iluminação radial extremamente sutil e difusa (#16A34A14) atrás do bloco do logotipo
        var glowCenter = new SKPoint(w * 0.16f, h * 0.43f);
        float glowRadius = w * 0.42f;
        using (var glowShader = SKShader.CreateRadialGradient(
                   glowCenter,
                   glowRadius,
                   [LeftAmbientGlowColor, new SKColor(0x16, 0xA3, 0x4A, 0x06), SKColors.Transparent],
                   [0.0f, 0.48f, 1.0f],
                   SKShaderTileMode.Clamp))
        using (var glowPaint = new SKPaint { Shader = glowShader, IsAntialias = true })
        {
            canvas.DrawRect(0f, 0f, w, h, glowPaint);
        }
    }

    private static void DrawIsometricCubeGeometricWatermark(SKCanvas canvas, int w, int h, float scale)
    {
        // Marca d'água geométrica ortogonal isométrica centrada no canto direito ao fundo,
        // remetendo exclusivamente às subdivisões 2×2 do logotipo cúbico do Caixa Maker (sem desenho 3D ou textos).
        var center = new Vector2(w * 0.855f, h * 0.435f);
        float radius = h * 0.36f;

        const float cos30 = 0.8660254f;
        const float sin30 = 0.5f;

        // Vetores unitários dos três eixos ortogonais isométricos
        var axisUp = new Vector2(0f, -1f);
        var axisRightUp = new Vector2(cos30, -sin30);
        var axisLeftUp = new Vector2(-cos30, -sin30);

        var fadeCenter = new SKPoint(center.X, center.Y);
        float fadeRadius = h * 0.62f;

        using SKShader neutralShader = SKShader.CreateRadialGradient(
            fadeCenter,
            fadeRadius,
            [WatermarkNeutralColor, new SKColor(0xFF, 0xFF, 0xFF, 0x04), SKColors.Transparent],
            [0.0f, 0.62f, 1.0f],
            SKShaderTileMode.Clamp);

        using SKShader emeraldShader = SKShader.CreateRadialGradient(
            fadeCenter,
            fadeRadius,
            [WatermarkEmeraldColor, new SKColor(0x22, 0xC5, 0x5E, 0x05), SKColors.Transparent],
            [0.0f, 0.58f, 1.0f],
            SKShaderTileMode.Clamp);

        using var fineNeutralPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f * scale,
            Shader = neutralShader,
            IsAntialias = true,
        };

        using var guideNeutralPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.8f * scale,
            Shader = neutralShader,
            IsAntialias = true,
        };

        using var accentEmeraldPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.1f * scale,
            Shader = emeraldShader,
            IsAntialias = true,
        };

        // 1. Eixos ortogonais isométricos contínuos ultra-discretos cruzando o plano de fundo direito
        float axisReach = radius * 1.95f;
        DrawAxisRay(canvas, center, axisUp, axisReach, guideNeutralPaint);
        DrawAxisRay(canvas, center, axisRightUp, axisReach, guideNeutralPaint);
        DrawAxisRay(canvas, center, axisLeftUp, axisReach, guideNeutralPaint);

        // 2. Hexágono externo da projeção isométrica do cubo (sem preenchimento, apenas traço fino)
        Vector2 pTop = new(center.X, center.Y - radius);
        Vector2 pTopRight = new(center.X + cos30 * radius, center.Y - sin30 * radius);
        Vector2 pBotRight = new(center.X + cos30 * radius, center.Y + sin30 * radius);
        Vector2 pBottom = new(center.X, center.Y + radius);
        Vector2 pBotLeft = new(center.X - cos30 * radius, center.Y + sin30 * radius);
        Vector2 pTopLeft = new(center.X - cos30 * radius, center.Y - sin30 * radius);

        using (var outerHex = new SKPath())
        {
            outerHex.MoveTo(pTop.X, pTop.Y);
            outerHex.LineTo(pTopRight.X, pTopRight.Y);
            outerHex.LineTo(pBotRight.X, pBotRight.Y);
            outerHex.LineTo(pBottom.X, pBottom.Y);
            outerHex.LineTo(pBotLeft.X, pBotLeft.Y);
            outerHex.LineTo(pTopLeft.X, pTopLeft.Y);
            outerHex.Close();
            canvas.DrawPath(outerHex, accentEmeraldPaint);
        }

        // Eixo interno em Y unindo o centro às arestas frontais
        canvas.DrawLine(center.X, center.Y, pTopLeft.X, pTopLeft.Y, accentEmeraldPaint);
        canvas.DrawLine(center.X, center.Y, pTopRight.X, pTopRight.Y, accentEmeraldPaint);
        canvas.DrawLine(center.X, center.Y, pBottom.X, pBottom.Y, accentEmeraldPaint);

        // 3. Subdivisões ortogonais 2×2 remetendo diretamente às 3 faces quadriculadas do símbolo oficial
        // Face superior
        DrawMidLine(canvas, Midpoint(pTop, pTopLeft), Midpoint(pTopRight, center), fineNeutralPaint);
        DrawMidLine(canvas, Midpoint(pTop, pTopRight), Midpoint(pTopLeft, center), fineNeutralPaint);

        // Face frontal esquerda
        DrawMidLine(canvas, Midpoint(pTopLeft, pBotLeft), Midpoint(center, pBottom), fineNeutralPaint);
        DrawMidLine(canvas, Midpoint(pTopLeft, center), Midpoint(pBotLeft, pBottom), fineNeutralPaint);

        // Face frontal direita
        DrawMidLine(canvas, Midpoint(center, pBottom), Midpoint(pTopRight, pBotRight), fineNeutralPaint);
        DrawMidLine(canvas, Midpoint(center, pTopRight), Midpoint(pBottom, pBotRight), fineNeutralPaint);

        // Anel geométrico de construção externo muito leve em torno do hexágono
        canvas.DrawCircle(center.X, center.Y, radius * 1.18f, guideNeutralPaint);
    }

    private static void DrawAxisRay(SKCanvas canvas, Vector2 origin, Vector2 unitDir, float reach, SKPaint paint)
    {
        Vector2 a = origin - (unitDir * reach);
        Vector2 b = origin + (unitDir * reach);
        canvas.DrawLine(a.X, a.Y, b.X, b.Y, paint);
    }

    private static void DrawMidLine(SKCanvas canvas, Vector2 a, Vector2 b, SKPaint paint)
    {
        canvas.DrawLine(a.X, a.Y, b.X, b.Y, paint);
    }

    private static Vector2 Midpoint(Vector2 a, Vector2 b) => (a + b) * 0.5f;
}
