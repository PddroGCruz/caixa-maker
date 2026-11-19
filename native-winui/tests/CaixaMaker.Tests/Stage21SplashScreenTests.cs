using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.Core.Storage;
using CaixaMaker.Presentation.Diagnostics;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage21SplashScreenTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;

    public Stage21SplashScreenTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"cm_stage21_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _paths = new AppPaths(new AppPathsOptions
        {
            DesktopMode = false,
            ProjectsDir = Path.Combine(_tempRoot, "projects"),
            OutputsDir = Path.Combine(_tempRoot, "outputs"),
            ConfigDir = Path.Combine(_tempRoot, "config"),
            LogsDir = Path.Combine(_tempRoot, "logs"),
            TempDir = Path.Combine(_tempRoot, "temp"),
        });
        _paths.EnsureDirs();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignora falha transitória na limpeza do diretório temporário.
        }
    }

    private static string ResolveRepoRoot()
    {
        string? versionFile = AppVersion.ResolveVersionFile(AppContext.BaseDirectory);
        versionFile.Should().NotBeNull();
        return Path.GetDirectoryName(versionFile!)!;
    }

    [Fact]
    public void SplashArtworkRenderer_ProducesMinimalistSlateBackdropWithSubtleWatermark()
    {
        using SKBitmap bitmap = SplashArtworkRenderer.RenderHeroArtworkBitmap(1360, 760);
        bitmap.Width.Should().Be(1360);
        bitmap.Height.Should().Be(760);

        UiScreenshotFrameStats stats = UiScreenshotManifest.EvaluateBgraFrameContent(
            bitmap.GetPixelSpan(),
            bitmap.Width,
            bitmap.Height);
        stats.MaxLuminance.Should().BeGreaterThan(stats.MinLuminance, "o gradiente grafite-ardósia e o glow difuso devem variar suavemente");
        stats.MinLuminance.Should().BeGreaterThanOrEqualTo(8);
        stats.MaxLuminance.Should().BeLessThan(40, "o fundo minimalista em grafite-ardósia não deve conter gráficos chamativos ou caixas 3D iluminadas");

        // Amostra o gradiente diagonal (#0B0E14 -> #111620) e o glow verde suave (#16A34A14) atrás do logo
        SKColor topLeft = bitmap.GetPixel(8, 8);
        SKColor leftGlowCenter = bitmap.GetPixel((int)(bitmap.Width * 0.16), (int)(bitmap.Height * 0.43));
        SKColor bottomRight = bitmap.GetPixel(bitmap.Width - 8, bitmap.Height - 8);
        ((int)topLeft.Red).Should().BeLessThanOrEqualTo(bottomRight.Red + 2);
        ((int)leftGlowCenter.Green).Should().BeGreaterThan(topLeft.Green);
        bottomRight.Blue.Should().BeGreaterThan(bottomRight.Red);

        int brightAccentPixels = 0;
        for (int y = 0; y < bitmap.Height; y += 4)
        {
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                SKColor c = bitmap.GetPixel(x, y);
                if (c.Red > 60 || c.Green > 65 || c.Blue > 65)
                {
                    brightAccentPixels++;
                }
            }
        }

        brightAccentPixels.Should().Be(0, "o canvas minimalista não deve renderizar feixes de laser, caixas 3D nem textos flutuantes");

        byte[] png = SplashArtworkRenderer.RenderHeroArtworkPng(1360, 760);
        png.Length.Should().BeGreaterThan(1_000);
        png.Take(4).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [Fact]
    public async Task StartupWarmupPipeline_RunsFiveRealStagesMonotonicallyToCompletion()
    {
        var observed = new List<SplashWarmupProgress>();
        bool mainWindowActivated = false;

        StartupWarmupResult result = await StartupWarmupPipeline.RunAsync(
            _paths,
            observed.Add,
            activateMainWindowAsync: () =>
            {
                mainWindowActivated = true;
                return Task.CompletedTask;
            },
            stageMinimumDuration: TimeSpan.Zero);

        result.CamBackendReady.Should().BeTrue();
        result.PrewarmedPreviewCount.Should().BeGreaterThanOrEqualTo(4);
        mainWindowActivated.Should().BeTrue();

        observed.Should().HaveCountGreaterThanOrEqualTo(StartupWarmupPipeline.TotalStageCount + 1);
        observed.First().Percent.Should().BeLessThan(20.0);
        observed.Last().Percent.Should().Be(100.0);

        for (int i = 1; i < observed.Count; i++)
        {
            observed[i].Percent.Should().BeGreaterThanOrEqualTo(observed[i - 1].Percent);
        }

        observed.Select(x => x.Percent).Should().Contain([20.0, 45.0, 75.0, 92.0, 100.0]);
    }

    [Fact]
    public void SplashScreenXamlWindowAndAssets_MatchV201MinimalistIdentityContracts()
    {
        string repoRoot = ResolveRepoRoot();
        string appDir = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App");
        string assetsDir = Path.Combine(appDir, "Assets");

        File.Exists(Path.Combine(assetsDir, "caixa-maker-symbol.png")).Should().BeTrue();

        string splashXaml = File.ReadAllText(Path.Combine(appDir, "Controls", "SplashScreenControl.xaml"));
        splashXaml.Should().Contain("Width=\"680\"");
        splashXaml.Should().Contain("Height=\"380\"");
        splashXaml.Should().Contain("Background=\"#0B0E14\"");
        splashXaml.Should().Contain("ms-appx:///Assets/caixa-maker-symbol.png");
        splashXaml.Should().Contain("Width=\"92\"");
        splashXaml.Should().Contain("Height=\"92\"");
        splashXaml.Should().Contain("CornerRadius=\"18\"");
        splashXaml.Should().Contain("Text=\"CAIXA\"");
        splashXaml.Should().Contain("Text=\"MAKER\"");
        splashXaml.Should().Contain("Text=\"v2.4.0\"");
        splashXaml.Should().Contain("Modelagem Paramétrica e Fabricação Digital");
        splashXaml.Should().Contain("Laboratório IFMaker do Juruá • IFAC Cruzeiro do Sul  |  Pedro G. Cruz (PddroGCruz)");
        splashXaml.Should().Contain("Height=\"3\"");
        splashXaml.Should().Contain("Background=\"#22C55E\"");

        // Garante remoção integral da poluição visual antiga (especificações HUD, cabeçalho duplicado e blocos brancos)
        splashXaml.Should().NotContain("ENGINE: .NET 8");
        splashXaml.Should().NotContain("PROJETO PIBIT");
        splashXaml.Should().NotContain("ifmaker-jurua.png");
        splashXaml.Should().NotContain("ifac-horizontal.png");
        splashXaml.Should().NotContain("pddrogcruz");

        string splashCs = File.ReadAllText(Path.Combine(appDir, "Controls", "SplashScreenControl.xaml.cs"));
        splashCs.Should().Contain("LogicalWidth = 680");
        splashCs.Should().Contain("LogicalHeight = 380");
        splashCs.Should().Contain("CurrentPercent");
        splashCs.Should().Contain("CurrentStatusText");
        splashCs.Should().Contain("EnsureHeroArtworkRendered()");

        string rendererCs = File.ReadAllText(Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.Presentation", "Rendering", "SplashArtworkRenderer.cs"));
        rendererCs.Should().NotContain("LASER CUT");
        rendererCs.Should().NotContain("KERF COMP");
        rendererCs.Should().NotContain("SAT COLLISION");
        rendererCs.Should().NotContain("BoxScene3DBuilder");

        string splashWindowCs = File.ReadAllText(Path.Combine(appDir, "SplashWindow.xaml.cs"));
        splashWindowCs.Should().Contain("SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false)");
        splashWindowCs.Should().Contain("IsAlwaysOnTop = true");
        splashWindowCs.Should().Contain("DisplayArea.GetFromWindowId");
        splashWindowCs.Should().Contain("GetDpiScaleFactor");

        string appXamlCs = File.ReadAllText(Path.Combine(appDir, "App.xaml.cs"));
        appXamlCs.Should().Contain("new SplashWindow()");
        appXamlCs.Should().Contain("StartupWarmupPipeline.RunAsync");

        string captureCs = File.ReadAllText(Path.Combine(appDir, "Diagnostics", "UiScreenshotCaptureService.cs"));
        captureCs.Should().Contain("09-splash-screen-dark.png");
    }
}
