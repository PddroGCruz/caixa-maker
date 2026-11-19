using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage20CmakerProjectSystemTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public Stage20CmakerProjectSystemTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Stage20Tests_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_sandboxRoot);

        _paths = AppPaths.Resolve(new AppPathsOptions
        {
            DesktopMode = false,
            ProjectsDir = Path.Combine(_sandboxRoot, "projects"),
            OutputsDir = Path.Combine(_sandboxRoot, "outputs"),
            ConfigDir = Path.Combine(_sandboxRoot, "config"),
            LogsDir = Path.Combine(_sandboxRoot, "logs"),
            TempDir = Path.Combine(_sandboxRoot, "temp"),
        });
        _paths.EnsureDirs();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandboxRoot))
            {
                Directory.Delete(_sandboxRoot, recursive: true);
            }
        }
        catch
        {
            // Melhor esforço ao limpar diretório temporário.
        }
    }

    private MainWindowViewModel CreateMainVm(NullNativeFileDialogService? dialogs = null)
        => new(_paths, dialogs ?? new NullNativeFileDialogService(), BoxModelRegistry.Default, ResolvedAppTheme.Dark);

    private string CreateExternalSampleSvg(string fileName)
    {
        string path = Path.Combine(_sandboxRoot, fileName);
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40mm" height="30mm" viewBox="0 0 40 30">
              <rect x="2" y="2" width="36" height="26" rx="3" fill="none" stroke="#ff0000" stroke-width="0.4" />
              <circle cx="20" cy="15" r="7" fill="none" stroke="#0000ff" stroke-width="0.4" />
            </svg>
            """;
        File.WriteAllText(path, svg, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string CreateExternalSamplePng(string fileName)
    {
        string path = Path.Combine(_sandboxRoot, fileName);
        using var bmp = new SKBitmap(64, 48);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            canvas.DrawRect(8, 8, 22, 32, paint);
            using var gray = new SKPaint { Color = new SKColor(96, 96, 96), Style = SKPaintStyle.Fill };
            canvas.DrawCircle(46, 24, 12, gray);
        }

        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public async Task CmakerRoundtrip_PreservesFullEditabilityCameraAndDeduplicatedAssetsAfterDeletingOriginals()
    {
        string externalSvg = CreateExternalSampleSvg("badge_ifac.svg");
        string externalPngA = CreateExternalSamplePng("emblema_a.png");
        string externalPngB = Path.Combine(_sandboxRoot, "emblema_copia_identica.png");
        File.Copy(externalPngA, externalPngB, overwrite: true);

        var sourceVm = CreateMainVm();
        sourceVm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        sourceVm.Box.DimensionMode = DimensionReferenceMode.InternalUsable;
        sourceVm.Box.InputWidth = 212.5;
        sourceVm.Box.InputDepth = 148.0;
        sourceVm.Box.InputHeight = 94.0;
        sourceVm.Box.CameraYawDeg = 61.5;
        sourceVm.Box.CameraPitchDeg = 33.25;
        sourceVm.Box.CameraZoom = 1.42;
        sourceVm.Box.CameraPanX = -14.5;
        sourceVm.Box.CameraPanY = 9.25;

        sourceVm.SetCenterViewMode(CenterViewMode.Split);
        sourceVm.SplitOrientation = SplitViewportOrientation.VerticalStacked;
        sourceVm.SplitViewportRatio = 0.61;
        sourceVm.Manufacturing.SelectedLaserBedPreset = LaserBedPreset.Laser900x600;

        sourceVm.Personalization.AddTextElement("IFMaker Acre 2026");
        VectorSvgPersonalizationElement svgEl = sourceVm.Personalization.ImportSvgFromFile(externalSvg);
        RasterImagePersonalizationElement rasterA = sourceVm.Personalization.ImportRasterFromFile(externalPngA);
        RasterImagePersonalizationElement rasterB = sourceVm.Personalization.ImportRasterFromFile(externalPngB);
        svgEl.OriginalFileName.Should().Be("badge_ifac.svg");
        svgEl.RawSvgText.Should().Contain("<svg");
        rasterA.OriginalFileName.Should().Be("emblema_a.png");
        rasterA.RawImageBytes.Should().NotBeNullOrEmpty();

        sourceVm.SavedProjectDescription = "Caixa protótipo completa para bancada CNC.";
        sourceVm.SavedProjectAuthor = "Pedro G. Cruz";
        sourceVm.SavedProjectTagsText = "ifac, cnc, prototipo";
        sourceVm.SavedProjectNotes = "MDF 3mm velocidade 18mm/s potência 65%.";

        string cmakerPath = Path.Combine(_sandboxRoot, "projeto_autocontido.cmaker");
        bool exported = await sourceVm.ExportPortablePackageAsync(cmakerPath);
        exported.Should().BeTrue();
        File.Exists(cmakerPath).Should().BeTrue();

        // Exclui todos os arquivos de origem externos do disco para provar autocontinência.
        File.Delete(externalSvg);
        File.Delete(externalPngA);
        File.Delete(externalPngB);

        // Inspeciona a estrutura interna do ZIP .cmaker.
        using (ZipArchive zip = ZipFile.OpenRead(cmakerPath))
        {
            HashSet<string> entryNames = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            entryNames.Should().Contain([
                CmakerPackageService.ManifestEntryPath,
                CmakerPackageService.MetadataEntryPath,
                CmakerPackageService.ModelEntryPath,
                CmakerPackageService.SurfacesIndexEntryPath,
                CmakerPackageService.PreviewThumbnail3DEntryPath,
                CmakerPackageService.PreviewSheet2DEntryPath,
            ]);

            // 1 SVG + 2 cópias idênticas do mesmo PNG -> exatamente 2 arquivos deduplicados em assets/.
            List<ZipArchiveEntry> assetEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal))
                .ToList();
            assetEntries.Should().HaveCount(2);

            // Verifica se as prévias PNG 3D e 2D são bitmaps válidos e não vazios.
            using var ms3d = new MemoryStream();
            zip.GetEntry(CmakerPackageService.PreviewThumbnail3DEntryPath)!.Open().CopyTo(ms3d);
            using SKBitmap thumb3d = SKBitmap.Decode(ms3d.ToArray());
            thumb3d.Should().NotBeNull();
            thumb3d.Width.Should().Be(320);
            thumb3d.Height.Should().Be(220);

            using var ms2d = new MemoryStream();
            zip.GetEntry(CmakerPackageService.PreviewSheet2DEntryPath)!.Open().CopyTo(ms2d);
            using SKBitmap sheet2d = SKBitmap.Decode(ms2d.ToArray());
            sheet2d.Should().NotBeNull();
            sheet2d.Width.Should().Be(360);
            sheet2d.Height.Should().Be(240);
        }

        // Abre em uma instância limpa de MainWindowViewModel após exclusão dos originais.
        var targetVm = CreateMainVm();
        bool opened = await targetVm.OpenExternalProjectFileAsync(cmakerPath, registerInLibrary: false);
        opened.Should().BeTrue();
        targetVm.InDesigner.Should().BeTrue();
        targetVm.SourceFormatBadgeTextOrDefault().Should().NotBeNull();

        targetVm.Box.DimensionMode.Should().Be(DimensionReferenceMode.InternalUsable);
        targetVm.Box.InputWidth.Should().BeApproximately(212.5, 0.1);
        targetVm.Box.CameraYawDeg.Should().BeApproximately(61.5, 0.01);
        targetVm.Box.CameraPitchDeg.Should().BeApproximately(33.25, 0.01);
        targetVm.Box.CameraZoom.Should().BeApproximately(1.42, 0.001);
        targetVm.Box.CameraPanX.Should().BeApproximately(-14.5, 0.01);
        targetVm.Box.CameraPanY.Should().BeApproximately(9.25, 0.01);
        targetVm.CenterViewMode.Should().Be(CenterViewMode.Split);
        targetVm.SplitOrientation.Should().Be(SplitViewportOrientation.VerticalStacked);
        targetVm.SplitViewportRatio.Should().BeApproximately(0.61, 0.01);
        targetVm.Manufacturing.SelectedLaserBedPreset.Should().Be(LaserBedPreset.Laser900x600);

        targetVm.SavedProjectAuthor.Should().Be("Pedro G. Cruz");
        targetVm.SavedProjectDescription.Should().Contain("bancada CNC");
        targetVm.SavedProjectNotes.Should().Contain("65%");
        targetVm.SavedProjectTagsText.Should().Contain("ifac");
        targetVm.SavedProjectAssetCount.Should().Be(2);

        List<PersonalizationElement> restoredElements = targetVm.Personalization.Manager.ListElements().ToList();
        restoredElements.Should().HaveCount(4);

        VectorSvgPersonalizationElement restoredSvg = restoredElements.OfType<VectorSvgPersonalizationElement>().Single();
        restoredSvg.RawSvgText.Should().Contain("<circle");
        restoredSvg.OriginalFileName.Should().Be("badge_ifac.svg");

        // Reprocessa o raster restaurado sem arquivo de origem no disco.
        RasterImagePersonalizationElement restoredRaster = restoredElements.OfType<RasterImagePersonalizationElement>().First();
        restoredRaster.RawImageBytes.Should().NotBeNullOrEmpty();
        string originalPreviewB64 = restoredRaster.ProcessedImageB64;

        targetVm.Personalization.SelectElement(restoredRaster.Id);
        targetVm.Personalization.SelectedRasterContrast = 1.65;
        targetVm.Personalization.SelectedRasterInvert = true;
        targetVm.Personalization.ApplySelectedInspectorChanges();

        restoredRaster.ProcessingOptions.Contrast.Should().BeApproximately(1.65, 0.01);
        restoredRaster.ProcessingOptions.Invert.Should().BeTrue();
        restoredRaster.ProcessedImageB64.Should().NotBeNullOrEmpty();
        restoredRaster.ProcessedImageB64.Should().NotBe(originalPreviewB64);

        // Alteração de 1 byte sem atualizar SHA-256 no manifest.json deve falhar na verificação criptográfica.
        string tamperedPath = Path.Combine(_sandboxRoot, "tampered.cmaker");
        File.Copy(cmakerPath, tamperedPath, overwrite: true);
        using (ZipArchive zipEdit = ZipFile.Open(tamperedPath, ZipArchiveMode.Update))
        {
            ZipArchiveEntry modelEntry = zipEdit.GetEntry(CmakerPackageService.ModelEntryPath)!;
            string json;
            using (var sr = new StreamReader(modelEntry.Open(), Encoding.UTF8))
            {
                json = sr.ReadToEnd();
            }

            modelEntry.Delete();
            ZipArchiveEntry replacement = zipEdit.CreateEntry(CmakerPackageService.ModelEntryPath);
            using var sw = new StreamWriter(replacement.Open(), Encoding.UTF8);
            sw.Write(json + " ");
        }

        var packageSvc = new CmakerPackageService();
        Action actTamper = () => packageSvc.LoadPackage(tamperedPath);
        actTamper.Should().Throw<InvalidDataException>();

        // Zip Slip (entrada maliciosa ../) deve ser rejeitado.
        string zipSlipPath = Path.Combine(_sandboxRoot, "zipslip.cmaker");
        using (var fs = File.Create(zipSlipPath))
        using (var zipSlip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zipSlip.CreateEntry("../outside.txt").Open());
            writer.Write("evil");
        }

        Action actZipSlip = () => packageSvc.LoadPackage(zipSlipPath);
        actZipSlip.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void MigrationAndBackwardCompatibility_HandlesLegacyJsonV1PackagesAndFutureSchemas()
    {
        var packageSvc = new CmakerPackageService();
        var migrationSvc = new CmakerMigrationService(BoxModelRegistry.Default, packageSvc);

        // 1. Cria um projeto legado .json na pasta projects/ + pacote .caixamaker.json externo.
        string pngPath = CreateExternalSamplePng("legacy_raster.png");
        byte[] pngBytes = File.ReadAllBytes(pngPath);

        var vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(OpenBoxModel.Id);
        vm.Personalization.AddTextElement("Legado 1.x");
        vm.Personalization.ImportRasterFromBytes(pngBytes, "legacy_raster.png");

        var legacyRepo = new ProjectRepository(_paths.ProjectsDir);
        ProjectRecord legacyRecord = legacyRepo.SaveFromRequest(new ProjectSaveRequest
        {
            Name = "Caixa Legada JSON",
            ModelId = vm.Box.SelectedModelId,
            ModelName = vm.Box.SelectedModel.DisplayName,
            BoxParameters = vm.Box.CurrentParameters,
            ViewerState = vm.Box.ToViewerState("front"),
            Manager = vm.Personalization.Manager,
            SafetyMarginMm = vm.Personalization.SafetyMarginMm,
        });

        string legacyJsonPath = Path.Combine(_paths.ProjectsDir, $"{legacyRecord.Id}.json");
        File.Exists(legacyJsonPath).Should().BeTrue();

        string legacyBundlePath = Path.Combine(_sandboxRoot, "pacote_antigo.caixamaker.json");
        var portableService = new PortablePackageService(BoxModelRegistry.Default);
        portableService.ExportBundleToFile(
            new ProjectSaveRequest
            {
                Name = "Pacote Legado Exportado",
                ModelId = vm.Box.SelectedModelId,
                ModelName = vm.Box.SelectedModel.DisplayName,
                BoxParameters = vm.Box.CurrentParameters,
                ViewerState = vm.Box.ToViewerState("front"),
                Manager = vm.Personalization.Manager,
            },
            legacyBundlePath);
        File.Exists(legacyBundlePath).Should().BeTrue();

        // Carrega ambos pelo CmakerMigrationService sem perder dados.
        LoadedProjectSession loadedBundle = migrationSvc.LoadAnyProjectFile(legacyBundlePath);
        loadedBundle.SourceFormat.Should().Be("legacy_caixamaker_json");
        loadedBundle.MigrationNotice.Should().NotBeNullOrWhiteSpace();
        loadedBundle.Manager.ListElements().Should().HaveCount(2);

        // Executa migração em lote da biblioteca preservando originais em _legacy_backup/.
        var librarySvc = new ProjectLibraryService(_paths.ProjectsDir, BoxModelRegistry.Default, packageSvc);
        librarySvc.CountPendingLegacyJsonProjects().Should().Be(1);

        LegacyCmakerMigrationReport report = librarySvc.MigrateLegacyProjects(archiveConvertedJson: true);
        report.MigratedCount.Should().Be(1);
        report.ErrorCount.Should().Be(0);
        librarySvc.CountPendingLegacyJsonProjects().Should().Be(0);

        string migratedCmakerPath = Path.Combine(_paths.ProjectsDir, $"{legacyRecord.Id}.cmaker");
        string backupJsonPath = Path.Combine(_paths.ProjectsDir, "_legacy_backup", $"{legacyRecord.Id}.json");
        File.Exists(migratedCmakerPath).Should().BeTrue();
        File.Exists(backupJsonPath).Should().BeTrue();

        ProjectSummary summary = packageSvc.ReadSummary(migratedCmakerPath);
        summary.ThumbnailPngBytes.Should().NotBeNullOrEmpty();
        using (SKBitmap bmp = SKBitmap.Decode(summary.ThumbnailPngBytes))
        {
            bmp.Should().NotBeNull();
            bmp.Width.Should().Be(320);
        }

        // Migração determinística v1 bytes -> v2 bytes.
        IBoxModelDefinition openModel = BoxModelRegistry.Default.Get(OpenBoxModel.Id);
        byte[] baseCmakerBytes = packageSvc.BuildPackage(
            new ProjectSaveRequest
            {
                Name = "Projeto V1",
                ModelId = openModel.ModelId,
                ModelName = openModel.DisplayName,
                BoxParameters = vm.Box.CurrentParameters,
                ViewerState = vm.Box.ToViewerState("front"),
                Manager = vm.Personalization.Manager,
            }).PackageBytes;

        byte[] v1CmakerBytes = RewriteManifestSchemaVersion(baseCmakerBytes, "1.0.0");
        byte[] v2Bytes = migrationSvc.MigrateV1BytesToV2Bytes(v1CmakerBytes);
        LoadedProjectSession v2Session = packageSvc.LoadPackageBytes(v2Bytes, "v2.cmaker");
        v2Session.PackageSchemaVersion.Should().Be(CmakerManifest.CurrentSchemaVersion);

        // Forward-compatibility com schema futuro 3.0.0.
        byte[] futureBytes = RewriteManifestSchemaVersion(v2Bytes, "3.0.0");
        string futurePath = Path.Combine(_sandboxRoot, "future_v3.cmaker");
        File.WriteAllBytes(futurePath, futureBytes);

        LoadedProjectSession futureLoaded = packageSvc.LoadPackage(futurePath);
        futureLoaded.PackageSchemaVersion.Should().Be("3.0.0");
        futureLoaded.MigrationNotice.Should().NotBeNullOrWhiteSpace();
    }

    private static byte[] RewriteManifestSchemaVersion(byte[] packageBytes, string targetSchemaVersion)
    {
        using var ms = new MemoryStream();
        ms.Write(packageBytes, 0, packageBytes.Length);
        ms.Position = 0;

        using (var zipEdit = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            ZipArchiveEntry manifestEntry = zipEdit.GetEntry(CmakerPackageService.ManifestEntryPath)!;
            JsonNode manifestNode;
            using (var sr = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            {
                manifestNode = JsonNode.Parse(sr.ReadToEnd())!;
            }

            manifestNode["schema_version"] = targetSchemaVersion;
            manifestEntry.Delete();
            ZipArchiveEntry updatedManifest = zipEdit.CreateEntry(CmakerPackageService.ManifestEntryPath);
            using var sw = new StreamWriter(updatedManifest.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sw.Write(manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        return ms.ToArray();
    }

    [Fact]
    public async Task ProjectLibraryAndManufacturingZip_SupportSearchDuplicateMetadataAndEmbeddedCmaker()
    {
        var dialogs = new NullNativeFileDialogService();
        var vm = CreateMainVm(dialogs);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.Personalization.AddTextElement("Projeto Juruá");

        vm.SaveProjectAs(
            "Organizador Juruá CNC",
            description: "Bandeja modular para laboratório IFMaker",
            author: "Pedro G. Cruz",
            tags: ["ifac", "bancada", "organizador"],
            notes: "Testar kerf 0.15 mm");

        vm.SavedProjects.Should().ContainSingle(p => p.Name == "Organizador Juruá CNC");

        // Busca por tag, autor e descrição.
        vm.ProjectSearchQuery = "bancada";
        vm.SavedProjects.Should().ContainSingle(p => p.Name == "Organizador Juruá CNC");

        vm.ProjectSearchQuery = "inexistente-xyz";
        vm.SavedProjects.Should().BeEmpty();

        vm.ProjectSearchQuery = string.Empty;
        vm.SavedProjects.Should().HaveCount(1);

        // Duplicação independente.
        string originalId = vm.SavedProjectId!;
        vm.DuplicateSavedProject(originalId, "Organizador Juruá CNC - V2").Should().BeTrue();
        vm.SavedProjects.Should().HaveCount(2);

        // Atualização de metadados preserva ID.
        await vm.UpdateActiveProjectMetadataAsync(
            name: "Organizador Juruá CNC - Final",
            description: "Versão final calibrada",
            author: "Pedro G. Cruz",
            tags: ["final", "ifmaker"],
            notes: "Lote MDF Novembro");
        vm.SavedProjectId.Should().Be(originalId);
        vm.SavedProjectTagsText.Should().Be("final, ifmaker");

        // Exportação do pacote ZIP CNC completo inclui arquivo .cmaker editável embutido.
        string cncZipPath = Path.Combine(_sandboxRoot, "producao_completa.zip");
        string? actualZip = await vm.ExportBundleZipAsync(cncZipPath);
        actualZip.Should().Be(cncZipPath);
        File.Exists(cncZipPath).Should().BeTrue();

        using (ZipArchive zip = ZipFile.OpenRead(cncZipPath))
        {
            zip.Entries.Should().Contain(e => e.FullName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
            ZipArchiveEntry? cmakerEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".cmaker", StringComparison.OrdinalIgnoreCase));
            cmakerEntry.Should().NotBeNull();

            using var ms = new MemoryStream();
            cmakerEntry!.Open().CopyTo(ms);
            LoadedProjectSession insideZip = new CmakerPackageService().LoadPackageBytes(ms.ToArray(), cmakerEntry.Name);
            insideZip.Record.ModelId.Should().Be(BoxWithLidAndHandlesModel.Id);
            insideZip.Manager.ListElements().Should().HaveCount(1);
        }

        // Confirmação negativa preserva; positiva exclui.
        dialogs.OnConfirmAction = (_, _) => false;
        (await vm.DeleteSavedProjectWithConfirmationAsync(originalId)).Should().BeFalse();
        vm.SavedProjects.Should().HaveCount(2);

        dialogs.OnConfirmAction = (_, _) => true;
        (await vm.DeleteSavedProjectWithConfirmationAsync(originalId)).Should().BeTrue();
        vm.SavedProjects.Should().HaveCount(1);
    }

    [Fact]
    public void WindowsFileAssociationAndCliLaunch_ExtractPathsAndVerifyInstallers()
    {
        ProjectLaunchHelper.ExtractProjectFilePath(["--smoke-test"]).Should().BeNull();
        ProjectLaunchHelper.ExtractProjectFilePath(["--capture-ui-screenshots", "projeto_falso.cmaker"]).Should().BeNull();
        ProjectLaunchHelper.ExtractProjectFilePath(["\"C:\\Temp\\meu_estojo.cmaker\""])
            .Should().EndWith("meu_estojo.cmaker");
        ProjectLaunchHelper.ExtractProjectFilePathFromCommandLine(
            "\"C:\\Program Files\\Caixa Maker\\CaixaMaker.exe\" \"D:\\Projetos\\peça teste.cmaker\"")
            .Should().EndWith("peça teste.cmaker");
        ProjectLaunchHelper.ExtractProjectFilePath(["legado.caixamaker.json"])
            .Should().EndWith("legado.caixamaker.json");
        ProjectLaunchHelper.ExtractProjectFilePath(["imagem.png"]).Should().BeNull();

        string repoRoot = FindNativeWinUiRoot();
        string nsisPath = Path.Combine(repoRoot, "installer", "nsis", "CaixaMaker.nsi");
        string wixPath = Path.Combine(repoRoot, "installer", "wix", "Product.wxs");

        string nsis = File.ReadAllText(nsisPath, Encoding.UTF8);
        nsis.Should().Contain(@"Software\Classes\.cmaker");
        nsis.Should().Contain(@"Software\Classes\CaixaMaker.Project\shell\open\command");
        nsis.Should().Contain(@"DeleteRegKey HKCU ""Software\Classes\.cmaker""");
        nsis.Should().Contain("SHChangeNotify");

        string wix = File.ReadAllText(wixPath, Encoding.UTF8);
        wix.Should().Contain(@"<ProgId Id=""CaixaMaker.Project""");
        wix.Should().Contain(@"<Extension Id=""cmaker""");
        wix.Should().Contain(@"<Verb Id=""open""");
    }

    private static string FindNativeWinUiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CaixaMaker.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Diretório native-winui/ não localizado a partir da pasta de execução.");
    }
}

internal static class Stage20TestExtensions
{
    public static string SourceFormatBadgeTextOrDefault(this MainWindowViewModel vm)
        => vm.SavedProjectFormatBadge;
}
