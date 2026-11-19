using System.Text.Json;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;
using CaixaMaker.Tests.Support;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Projects;

public sealed class ProjectPersistenceParityTests
{
    [Fact]
    public void ProjectRepository_SaveUpdateListLoadDelete_PreservesCreatedAtAndPreventsTmpLeak()
    {
        using var temp = new TempDirectoryScope();
        var repo = new ProjectRepository(temp.GetPath("projects"));
        var boxParams = new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 220.0,
            BoxDepth = 160.0,
            BoxHeight = 110.0,
            Folga = 0.15,
        };

        SurfaceManager manager = SurfaceManager.FromParameters(boxParams, BoxWithLidAndHandlesModel.Id, safetyMarginMm: 2.5);
        manager.AddElement(new TextPersonalizationElement
        {
            Name = "Título IFMaker",
            Content = "IFAC",
            PanelId = SurfaceId.Lid,
            WidthMm = 50.0,
            HeightMm = 18.0,
            Anchor = AnchorMode.Center,
            Vectorized = true,
            VectorPaths = [new VectorPathItem { D = "M 0 0 L 50 18" }],
        });

        ProjectRecord created = repo.SaveFromRequest(new ProjectSaveRequest
        {
            Name = "Caixa Laboratório v1",
            ModelId = BoxWithLidAndHandlesModel.Id,
            BoxParameters = boxParams,
            ViewerState = new ProjectViewerState { Exploded = true, LidAngle = 45.0, ActiveSurface = "lid" },
            Manager = manager,
            SafetyMarginMm = 2.5,
        });

        created.Id.Should().StartWith("proj_");
        created.SchemaVersion.Should().Be(1);
        Directory.GetFiles(repo.ProjectsDir, "*.tmp").Should().BeEmpty();

        ProjectRecord updated = repo.UpdateFromRequest(created.Id, new ProjectSaveRequest
        {
            Name = "Caixa Laboratório v2",
            ModelId = BoxWithLidAndHandlesModel.Id,
            BoxParameters = boxParams,
            ViewerState = new ProjectViewerState { Exploded = false, LidAngle = 0.0, ActiveSurface = "front" },
            Manager = manager,
            SafetyMarginMm = 2.5,
        });

        updated.Id.Should().Be(created.Id);
        updated.CreatedAt.Should().Be(created.CreatedAt);
        updated.Name.Should().Be("Caixa Laboratório v2");

        IReadOnlyList<ProjectSummary> list = repo.ListProjects();
        list.Should().ContainSingle(x => x.Id == created.Id);
        list[0].ElementCount.Should().Be(1);
        list[0].ValidationValid.Should().BeTrue();

        LoadedProjectSession opened = repo.OpenProject(created.Id);
        opened.BoxParameters.Should().Be(boxParams);
        opened.ViewerState.ActiveSurface.Should().Be("front");
        opened.Manager.ElementCount.Should().Be(1);
        opened.Manager.ListElements().Single().Anchor.Should().Be(AnchorMode.Center);

