using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CaixaMaker.Cam.Dxf;
using CaixaMaker.Cam.Nesting;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Nesting;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CaixaMaker.Tests;

public sealed class Stage27NestingEngineAndImportersTests
{
    private readonly ITestOutputHelper _output;

    public Stage27NestingEngineAndImportersTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static IReadOnlyList<(double X, double Y)> CreateLShape(double armLength = 100.0, double armWidth = 25.0) =>
    [
        (0.0, 0.0),
        (armWidth, 0.0),
        (armWidth, armLength - armWidth),
        (armLength, armLength - armWidth),
        (armLength, armLength),
        (0.0, armLength),
    ];

    private static IReadOnlyList<(double X, double Y)> CreateRect(double w, double h, double ox = 0.0, double oy = 0.0) =>
    [
        (ox, oy),
        (ox + w, oy),
        (ox + w, oy + h),
        (ox, oy + h),
    ];

    private static void AssertSheetInvariants(NestingResult result)
    {
        double minX = result.Config.MarginMm - 1e-3;
        double minY = result.Config.MarginMm - 1e-3;
        double maxX = result.Config.SheetWidthMm - result.Config.MarginMm + 1e-3;
        double maxY = result.Config.SheetHeightMm - result.Config.MarginMm + 1e-3;

        foreach (NestingSheetLayout sheet in result.Sheets)
        {
            foreach (PlacedNestingPart p in sheet.Placements)
            {
                p.MinXMm.Should().BeGreaterThanOrEqualTo(minX);
                p.MinYMm.Should().BeGreaterThanOrEqualTo(minY);
                p.MaxXMm.Should().BeLessThanOrEqualTo(maxX);
                p.MaxYMm.Should().BeLessThanOrEqualTo(maxY);
            }

            for (int i = 0; i < sheet.Placements.Count; i++)
            {
                for (int j = i + 1; j < sheet.Placements.Count; j++)
                {
                    PlacedNestingPart a = sheet.Placements[i];
                    PlacedNestingPart b = sheet.Placements[j];

                    // Se uma foi alocada dentro de um furo de outra, não aplicamos checagem externa-externa
                    if (a.PlacedInsideHole || b.PlacedInsideHole)
                    {
                        if (!a.PlacedInsideHole && b.PlacedInsideHole &&
                            PolygonNestingGeometry.IsPointInPolygon(b.TransformedOuterContour[0], a.TransformedOuterContour))
                        {
                            continue;
                        }

                        if (!b.PlacedInsideHole && a.PlacedInsideHole &&
                            PolygonNestingGeometry.IsPointInPolygon(a.TransformedOuterContour[0], b.TransformedOuterContour))
                        {
                            continue;
                        }
                    }

                    PolygonNestingGeometry.PolygonsIntersect(a.TransformedOuterContour, b.TransformedOuterContour)
                        .Should().BeFalse($"peças {a.Part.Name}#{a.InstanceIndex} e {b.Part.Name}#{b.InstanceIndex} não podem se sobrepor");

                    double dist = PolygonNestingGeometry.MinDistanceBetweenPolygons(a.TransformedOuterContour, b.TransformedOuterContour);
                    dist.Should().BeGreaterThanOrEqualTo(result.Config.PartSpacingMm - 1e-3);
                }
            }
        }
    }

