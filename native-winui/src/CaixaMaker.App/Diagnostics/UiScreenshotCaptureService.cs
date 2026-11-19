using System.Runtime.InteropServices.WindowsRuntime;
using CaixaMaker.App.Interop;
using CaixaMaker.App.Models;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Views.Dialogs;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Logging;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using CaixaMaker.Presentation.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace CaixaMaker.App.Diagnostics;

/// <summary>
/// Pipeline automático de captura visual real da janela WinUI 3 (1360x860) em cenas determinísticas,
/// usando RenderTargetBitmap (+ composição de popups/ContentDialogs) com fallback para PrintWindow.
/// </summary>
public static class UiScreenshotCaptureService
{
    public static async Task RunAsync(
        MainWindow window,
        MainWindowViewModel vm,
        AppPaths paths,
        RotatingFileLogger logger,
        string outputDirectory)
    {
        try
        {
            string fullOutDir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullOutDir);
            logger.Info("Screenshots", $"Iniciando captura automatizada em '{fullOutDir}'.");

            await Task.Delay(350);
            PrepareWindowForCapture(window);
            await WaitForUiSettledAsync(window, 450);

            var manifest = new UiScreenshotManifest
            {
                GeneratedAt = DateTimeOffset.Now.ToString("O"),
                AppVersion = AppVersion.Version,
                TargetWidth = UiScreenshotManifest.TargetWindowWidth,
                TargetHeight = UiScreenshotManifest.TargetWindowHeight,
            };

            // Seed inicial para o Catálogo (2 projetos salvos + rascunho de autosave visível)
            await SeedCatalogDemoDataAsync(vm);

            // 1. 01-catalogo-dark.png
            vm.Theme.SetPreference(AppThemePreference.Dark);
            await WaitForUiSettledAsync(window, 500);
            manifest.Screenshots.Add(await CaptureSceneAsync(
                window,
                fullOutDir,
                UiScreenshotManifest.DefaultScenes[0],
                logger));

            // 2. 02-modelagem-dark.png (200x150x100 mm, MDF 3.0 mm, cotas 3D ativas)
            vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
            vm.Box.BoxWidth = 200;
            vm.Box.BoxDepth = 150;
            vm.Box.BoxHeight = 100;
            vm.Box.BoxThickness = 3.0;
            vm.Box.ShowDimensions = true;
            vm.SetWorkspaceArea(WorkspaceArea.Modeling);
            vm.SetCenterViewMode(CenterViewMode.Mode3D);
            vm.Theme.SetPreference(AppThemePreference.Dark);
            await WaitForUiSettledAsync(window, 600);
            manifest.Screenshots.Add(await CaptureSceneAsync(
                window,
                fullOutDir,
                UiScreenshotManifest.DefaultScenes[1],
                logger));

            // 3. 03-personalizacao-split-dark.png (3D + 2D, texto "CAIXA MAKER" selecionado na tampa)
            vm.SetWorkspaceArea(WorkspaceArea.Personalization);
            vm.SetCenterViewMode(CenterViewMode.Split);
            vm.Personalization.SelectSurface(SurfaceId.Lid);
            vm.Personalization.ClearAllElements();
            TextPersonalizationElement addedText = vm.Personalization.AddTextElement("CAIXA MAKER");
            vm.Personalization.SelectElement(addedText.Id);
            await WaitForUiSettledAsync(window, 650);
            manifest.Screenshots.Add(await CaptureSceneAsync(
                window,
                fullOutDir,
                UiScreenshotManifest.DefaultScenes[2],
                logger));

            // 4. 04-fabricacao-dark.png (Split Lado a Lado + Mesa CNC Customizada 500x420 + Pente de Kerf expandido)
            vm.SetWorkspaceArea(WorkspaceArea.Fabrication);
            vm.SetCenterViewMode(CenterViewMode.Split);
            vm.SetSplitOrientation(SplitViewportOrientation.HorizontalSideBySide);
            vm.Manufacturing.SelectedLaserBedPreset = LaserBedPreset.Custom;
            vm.Manufacturing.CustomBedWidthMm = 500.0;
            vm.Manufacturing.CustomBedHeightMm = 420.0;
            vm.Theme.SetPreference(AppThemePreference.Dark);
            await WaitForUiSettledAsync(window, 650);
            manifest.Screenshots.Add(await CaptureSceneAsync(
                window,
                fullOutDir,
                UiScreenshotManifest.DefaultScenes[3],
                logger));

            // 5. 05-modelagem-light.png
            vm.SetSplitOrientation(SplitViewportOrientation.VerticalStacked);
            vm.Personalization.ClearAllElements();
            vm.ForceClearDirtyForAutomation();
            vm.SetWorkspaceArea(WorkspaceArea.Modeling);
            vm.SetCenterViewMode(CenterViewMode.Mode3D);
            vm.Theme.SetPreference(AppThemePreference.Light);
            await WaitForUiSettledAsync(window, 600);
            manifest.Screenshots.Add(await CaptureSceneAsync(
                window,
                fullOutDir,
                UiScreenshotManifest.DefaultScenes[4],
                logger));

            // 6. 06-sobre-institucional-dark.png (Modal institucional aba 2: Instituição com 3 identidades)
            vm.Theme.SetPreference(AppThemePreference.Dark);
            await WaitForUiSettledAsync(window, 450);

            manifest.Screenshots.Add(await CaptureAboutDialogSceneAsync(
                window,
                vm,
                paths,
                fullOutDir,
                UiScreenshotManifest.DefaultScenes[5],
                initialTabIndex: 2,
                logger));

            // 7-8. Verificação adicional do modal "Sobre" no Tema Claro (Abas 0 e 2)
            vm.Theme.SetPreference(AppThemePreference.Light);
            await WaitForUiSettledAsync(window, 500);

            UiScreenshotManifestItem lightTab0 = await CaptureAboutDialogSceneAsync(
                window,
                vm,
                paths,
                fullOutDir,
                new UiScreenshotSceneSpec(
                    "sobre-light",
                    "07-sobre-light.png",
                    "Modal Sobre no Tema Claro (Aba 0 - Visão Geral)"),
                initialTabIndex: 0,
                logger);

            UiScreenshotManifestItem lightTab2 = await CaptureAboutDialogSceneAsync(
                window,
                vm,
                paths,
                fullOutDir,
                new UiScreenshotSceneSpec(
                    "sobre-institucional-light",
                    "08-sobre-institucional-light.png",
                    "Modal Sobre no Tema Claro (Aba 2 - Instituição e Identidades)"),
                initialTabIndex: 2,
                logger);

            vm.Theme.SetPreference(AppThemePreference.Dark);

            // 9. Splash Screen v2.0.1 (740x440, pré-aquecimento a 68%)
            UiScreenshotManifestItem splashItem = await CaptureSplashScreenSceneAsync(
                window,
                fullOutDir,
                logger);

            manifest.AllValid = manifest.Screenshots.Count == UiScreenshotManifest.DefaultScenes.Count
                && manifest.Screenshots.All(s => s.IsNonBlank && s.Width == UiScreenshotManifest.TargetWindowWidth && s.Height == UiScreenshotManifest.TargetWindowHeight)
                && lightTab0.IsNonBlank
                && lightTab2.IsNonBlank
                && splashItem.IsNonBlank
                && splashItem.Width == Controls.SplashScreenControl.LogicalWidth
                && splashItem.Height == Controls.SplashScreenControl.LogicalHeight;

            string manifestPath = Path.Combine(fullOutDir, UiScreenshotManifest.ManifestFileName);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJson());
            logger.Info("Screenshots", $"Manifesto gravado em '{manifestPath}'. AllValid={manifest.AllValid}");