        repo.DeleteProject(created.Id);
        repo.ListProjects().Should().BeEmpty();
        Action loadDeleted = () => repo.LoadProject(created.Id);
        loadDeleted.Should().Throw<FileNotFoundException>();
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a")]
    [InlineData("bad id with spaces")]
    public void ProjectRepository_RejectsUnsafeProjectIds(string badId)
    {
        using var temp = new TempDirectoryScope();
        var repo = new ProjectRepository(temp.GetPath("projects"));

        Action act = () => repo.LoadProject(badId);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PortablePackageService_ExportAndImportBundle_RoundTripsAllDataCleanly()
    {
        using var temp = new TempDirectoryScope();
        var repo = new ProjectRepository(temp.GetPath("projects"));
        var service = new PortablePackageService();

        var boxParams = new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 180.0,
            BoxDepth = 120.0,
            BoxHeight = 90.0,
            Folga = 0.1,
        };
        SurfaceManager manager = SurfaceManager.FromParameters(boxParams, OpenBoxModel.Id, safetyMarginMm: 3.0);
        manager.AddElement(new VectorSvgPersonalizationElement
        {
            Name = "Brasão Vetorial",
            PanelId = SurfaceId.Front,
            WidthMm = 35.0,
            HeightMm = 25.0,
            SourceFilename = "brasao.svg",
            Paths = [new VectorPathItem { D = "M 0 0 L 35 0 L 35 25 L 0 25 Z" }],
        });

        PortablePackageExportResult exported = service.ExportBundle(new ProjectSaveRequest
        {
            Name = "Caixa Aberta / IFAC!",
            ModelId = OpenBoxModel.Id,
            BoxParameters = boxParams,
            ViewerState = new ProjectViewerState { Exploded = true, ActiveSurface = "front" },
            Manager = manager,
            SafetyMarginMm = 3.0,
        });

        exported.Filename.Should().Be("Caixa_Aberta_IFAC.caixamaker.json");
        exported.Bundle.Format.Should().Be(PortableProjectBundle.CanonicalFormat);
        exported.Bundle.SchemaVersion.Should().Be(1);
        exported.Bundle.AppVersion.Should().Be(AppVersion.Version);

        string bundlePath = temp.GetPath(exported.Filename);
        service.ExportBundleToFile(new ProjectSaveRequest
        {
            Name = "Caixa Aberta / IFAC!",
            ModelId = OpenBoxModel.Id,
            BoxParameters = boxParams,
            ViewerState = new ProjectViewerState { Exploded = true, ActiveSurface = "front" },
            Manager = manager,
            SafetyMarginMm = 3.0,
        }, bundlePath, appVersion: AppVersion.Version);

        LoadedProjectSession imported = service.ImportBundleFile(bundlePath, repo);
        imported.Summary.ModelId.Should().Be(OpenBoxModel.Id);
        imported.BoxParameters.Should().Be(boxParams);
        imported.ViewerState.Exploded.Should().BeTrue();
        imported.Manager.SafetyMarginMm.Should().Be(3.0);
        imported.Manager.ElementCount.Should().Be(1);
        repo.ListProjects().Should().ContainSingle(x => x.Id == imported.Summary.Id);
    }

    [Fact]
    public void PortablePackageService_ImportsLegacyV120WrappedBundleWithFallbackModelAndMmKeys()
    {
        const string legacyWrappedJson = """
        {
          "bundle": {
            "format": "caixa-maker-project",
            "schema_version": 1,
            "app_version": "1.2.0",
            "name": "Legado Python",
            "safety_margin_mm": 2.0,
            "personalization": {
              "model_id": "box_with_lid_and_handles",
              "box_parameters": {
                "width_mm": 210.0,
                "depth_mm": 140.0,
                "height_mm": 95.0,
                "thickness_mm": 3.0,
                "folga_mm": 0.25
              },
              "safety_margin_mm": 2.0,
              "elements": [
                {
                  "id": "legacyel0001",
                  "name": "Texto v1.2",
                  "element_type": "text",
                  "panel_id": "front",
                  "x_mm": 85.0,
                  "y_mm": 40.0,
                  "width_mm": 30.0,
                  "height_mm": 12.0,
                  "rotation_deg": 0.0,
                  "preserve_aspect_ratio": true,
                  "operation": "engrave_vector",
                  "anchor": "center",
                  "rel_x": 0.5,
                  "rel_y": 0.5,
                  "visible": true,
                  "locked": false,
                  "content": "PIBIT IFAC",
                  "font_family": "Liberation Sans",
                  "font_size_mm": 10.0,
                  "vectorized": true,
                  "vector_paths": [
                    { "d": "M 0 0 L 30 12", "operation": "engrave_vector", "is_closed": false, "has_fill": false, "has_stroke": true }
                  ]
                }
              ]
            }
          }
        }
        """;

        var service = new PortablePackageService();
        LoadedProjectSession session = service.ImportBundleJson(legacyWrappedJson);

        session.Summary.Name.Should().Be("Legado Python");
        session.Summary.ModelId.Should().Be(BoxWithLidAndHandlesModel.Id);
        session.BoxParameters.BoxWidth.Should().Be(210.0);
        session.BoxParameters.BoxDepth.Should().Be(140.0);
        session.BoxParameters.BoxHeight.Should().Be(95.0);
        session.BoxParameters.BoxThickness.Should().Be(3.0);
        session.BoxParameters.Folga.Should().Be(0.25);

        PersonalizationElement el = session.Manager.GetElement("legacyel0001");
        el.Anchor.Should().Be(AnchorMode.Center);
        ((TextPersonalizationElement)el).Content.Should().Be("PIBIT IFAC");
    }
}