    [Fact]
    public void DxfImporter_ReadsLwPolylineCircleArcStitchesLines_ConvertsInches_AndGroupsHierarchy()
    {
        var importer = new DxfImporter();

        // 1) Conversão $INSUNITS = 1 (polegadas -> mm: 2.0 in = 50.8 mm) + LWPOLYLINE fechada + CIRCLE + ARC + LINE costuradas
        const string inchAndStitchDxf = """
            0
            SECTION
            2
            HEADER
            9
            $INSUNITS
            70
            1
            0
            ENDSEC
            0
            SECTION
            2
            ENTITIES
            0
            LINE
            8
            CUT_EXTERNAL
            10
            0.0
            20
            0.0
            11
            2.0
            21
            0.0
            0
            LINE
            8
            CUT_EXTERNAL
            10
            2.0
            20
            0.0
            11
            2.0
            21
            1.0
            0
            LINE
            8
            CUT_EXTERNAL
            10
            2.0
            20
            1.0
            11
            0.0
            21
            1.0
            0
            LINE
            8
            CUT_EXTERNAL
            10
            0.0
            20
            1.0
            11
            0.0
            21
            0.0
            0
            CIRCLE
            8
            CUT_INTERNAL
            10
            1.0
            20
            0.5
            40
            0.2
            0
            ARC
            8
            ENGRAVE_VECTOR
            10
            1.0
            20
            0.5
            40
            0.35
            50
            0.0
            51
            180.0
            0
            ENDSEC
            0
            EOF
            """;

        DxfImportResult resInches = importer.ImportFromString(inchAndStitchDxf, "PlacaPolegadas");
        resInches.UnitScaleToMm.Should().BeApproximately(25.4, 1e-6);
        resInches.Parts.Should().HaveCount(1);
        NestingPart pInch = resInches.Parts[0];
        pInch.WidthMm.Should().BeApproximately(50.8, 1e-2);
        pInch.HeightMm.Should().BeApproximately(25.4, 1e-2);
        pInch.InternalFeatures.Should().Contain(f => f.Layer == NestingSemanticLayer.CutInternal && f.IsClosed);
        pInch.InternalFeatures.Should().Contain(f => f.Layer == NestingSemanticLayer.EngraveVector);

        // 2) DXF em milímetros com 2 peças distintas, furo interno e linha de gravação em ENGRAVE_VECTOR acoplados às respectivas peças-pai
        const string multiPartDxf = """
            0
            SECTION
            2
            HEADER
            9
            $INSUNITS
            70
            4
            0
            ENDSEC
            0
            SECTION
            2
            ENTITIES
            0
            LWPOLYLINE
            8
            CUT_EXTERNAL
            90
            4
            70
            1
            10
            0
            20
            0
            10
            100
            20
            0
            10
            100
            20
            80
            10
            0
            20
            80
            0
            LWPOLYLINE
            8
            CUT_INTERNAL
            90
            4
            70
            1
            10
            20
            20
            20
            10
            60
            20
            20
            10
            60
            20
            50
            10
            20
            20
            50
            0
            LINE
            8
            ENGRAVE_VECTOR
            10
            10
            20
            10
            11
            90
            21
            10
            0
            LWPOLYLINE
            8
            CUT_EXTERNAL
            90
            4
            70
            1
            10
            150
            20
            0
            10
            210
            20
            0
            10
            210
            20
            60
            10
            150
            20
            60
            0
            LINE
            8
            ENGRAVE_VECTOR
            10
            160
            20
            30
            11
            200
            21
            30
            0
            ENDSEC
            0
            EOF
            """;

        DxfImportResult resMulti = importer.ImportFromString(multiPartDxf, "DuasPecas");
        resMulti.UnitScaleToMm.Should().Be(1.0);
        resMulti.Parts.Should().HaveCount(2);

        NestingPart big = resMulti.Parts.OrderByDescending(p => p.OuterAreaMm2).First();
        NestingPart small = resMulti.Parts.OrderBy(p => p.OuterAreaMm2).First();

        big.WidthMm.Should().BeApproximately(100.0, 1e-2);
        big.HeightMm.Should().BeApproximately(80.0, 1e-2);
        big.InternalFeatures.Count(f => f.Layer == NestingSemanticLayer.CutInternal && f.IsClosed).Should().Be(1);
        big.InternalFeatures.Count(f => f.Layer == NestingSemanticLayer.EngraveVector).Should().Be(1);

        small.WidthMm.Should().BeApproximately(60.0, 1e-2);
        small.HeightMm.Should().BeApproximately(60.0, 1e-2);
        small.InternalFeatures.Count(f => f.Layer == NestingSemanticLayer.CutInternal).Should().Be(0);
        small.InternalFeatures.Count(f => f.Layer == NestingSemanticLayer.EngraveVector).Should().Be(1);
    }