            if (!manifest.AllValid)
            {
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            logger.Error("Screenshots", $"Falha na captura automatizada de telas: {ex.Message}", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            window.DispatcherQueue.TryEnqueue(() => window.ForceCloseForSmokeTest());
        }
    }

    private static async Task<UiScreenshotManifestItem> CaptureAboutDialogSceneAsync(
        MainWindow window,
        MainWindowViewModel vm,
        AppPaths paths,
        string outputDir,
        UiScreenshotSceneSpec scene,
        int initialTabIndex,
        RotatingFileLogger logger)
    {
        var dialogOpenedTcs = new TaskCompletionSource<ContentDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task dialogTask = AppDialogs.ShowAboutDialogAsync(
            window.RootElement.XamlRoot,
            vm,
            paths,
            initialTabIndex: initialTabIndex,
            onOpened: dlg => dialogOpenedTcs.TrySetResult(dlg));

        ContentDialog? openDialog = null;
        try
        {
            Task completed = await Task.WhenAny(dialogOpenedTcs.Task, Task.Delay(4000));
            if (completed == dialogOpenedTcs.Task)
            {
                openDialog = await dialogOpenedTcs.Task;
            }

            await WaitForUiSettledAsync(window, 500);
            return await CaptureSceneAsync(
                window,
                outputDir,
                scene,
                logger);
        }
        finally
        {
            openDialog?.Hide();
            await Task.WhenAny(dialogTask, Task.Delay(1500));
        }
    }

