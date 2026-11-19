using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace CaixaMaker.App.Helpers;

/// <summary>
/// Ponte gerenciada sem <c>unsafe</c> para transferir buffers BGRA8888 do SkiaSharp para <see cref="WriteableBitmap"/> no WinUI 3,
/// reutilizando o bitmap alvo sempre que as dimensões permanecerem constantes.
/// </summary>
internal static class SkiaBitmapBridge
{
    public static WriteableBitmap BlitSurface(
        SKSurface surface,
        Image targetImage,
        WriteableBitmap? currentBitmap,
        int widthPx,
        int heightPx)
    {
        using SKPixmap pixmap = surface.PeekPixels();
        return BlitPixelSpan(pixmap.GetPixelSpan(), targetImage, currentBitmap, widthPx, heightPx);
    }

    public static WriteableBitmap BlitBitmap(
        SKBitmap bitmap,
        Image targetImage,
        WriteableBitmap? currentBitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(targetImage);

        return BlitPixelSpan(
            bitmap.GetPixelSpan(),
            targetImage,
            currentBitmap,
            bitmap.Width,
            bitmap.Height);
    }

    private static WriteableBitmap BlitPixelSpan(
        ReadOnlySpan<byte> bgraPixels,
        Image targetImage,
        WriteableBitmap? currentBitmap,
        int widthPx,
        int heightPx)
    {
        bool allocated = currentBitmap is null ||
                         currentBitmap.PixelWidth != widthPx ||
                         currentBitmap.PixelHeight != heightPx;

        WriteableBitmap wb = allocated
            ? new WriteableBitmap(widthPx, heightPx)
            : currentBitmap!;

        using Stream stream = wb.PixelBuffer.AsStream();
        stream.Position = 0;
        stream.Write(bgraPixels);
        wb.Invalidate();

        if (allocated || !ReferenceEquals(targetImage.Source, wb))
        {
            targetImage.Source = wb;
        }

        return wb;
    }
}
