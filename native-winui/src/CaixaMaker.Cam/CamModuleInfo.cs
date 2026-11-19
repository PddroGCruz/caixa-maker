using CaixaMaker.Core.Storage;
using SkiaSharp;

namespace CaixaMaker.Cam;

/// <summary>
/// Descritor do módulo CAM/Vetorial nativo (preparado com SkiaSharp para as próximas etapas).
/// </summary>
public static class CamModuleInfo
{
    public static string EngineName => "CaixaMaker.Cam (SkiaSharp)";

    public static string CoreVersion => AppVersion.Version;

    public static bool VerifyGraphicsBackend()
    {
        using SKPath probePath = new();
        probePath.MoveTo(0, 0);
        probePath.LineTo(10, 0);
        probePath.LineTo(10, 10);
        probePath.Close();
        return probePath.Bounds.Width > 0 && probePath.Bounds.Height > 0;
    }
}