    private static async Task<UiScreenshotManifestItem> CaptureSplashScreenSceneAsync(
        MainWindow window,
        string outputDir,
        RotatingFileLogger logger)
    {
        var splashControl = new Controls.SplashScreenControl();
        splashControl.EnsureHeroArtworkRendered();
        splashControl.SetProgress(68.0, "Aquecendo pipeline SkiaSharp e renderizando miniaturas 3D...");

        var popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup
        {
            XamlRoot = window.RootElement.XamlRoot,
            Child = splashControl,
            HorizontalOffset = Math.Max(0, (UiScreenshotManifest.TargetWindowWidth - Controls.SplashScreenControl.LogicalWidth) / 2.0),
            VerticalOffset = Math.Max(0, (UiScreenshotManifest.TargetWindowHeight - Controls.SplashScreenControl.LogicalHeight) / 2.0),
            IsOpen = true,
        };

        try
        {
            // Aguarda layout e decodificação assíncrona das logomarcas PNG ms-appx:///Assets/
            splashControl.UpdateLayout();
            await Task.Delay(550);
            splashControl.UpdateLayout();

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(splashControl);
            IBuffer pixelBuffer = await rtb.GetPixelsAsync();
            byte[] rawBgra = pixelBuffer.ToArray();
            int rawW = Math.Max(1, rtb.PixelWidth);
            int rawH = Math.Max(1, rtb.PixelHeight);

            UiScreenshotFrameStats stats = UiScreenshotManifest.EvaluateBgraFrameContent(rawBgra, rawW, rawH);

            const string fileName = "09-splash-screen-dark.png";
            string filePath = Path.Combine(outputDir, fileName);
            using SKBitmap finalBitmap = NormalizeToTargetResolution(
                rawBgra,
                rawW,
                rawH,
                Controls.SplashScreenControl.LogicalWidth,
                Controls.SplashScreenControl.LogicalHeight);

            using (SKData encoded = finalBitmap.Encode(SKEncodedImageFormat.Png, 100))
            using (FileStream fs = File.Create(filePath))
            {
                encoded.SaveTo(fs);
            }

            long byteSize = new FileInfo(filePath).Length;
            logger.Info(
                "Screenshots",
                $"Cena '{fileName}' capturada via RenderTargetBitmap: {finalBitmap.Width}x{finalBitmap.Height}, {byteSize} bytes, NonBlank={stats.IsNonBlank}, Lum=[{stats.MinLuminance}..{stats.MaxLuminance}]");

            return new UiScreenshotManifestItem
            {
                SceneId = "splash-screen-dark",
                FileName = fileName,
                Description = $"Splash Screen nativa ({Controls.SplashScreenControl.LogicalWidth}x{Controls.SplashScreenControl.LogicalHeight}) no tema escuro minimalista com progresso de pré-aquecimento em 68%.",
                Width = finalBitmap.Width,
                Height = finalBitmap.Height,
                ByteSize = byteSize,
                IsNonBlank = stats.IsNonBlank,
                CaptureMethod = "RenderTargetBitmap",
                LuminanceMin = stats.MinLuminance,
                LuminanceMax = stats.MaxLuminance,
            };
        }
        finally
        {
            popup.IsOpen = false;
        }
    }