    [Fact]
    public void SvgNestingImporter_Reads1To1Mm_PreservesHolesAndEngravingLayers()
    {
        const string svg = """
            <?xml version="1.0" encoding="utf-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" width="260mm" height="120mm" viewBox="0 0 260 120">
              <g id="layer_cut_external">
                <rect x="10" y="10" width="90" height="70" fill="none" stroke="#000000" stroke-width="0.03mm" />
                <rect x="130" y="15" width="50" height="40" fill="none" stroke="#000000" stroke-width="0.03mm" />
              </g>
              <g id="layer_cut_internal">
                <circle cx="55" cy="45" r="12" fill="none" stroke="#B71C1C" stroke-width="0.03mm" />
              </g>
              <g id="layer_engrave_vector">
                <line x1="135" y1="35" x2="175" y2="35" stroke="#0D47A1" stroke-width="0.1mm" />
              </g>
            </svg>
            """;

        var importer = new SvgNestingImporter();
        SvgNestingImportResult res = importer.ImportFromString(svg, "PecasSvg");

        res.DocumentWidthMm.Should().BeApproximately(260.0, 1e-4);
        res.DocumentHeightMm.Should().BeApproximately(120.0, 1e-4);
        res.Parts.Should().HaveCount(2);

        NestingPart p1 = res.Parts.OrderByDescending(p => p.OuterAreaMm2).First();
        p1.WidthMm.Should().BeApproximately(90.0, 0.05);
        p1.HeightMm.Should().BeApproximately(70.0, 0.05);
        p1.InternalFeatures.Should().ContainSingle(f => f.Layer == NestingSemanticLayer.CutInternal && f.IsClosed);

        NestingPart p2 = res.Parts.OrderBy(p => p.OuterAreaMm2).First();
        p2.WidthMm.Should().BeApproximately(50.0, 0.05);
        p2.HeightMm.Should().BeApproximately(40.0, 0.05);
        p2.InternalFeatures.Should().ContainSingle(f => f.Layer == NestingSemanticLayer.EngraveVector);
    }

    [Fact]
    public void BoxPanelNestingAdapter_Extracts6And5PanelsInMemoryWithCoupledPersonalization()
    {
        var adapter = new BoxPanelNestingAdapter();
        var lidModel = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var openModel = BoxModelRegistry.Default.Get(OpenBoxModel.Id);
        var parameters = new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 100.0,
            Folga = 0.0,
        };

        BoxCalculator calc = lidModel.CreateCalculator(parameters);
        var manager = new SurfaceManager(calc, boxModel: lidModel);

        // Adiciona texto vetorizado na tampa e forma paramétrica 1:1 (círculo de corte) na frente
        manager.AddElement(
            new TextPersonalizationElement
            {
                PanelId = SurfaceId.Lid,
                Content = "IFMAKER",
                FontSizeMm = 14.0,
                WidthMm = 60.0,
                HeightMm = 16.0,
                Anchor = AnchorMode.Center,
                RenderMode = TextRenderMode.Outline,
                Operation = CncOperationType.EngraveVector,
            },
            xMm: 100.0,
            yMm: 75.0,
            centerOnSurface: false);

        var circleShape = new ShapePersonalizationElement(
            new GeometricShapeSpec
            {
                Kind = GeometricShapeKind.Circle,
                DiameterMm = 24.0,
            },
            OperationMode.Cut)
        {
            PanelId = SurfaceId.Front,
            WidthMm = 24.0,
            HeightMm = 24.0,
            Anchor = AnchorMode.Center,
        };
        manager.AddElement(circleShape, xMm: 100.0, yMm: 50.0, centerOnSurface: false);

        IReadOnlyList<NestingPart> lidParts = adapter.ExtractParts(lidModel, parameters, manager, boxCopies: 2);
        lidParts.Should().HaveCount(6);
        lidParts.Should().OnlyContain(p => p.Quantity == 2 && p.SourceTag == "Caixa" && p.OuterContour.Count >= 4);

        NestingPart lidPanel = lidParts.Single(p => p.SourcePanelKey == "lid");
        lidPanel.InternalFeatures.Should().Contain(f => f.Layer == NestingSemanticLayer.EngraveVector);

