using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Cam.Raster;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Cam.Text;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests.Cam;

public sealed class PersonalizationSvgExporterTests
{
    private readonly TextVectorizer _textVectorizer = new();
    private readonly SvgImporter _svgImporter = new();
    private readonly RasterProcessor _rasterProcessor = new();

    [Fact]
    public void ExportFullBoxSvg_ContainsAllSixCncLayers_ValidXml_AndRotatesLidBy180Degrees()
    {
        var manager = new SurfaceManager(new BoxCalculator(3.0, 200.0, 150.0, 100.0));

        var frontText = new TextPersonalizationElement
        {
            PanelId = SurfaceId.Front,
            Content = "IFAC",
            FontSizeMm = 14.0,
            RenderMode = TextRenderMode.Outline,
        };
        _textVectorizer.VectorizeElement(frontText);
        manager.AddElement(frontText);

        var lidText = new TextPersonalizationElement
        {
            PanelId = SurfaceId.Lid,
            Content = "TAMPA",
            FontSizeMm = 12.0,
            RenderMode = TextRenderMode.Fill,
            RotationDeg = 15.0,
        };
        _textVectorizer.VectorizeElement(lidText);
        manager.AddElement(lidText);

        var markText = new TextPersonalizationElement
        {
            PanelId = SurfaceId.Right,
            Content = "DIR",
            FontSizeMm = 10.0,
            RenderMode = TextRenderMode.Outline,
        };
        _textVectorizer.VectorizeElement(markText);
        markText.Operation = CncOperationType.Mark;
        manager.AddElement(markText);

        RasterImagePersonalizationElement raster = _rasterProcessor.ProcessFromBytes(
            CreateSamplePng(),
            "badge.png",
            panelId: SurfaceId.Back,
            targetWidthMm: 20.0);
        manager.AddElement(raster);

        var exporter = new PersonalizationSvgExporter(manager);
        string svg = exporter.ExportFullBoxSvg();

        XDocument doc = XDocument.Parse(svg);
        XNamespace svgNs = "http://www.w3.org/2000/svg";
        List<string?> layerIds = doc.Root!
            .Elements(svgNs + "g")
            .Select(g => g.Attribute("id")?.Value)
            .ToList();

        layerIds.Should().Equal(
            "layer_engrave_raster",
            "layer_engrave_fill",
            "layer_engrave_vector",
            "layer_mark",
            "layer_cut_internal",
            "layer_cut_external");

        // Elemento na tampa deve receber rotação acumulada +180° (15 + 180 = 195°) na orientação CNC da mesa
        XElement lidGroup = doc.Descendants(svgNs + "g")
            .First(g => g.Attribute("data-element-id")?.Value == lidText.Id);
        lidGroup.Attribute("transform")?.Value.Should().Contain("rotate(195)");

        // Imagem raster vai na layer_engrave_raster
        XElement rasterLayer = doc.Root!.Elements(svgNs + "g").First(g => g.Attribute("id")?.Value == "layer_engrave_raster");
        rasterLayer.Descendants(svgNs + "image").Should().ContainSingle();
    }

    [Fact]
    public void ExportPanelCombinedAndEngraveOnly_HonorCutLayerPresenceAndOrientationFlag()
    {
        var manager = new SurfaceManager(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        SvgImportResult logo = _svgImporter.ImportFromString(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="40mm" height="20mm" viewBox="0 0 40 20">
              <rect x="2" y="2" width="36" height="16" fill="none" stroke="#0D47A1"/>
            </svg>
            """,
            panelId: SurfaceId.Lid);
        logo.Element.RotationDeg = 10.0;
        manager.AddElement(logo.Element);

        var exporter = new PersonalizationSvgExporter(manager);

        string combined = exporter.ExportPanelCombinedSvg(SurfaceId.Lid);
        combined.Should().Contain("layer_cut_external");
        combined.Should().Contain("layer_cut_internal");
        combined.Should().Contain("rotate(190)");

        string engraveUnrotated = exporter.ExportPanelEngraveOnlySvg(SurfaceId.Lid, applyCncOrientation: false);
        engraveUnrotated.Should().NotContain("layer_cut_external");
        engraveUnrotated.Should().NotContain("layer_cut_internal");
        engraveUnrotated.Should().Contain("rotate(10)");

        string engraveRotated = exporter.ExportPanelEngraveOnlySvg(SurfaceId.Lid, applyCncOrientation: true);
        engraveRotated.Should().Contain("rotate(190)");
    }

    [Fact]
    public void ExportAllPanelsBundle_ProducesValidZipWithManifestAndExpectedPanelFiles()
    {
        var managerWithLid = new SurfaceManager(new BoxCalculator(3.0, 180.0, 120.0, 80.0));
        var exporterWithLid = new PersonalizationSvgExporter(managerWithLid);
        ExportBundleResult bundle6 = exporterWithLid.ExportAllPanelsBundle(projectPrefix: "proj");

        using (var ms = new MemoryStream(bundle6.ZipBytes))
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
        {
            List<string> entryNames = zip.Entries.Select(e => e.FullName).ToList();
            entryNames.Should().Contain("manifest.json");
            entryNames.Should().Contain("proj_folha_completa.svg");
            entryNames.Count(n => n.StartsWith("pecas_corte_e_gravacao/", StringComparison.Ordinal)).Should().Be(6);
            entryNames.Count(n => n.StartsWith("pecas_somente_gravacao/", StringComparison.Ordinal)).Should().Be(6);

            using StreamReader sr = new(zip.GetEntry("manifest.json")!.Open());
            using JsonDocument manifestDoc = JsonDocument.Parse(sr.ReadToEnd());
            manifestDoc.RootElement.GetProperty("project_prefix").GetString().Should().Be("proj");
            manifestDoc.RootElement.GetProperty("box_parameters").GetProperty("width_mm").GetDouble().Should().Be(180.0);
        }

        IBoxModelDefinition openModel = BoxModelRegistry.Default.Get(OpenBoxModel.Id);
        var managerOpen = new SurfaceManager(new BoxCalculator(3.0, 180.0, 120.0, 80.0), boxModel: openModel);
        var exporterOpen = new PersonalizationSvgExporter(managerOpen);
        ExportBundleResult bundle5 = exporterOpen.ExportAllPanelsBundle(projectPrefix: "aberta");

        bundle5.Files.Keys.Count(n => n.StartsWith("pecas_corte_e_gravacao/", StringComparison.Ordinal)).Should().Be(5);
        bundle5.Files.Keys.Count(n => n.StartsWith("pecas_somente_gravacao/", StringComparison.Ordinal)).Should().Be(5);
        bundle5.Files.Keys.Should().NotContain(k => k.Contains("_lid_", StringComparison.Ordinal));
    }

    private static byte[] CreateSamplePng()
    {
        using var bmp = new SKBitmap(16, 16, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bmp.Erase(SKColors.Black);
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