    private static async Task SeedCatalogDemoDataAsync(MainWindowViewModel vm)
    {
        if (vm.SavedProjects.Count == 0)
        {
            vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
            vm.Box.BoxWidth = 200;
            vm.Box.BoxDepth = 150;
            vm.Box.BoxHeight = 100;
            vm.Box.BoxThickness = 3.0;
            vm.SaveProjectAs("Organizador Modular Juruá");

            vm.StartNewProjectFromCatalog(OpenBoxModel.Id);
            vm.Box.BoxWidth = 160;
            vm.Box.BoxDepth = 110;
            vm.Box.BoxHeight = 65;
            vm.Box.BoxThickness = 3.0;
            vm.SaveProjectAs("Estojo Didático IFAC");
        }

        // Cria um rascunho de autosave ativo antes de voltar para a Central de Trabalho
        vm.StartNewProjectFromCatalog(OpenBoxModel.Id);
        vm.Box.BoxWidth = 180;
        vm.Box.BoxDepth = 130;
        vm.Box.BoxHeight = 85;
        vm.PersistDraftIfEnabled();
        vm.ForceClearDirtyForAutomation();
        await vm.ReturnToCatalogAsync();
    }

    private static void PrepareWindowForCapture(MainWindow window)
    {
        nint hwnd = WindowNative.GetWindowHandle(window);
        NativeWindowHelper.BringWindowToFront(hwnd);

        double scale = NativeWindowHelper.GetDpiScaleFactor(hwnd);
        int targetOuterW = (int)Math.Round(UiScreenshotManifest.TargetWindowWidth * scale);
        int targetOuterH = (int)Math.Round(UiScreenshotManifest.TargetWindowHeight * scale);
        window.AppWindow.Resize(new SizeInt32(targetOuterW, targetOuterH));

        // Ajusta para que a moldura visível DWM fique o mais próxima possível de 1360x860 * scale
        if (NativeWindowHelper.TryGetVisibleFrameBounds(hwnd, out NativeWindowHelper.Rect frame))
        {
            int diffW = targetOuterW - frame.Width;
            int diffH = targetOuterH - frame.Height;
            if (Math.Abs(diffW) > 0 && Math.Abs(diffW) <= 40 && Math.Abs(diffH) > 0 && Math.Abs(diffH) <= 40)
            {
                window.AppWindow.Resize(new SizeInt32(targetOuterW + diffW, targetOuterH + diffH));
            }
        }
    }

    private static async Task WaitForUiSettledAsync(MainWindow window, int delayMs)
    {
        window.RootElement.UpdateLayout();
        await Task.Delay(delayMs);
        window.RootElement.UpdateLayout();
        await Task.Delay(80);
    }

    private static async Task<UiScreenshotManifestItem> CaptureSceneAsync(
        MainWindow window,
        string outputDir,
        UiScreenshotSceneSpec scene,
        RotatingFileLogger logger)
    {
        nint hwnd = WindowNative.GetWindowHandle(window);
        NativeWindowHelper.BringWindowToFront(hwnd);

        int rawW = 0;
        int rawH = 0;
        byte[]? rawBgra = null;
        string method = "RenderTargetBitmap";
        UiScreenshotFrameStats stats = new(false, 0, 0, 0.0);

        try
        {
            (int rtbW, int rtbH, byte[] rtbBytes) = await CaptureVisualTreeFallbackAsync(window.RootElement);
            UiScreenshotFrameStats rtbStats = UiScreenshotManifest.EvaluateBgraFrameContent(rtbBytes, rtbW, rtbH);
            if (rtbStats.IsNonBlank)
            {
                rawW = rtbW;
                rawH = rtbH;
                rawBgra = rtbBytes;
                stats = rtbStats;
            }
        }
        catch (Exception ex)
        {
            logger.Warn("Screenshots", $"RenderTargetBitmap falhou em '{scene.FileName}', tentando PrintWindow: {ex.Message}");
        }

        if (rawBgra is null && NativeWindowHelper.TryCaptureWindowBgra(hwnd, out int pwW, out int pwH, out byte[] pwBytes))
        {
            method = "PrintWindow";
            rawW = pwW;
            rawH = pwH;
            rawBgra = pwBytes;
            stats = UiScreenshotManifest.EvaluateBgraFrameContent(rawBgra, rawW, rawH);
        }

        rawBgra ??= [];

        string filePath = Path.Combine(outputDir, scene.FileName);
        using SKBitmap finalBitmap = NormalizeToTargetResolution(
            rawBgra,
            rawW,
            rawH,
            UiScreenshotManifest.TargetWindowWidth,
            UiScreenshotManifest.TargetWindowHeight);

        using (SKData encoded = finalBitmap.Encode(SKEncodedImageFormat.Png, 100))
        using (FileStream fs = File.Create(filePath))
        {
            encoded.SaveTo(fs);
        }

        long byteSize = new FileInfo(filePath).Length;
        logger.Info(
            "Screenshots",
            $"Cena '{scene.FileName}' capturada via {method}: {finalBitmap.Width}x{finalBitmap.Height}, {byteSize} bytes, NonBlank={stats.IsNonBlank}, Lum=[{stats.MinLuminance}..{stats.MaxLuminance}]");

        return new UiScreenshotManifestItem
        {
            SceneId = scene.SceneId,
            FileName = scene.FileName,
            Description = scene.Description,
            Width = finalBitmap.Width,
            Height = finalBitmap.Height,
            ByteSize = byteSize,
            IsNonBlank = stats.IsNonBlank,
            CaptureMethod = method,
            LuminanceMin = stats.MinLuminance,
            LuminanceMax = stats.MaxLuminance,
        };
    }

