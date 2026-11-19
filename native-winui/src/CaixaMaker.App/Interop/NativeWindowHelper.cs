using System.Runtime.InteropServices;

namespace CaixaMaker.App.Interop;

/// <summary>
/// P/Invoke Win32 para foco de instância única (ShowWindow / SetForegroundWindow)
/// e restrição de tamanho mínimo de janela via WM_GETMINMAXINFO.
/// </summary>
internal static class NativeWindowHelper
{
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const nuint MinSizeSubclassId = 0x434D_0001;
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint PwRenderFullContent = 0x00000002;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    private delegate nint SubclassProc(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint hwnd,
        int dwAttribute,
        out Rect pvAttribute,
        int cbAttribute);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(
        nint hdc,
        ref BitmapInfo pbmi,
        uint usage,
        out nint ppvBits,
        nint hSection,
        uint offset);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd,
        SubclassProc pfnSubclass,
        nuint uIdSubclass,
        nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd,
        SubclassProc pfnSubclass,
        nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam);

    public static void BringWindowToFront(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        int showCommand = IsIconic(hwnd) ? SwRestore : SwShow;
        _ = ShowWindow(hwnd, showCommand);
        _ = SetForegroundWindow(hwnd);
    }

    public static double GetDpiScaleFactor(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return 1.0;
        }

        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static bool TryGetVisibleFrameBounds(nint hwnd, out Rect frameBounds)
    {
        frameBounds = default;
        if (hwnd == nint.Zero)
        {
            return false;
        }

        int hr = DwmGetWindowAttribute(
            hwnd,
            DwmwaExtendedFrameBounds,
            out frameBounds,
            Marshal.SizeOf<Rect>());
        if (hr == 0 && frameBounds.Width > 0 && frameBounds.Height > 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out frameBounds) && frameBounds.Width > 0 && frameBounds.Height > 0;
    }

    /// <summary>
    /// Captura a janela real via PrintWindow (PW_RENDERFULLCONTENT) recortada para os limites visíveis do DWM
    /// e garante canal Alpha opaco (255) no buffer BGRA32 resultante.
    /// </summary>
    public static bool TryCaptureWindowBgra(
        nint hwnd,
        out int width,
        out int height,
        out byte[] bgraPixels)
    {
        width = 0;
        height = 0;
        bgraPixels = [];

        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out Rect windowRect))
        {
            return false;
        }

        int fullW = windowRect.Width;
        int fullH = windowRect.Height;
        if (fullW <= 0 || fullH <= 0)
        {
            return false;
        }

        Rect frameRect = windowRect;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect dwmRect, Marshal.SizeOf<Rect>()) == 0
            && dwmRect.Width > 0
            && dwmRect.Height > 0)
        {
            frameRect = dwmRect;
        }

        nint screenDc = GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            return false;
        }

        nint memDc = nint.Zero;
        nint dibBitmap = nint.Zero;
        nint oldObject = nint.Zero;

        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == nint.Zero)
            {
                return false;
            }

            var bmi = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = fullW,
                    biHeight = -fullH, // Top-down BGRA
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BiRgb,
                },
            };

            dibBitmap = CreateDIBSection(memDc, ref bmi, DibRgbColors, out nint bitsPtr, nint.Zero, 0);
            if (dibBitmap == nint.Zero || bitsPtr == nint.Zero)
            {
                return false;
            }

            oldObject = SelectObject(memDc, dibBitmap);

            if (!PrintWindow(hwnd, memDc, PwRenderFullContent))
            {
                return false;
            }

            int fullByteCount = fullW * fullH * 4;
            byte[] fullBuffer = new byte[fullByteCount];
            Marshal.Copy(bitsPtr, fullBuffer, 0, fullByteCount);

            int cropX = Math.Clamp(frameRect.Left - windowRect.Left, 0, Math.Max(0, fullW - 1));
            int cropY = Math.Clamp(frameRect.Top - windowRect.Top, 0, Math.Max(0, fullH - 1));
            int cropW = Math.Clamp(frameRect.Width, 1, fullW - cropX);
            int cropH = Math.Clamp(frameRect.Height, 1, fullH - cropY);

            byte[] cropped = new byte[cropW * cropH * 4];
            int srcStride = fullW * 4;
            int dstStride = cropW * 4;

            for (int y = 0; y < cropH; y++)
            {
                int srcRow = (cropY + y) * srcStride + cropX * 4;
                int dstRow = y * dstStride;
                Buffer.BlockCopy(fullBuffer, srcRow, cropped, dstRow, dstStride);
                for (int x = 3; x < dstStride; x += 4)
                {
                    cropped[dstRow + x] = 255;
                }
            }

            width = cropW;
            height = cropH;
            bgraPixels = cropped;
            return true;
        }
        finally
        {
            if (oldObject != nint.Zero && memDc != nint.Zero)
            {
                _ = SelectObject(memDc, oldObject);
            }
            if (dibBitmap != nint.Zero)
            {
                _ = DeleteObject(dibBitmap);
            }
            if (memDc != nint.Zero)
            {
                _ = DeleteDC(memDc);
            }
            _ = ReleaseDC(nint.Zero, screenDc);
        }
    }

    public static IDisposable AttachMinSizeSubclass(nint hwnd, int minWidthDip, int minHeightDip)
    {
        return new MinSizeSubclassHandle(hwnd, minWidthDip, minHeightDip);
    }

    private sealed class MinSizeSubclassHandle : IDisposable
    {
        private readonly nint _hwnd;
        private readonly int _minWidthDip;
        private readonly int _minHeightDip;
        private readonly SubclassProc _proc;
        private bool _attached;

        public MinSizeSubclassHandle(nint hwnd, int minWidthDip, int minHeightDip)
        {
            _hwnd = hwnd;
            _minWidthDip = minWidthDip;
            _minHeightDip = minHeightDip;
            _proc = HandleWindowMessage;

            if (_hwnd != nint.Zero)
            {
                _attached = SetWindowSubclass(_hwnd, _proc, MinSizeSubclassId, 0);
            }
        }

        private nint HandleWindowMessage(
            nint hWnd,
            uint uMsg,
            nint wParam,
            nint lParam,
            nuint uIdSubclass,
            nuint dwRefData)
        {
            if (uMsg == WmGetMinMaxInfo && lParam != nint.Zero)
            {
                double scale = GetDpiScaleFactor(hWnd);
                MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, (int)Math.Ceiling(_minWidthDip * scale));
                info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, (int)Math.Ceiling(_minHeightDip * scale));
                Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
                return 0;
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_attached && _hwnd != nint.Zero)
            {
                _ = RemoveWindowSubclass(_hwnd, _proc, MinSizeSubclassId);
                _attached = false;
            }
        }
    }
}