        NestingPart frontPanel = lidParts.Single(p => p.SourcePanelKey == "front");
        frontPanel.InternalFeatures.Should().Contain(f => f.Layer == NestingSemanticLayer.CutInternal && f.IsClosed);

        NestingPart leftPanel = lidParts.Single(p => p.SourcePanelKey == "left");
        leftPanel.InternalFeatures.Count(f => f.Layer == NestingSemanticLayer.CutInternal).Should().BeGreaterThanOrEqualTo(2,
            "lateral com alça possui rasgo de alça e furo de eixo da dobradiça");

        IReadOnlyList<NestingPart> openParts = adapter.ExtractParts(openModel, parameters);
        openParts.Should().HaveCount(5);
        openParts.Should().NotContain(p => p.SourcePanelKey == "lid");
    }

    [Fact]
    public void TrueShapeNestingEngine_RespectsRotationPolicyAndRejectsOversizedPartsCleanly()
    {
        var rect = new NestingPart(
            id: "long_bar",
            name: "Barra 180x60",
            outerContour: CreateRect(180.0, 60.0),
            rotationPolicy: NestingRotationPolicy.Locked0);

        var config = new NestingSheetConfig(
            SheetWidthMm: 100.0,
            SheetHeightMm: 220.0,
            MarginMm: 5.0,
            PartSpacingMm: 3.0);

        var engine = new TrueShapeNestingEngine();

        // Com Locked0, largura 180 mm > 90 mm úteis -> deve ir para UnplacedParts
        NestingResult lockedResult = engine.Optimize([rect], config);
        lockedResult.TotalPlacedInstances.Should().Be(0);
        lockedResult.UnplacedParts.Should().HaveCount(1);
        lockedResult.UnplacedParts[0].Reason.Should().NotBeNullOrWhiteSpace();

        // Com Step90, rotaciona 90° (vira 60x180) e encaixa dentro de 90x210 úteis!
        rect.RotationPolicy = NestingRotationPolicy.Step90;
        NestingResult rotatedResult = engine.Optimize([rect], config);
        rotatedResult.TotalPlacedInstances.Should().Be(1);
        rotatedResult.UnplacedParts.Should().BeEmpty();
        rotatedResult.Sheets.Should().HaveCount(1);
        (rotatedResult.Sheets[0].Placements[0].RotationDeg is 90.0 or 270.0).Should().BeTrue();
        AssertSheetInvariants(rotatedResult);
    }

    [Fact]
    public void TrueShapeNestingEngine_InterlocksConcaveLShapesTighterThanBoundingBoxes()
    {
        // Cada L tem 100x100 mm com braço 25 mm. Dois AABBs nunca caberiam em 140x110 mm úteis!
        var lPart = new NestingPart(
            id: "l_bracket",
            name: "Cantoneira L",
            outerContour: CreateLShape(100.0, 25.0),
            quantity: 2,
            rotationPolicy: NestingRotationPolicy.Step90);

        var config = new NestingSheetConfig(
            SheetWidthMm: 150.0,
            SheetHeightMm: 120.0,
            MarginMm: 5.0,
            PartSpacingMm: 3.0);

        var engine = new TrueShapeNestingEngine();
        NestingResult result = engine.Optimize([lPart], config);

        result.TotalPlacedInstances.Should().Be(2);
        result.Sheets.Should().HaveCount(1, "concavidades das duas peças em L devem se encaixar em única chapa via True-Shape");
        result.UnplacedParts.Should().BeEmpty();
        AssertSheetInvariants(result);

        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[Stage27 True-Shape L-Nesting] 2x L(100x100, braço 25) em retalho 150x120 mm -> {0:0.00}% aproveitamento (bloco usado {1:0.#}x{2:0.#} mm)",
            result.OverallUtilizationPercent,
            result.Sheets[0].UsedBoundsWidthMm,
            result.Sheets[0].UsedBoundsHeightMm));
    }

    [Fact]
    public void TrueShapeNestingEngine_NestsSmallerPartInsideHoleOnlyWhenEnabled()
    {
        // Moldura 200x200 mm com janela interna 140x140 mm
        var frameOuter = CreateRect(200.0, 200.0);
        var frameHole = new NestingPolyline2D(
            CreateRect(140.0, 140.0, 30.0, 30.0),
            IsClosed: true,
            NestingSemanticLayer.CutInternal);

        var frame = new NestingPart("frame", "Moldura", frameOuter, [frameHole]);
        var insert = new NestingPart("insert", "Miolo 60x60", CreateRect(60.0, 60.0));

        var baseConfig = new NestingSheetConfig(
            SheetWidthMm: 220.0,
            SheetHeightMm: 220.0,
            MarginMm: 5.0,
            PartSpacingMm: 3.0);

        var engine = new TrueShapeNestingEngine();

        NestingResult allowed = engine.Optimize([frame, insert], baseConfig with { AllowNestingInHoles = true });
        allowed.TotalPlacedInstances.Should().Be(2);
        allowed.Sheets.Should().HaveCount(1);
        allowed.Sheets[0].Placements.Should().ContainSingle(p => p.Part.Id == "insert" && p.PlacedInsideHole);
        AssertSheetInvariants(allowed);

        NestingResult disallowed = engine.Optimize([frame, insert], baseConfig with { AllowNestingInHoles = false });
        disallowed.TotalPlacedInstances.Should().Be(2);
        disallowed.Sheets.Should().HaveCount(2, "com AllowNestingInHoles=false a peça menor não pode invadir a janela da moldura");
        disallowed.Sheets.SelectMany(s => s.Placements).Should().NotContain(p => p.PlacedInsideHole);
        AssertSheetInvariants(disallowed);
    }

    [Fact]
    public void TrueShapeNestingEngine_DistributesBoxBatchAcrossMultipleSheetsDeterministicallyAndSupportsCancel()
    {
        var adapter = new BoxPanelNestingAdapter();
        var lidModel = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var parameters = new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 180.0,
            BoxDepth = 130.0,
            BoxHeight = 90.0,
            Folga = 0.0,
        };
        IReadOnlyList<NestingPart> parts = adapter.ExtractParts(lidModel, parameters, boxCopies: 3);

        parts.Should().HaveCount(6);

        var config = new NestingSheetConfig(
            SheetWidthMm: 600.0,
            SheetHeightMm: 400.0,
            MarginMm: 5.0,
            PartSpacingMm: 3.0,
            AllowNestingInHoles: true);

        var engine = new TrueShapeNestingEngine();
        NestingResult run1 = engine.Optimize(parts, config);
        NestingResult run2 = engine.Optimize(parts, config);

        run1.TotalRequestedInstances.Should().Be(18);
        run1.TotalPlacedInstances.Should().Be(18);
        run1.UnplacedParts.Should().BeEmpty();
        run1.Sheets.Count.Should().BeGreaterThanOrEqualTo(2);
        AssertSheetInvariants(run1);

        // Determinismo absoluto entre execuções
        run2.Sheets.Count.Should().Be(run1.Sheets.Count);
        run2.OverallUtilizationPercent.Should().Be(run1.OverallUtilizationPercent);
        run2.WinningStrategy.Should().Be(run1.WinningStrategy);

        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[Stage27 Lote 3x Caixa 180x130x90] 18 chapas alocadas em {0} folha(s) 600x400 mm | Estratégia: {1} | Aproveitamento global: {2:0.00}% | Tempo: {3:0.#} ms",
            run1.Sheets.Count,
            run1.WinningStrategy,
            run1.OverallUtilizationPercent,
            run1.Elapsed.TotalMilliseconds));

        // Cancelamento limpo
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Action canceledCall = () => engine.Optimize(parts, config, cancellationToken: cts.Token);
        canceledCall.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void NestingSheetExporter_GeneratesValid1To1SvgDxfPdfAndZipBundle()
    {
        var adapter = new BoxPanelNestingAdapter();
        var lidModel = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var parameters = new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 180.0,
            BoxDepth = 130.0,
            BoxHeight = 90.0,
            Folga = 0.0,
        };
        BoxCalculator calc = lidModel.CreateCalculator(parameters);
        var manager = new SurfaceManager(calc, boxModel: lidModel);
        manager.AddElement(
            new TextPersonalizationElement
            {
                PanelId = SurfaceId.Front,
                Content = "LOTE",
                FontSizeMm = 14.0,
                WidthMm = 50.0,
                HeightMm = 15.0,
                Anchor = AnchorMode.Center,
                RenderMode = TextRenderMode.Outline,
                Operation = CncOperationType.EngraveVector,
            },
            xMm: 90.0,
            yMm: 45.0,
            centerOnSurface: false);

        IReadOnlyList<NestingPart> parts = adapter.ExtractParts(lidModel, parameters, manager, boxCopies: 2);
        var config = new NestingSheetConfig(600.0, 400.0, 5.0, 3.0);
        NestingResult result = new TrueShapeNestingEngine().Optimize(parts, config);

        result.TotalPlacedInstances.Should().Be(12);
        result.Sheets.Should().NotBeEmpty();

        var exporter = new NestingSheetExporter();
        NestingSheetLayout firstSheet = result.Sheets[0];

        string svg = exporter.ExportSheetToSvg(firstSheet, includeSheetBoundary: true);
        XDocument doc = XDocument.Parse(svg);
        doc.Root!.Attribute("width")!.Value.Should().Be("600mm");
        doc.Root.Attribute("height")!.Value.Should().Be("400mm");
        doc.Root.Attribute("viewBox")!.Value.Should().Be("0 0 600 400");
        svg.Should().Contain("layer_cut_external");
        svg.Should().Contain("layer_cut_internal");
        svg.Should().Contain("layer_sheet_bounds");

        string dxf = exporter.ExportSheetToDxf(firstSheet, includeSheetBoundary: true);
        dxf.Should().Contain("AC1015");
        dxf.Should().Contain("CUT_EXTERNAL");
        dxf.Should().Contain("SHEET_BOUNDS");

        byte[] pdf = exporter.ExportSheetToPdf(firstSheet, includeSheetBoundary: true);
        pdf.Length.Should().BeGreaterThan(250);
        Encoding.ASCII.GetString(pdf, 0, 8).Should().StartWith("%PDF-1.4");

        string tempZip = Path.Combine(Path.GetTempPath(), $"cm_stage27_nesting_{Guid.NewGuid():N}.zip");
        try
        {
            exporter.ExportResultToZip(result, tempZip);
            File.Exists(tempZip).Should().BeTrue();

            using ZipArchive zip = ZipFile.OpenRead(tempZip);
            List<string> entryNames = zip.Entries.Select(e => e.FullName).ToList();
            entryNames.Should().Contain("chapa_01.svg");
            entryNames.Should().Contain("chapa_01.dxf");
            entryNames.Should().Contain("chapa_01.pdf");
            entryNames.Should().Contain("relatorio_nesting.txt");
            if (result.Sheets.Count >= 2)
            {
                entryNames.Should().Contain("chapa_02.svg");
                entryNames.Should().Contain("chapa_02.dxf");
                entryNames.Should().Contain("chapa_02.pdf");
            }
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                File.Delete(tempZip);
            }
        }
    }

    [Fact]
    public void EngraveFillCompoundPaths_PreservedAcrossSvgImporterBoxAdapterSvgAndPdfEvenOdd()
    {
        // 1) SvgNestingImporter: <path> composto com furo + dois <rect> concêntricos preenchidos + traço vazado (fill="none")
        const string compoundSvg = """
            <?xml version="1.0" encoding="utf-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" width="140mm" height="90mm" viewBox="0 0 140 90">
              <g id="layer_cut_external">
                <rect x="5" y="5" width="130" height="80" fill="none" stroke="#000000" stroke-width="0.03mm" />
              </g>
              <g id="layer_engrave_fill">
                <path d="M 15 15 L 55 15 L 55 65 L 15 65 Z M 25 25 L 45 25 L 45 55 L 25 55 Z" fill="#6A1B9A" stroke="none" fill-rule="evenodd" />
                <rect x="70" y="15" width="40" height="40" fill="#6A1B9A" stroke="none" />
                <rect x="80" y="25" width="20" height="20" fill="#6A1B9A" stroke="none" />
                <rect x="115" y="15" width="12" height="12" fill="none" stroke="#6A1B9A" stroke-width="0.1mm" />
              </g>
            </svg>
            """;

        var svgImporter = new SvgNestingImporter();
        SvgNestingImportResult imported = svgImporter.ImportFromString(compoundSvg, "PlacaComposta");
        imported.Parts.Should().HaveCount(1);

        NestingPart svgPart = imported.Parts[0];
        List<NestingPolyline2D> filledGroups = svgPart.InternalFeatures
            .Where(f => f.Layer == NestingSemanticLayer.EngraveFill && f.IsFilled)
            .ToList();
        filledGroups.Should().HaveCount(4);

        // Deve haver exatamente 2 grupos compostos (2 subcaminhos do <path> + 2 retângulos concêntricos unificados)
        var byGroup = filledGroups
            .GroupBy(f => f.CompoundGroupId)
            .ToList();
        byGroup.Should().HaveCount(2);
        byGroup.Should().OnlyContain(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() == 2);

        // O retângulo com fill="none" stroke="#6A1B9A" deve manter IsFilled = false (sem preencher sólido)
        svgPart.InternalFeatures
            .Where(f => f.Layer == NestingSemanticLayer.EngraveFill && !f.IsFilled)
            .Should().HaveCount(1);

        // 2) BoxPanelNestingAdapter: texto preenchido ("O") + logo SVG composta (contorno + furo) + contorno stroke-only
        var adapter = new BoxPanelNestingAdapter();
        var lidModel = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var parameters = new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 180.0,
            BoxDepth = 130.0,
            BoxHeight = 90.0,
            Folga = 0.0,
        };
        BoxCalculator calc = lidModel.CreateCalculator(parameters);
        var manager = new SurfaceManager(calc, boxModel: lidModel);

        manager.AddElement(
            new TextPersonalizationElement
            {
                PanelId = SurfaceId.Front,
                Content = "ORAD",
                FontSizeMm = 16.0,
                WidthMm = 60.0,
                HeightMm = 18.0,
                Anchor = AnchorMode.Center,
                RenderMode = TextRenderMode.Fill,
                Operation = CncOperationType.EngraveFill,
            },
            xMm: 90.0,
            yMm: 28.0,
            centerOnSurface: false);

        manager.AddElement(
            new VectorSvgPersonalizationElement
            {
                PanelId = SurfaceId.Front,
                Name = "LogoComposta",
                BaseWidthMm = 40.0,
                BaseHeightMm = 40.0,
                WidthMm = 30.0,
                HeightMm = 30.0,
                Operation = CncOperationType.EngraveFill,
                Paths =
                [
                    new VectorPathItem
                    {
                        D = "M 0 0 L 40 0 L 40 40 L 0 40 Z M 10 10 L 30 10 L 30 30 L 10 30 Z",
                        Operation = CncOperationType.EngraveFill,
                        IsClosed = true,
                        HasFill = true,
                        HasStroke = false,
                    },
                    new VectorPathItem
                    {
                        D = "M 2 2 L 8 2 L 8 8 L 2 8 Z",
                        Operation = CncOperationType.EngraveFill,
                        IsClosed = true,
                        HasFill = false,
                        HasStroke = true,
                    },
                ],
            },
            xMm: 90.0,
            yMm: 62.0,
            centerOnSurface: false);

        IReadOnlyList<NestingPart> boxParts = adapter.ExtractParts(lidModel, parameters, manager, boxCopies: 1);
        NestingPart frontPanel = boxParts.Single(p => p.SourcePanelKey == "front");

        // Deve preservar grupos compostos com >=2 subcaminhos (furos internos das letras O/R/A/D e da logo)
        frontPanel.InternalFeatures
            .Where(f => f.Layer == NestingSemanticLayer.EngraveFill && f.IsFilled && !string.IsNullOrWhiteSpace(f.CompoundGroupId))
            .GroupBy(f => f.CompoundGroupId)
            .Should().Contain(g => g.Count() >= 2);

        // Caminho com HasFill = false deve continuar stroke-only
        frontPanel.InternalFeatures
            .Should().Contain(f => f.Layer == NestingSemanticLayer.EngraveFill && !f.IsFilled);

        // 3) Nesting + Exportação SVG (único <path fill-rule="evenodd" com múltiplos M...Z>) + PDF (operador f*) + DXF
        var config = new NestingSheetConfig(400.0, 300.0, MarginMm: 5.0, PartSpacingMm: 3.0);
        NestingResult nested = new TrueShapeNestingEngine().Optimize([svgPart], config);
        nested.TotalPlacedInstances.Should().Be(1);
        NestingSheetLayout sheet = nested.Sheets[0];

        // Transformação rígida deve preservar CompoundGroupId e IsFilled
        sheet.Placements[0].TransformedInternalFeatures
            .Where(f => f.Layer == NestingSemanticLayer.EngraveFill && f.IsFilled && !string.IsNullOrWhiteSpace(f.CompoundGroupId))
            .GroupBy(f => f.CompoundGroupId)
            .Should().HaveCount(2)
            .And.OnlyContain(g => g.Count() == 2);

        var exporter = new NestingSheetExporter();
        string exportedSvg = exporter.ExportSheetToSvg(sheet, includeSheetBoundary: false);
        XDocument doc = XDocument.Parse(exportedSvg);
        XNamespace svgNs = "http://www.w3.org/2000/svg";
        XElement engraveFillLayer = doc.Descendants(svgNs + "g")
            .Single(g => (string?)g.Attribute("id") == "layer_engrave_fill");

        List<XElement> fillPaths = engraveFillLayer.Elements(svgNs + "path").ToList();
        List<XElement> filledCompoundPaths = fillPaths
            .Where(p => string.Equals((string?)p.Attribute("fill-rule"), "evenodd", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals((string?)p.Attribute("fill"), "none", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Exatamente 2 <path> compostos (em vez de 4 <path> separados!), cada um com >=2 subcaminhos "M ... Z"
        filledCompoundPaths.Should().HaveCount(2);
        foreach (XElement cp in filledCompoundPaths)
        {
            string dAttr = cp.Attribute("d")!.Value;
            dAttr.Count(ch => ch == 'M').Should().Be(2);
            dAttr.Count(ch => ch == 'Z').Should().Be(2);
        }

        // E o caminho stroke-only permanece fill="none"
        fillPaths.Should().ContainSingle(p => string.Equals((string?)p.Attribute("fill"), "none", StringComparison.OrdinalIgnoreCase));

        // PDF 1.4 deve usar o operador de preenchimento par-ímpar f* após os subcaminhos fechados (h)
        byte[] pdfBytes = exporter.ExportSheetToPdf(sheet, includeSheetBoundary: false);
        string pdfStreamText = ExtractDecompressedPdfContentStream(pdfBytes);
        pdfStreamText.Should().Contain("\nf*\n");

        // DXF continua emitindo LWPOLYLINE fechadas na camada ENGRAVE_FILL
        string dxf = exporter.ExportSheetToDxf(sheet, includeSheetBoundary: false);
        dxf.Should().Contain("ENGRAVE_FILL");
        dxf.Should().Contain("LWPOLYLINE");
    }

    private static string ExtractDecompressedPdfContentStream(byte[] pdfBytes)
    {
        byte[] markerStart = Encoding.ASCII.GetBytes("stream\n");
        byte[] markerEnd = Encoding.ASCII.GetBytes("\nendstream");

        int searchOffset = 0;
        var sb = new StringBuilder();
        while (searchOffset < pdfBytes.Length)
        {
            int startIdx = IndexOfBytes(pdfBytes, markerStart, searchOffset);
            if (startIdx < 0)
            {
                break;
            }

            int dataStart = startIdx + markerStart.Length;
            int endIdx = IndexOfBytes(pdfBytes, markerEnd, dataStart);
            if (endIdx < 0)
            {
                break;
            }

            int len = endIdx - dataStart;
            using var ms = new MemoryStream(pdfBytes, dataStart, len, writable: false);
            using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.ASCII);
            sb.Append(reader.ReadToEnd());

            searchOffset = endIdx + markerEnd.Length;
        }

        return sb.ToString();
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int startIndex)
    {
        int limit = haystack.Length - needle.Length;
        for (int i = startIndex; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