    private static async Task<(int Width, int Height, byte[] Bgra)> CaptureVisualTreeFallbackAsync(FrameworkElement root)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(root);
        IBuffer pixelBuffer = await rtb.GetPixelsAsync();
        byte[] bgra = pixelBuffer.ToArray();
        int width = Math.Max(1, rtb.PixelWidth);
        int height = Math.Max(1, rtb.PixelHeight);

        // Se houver popups/ContentDialogs abertos na XamlRoot, compõe por cima da cena base
        if (root.XamlRoot is not null)
        {
            using SKBitmap baseBmp = CreateSkBitmapFromBgra(bgra, width, height);
            using var canvas = new SKCanvas(baseBmp);

            foreach (Microsoft.UI.Xaml.Controls.Primitives.Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
            {
                if (popup.Child is not FrameworkElement popupChild || popupChild.ActualWidth <= 0 || popupChild.ActualHeight <= 0)
                {
                    continue;
                }

                var popupRtb = new RenderTargetBitmap();
                await popupRtb.RenderAsync(popupChild);
                IBuffer popupBuf = await popupRtb.GetPixelsAsync();
                using SKBitmap popupBmp = CreateSkBitmapFromBgra(popupBuf.ToArray(), popupRtb.PixelWidth, popupRtb.PixelHeight);

                float scaleX = width / (float)Math.Max(1.0, root.ActualWidth);
                float scaleY = height / (float)Math.Max(1.0, root.ActualHeight);
                float dstX = (float)popup.HorizontalOffset * scaleX;
                float dstY = (float)popup.VerticalOffset * scaleY;
                if (dstX == 0 && dstY == 0 && popupBmp.Width < width)
                {
                    dstX = (width - popupBmp.Width) / 2f;
                    dstY = (height - popupBmp.Height) / 2f;
                }

                canvas.DrawBitmap(popupBmp, dstX, dstY);
            }

            return (baseBmp.Width, baseBmp.Height, baseBmp.Bytes);
        }

        return (width, height, bgra);
    }

    private static SKBitmap CreateSkBitmapFromBgra(byte[] bgra, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), Math.Min(bgra.Length, bmp.ByteCount));
        return bmp;
    }

    private static SKBitmap NormalizeToTargetResolution(
        byte[] bgra,
        int srcWidth,
        int srcHeight,
        int targetWidth,
        int targetHeight)
    {
        using SKBitmap source = CreateSkBitmapFromBgra(bgra, srcWidth, srcHeight);
        if (srcWidth == targetWidth && srcHeight == targetHeight)
        {
            return source.Copy();
        }

        var targetInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        var resized = source.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.Mitchell));
        return resized ?? source.Copy();
    }
}
