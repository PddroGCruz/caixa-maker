using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Cam.Nesting;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Nesting;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage29AdaptiveFingerJointsTests
{
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
                Directory.Exists(Path.Combine(dir.FullName, "native-winui")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Não foi possível localizar a raiz do repositório.");
    }

    [Fact]
    public void AutoMode_ScalesVerticalAndBaseJointsProportionallyAcrossBoxSizesAndMdfThicknesses()
    {
        IBoxModelDefinition lidModel = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);

        // 1) 200x150x100 vs 200x150x300 (MDF 3 mm): dentes verticais crescem proporcionalmente sem esticar para ~20 mm
        BoxCalculationSummary std100 = lidModel.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 100.0,
            FingerJointMode = FingerJointSizingMode.Auto,
        });
        BoxCalculationSummary tall300 = lidModel.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 300.0,
            FingerJointMode = FingerJointSizingMode.Auto,
        });

        std100.Joints.Vertical.Count.Should().Be(9);
        std100.Joints.Vertical.SegmentLengthMm.Should().BeApproximately(72.75 / 9.0, 1e-3);
        tall300.Joints.Vertical.Count.Should().BeGreaterThan(std100.Joints.Vertical.Count);
        tall300.Joints.Vertical.Count.Should().Be(29);
        tall300.Joints.Vertical.SegmentLengthMm.Should().BeInRange(6.5, 9.0);
        tall300.ComputedDimensions.VerticalFingerCount.Should().Be(29);
        tall300.ComputedDimensions.VerticalFingerWidthMm.Should().BeApproximately(tall300.Joints.Vertical.TabLengthMm, 1e-4);

        // 2) 200x150x150 vs 300x400x150 (MDF 3 mm): encaixes da base frontal e lateral crescem independentemente
        BoxCalculationSummary med150 = lidModel.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 150.0,
            FingerJointMode = FingerJointSizingMode.Auto,
        });
        BoxCalculationSummary large300x400 = lidModel.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 300.0,
            BoxDepth = 400.0,
            BoxHeight = 150.0,
            FingerJointMode = FingerJointSizingMode.Auto,
        });

        large300x400.ComputedDimensions.BaseFrontFingerCount.Should().BeGreaterThan(med150.ComputedDimensions.BaseFrontFingerCount);
        large300x400.ComputedDimensions.BaseSideFingerCount.Should().BeGreaterThan(med150.ComputedDimensions.BaseSideFingerCount);
        large300x400.ComputedDimensions.BaseSideFingerCount.Should().BeGreaterThan(large300x400.ComputedDimensions.BaseFrontFingerCount);
        large300x400.ComputedDimensions.BaseFrontFingerWidthMm.Should().BeInRange(6.5, 9.0);
        large300x400.ComputedDimensions.BaseSideFingerWidthMm.Should().BeInRange(6.5, 9.0);

        // 3) Caixa pequena com tampa (158x58x54 mm externa / 150x50x50 mm interna útil, MDF 4 mm):
        //    deve manter >= 5 segmentos verticais (>= 2 espigas), evitando 3 segmentos (efeito pivô)
        BoxCalculationSummary smallMdf4 = lidModel.BuildSummary(new BoxParameters
        {
            BoxThickness = 4.0,
            BoxWidth = 158.0,
            BoxDepth = 58.0,
            BoxHeight = 54.0,
            FingerJointMode = FingerJointSizingMode.Auto,
        });

        smallMdf4.Joints.Vertical.Count.Should().BeGreaterThanOrEqualTo(5);
        smallMdf4.Joints.Vertical.TabCount.Should().BeGreaterThanOrEqualTo(2);
        smallMdf4.Joints.Vertical.SegmentLengthMm.Should().BeApproximately(7.5, 1e-3);
        smallMdf4.Joints.FrontBottom.TabCount.Should().Be(8);
        smallMdf4.Joints.SideBottom.TabCount.Should().Be(2);

        // 4) Caixa rasa 200x200x20 mm (MDF 3 mm): reduz com segurança para 3 segmentos verticais (>= 3.0 mm)
        BoxCalculationSummary shallow20 = lidModel.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 200.0,
            BoxHeight = 20.0,
            FingerJointMode = FingerJointSizingMode.Auto,
        });

        shallow20.Joints.Vertical.Count.Should().Be(3);
        shallow20.Joints.Vertical.SegmentLengthMm.Should().BeApproximately(4.25, 1e-3);
        shallow20.Warnings.Should().BeEmpty();

        // 5) Adaptação à espessura do MDF (3 mm vs 6 mm vs 9 mm): dentes mais largos em chapas mais grossas
        BoxCalculationSummary mdf3 = lidModel.BuildSummary(new BoxParameters { BoxThickness = 3.0, BoxWidth = 300.0, BoxDepth = 240.0, BoxHeight = 160.0 });
        BoxCalculationSummary mdf6 = lidModel.BuildSummary(new BoxParameters { BoxThickness = 6.0, BoxWidth = 300.0, BoxDepth = 240.0, BoxHeight = 160.0 });
        BoxCalculationSummary mdf9 = lidModel.BuildSummary(new BoxParameters { BoxThickness = 9.0, BoxWidth = 300.0, BoxDepth = 240.0, BoxHeight = 160.0 });

        BoxCalculator.ComputeAutoTargetFingerWidthMm(3.0).Should().BeApproximately(7.5, 1e-6);
        BoxCalculator.ComputeAutoTargetFingerWidthMm(4.0).Should().BeApproximately(8.8, 1e-6);
        BoxCalculator.ComputeAutoTargetFingerWidthMm(6.0).Should().BeApproximately(13.2, 1e-6);
        BoxCalculator.ComputeAutoTargetFingerWidthMm(9.0).Should().BeApproximately(16.0, 1e-6);

        mdf6.Joints.FrontBottom.SegmentLengthMm.Should().BeGreaterThan(mdf3.Joints.FrontBottom.SegmentLengthMm);
        mdf9.Joints.FrontBottom.SegmentLengthMm.Should().BeGreaterThan(mdf6.Joints.FrontBottom.SegmentLengthMm);
        mdf6.Joints.Vertical.SegmentLengthMm.Should().BeGreaterThan(mdf3.Joints.Vertical.SegmentLengthMm);
    }

    [Fact]
    public void TargetSizeMode_AdjustsJointCountsAndMatchesRequestedWidthAcrossAllEdges()
    {
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(OpenBoxModel.Id);

        BoxCalculationSummary fine6 = model.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 240.0,
            BoxDepth = 180.0,
            BoxHeight = 140.0,
            FingerJointMode = FingerJointSizingMode.TargetSizeMm,
            TargetFingerWidthMm = 6.0,
        });
        BoxCalculationSummary coarse18 = model.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 240.0,
            BoxDepth = 180.0,
            BoxHeight = 140.0,
            FingerJointMode = FingerJointSizingMode.TargetSizeMm,
            TargetFingerWidthMm = 18.0,
        });

        coarse18.Joints.Vertical.Count.Should().BeLessThan(fine6.Joints.Vertical.Count);
        coarse18.Joints.FrontBottom.Count.Should().BeLessThan(fine6.Joints.FrontBottom.Count);
        coarse18.Joints.SideBottom.Count.Should().BeLessThan(fine6.Joints.SideBottom.Count);

        fine6.Joints.Vertical.SegmentLengthMm.Should().BeInRange(5.0, 7.0);
        fine6.Joints.FrontBottom.SegmentLengthMm.Should().BeInRange(5.0, 7.0);
        coarse18.Joints.Vertical.SegmentLengthMm.Should().BeInRange(14.5, 22.0);
        coarse18.Joints.FrontBottom.SegmentLengthMm.Should().BeInRange(15.5, 21.0);
    }

    [Fact]
    public void FixedCountMode_ReproducesClassic5AndCustom8_AndSafelyReducesOnShortEdges()
    {
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);

        BoxCalculationSummary classic5 = model.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 100.0,
            FingerJointMode = FingerJointSizingMode.FixedCount,
            FixedFingerCount = 5,
        });
        classic5.Joints.Vertical.Count.Should().Be(11);
        classic5.Joints.Vertical.TabCount.Should().Be(5);
        classic5.Joints.FrontBottom.TabCount.Should().Be(5);
        classic5.Joints.SideBottom.TabCount.Should().Be(5);
        classic5.Warnings.Should().BeEmpty();

        BoxCalculationSummary fixed8 = model.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 100.0,
            FingerJointMode = FingerJointSizingMode.FixedCount,
            FixedFingerCount = 8,
        });
        fixed8.Joints.Vertical.Count.Should().Be(17);
        fixed8.Joints.Vertical.TabCount.Should().Be(8);
        fixed8.Joints.FrontBottom.Count.Should().Be(17);
        fixed8.Joints.FrontBottom.TabCount.Should().Be(8);
        fixed8.Joints.SideBottom.Count.Should().Be(17);
        fixed8.Joints.SideBottom.TabCount.Should().Be(8);

        // Proteção contra aresta curta demais: 15 espigas em altura 20 mm (vão útil 12.75 mm) reduz para 3 segmentos com aviso
        BoxCalculationSummary shortEdge = model.BuildSummary(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 20.0,
            FingerJointMode = FingerJointSizingMode.FixedCount,
            FixedFingerCount = 15,
        });
        shortEdge.Joints.Vertical.Count.Should().Be(3);
        shortEdge.Joints.Vertical.WasReducedByLimit.Should().BeTrue();
        shortEdge.Joints.Vertical.SegmentLengthMm.Should().BeGreaterThanOrEqualTo(3.0);
        shortEdge.Warnings.Should().Contain(w => w.Contains("reduzida automaticamente", StringComparison.OrdinalIgnoreCase));

        // Validador rejeita faixas inválidas
        BoxParametersValidator.Validate(new BoxParameters { TargetFingerWidthMm = 2.0 }).IsValid.Should().BeFalse();
        BoxParametersValidator.Validate(new BoxParameters { TargetFingerWidthMm = 50.0 }).IsValid.Should().BeFalse();
        BoxParametersValidator.Validate(new BoxParameters { FixedFingerCount = 1 }).IsValid.Should().BeFalse();
        BoxParametersValidator.Validate(new BoxParameters { FixedFingerCount = 30 }).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(BoxWithLidAndHandlesModel.Id, 200.0, 150.0, 300.0, FingerJointSizingMode.Auto, 10.0, 5)]
    [InlineData(OpenBoxModel.Id, 300.0, 400.0, 150.0, FingerJointSizingMode.Auto, 10.0, 5)]
    [InlineData(BoxWithLidAndHandlesModel.Id, 200.0, 200.0, 20.0, FingerJointSizingMode.Auto, 10.0, 5)]
    [InlineData(OpenBoxModel.Id, 220.0, 160.0, 120.0, FingerJointSizingMode.TargetSizeMm, 7.5, 5)]
    [InlineData(BoxWithLidAndHandlesModel.Id, 200.0, 150.0, 100.0, FingerJointSizingMode.FixedCount, 10.0, 8)]
    public void FullPipelineParity_2DGeometry_3DScene_NestingAdapter_AndSurfaceManager_WorkCoherently(
        string modelId,
        double w,
        double d,
        double h,
        FingerJointSizingMode mode,
        double targetMm,
        int fixedCount)
    {
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(modelId);
        var parameters = new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = w,
            BoxDepth = d,
            BoxHeight = h,
            Folga = 0.0,
            FingerJointMode = mode,
            TargetFingerWidthMm = targetMm,
            FixedFingerCount = fixedCount,
        };

        BoxCalculator calc = model.CreateCalculator(parameters);
        BoxDrawer drawer = model.CreateDrawer(calc);
        BoxSheetGeometry2D sheet = model.BuildSheetGeometry(calc, drawer);
        BoxCalculationSummary summary = model.BuildSummary(calc, drawer);

        // 1. Geometria 2D fechada e quantidade exata de rasgos na base e laterais
        sheet.Panels.Should().HaveCount(model.TotalPanels);
        foreach (PanelGeometry2D panel in sheet.Panels)
        {
            panel.OuterContour.Vertices.Count.Should().BeGreaterThanOrEqualTo(4);
        }

        int expectedBottomSlots = (2 * summary.Joints.FrontBottom.TabCount) + (2 * summary.Joints.SideBottom.TabCount);
        sheet.PanelsById["bottom"].InternalHoles.Should().HaveCount(expectedBottomSlots);

        int expectedSideVerticalSlots = 2 * summary.Joints.Vertical.TabCount;
        sheet.PanelsById["right"].InternalHoles.Should().HaveCount(expectedSideVerticalSlots);

        // 2. SurfaceManager e zonas restritas Finger Joint coerentes
        var manager = new SurfaceManager(calc, boxModel: model, drawer: drawer);
        manager.Surfaces.GetSurface(SurfaceId.Front).ForbiddenZones
            .Should().Contain(z => z.ZoneType == ForbiddenZoneType.FingerJoint);
        manager.Validate().IsValid.Should().BeTrue();

        // 3. Cena 3D extrudada sem erros e com malha válida para cada chapa
        BoxScene3D scene = BoxScene3DBuilder.BuildScene(calc, model, sheet, manager, lidAngleDeg: 35.0, isExplodedView: false);
        scene.Panels.Should().HaveCount(model.TotalPanels);
        scene.Panels.Should().OnlyContain(p => p.Triangles.Count > 0);

        // 4. Adaptador de Nesting extrai contornos e furos dinâmicos sem hardcodes
        var nestingAdapter = new BoxPanelNestingAdapter();
        IReadOnlyList<NestingPart> nestingParts = nestingAdapter.ExtractParts(calc, model, manager);
        nestingParts.Should().HaveCount(model.TotalPanels);
        NestingPart bottomNest = nestingParts.Single(p => p.SourcePanelKey == "bottom");
        bottomNest.InternalFeatures.Count(f => f.Layer == NestingSemanticLayer.CutInternal).Should().Be(expectedBottomSlots);
    }

    [Fact]
    public void CmakerPersistence_RoundTripsFingerJointConfig_AndLoadsLegacyFilesAsAuto()
    {
        var service = new CmakerPackageService(BoxModelRegistry.Default);
        var customParams = new BoxParameters
        {
            BoxThickness = 4.0,
            BoxWidth = 260.0,
            BoxDepth = 180.0,
            BoxHeight = 140.0,
            Folga = 0.05,
            FingerJointMode = FingerJointSizingMode.TargetSizeMm,
            TargetFingerWidthMm = 14.5,
            FixedFingerCount = 7,
        };

        CmakerPackageBuildResult built = service.BuildPackage(new ProjectSaveRequest
        {
            Name = "Projeto Finger Joint Adaptativo",
            ModelId = BoxWithLidAndHandlesModel.Id,
            BoxParameters = customParams,
        });

        LoadedProjectSession loaded = service.LoadPackageBytes(built.PackageBytes, built.FileName);
        loaded.BoxParameters.FingerJointMode.Should().Be(FingerJointSizingMode.TargetSizeMm);
        loaded.BoxParameters.TargetFingerWidthMm.Should().BeApproximately(14.5, 1e-4);
        loaded.BoxParameters.FixedFingerCount.Should().Be(7);

        // Retrocompatibilidade: JSON antigo (v2.0..v2.3) sem os novos campos carrega em modo Auto sem erros
        const string legacyJson = """
            {
              "box_thickness": 3.0,
              "box_width": 200.0,
              "box_depth": 150.0,
              "box_height": 100.0,
              "folga": 0.0
            }
            """;
        ProjectBoxParameters? legacyDto = JsonSerializer.Deserialize<ProjectBoxParameters>(legacyJson);
        legacyDto.Should().NotBeNull();
        BoxParameters legacyResolved = legacyDto!.ToBoxParameters();
        legacyResolved.FingerJointMode.Should().Be(FingerJointSizingMode.Auto);
        legacyResolved.TargetFingerWidthMm.Should().Be(BoxParameters.DefaultTargetFingerWidthMm);
        legacyResolved.FixedFingerCount.Should().Be(BoxParameters.DefaultFixedFingerCount);

        // Simula pacote .cmaker antigo removendo os campos novos do model.json e atualizando SHA-256
        byte[] legacyPackageBytes = StripFingerJointFieldsFromCmaker(built.PackageBytes);
        LoadedProjectSession loadedLegacy = service.LoadPackageBytes(legacyPackageBytes, "legado_v23.cmaker");
        loadedLegacy.BoxParameters.FingerJointMode.Should().Be(FingerJointSizingMode.Auto);
        loadedLegacy.BoxParameters.TargetFingerWidthMm.Should().Be(10.0);
        loadedLegacy.BoxParameters.FixedFingerCount.Should().Be(5);
    }

    [Fact]
    public void BoxWorkspaceViewModel_AndModelingLeftPanelXaml_ExposeUnifiedFingerJointControls()
    {
        var vm = new BoxWorkspaceViewModel(BoxModelRegistry.Default);
        vm.FingerJointMode.Should().Be(FingerJointSizingMode.Auto);
        vm.IsFingerJointAutoMode.Should().BeTrue();
        vm.IsFingerJointTargetSizeVisible.Should().BeFalse();
        vm.IsFingerJointFixedCountVisible.Should().BeFalse();
        vm.FingerJointAutoHintText.Should().Contain("7,5 mm");
        vm.BaseJointsSummaryDisplay.Should().Contain("×").And.Contain(" / ").And.EndWith(" mm");
        vm.BaseJointsSummaryDisplay.Length.Should().BeLessThanOrEqualTo(22);
        vm.BaseJointsSummaryTooltip.Should().Contain("espigas frontais").And.Contain("espigas laterais");

        vm.BoxThickness = 4.0;
        vm.FingerJointAutoHintText.Should().Contain("8,8 mm");
        vm.BoxThickness = 3.0;

        vm.SetFingerJointModeCommand.Execute(FingerJointSizingMode.TargetSizeMm);
        vm.IsFingerJointTargetSizeVisible.Should().BeTrue();
        vm.IsFingerJointFixedCountVisible.Should().BeFalse();
        vm.TargetFingerWidthMm = 16.0;
        vm.Summary!.Joints.FrontBottom.SegmentLengthMm.Should().BeGreaterThan(13.0);

        vm.SetFingerJointModeCommand.Execute(FingerJointSizingMode.FixedCount);
        vm.IsFingerJointFixedCountVisible.Should().BeTrue();
        vm.FixedFingerCount = 8;
        vm.Summary!.Joints.Vertical.Count.Should().Be(17);

        // Auditoria XAML do seletor Auto / Tamanho / Qtd. em ModelingAreaLeftPanel.xaml
        string repoRoot = FindRepoRoot();
        string xamlPath = Path.Combine(
            repoRoot,
            "native-winui",
            "src",
            "CaixaMaker.App",
            "Views",
            "Areas",
            "ModelingAreaLeftPanel.xaml");
        XDocument doc = XDocument.Load(xamlPath);
        string xml = doc.ToString();
        xml.Should().Contain("FingerJointAutoBtn");
        xml.Should().Contain("FingerJointSizeBtn");
        xml.Should().Contain("FingerJointCountBtn");
        xml.Should().Contain("Content=\"Auto\"");
        xml.Should().Contain("Content=\"Tamanho\"");
        xml.Should().Contain("Content=\"Qtd.\"");
        xml.Should().Contain("TargetFingerWidthInput");
        xml.Should().Contain("FixedFingerCountInput");
    }

    private static byte[] StripFingerJointFieldsFromCmaker(byte[] originalBytes)
    {
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        using (var inMs = new MemoryStream(originalBytes, writable: false))
        using (var inZip = new ZipArchive(inMs, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in inZip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                using Stream s = entry.Open();
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                entries[entry.FullName] = copy.ToArray();
            }
        }

        string modelJson = Encoding.UTF8.GetString(entries["model.json"]);
        using JsonDocument doc = JsonDocument.Parse(modelJson);
        var rootDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(modelJson)!;
        var bpDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(rootDict["box_parameters"].GetRawText())!;
        bpDict.Remove("finger_joint_mode");
        bpDict.Remove("target_finger_width_mm");
        bpDict.Remove("fixed_finger_count");

        var rebuiltModel = new Dictionary<string, object?>();
        foreach ((string k, JsonElement v) in rootDict)
        {
            rebuiltModel[k] = k == "box_parameters" ? bpDict : v;
        }

        entries["model.json"] = JsonSerializer.SerializeToUtf8Bytes(rebuiltModel, PersonalizationJsonOptions.Indented);

        CmakerManifest manifest = JsonSerializer.Deserialize<CmakerManifest>(entries["manifest.json"], PersonalizationJsonOptions.Default)!;
        foreach (CmakerManifestEntry e in manifest.Entries)
        {
            if (entries.TryGetValue(e.Path, out byte[]? bytes))
            {
                e.SizeBytes = bytes.LongLength;
                e.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            }
        }

        entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, PersonalizationJsonOptions.Indented);

        using var outMs = new MemoryStream();
        using (var outZip = new ZipArchive(outMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] data) in entries)
            {
                ZipArchiveEntry ze = outZip.CreateEntry(path, CompressionLevel.Optimal);
                using Stream es = ze.Open();
                es.Write(data, 0, data.Length);
            }
        }

        return outMs.ToArray();
    }
}
