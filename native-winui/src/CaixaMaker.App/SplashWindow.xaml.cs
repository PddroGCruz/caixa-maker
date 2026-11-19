using CaixaMaker.App.Controls;
using CaixaMaker.App.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace CaixaMaker.App;

/// <summary>
/// Janela sem bordas (680 × 380 px lógicos, centralizada e AlwaysOnTop) exibida durante o
/// pré-aquecimento assíncrono do Caixa Maker na inicialização desktop.
/// </summary>
public sealed partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        ConfigureWindow();
    }

    public SplashScreenControl SplashContent => SplashControl;

    public void UpdateProgress(double percent, string statusText)
    {
        SplashControl.SetProgress(percent, statusText);
    }

    private void ConfigureWindow()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        double dpiScale = NativeWindowHelper.GetDpiScaleFactor(hwnd);
        int physicalW = (int)Math.Round(SplashScreenControl.LogicalWidth * dpiScale);
        int physicalH = (int)Math.Round(SplashScreenControl.LogicalHeight * dpiScale);

        AppWindow.Resize(new SizeInt32(physicalW, physicalH));
        CenterOnDisplayArea(physicalW, physicalH);

        SplashControl.EnsureHeroArtworkRendered();
    }

    private void CenterOnDisplayArea(int widthPx, int heightPx)
    {
        DisplayArea? display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is null)
        {
            return;
        }

        RectInt32 work = display.WorkArea;
        int x = work.X + Math.Max(0, (work.Width - widthPx) / 2);
        int y = work.Y + Math.Max(0, (work.Height - heightPx) / 2);
        AppWindow.Move(new PointInt32(x, y));
    }
}
