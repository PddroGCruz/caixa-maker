using CaixaMaker.App.Helpers;
using CaixaMaker.App.Rendering;
using CaixaMaker.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace CaixaMaker.App.Controls;

/// <summary>
/// Splash Screen minimalista e editorial do Caixa Maker (680 × 380 px lógicos),
/// centrada na identidade oficial da marca com pré-aquecimento assíncrono real.
/// </summary>
public sealed partial class SplashScreenControl : UserControl
{
    public const int LogicalWidth = 680;
    public const int LogicalHeight = 380;

    private WriteableBitmap? _artworkBitmap;
    private double _currentPercent;
    private bool _artworkRendered;

    public SplashScreenControl()
    {
        InitializeComponent();

        VersionBadgeTextBlock.Text = $"v{AppVersion.Version}";

        ProgressTrackBorder.SizeChanged += (_, _) => UpdateProgressFillWidth();
        Loaded += (_, _) => EnsureHeroArtworkRendered();
        SetProgress(0.0, "Inicializando plataforma CAD/CAM...");
    }

    public double CurrentPercent => _currentPercent;

    public string CurrentStatusText => StatusStepTextBlock.Text;

    public void EnsureHeroArtworkRendered()
    {
        if (_artworkRendered)
        {
            return;
        }

        _artworkRendered = true;
        using SKBitmap heroBitmap = SplashArtworkRenderer.RenderHeroArtworkBitmap(LogicalWidth * 2, LogicalHeight * 2);
        _artworkBitmap = SkiaBitmapBridge.BlitBitmap(heroBitmap, HeroArtworkImage, _artworkBitmap);
    }

    public void SetProgress(double percent, string statusText)
    {
        double clamped = double.IsFinite(percent) ? Math.Clamp(percent, 0.0, 100.0) : 0.0;
        _currentPercent = clamped;

        StatusStepTextBlock.Text = string.IsNullOrWhiteSpace(statusText)
            ? "Inicializando..."
            : statusText.Trim();
        ProgressPercentTextBlock.Text = $"{Math.Round(clamped):0}%";
        UpdateProgressFillWidth();
    }

    private void UpdateProgressFillWidth()
    {
        double trackWidth = ProgressTrackBorder.ActualWidth > 0 ? ProgressTrackBorder.ActualWidth : LogicalWidth - 2.0;
        ProgressFillBar.Width = Math.Round(trackWidth * (_currentPercent / 100.0), 1);
    }
}
