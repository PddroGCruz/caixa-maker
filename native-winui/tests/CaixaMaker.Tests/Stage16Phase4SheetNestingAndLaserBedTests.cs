using System.Text;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage16Phase4SheetNestingAndLaserBedTests
{
    private static AppPaths CreateTempPaths(out string sandboxRoot)
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateTempPaths(out sandboxRoot);

    private static MainWindowViewModel CreateMainVm(AppPaths paths)
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(paths);

    private static void SafeDeleteDirectory(string path)
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.SafeDeleteDirectory(path);

    [Fact]
    public void EvaluateLaserBedFit_Default200x150x100_DetectsExactOverflowOn600x400AndFitsOn900x600()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm = CreateMainVm(paths);
            vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

            BoxSheetGeometry2D? sheet = vm.Box.SheetGeometry;
            sheet.Should().NotBeNull();
            sheet!.Layout.SheetWidth.Should().BeApproximately(432.0, 0.25);
            sheet.Layout.SheetHeight.Should().BeApproximately(401.0, 0.25);

            LaserBedFitResult compact = vm.Manufacturing.EvaluateLaserBedFit(sheet, LaserBedPreset.Laser400x400);
            compact.FitsWithoutRotation.Should().BeFalse();
            compact.FitsIfRotated90.Should().BeFalse();
            compact.ExcessXMm.Should().BeApproximately(32.0, 0.25);
            compact.ExcessYMm.Should().BeApproximately(1.0, 0.25);
            compact.Severity.Should().Be(LaserBedFitSeverity.ExceedsBed);

            LaserBedFitResult bench = vm.Manufacturing.EvaluateLaserBedFit(sheet, LaserBedPreset.Laser600x400);
            bench.FitsWithoutRotation.Should().BeFalse("folha 432×401 excede 600×400 em 1 mm no eixo Y");
            bench.FitsIfRotated90.Should().BeFalse("menor lado da folha (401 mm) supera 400 mm");
            bench.ExcessXMm.Should().Be(0.0);
            bench.ExcessYMm.Should().BeApproximately(1.0, 0.1);
            bench.Severity.Should().Be(LaserBedFitSeverity.ExceedsBed);
            bench.SummaryText.Should().Contain("excede mesa");
            bench.SummaryText.Should().Contain("900×600");

            LaserBedFitResult industrial = vm.Manufacturing.EvaluateLaserBedFit(sheet, LaserBedPreset.Laser900x600);
            industrial.FitsWithoutRotation.Should().BeTrue();
            industrial.ExcessXMm.Should().Be(0.0);
            industrial.ExcessYMm.Should().Be(0.0);
            industrial.UtilizationPercent.Should().BeGreaterThan(25.0);
            industrial.Severity.Should().Be(LaserBedFitSeverity.Fits);
            industrial.SummaryText.Should().Contain("cabe na mesa 900×600");

            LaserBedFitResult router = vm.Manufacturing.EvaluateLaserBedFit(sheet, LaserBedPreset.Laser1300x900);
            router.FitsWithoutRotation.Should().BeTrue();
            router.Severity.Should().Be(LaserBedFitSeverity.Fits);

            LaserBedFitResult free = vm.Manufacturing.EvaluateLaserBedFit(sheet, LaserBedPreset.None);
            free.Bed.HasLimit.Should().BeFalse();
            free.FitsWithoutRotation.Should().BeTrue();
            free.Severity.Should().Be(LaserBedFitSeverity.NoLimit);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void EvaluateLaserBedFit_DetectsRotated90FitWhenWidthExceedsHeightLimitButLengthFitsX()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm = CreateMainVm(paths);
            vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
            // Caixa bem compacta em profundidade/altura e comprida na largura -> folha cabe somente na 600x400
            vm.Box.ApplyParameters(
                new BoxParameters
                {
                    BoxWidth = 140.0,
                    BoxDepth = 110.0,
                    BoxHeight = 75.0,
                    BoxThickness = 3.0,
                    Folga = 0.15,
                },
                markAsEdit: false);

            BoxSheetGeometry2D? sheet = vm.Box.SheetGeometry;
            sheet.Should().NotBeNull();

            LaserBedFitResult fit600 = vm.Manufacturing.EvaluateLaserBedFit(sheet, LaserBedPreset.Laser600x400);
            fit600.FitsWithoutRotation.Should().BeTrue();
            fit600.AllSinglePanelsFit.Should().BeTrue();
            fit600.Severity.Should().Be(LaserBedFitSeverity.Fits);

            // Caixa estreita e alta -> excede altura Y=400 sem giro, mas cabe rotacionada 90° em mesa 600×400
            vm.Box.ApplyParameters(
                new BoxParameters
                {
                    BoxWidth = 140.0,
                    BoxDepth = 120.0,
                    BoxHeight = 160.0,
                    BoxThickness = 3.0,
                    Folga = 0.0,
                },
                markAsEdit: false);
            BoxSheetGeometry2D? tallSheet = vm.Box.SheetGeometry;
            tallSheet.Should().NotBeNull();
            LaserBedFitResult rotFit = vm.Manufacturing.EvaluateLaserBedFit(tallSheet, LaserBedPreset.Laser600x400);
            rotFit.FitsWithoutRotation.Should().BeFalse();
            rotFit.FitsIfRotated90.Should().BeTrue();
            rotFit.Severity.Should().Be(LaserBedFitSeverity.FitsRotatedOnly);
            rotFit.SummaryText.Should().Contain("rotacionada 90°");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void RenderManufacturingSheet2D_DarkAndLightThemes_Renders6PanelsWithLidPersonalizationAndLaserBed()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm = CreateMainVm(paths);
            vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

            vm.Personalization.SelectSurface(SurfaceId.Lid);
            vm.Personalization.DraftFontSizeMm = 16.0;
            TextPersonalizationElement added = vm.Personalization.AddTextElement("CAIXA MAKER");
            added.Should().NotBeNull();

            BoxSheetGeometry2D sheet = vm.Box.SheetGeometry!;
            sheet.Panels.Count.Should().Be(6);

            var darkOptions = new RenderManufacturingSheet2DOptions
            {
                WidthPx = 920,
                HeightPx = 500,
                DarkTheme = true,
                SelectedPartId = "lid",
                LaserBedWidthMm = 600.0,
                LaserBedHeightMm = 400.0,
                LaserBedLabel = "600×400 mm",
                ShowLaserBedOverlay = true,
                ShowSheetDimensions = true,
            };

            byte[] darkPng = OffscreenViewportRenderer.RenderManufacturingSheet2DPng(
                sheet,
                vm.Personalization.Manager.Surfaces,
                vm.Personalization.Manager.ListElements(),
                darkOptions);
            darkPng.Length.Should().BeGreaterThan(10_000);
            Encoding.ASCII.GetString(darkPng, 1, 3).Should().Be("PNG");

            using SKBitmap darkBmp = OffscreenViewportRenderer.RenderManufacturingSheet2DBitmap(
                sheet,
                vm.Personalization.Manager.Surfaces,
                vm.Personalization.Manager.ListElements(),
                darkOptions);

            var lightOptions = new RenderManufacturingSheet2DOptions
            {
                WidthPx = 920,
                HeightPx = 500,
                DarkTheme = false,
                SelectedPartId = "lid",
                LaserBedWidthMm = 600.0,
                LaserBedHeightMm = 400.0,
                LaserBedLabel = "600×400 mm",
                ShowLaserBedOverlay = true,
                ShowSheetDimensions = true,
            };

            using SKBitmap lightBmp = OffscreenViewportRenderer.RenderManufacturingSheet2DBitmap(
                sheet,
                vm.Personalization.Manager.Surfaces,
                vm.Personalization.Manager.ListElements(),
                lightOptions);

            // Verifica pixels de madeira MDF, borda ciano da mesa laser e contraste entre Dark e Light
            int mdfPixels = 0;
            int greenSelectionPixels = 0;
            int cyanBedPixels = 0;
            int themeDiffPixels = 0;

            for (int y = 24; y < darkBmp.Height; y += 2)
            {
                for (int x = 24; x < darkBmp.Width; x += 2)
                {
                    SKColor d = darkBmp.GetPixel(x, y);
                    SKColor l = lightBmp.GetPixel(x, y);

                    if (d != l) themeDiffPixels++;
                    if (d.Red >= 230 && d.Green >= 215 && d.Blue >= 190 && d.Blue < d.Red)
                    {
                        mdfPixels++;
                    }
                    if (d.Green >= 180 && d.Green > d.Red + 60 && d.Blue >= 70 && d.Blue < d.Green)
                    {
                        greenSelectionPixels++;
                    }
                    if (d.Blue >= 200 && d.Green >= 160 && d.Red < 90)
                    {
                        cyanBedPixels++;
                    }
                }
            }

            mdfPixels.Should().BeGreaterThan(5_000, "as 6 chapas em MDF claro devem preencher grande área da prancha 1:1");
            greenSelectionPixels.Should().BeGreaterThan(20, "chapa selecionada ('lid') deve receber destaque verde #22C55E");
            cyanBedPixels.Should().BeGreaterThan(25, "overlay tracejado ciano da mesa CNC 600×400 deve aparecer no render");
            themeDiffPixels.Should().BeGreaterThan(20_000);

            // Toggle de visibilidade de camada altera a renderização
            var hiddenEngraveOpts = new RenderManufacturingSheet2DOptions
            {
                WidthPx = 920,
                HeightPx = 500,
                DarkTheme = true,
                SelectedPartId = null,
                IsLayerVisible = op => op != CncOperationType.EngraveVector,
                ShowLaserBedOverlay = false,
            };
            using SKBitmap allLayersBmp = OffscreenViewportRenderer.RenderManufacturingSheet2DBitmap(
                sheet,
                vm.Personalization.Manager.Surfaces,
                vm.Personalization.Manager.ListElements(),
                new RenderManufacturingSheet2DOptions
                {
                    WidthPx = 920,
                    HeightPx = 500,
                    DarkTheme = true,
                    ShowLaserBedOverlay = false,
                });
            using SKBitmap hiddenEngraveBmp = OffscreenViewportRenderer.RenderManufacturingSheet2DBitmap(
                sheet,
                vm.Personalization.Manager.Surfaces,
                vm.Personalization.Manager.ListElements(),
                hiddenEngraveOpts);

            int layerDiffPixels = 0;
            for (int y = 24; y < allLayersBmp.Height; y += 2)
            {
                for (int x = 24; x < allLayersBmp.Width; x += 2)
                {
                    if (allLayersBmp.GetPixel(x, y) != hiddenEngraveBmp.GetPixel(x, y))
                    {
                        layerDiffPixels++;
                    }
                }
            }
            layerDiffPixels.Should().BeGreaterThan(20, "ocultar camada ENGRAVE_VECTOR deve esconder o texto 'CAIXA MAKER' na folha 2D");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void HitTestManufacturingSheetPanel_OpenBox5Panels_SelectsClickedPanelAndSynchronizesBoxSelection()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm = CreateMainVm(paths);
            vm.StartNewProjectFromCatalog(OpenBoxModel.Id);

            BoxSheetGeometry2D sheet = vm.Box.SheetGeometry!;
            sheet.Panels.Count.Should().Be(5);

            var opts = new RenderManufacturingSheet2DOptions
            {
                WidthPx = 900,
                HeightPx = 480,
                DarkTheme = true,
                LaserBedWidthMm = 600.0,
                LaserBedHeightMm = 400.0,
                ShowLaserBedOverlay = true,
            };

            SheetViewTransform2D tx = OffscreenViewportRenderer.ComputeManufacturingSheetTransform(sheet, opts);
            PanelGeometry2D frontPanel = sheet.PanelsById["front"];
            Point2D frontCenterMm = frontPanel.SheetBounds.Center;

            double frontPxX = tx.OriginPxX + frontCenterMm.X * tx.EffectiveScalePxPerMm;
            double frontPxY = tx.OriginPxY + frontCenterMm.Y * tx.EffectiveScalePxPerMm;

            ManufacturingSheetPanelHit? hit = OffscreenViewportRenderer.HitTestManufacturingSheetPanel(
                sheet,
                opts,
                frontPxX,
                frontPxY);

            hit.Should().NotBeNull();
            hit!.Value.PanelId.Should().Be("front");
            hit.Value.PartId.Should().Be("front_back");
            hit.Value.SurfaceId.Should().Be(SurfaceId.Front);

            vm.Box.SelectPart(hit.Value.PartId);
            vm.Box.SelectedPartId.Should().Be("front_back");

            PanelGeometry2D bottomPanel = sheet.PanelsById["bottom"];
            Point2D bottomCenterMm = bottomPanel.SheetBounds.Center;
            double bottomPxX = tx.OriginPxX + bottomCenterMm.X * tx.EffectiveScalePxPerMm;
            double bottomPxY = tx.OriginPxY + bottomCenterMm.Y * tx.EffectiveScalePxPerMm;

            ManufacturingSheetPanelHit? bottomHit = OffscreenViewportRenderer.HitTestManufacturingSheetPanel(
                sheet,
                opts,
                bottomPxX,
                bottomPxY);
            bottomHit.Should().NotBeNull();
            bottomHit!.Value.PanelId.Should().Be("bottom");
            bottomHit.Value.PartId.Should().Be("bottom");
            bottomHit.Value.SurfaceId.Should().Be(SurfaceId.Bottom);

            // Clique fora de qualquer peça retorna null
            ManufacturingSheetPanelHit? miss = OffscreenViewportRenderer.HitTestManufacturingSheetPanel(sheet, opts, 20.0, 20.0);
            miss.Should().BeNull();
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void MainWindowViewModel_FabricationTransitionsAndScopePreviewToggle_WorkCleanly()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm = CreateMainVm(paths);
            vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

            vm.ActiveArea.Should().Be(WorkspaceArea.Modeling);
            vm.CenterViewMode.Should().Be(CenterViewMode.Mode3D);
            vm.Manufacturing.CurrentSheetGeometry.Should().NotBeNull();

            vm.SetWorkspaceArea(WorkspaceArea.Fabrication);
            vm.ActiveArea.Should().Be(WorkspaceArea.Fabrication);
            vm.CenterViewMode.Should().Be(CenterViewMode.Split, "entrar em Fabricação a partir de 3D deve abrir o Split para exibir a folha 1:1");
            vm.Manufacturing.ShowFullSheetPreviewInFabrication.Should().BeTrue();

            vm.Manufacturing.SelectedExportScope = PersonalizationExportMode.PanelCombined;
            vm.Manufacturing.ShowFullSheetPreviewInFabrication.Should().BeFalse("escopo Chapa 1:1 alterna preview para chapa isolada");

            vm.Manufacturing.SelectedExportScope = PersonalizationExportMode.FullBox;
            vm.Manufacturing.ShowFullSheetPreviewInFabrication.Should().BeTrue("escopo Folha Completa retorna preview para plano de corte 1:1");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }
}
