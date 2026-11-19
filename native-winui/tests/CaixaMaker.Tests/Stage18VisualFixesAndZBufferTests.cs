using System.Numerics;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage18VisualFixesAndZBufferTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public Stage18VisualFixesAndZBufferTests()
    {
        _paths = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateTempPaths(out _sandboxRoot);
    }

    public void Dispose()
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.SafeDeleteDirectory(_sandboxRoot);

    [Fact]
    public void ZBufferRenderer_ShouldOccludeBackSurfaceEngravingsBehindOpaqueFrontPanels()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        // OpenBoxModel não possui recortes de alça na chapa frontal, logo a parede traseira fica 100% ocluída em vista frontal
        vm.StartNewProjectFromCatalog(OpenBoxModel.Id);

        BoxScene3D sceneWithoutBackEngraving = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 0.0,
            isExplodedView: false);

        var frontCamera = new Camera3DController();
        frontCamera.ApplyPreset(CameraPreset.Front);

        var frontOptions = new RenderScene3DOptions
        {
            WidthPx = 560,
            HeightPx = 400,
            DarkTheme = true,
            ShowDimensions = false,
            SupersampleScale = 1.0f,
        };

        using SKBitmap baselineFront = OffscreenViewportRenderer.RenderScene3DBitmap(
            sceneWithoutBackEngraving,
            frontCamera,
            vm.Personalization.Manager,
            frontOptions);

        var backCamera = new Camera3DController();
        backCamera.ApplyPreset(CameraPreset.Back);
        using SKBitmap baselineBack = OffscreenViewportRenderer.RenderScene3DBitmap(
            sceneWithoutBackEngraving,
            backCamera,
            vm.Personalization.Manager,
            frontOptions);

        // Adiciona gravação escura grande na chapa traseira (Back)
        vm.Personalization.ActiveSurfaceId = SurfaceId.Back;
        TextPersonalizationElement backText = vm.Personalization.AddTextElement("OCULTO");
        vm.Personalization.SelectElement(backText.Id);
        vm.Personalization.SelectedTextFontSizeMm = 26.0;
        vm.Personalization.SelectedTextRenderMode = TextRenderMode.Hatch;

        BoxScene3D sceneWithBackEngraving = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 0.0,
            isExplodedView: false);

        using SKBitmap occludedFront = OffscreenViewportRenderer.RenderScene3DBitmap(
            sceneWithBackEngraving,
            frontCamera,
            vm.Personalization.Manager,
            frontOptions);

        // Na vista frontal, a chapa frontal mais próxima precisa ocluir 100% a gravação traseira
        occludedFront.Bytes.Should().Equal(baselineFront.Bytes);

        // Ao girar para trás (Back), a mesma gravação deve ficar claramente visível
        using SKBitmap visibleBack = OffscreenViewportRenderer.RenderScene3DBitmap(
            sceneWithBackEngraving,
            backCamera,
            vm.Personalization.Manager,
            frontOptions);

        int changedBackPixels = CountChangedPixels(baselineBack, visibleBack);
        changedBackPixels.Should().BeGreaterThan(150);
    }

    [Theory]
    [InlineData(0.0, 35.0)]
    [InlineData(25.0, 135.0)]
    [InlineData(55.0, -45.0)]
    [InlineData(0.0, 215.0)]
    public void ZBufferRenderer_ShouldRenderInterlockingFingerJointsCrisplyAcrossCameraAngles(
        double lidAngleDeg,
        double yawDeg)
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        BoxScene3D scene = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: lidAngleDeg,
            isExplodedView: false);

        vm.Box.CameraYawDeg = yawDeg;
        vm.Box.CameraPitchDeg = 26.0;
        var camera = new Camera3DController();
        camera.SyncFromViewModel(vm.Box);

        using SKBitmap bmp1x = OffscreenViewportRenderer.RenderScene3DBitmap(
            scene,
            camera,
            vm.Personalization.Manager,
            new RenderScene3DOptions
            {
                WidthPx = 640,
                HeightPx = 480,
                DarkTheme = true,
                ShowDimensions = false,
                SupersampleScale = 1.0f,
            });

        using SKBitmap bmp2x = OffscreenViewportRenderer.RenderScene3DBitmap(
            scene,
            camera,
            vm.Personalization.Manager,
            new RenderScene3DOptions
            {
                WidthPx = 640,
                HeightPx = 480,
                DarkTheme = true,
                ShowDimensions = false,
                SupersampleScale = 2.0f,
            });

        bmp1x.Width.Should().Be(640);
        bmp1x.Height.Should().Be(480);
        bmp2x.Width.Should().Be(640);
        bmp2x.Height.Should().Be(480);

        // O renderizador com SSAA 2x deve produzir pixels distintos (bordas suavizadas) e cena rica em tons de madeira
        CountDistinctColors(bmp2x).Should().BeGreaterThan(25);
        bmp1x.Bytes.Should().NotEqual(bmp2x.Bytes);

        // Ponto central da parede mais próxima à câmera deve conter madeira iluminada (não fundo escuro vazio)
        CameraFrame frame = camera.BuildFrame(scene, 640, 480);
        Vector3 midPoint = MathF.Abs(frame.Eye.X) > MathF.Abs(frame.Eye.Z)
            ? new Vector3(MathF.Sign(frame.Eye.X) * (float)scene.WidthMm * 0.48f, (float)scene.HeightMm * 0.42f, 0f)
            : new Vector3(0f, (float)scene.HeightMm * 0.42f, MathF.Sign(frame.Eye.Z) * (float)scene.DepthMm * 0.48f);
        frame.TryProject(midPoint, out Vector2 midScreen, out _).Should().BeTrue();
        int px = Math.Clamp((int)MathF.Round(midScreen.X), 0, bmp2x.Width - 1);
        int py = Math.Clamp((int)MathF.Round(midScreen.Y), 0, bmp2x.Height - 1);
        SKColor sample = bmp2x.GetPixel(px, py);
        Math.Max(sample.Red, Math.Max(sample.Green, sample.Blue)).Should().BeGreaterThan(75);
    }

    [Fact]
    public void DialogThemePalette_LightAndDark_ShouldGuaranteeWcagContrastAndExplicitColors()
    {
        DialogThemePalette light = DialogThemePalette.ForTheme(ResolvedAppTheme.Light);
        DialogThemePalette dark = DialogThemePalette.ForTheme(ResolvedAppTheme.Dark);

        light.IsDark.Should().BeFalse();
        dark.IsDark.Should().BeTrue();

        // Valores exatos esperados para o Tema Claro
        light.DialogSurfaceArgb.Should().Be(0xFFF4F6F9u);
        light.CardSurfaceArgb.Should().Be(0xFFFFFFFFu);
        light.HeaderStripArgb.Should().Be(0xFFEAEFF5u);
        light.TextPrimaryArgb.Should().Be(0xFF111827u);
        light.TextSecondaryArgb.Should().Be(0xFF4B5563u);
        light.KbdBgArgb.Should().Be(0xFFE2E8F0u);
        light.KbdTextArgb.Should().Be(0xFF0F172Au);

        foreach (DialogThemePalette palette in new[] { light, dark })
        {
            uint[] surfaces =
            [
                palette.DialogSurfaceArgb,
                palette.CardSurfaceArgb,
                palette.HeaderStripArgb,
                palette.RowEvenArgb,
                palette.RowAltArgb,
                palette.ButtonSurfaceArgb,
            ];

            foreach (uint bg in surfaces)
            {
                DialogThemePalette.ContrastRatio(palette.TextPrimaryArgb, bg).Should().BeGreaterThanOrEqualTo(4.5);
                DialogThemePalette.ContrastRatio(palette.TextSecondaryArgb, bg).Should().BeGreaterThanOrEqualTo(4.5);
            }

            DialogThemePalette.ContrastRatio(palette.KbdTextArgb, palette.KbdBgArgb).Should().BeGreaterThanOrEqualTo(4.5);
            DialogThemePalette.RelativeLuminance(palette.BrandPlaqueBgArgb).Should().BeGreaterThan(0.85);
        }
    }

    [Fact]
    public void CatalogAndModelingSummaries_ShouldNotExposeInternalCodeIds()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        string[] forbiddenTokens = ["box_with_lid_and_handles", "open_box", "com_tampa", "ID:"];

        foreach (string modelId in new[] { BoxWithLidAndHandlesModel.Id, OpenBoxModel.Id })
        {
            vm.StartNewProjectFromCatalog(modelId);
            string summary = vm.Box.StructuralModelSummaryDisplay;
            foreach (string token in forbiddenTokens)
            {
                summary.ToLowerInvariant().Should().NotContain(token.ToLowerInvariant());
            }

            summary.Should().Contain("chapas paramétricas");
        }

        string repoRoot = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.ResolveRepoRoot();
        string catalogCs = File.ReadAllText(Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App", "Views", "CatalogHomeView.xaml.cs"));
        string leftPanelXaml = File.ReadAllText(Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App", "Views", "Areas", "ModelingAreaLeftPanel.xaml"));
        string leftPanelCs = File.ReadAllText(Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App", "Views", "Areas", "ModelingAreaLeftPanel.xaml.cs"));
        string dialogsCs = File.ReadAllText(Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App", "Views", "Dialogs", "AppDialogs.cs"));

        catalogCs.Should().NotContain("Text = model.Id");
        leftPanelXaml.Should().NotContain("com_tampa");
        leftPanelCs.Should().NotContain("ID: {_vm.SelectedModelId}");
        dialogsCs.Should().NotContain("SelectedModelId");
    }

    [Fact]
    public async Task KerfCalibrationComb_SaveAsFlow_ShouldHonorUserPathCancelAndFormatSelection()
    {
        var dialogMock = new NullNativeFileDialogService();
        var vm = new MainWindowViewModel(_paths, dialogMock, BoxModelRegistry.Default, ResolvedAppTheme.Dark);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.Box.BoxThickness = 3.0;

        string suggestedSvg = ManufacturingWorkspaceViewModel.BuildSuggestedKerfFileName(3.0, VectorExportFormat.Svg);
        suggestedSvg.Should().Be("pente-kerf-mdf-3.0mm.svg");
        Path.GetFileNameWithoutExtension(suggestedSvg).Should().Be("pente-kerf-mdf-3.0mm");

        // 1. Cancelamento não grava nenhum arquivo e atualiza status
        dialogMock.OnPickSaveFile = (_, _, _) => null;
        string? cancelledResult = await vm.ExportKerfCalibrationCombWithDialogAsync("svg");
        cancelledResult.Should().BeNull();
        vm.Manufacturing.LastExportStatus.Should().Be("Exportação do pente cancelada.");
        vm.StatusMessage.Should().Be("Exportação do pente cancelada.");
        vm.Manufacturing.RecentOutputs.Should().BeEmpty();
        Directory.GetFiles(_paths.OutputsDir).Should().BeEmpty();

        // 2. Confirmação SVG / DXF / PDF em diretório escolhido pelo usuário
        string customDir = Path.Combine(_sandboxRoot, "escolha-usuario");
        Directory.CreateDirectory(customDir);

        (string Req, string Target, string ExpectedMagic)[] cases =
        [
            ("svg", Path.Combine(customDir, "meu-pente-3mm.svg"), "<svg"),
            ("dxf", Path.Combine(customDir, "meu-pente-3mm.dxf"), "SECTION"),
            ("pdf", Path.Combine(customDir, "meu-pente-3mm.pdf"), "%PDF-1."),
            // Mesmo clicando SVG, usuário escolheu filtro DXF no FileSavePicker
            ("svg", Path.Combine(customDir, "trocado-no-picker.dxf"), "SECTION"),
        ];

        foreach ((string req, string target, string expectedMagic) in cases)
        {
            string? capturedTitle = null;
            string? capturedSuggested = null;
            IReadOnlyList<NativeFileFilter>? capturedFilters = null;

            dialogMock.OnPickSaveFile = (title, suggested, filters) =>
            {
                capturedTitle = title;
                capturedSuggested = suggested;
                capturedFilters = filters;
                return target;
            };

            string? saved = await vm.ExportKerfCalibrationCombWithDialogAsync(req);
            saved.Should().Be(Path.GetFullPath(target));
            capturedTitle.Should().Contain("Pente");
            capturedSuggested.Should().Be($"pente-kerf-mdf-3.0mm.{req}");
            capturedFilters.Should().NotBeNull();
            capturedFilters!.Should().HaveCount(3);
            capturedFilters[0].Extensions.Should().Contain(req);

            File.Exists(target).Should().BeTrue();
            string header = File.ReadAllText(target);
            header.Should().Contain(expectedMagic);
        }

        // Nenhum pente foi despejado silenciosamente em OutputsDir padrão
        Directory.GetFiles(_paths.OutputsDir).Should().BeEmpty();
        vm.Manufacturing.RecentOutputs.Should().HaveCount(4);
    }

    [Fact]
    public void ZBufferRenderer_ShouldProduceIdenticalOutputOnStaAndMtaThreads()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.Personalization.ActiveSurfaceId = SurfaceId.Front;
        vm.Personalization.AddTextElement("IFMAKER");

        BoxScene3D scene = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 0.0,
            isExplodedView: false);

        var camera = new Camera3DController();
        camera.ApplyPreset(CameraPreset.Isometric);

        var options = new RenderScene3DOptions
        {
            WidthPx = 480,
            HeightPx = 320,
            DarkTheme = true,
            ShowDimensions = false,
            SupersampleScale = 2.0f,
        };

        byte[] RunOnApartment(ApartmentState state)
        {
            byte[]? captured = null;
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    using SKBitmap bmp = OffscreenViewportRenderer.RenderScene3DBitmap(
                        scene,
                        camera,
                        vm.Personalization.Manager,
                        options);
                    captured = bmp.Bytes;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            if (OperatingSystem.IsWindows())
            {
                worker.SetApartmentState(state);
            }

            worker.Start();
            worker.Join();

            failure.Should().BeNull();
            return captured!;
        }

        byte[] mtaBytes = RunOnApartment(ApartmentState.MTA);
        byte[] staBytes = RunOnApartment(ApartmentState.STA);

        staBytes.Should().NotBeEmpty();
        staBytes.Should().Equal(mtaBytes);
    }

    private static int CountChangedPixels(SKBitmap a, SKBitmap b)
    {
        int count = 0;
        byte[] ba = a.Bytes;
        byte[] bb = b.Bytes;
        for (int i = 0; i + 3 < Math.Min(ba.Length, bb.Length); i += 4)
        {
            int diff = Math.Abs(ba[i] - bb[i])
                + Math.Abs(ba[i + 1] - bb[i + 1])
                + Math.Abs(ba[i + 2] - bb[i + 2]);
            if (diff > 24)
            {
                count++;
            }
        }
        return count;
    }

    private static int CountDistinctColors(SKBitmap bmp)
    {
        var seen = new HashSet<uint>();
        byte[] bytes = bmp.Bytes;
        for (int i = 0; i + 3 < bytes.Length; i += 16)
        {
            uint key = (uint)((bytes[i] >> 2) | ((bytes[i + 1] >> 2) << 8) | ((bytes[i + 2] >> 2) << 16));
            seen.Add(key);
            if (seen.Count > 64) break;
        }
        return seen.Count;
    }
}
