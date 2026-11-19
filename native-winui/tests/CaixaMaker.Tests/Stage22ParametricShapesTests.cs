using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage22ParametricShapesTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public Stage22ParametricShapesTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Stage22Tests_" + Guid.NewGuid().ToString("N")[..10]);
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
            // Melhor esforço na limpeza do diretório temporário.
        }
    }

    [Fact]
    public void ShapeGeometryBuilder_RectangleSharpAndRounded_ProducesExactBoundsAndClampsLimits()
    {
        // 1. Retângulo com canto vivo (CornerRadiusMm = 0) -> polígono fechado exato de 4 vértices
        var sharpSpec = new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 32.5,
            HeightMm = 18.0,
            CornerRadiusMm = 0.0,
        };

        ShapeGeometryResult sharp = ShapeGeometryBuilder.Build(sharpSpec);
        sharp.WidthMm.Should().Be(32.5);
        sharp.HeightMm.Should().Be(18.0);
        sharp.PrimaryContour.Should().HaveCount(4);
        sharp.TightBounds.MinX.Should().BeApproximately(0.0, 1e-4);
        sharp.TightBounds.MinY.Should().BeApproximately(0.0, 1e-4);
        sharp.TightBounds.Width.Should().BeApproximately(32.5, 1e-4);
        sharp.TightBounds.Height.Should().BeApproximately(18.0, 1e-4);
        sharp.SvgPathData.Should().StartWith("M 0 0 L 32.5 0 L 32.5 18 L 0 18 Z");

        // 2. Retângulo com cantos arredondados e clamping automático de raio e dimensões mínimas
        var roundedSpec = new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 24.0,
            HeightMm = 14.0,
            CornerRadiusMm = 50.0, // Deve ser clampado automaticamente em min(24, 14) / 2 = 7.0 mm
        };

        roundedSpec.CornerRadiusMm.Should().BeApproximately(7.0, 1e-6);
        ShapeGeometryResult rounded = ShapeGeometryBuilder.Build(roundedSpec);
        rounded.WidthMm.Should().Be(24.0);
        rounded.HeightMm.Should().Be(14.0);
        rounded.PrimaryContour.Count.Should().BeGreaterOrEqualTo(36);
        rounded.TightBounds.Width.Should().BeApproximately(24.0, 1e-3);
        rounded.TightBounds.Height.Should().BeApproximately(14.0, 1e-3);

        // 3. Clamping de valores abaixo de 0.5 mm ou negativos
        var minSpec = new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = -5.0,
            HeightMm = 0.1,
            CornerRadiusMm = -2.0,
        };
        minSpec.WidthMm.Should().Be(0.5);
        minSpec.HeightMm.Should().Be(0.5);
        minSpec.CornerRadiusMm.Should().Be(0.0);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(10.0)]
    [InlineData(25.4)]
    [InlineData(80.0)]
    public void ShapeGeometryBuilder_Circle_DiscretizesAdaptivelyWithChordalErrorUnder002Mm(double diameterMm)
    {
        var spec = new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Circle,
            DiameterMm = diameterMm,
        };

        ShapeGeometryResult result = ShapeGeometryBuilder.Build(spec);
        result.WidthMm.Should().BeApproximately(diameterMm, 1e-4);
        result.HeightMm.Should().BeApproximately(diameterMm, 1e-4);
        result.TightBounds.Width.Should().BeApproximately(diameterMm, 1e-3);
        result.TightBounds.Height.Should().BeApproximately(diameterMm, 1e-3);

        int count = result.PrimaryContour.Count;
        count.Should().BeInRange(ShapeGeometryBuilder.MinCircleSegments, ShapeGeometryBuilder.MaxCircleSegments);

        double radius = diameterMm * 0.5;
        double halfStepAngle = Math.PI / count;
        double chordalError = radius * (1.0 - Math.Cos(halfStepAngle));
        if (radius <= 60.0)
        {
            chordalError.Should().BeLessOrEqualTo(ShapeGeometryBuilder.MaxChordalErrorMm + 1e-4);
        }
    }

    [Fact]
    public void ShapeGeometryBuilder_Slot_BuildsHorizontalOblongAndDegeneratesSmoothlyWhenEqual()
    {
        // Slot normal L=30, W=10 (mesmo passando WidthMm < HeightMm, L assume max e W assume min)
        var slotSpec = new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Slot,
            WidthMm = 10.0,
            HeightMm = 30.0,
        };

        ShapeGeometryResult slot = ShapeGeometryBuilder.Build(slotSpec);
        slot.WidthMm.Should().Be(30.0);
        slot.HeightMm.Should().Be(10.0);
        slot.TightBounds.Width.Should().BeApproximately(30.0, 1e-3);
        slot.TightBounds.Height.Should().BeApproximately(10.0, 1e-3);
        slot.PrimaryContour.Count.Should().BeGreaterOrEqualTo(36);

        // Slot degenerado L == W -> círculo de diâmetro W
        var eqSpec = new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Slot,
            WidthMm = 12.0,
            HeightMm = 12.0,
        };

        ShapeGeometryResult eqSlot = ShapeGeometryBuilder.Build(eqSpec);
        eqSlot.WidthMm.Should().Be(12.0);
        eqSlot.HeightMm.Should().Be(12.0);
        eqSlot.TightBounds.Width.Should().BeApproximately(12.0, 1e-3);
        eqSlot.TightBounds.Height.Should().BeApproximately(12.0, 1e-3);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void ShapeGeometryBuilder_PolygonFrom3To12Sides_HasExactVertexCountInscribedRadiusAndFlatBottom(int sides)
    {
        const double diameter = 16.0;
        const double radius = diameter * 0.5;

        var spec = new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Polygon,
            DiameterMm = diameter,
            Sides = sides,
        };

        ShapeGeometryResult poly = ShapeGeometryBuilder.Build(spec);
        poly.WidthMm.Should().Be(diameter);
        poly.HeightMm.Should().Be(diameter);
        poly.PrimaryContour.Should().HaveCount(sides);

        // Todos os vértices devem estar inscritos na circunferência de raio D/2 centrada em (r, r)
        foreach (Point2D pt in poly.PrimaryContour)
        {
            double dist = Math.Sqrt(Math.Pow(pt.X - radius, 2) + Math.Pow(pt.Y - radius, 2));
            dist.Should().BeApproximately(radius, 1e-3);
        }

        // Os dois primeiros vértices formam a aresta inferior plana (mesmo Y)
        poly.PrimaryContour[0].Y.Should().BeApproximately(poly.PrimaryContour[1].Y, 1e-3);

        // Valida clamping de Sides em [3, 24]
        var clampLow = new GeometricShapeSpec { Kind = GeometricShapeKind.Polygon, Sides = 1 };
        var clampHigh = new GeometricShapeSpec { Kind = GeometricShapeKind.Polygon, Sides = 99 };
        clampLow.Sides.Should().Be(3);
        clampHigh.Sides.Should().Be(24);
    }

    [Fact]
    public void ShapeGeometryBuilder_Crosshair_GeneratesOrthogonalAxesReferenceCircleAndDefaultEngraveMode()
    {
        var spec = new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Crosshair,
            DiameterMm = 14.0,
        };

        var element = new ShapePersonalizationElement(spec);
        element.OperationMode.Should().Be(OperationMode.Engrave);
        element.WidthMm.Should().Be(14.0);
        element.HeightMm.Should().Be(14.0);
        element.Polylines.Should().HaveCount(3);
        element.Polylines[0].Should().HaveCount(2); // eixo horizontal
        element.Polylines[1].Should().HaveCount(2); // eixo vertical
        element.Polylines[2].Count.Should().BeGreaterOrEqualTo(36); // círculo central de referência
        element.VectorPaths.Should().HaveCount(3);
    }

    [Fact]
    public void GeometryValidator_ValidatePanel_DetectsValidShape_FingerJointCollision_AndShapeOverlap()
    {
        var calc = new BoxCalculator(3.0, 200.0, 150.0, 100.0);
        var manager = new SurfaceManager(calc, safetyMarginMm: 2.0);

        // 1. Furo circular e Crosshair válidos no centro da chapa frontal
        var validHole = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Circle,
            DiameterMm = 16.0,
        });
        validHole.PanelId = SurfaceId.Front;
        manager.AddElement(validHole, centerOnSurface: true);

        var validCrosshair = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Crosshair,
            DiameterMm = 10.0,
        });
        validCrosshair.PanelId = SurfaceId.Front;
        manager.AddElement(validCrosshair, xMm: 30.0, yMm: 30.0, centerOnSurface: false);

        ValidationReport validReport = manager.ValidatePanel(SurfaceId.Front);
        validReport.IsValid.Should().BeTrue();
        validReport.Errors.Should().BeEmpty();
        validReport.Warnings.Should().BeEmpty();

        // 2. Recorte retangular invadindo a zona de dentes Finger Joint (left_corner_tabs: x in [0..3])
        var jointCollidingRect = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 20.0,
            HeightMm = 12.0,
            CornerRadiusMm = 2.0,
        })
        {
            Id = "shape_finger_hit",
            PanelId = SurfaceId.Front,
        };
        manager.AddElement(jointCollidingRect, xMm: 1.5, yMm: 40.0, centerOnSurface: false);

        ValidationReport collisionReport = manager.ValidatePanel(SurfaceId.Front);
        collisionReport.IsValid.Should().BeFalse();
        collisionReport.Errors.Should().Contain(e =>
            e.ElementId == "shape_finger_hit" &&
            e.Code == "COLLISION_FORBIDDEN_ZONE" &&
            e.ZoneId == "left_corner_tabs");

        // 3. Segundo recorte sobrepondo o furo central
        var overlappingSlot = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Slot,
            WidthMm = 28.0,
            HeightMm = 10.0,
        })
        {
            Id = "shape_overlap_slot",
            PanelId = SurfaceId.Front,
        };
        manager.AddElement(overlappingSlot, centerOnSurface: true);

        ValidationReport overlapReport = manager.ValidatePanel(SurfaceId.Front);
        overlapReport.Warnings.Should().Contain(w =>
            w.Code == "ELEMENT_OVERLAP" &&
            (w.ElementId == validHole.Id || w.ElementId == "shape_overlap_slot"));
    }

    [Fact]
    public void CamExporters_ExportShapes1To1InSvgDxfR2000AndPdf14_AcrossAllTargetLayers()
    {
        var calc = new BoxCalculator(3.0, 200.0, 150.0, 100.0);
        var manager = new SurfaceManager(calc);

        // 1. CUT_INTERNAL (Círculo de corte)
        var cutCircle = new ShapePersonalizationElement(
            new GeometricShapeSpec { Kind = GeometricShapeKind.Circle, DiameterMm = 14.0 },
            OperationMode.Cut);
        cutCircle.PanelId = SurfaceId.Front;
        manager.AddElement(cutCircle, xMm: 25.0, yMm: 25.0, centerOnSurface: false);

        // 2. ENGRAVE_VECTOR + ENGRAVE_FILL (Retângulo arredondado em modo Engrave com Hachura 45°)
        var engraveHatchRect = new ShapePersonalizationElement(
            new GeometricShapeSpec
            {
                Kind = GeometricShapeKind.Rectangle,
                WidthMm = 24.0,
                HeightMm = 14.0,
                CornerRadiusMm = 3.0,
                FillHatch = true,
                HatchSpacingMm = 1.0,
            },
            OperationMode.Engrave);
        engraveHatchRect.PanelId = SurfaceId.Front;
        manager.AddElement(engraveHatchRect, xMm: 60.0, yMm: 25.0, centerOnSurface: false);

        // 3. Ambos (Polígono sextavado com contorno em CUT_INTERNAL e preenchimento/marcação em ENGRAVE_FILL / ENGRAVE_VECTOR)
        var bothHex = new ShapePersonalizationElement(
            new GeometricShapeSpec
            {
                Kind = GeometricShapeKind.Polygon,
                DiameterMm = 12.0,
                Sides = 6,
                FillHatch = true,
                HatchSpacingMm = 1.0,
            },
            OperationMode.Both);
        bothHex.PanelId = SurfaceId.Front;
        manager.AddElement(bothHex, xMm: 105.0, yMm: 25.0, centerOnSurface: false);

        // 4. MARK (Cruz de centro para furação)
        var markCross = new ShapePersonalizationElement(
            new GeometricShapeSpec { Kind = GeometricShapeKind.Crosshair, DiameterMm = 10.0 },
            OperationMode.Engrave);
        markCross.PanelId = SurfaceId.Front;
        manager.AddElement(markCross, xMm: 140.0, yMm: 25.0, centerOnSurface: false);

        var exporter = new PersonalizationSvgExporter(manager);
        string svg = exporter.ExportFullBoxSvg();

        // Verifica XML SVG e presença dos elementos nas 4 camadas
        XDocument doc = XDocument.Parse(svg);
        XNamespace svgNs = "http://www.w3.org/2000/svg";

        XElement layerCutInt = doc.Root!.Elements(svgNs + "g").First(g => g.Attribute("id")?.Value == "layer_cut_internal");
        XElement layerEngVec = doc.Root!.Elements(svgNs + "g").First(g => g.Attribute("id")?.Value == "layer_engrave_vector");
        XElement layerEngFill = doc.Root!.Elements(svgNs + "g").First(g => g.Attribute("id")?.Value == "layer_engrave_fill");
        XElement layerMark = doc.Root!.Elements(svgNs + "g").First(g => g.Attribute("id")?.Value == "layer_mark");

        layerCutInt.Descendants(svgNs + "g").Should().Contain(g => (string?)g.Attribute("data-element-id") == cutCircle.Id);
        layerCutInt.Descendants(svgNs + "g").Should().Contain(g => (string?)g.Attribute("data-element-id") == bothHex.Id);
        layerEngVec.Descendants(svgNs + "g").Should().Contain(g => (string?)g.Attribute("data-element-id") == engraveHatchRect.Id);
        layerEngFill.Descendants(svgNs + "g").Should().Contain(g => (string?)g.Attribute("data-element-id") == engraveHatchRect.Id);
        layerMark.Descendants(svgNs + "g").Should().Contain(g => (string?)g.Attribute("data-element-id") == markCross.Id);

        // Verifica exportação DXF R2000 (AC1015) com as layers semânticas
        string dxf = VectorConverter.SvgToDxf(svg);
        dxf.Should().Contain("$ACADVER\n  1\nAC1015");
        dxf.Should().Contain("  8\nCUT_INTERNAL\n");
        dxf.Should().Contain("  8\nENGRAVE_VECTOR\n");
        dxf.Should().Contain("  8\nENGRAVE_FILL\n");
        dxf.Should().Contain("  8\nMARK\n");
        dxf.Should().Contain("\nLWPOLYLINE\n");

        // Verifica exportação PDF 1.4 1:1 (descompactando o stream FlateDecode para validar operadores e cores das 4 layers)
        byte[] pdfBytes = VectorConverter.SvgToPdf(svg);
        string pdfText = Encoding.Latin1.GetString(pdfBytes);
        pdfText.Should().StartWith("%PDF-1.4");
        pdfText.TrimEnd().Should().EndWith("%%EOF");
        pdfText.Should().Contain("/Filter /FlateDecode");

        string decompressedStream = DecompressFirstPdfFlateStream(pdfBytes);
        decompressedStream.Should().Contain("0.7176 0.1098 0.1098 RG"); // #B71C1C (CUT_INTERNAL)
        decompressedStream.Should().Contain("0.051 0.2784 0.6314 RG");  // #0D47A1 (ENGRAVE_VECTOR)
        decompressedStream.Should().Contain("0.4157 0.1059 0.6039 RG"); // #6A1B9A (ENGRAVE_FILL hatch)
        decompressedStream.Should().Contain("0.1059 0.3686 0.1255 RG"); // #1B5E20 (MARK)
    }

    private static string DecompressFirstPdfFlateStream(byte[] pdfBytes)
    {
        string raw = Encoding.Latin1.GetString(pdfBytes);
        int streamIdx = raw.IndexOf("stream\n", StringComparison.Ordinal);
        int start = streamIdx + "stream\n".Length;
        int end = raw.IndexOf("\nendstream", start, StringComparison.Ordinal);
        using var ms = new MemoryStream(pdfBytes, start, end - start);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var sr = new StreamReader(zlib, Encoding.ASCII);
        return sr.ReadToEnd();
    }

    [Fact]
    public void CmakerPackageService_RoundtripWithAllFiveShapes_PreservesSpecsAndZeroExtraAssets()
    {
        var calc = new BoxCalculator(3.0, 210.0, 150.0, 95.0);
        var model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var manager = new SurfaceManager(calc, model);

        var rect = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 34.5,
            HeightMm = 18.2,
            CornerRadiusMm = 3.5,
        })
        { PanelId = SurfaceId.Front };

        var circle = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Circle,
            DiameterMm = 12.8,
        })
        { PanelId = SurfaceId.Back };

        var slot = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Slot,
            WidthMm = 28.0,
            HeightMm = 9.5,
        })
        { PanelId = SurfaceId.Left };

        var poly = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Polygon,
            DiameterMm = 11.0,
            Sides = 8,
            FillHatch = true,
            HatchSpacingMm = 0.75,
        }, OperationMode.Both)
        { PanelId = SurfaceId.Right };

        var cross = new ShapePersonalizationElement(new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Crosshair,
            DiameterMm = 9.0,
        }, OperationMode.Engrave)
        { PanelId = SurfaceId.Lid };

        manager.AddElement(rect, centerOnSurface: true);
        manager.AddElement(circle, centerOnSurface: true);
        manager.AddElement(slot, centerOnSurface: true);
        manager.AddElement(poly, centerOnSurface: true);
        manager.AddElement(cross, centerOnSurface: true);

        var pkgService = new CmakerPackageService(BoxModelRegistry.Default);
        string cmakerPath = Path.Combine(_sandboxRoot, "cinco_formas_stage22.cmaker");

        CmakerPackageBuildResult built = pkgService.SaveToFile(
            new ProjectSaveRequest
            {
                Name = "Projeto 5 Primitivas 1:1",
                Description = "Teste de round-trip das 5 primitivas geométricas paramétricas",
                ModelId = model.ModelId,
                ModelName = model.DisplayName,
                BoxParameters = new BoxParameters
                {
                    BoxWidth = 210.0,
                    BoxDepth = 150.0,
                    BoxHeight = 95.0,
                    BoxThickness = 3.0,
                    Folga = 0.1,
                },
                ViewerState = new ProjectViewerState(),
                Manager = manager,
            },
            cmakerPath);

        File.Exists(cmakerPath).Should().BeTrue();
        built.Metadata.ElementCount.Should().Be(5);
        built.Metadata.AssetCount.Should().Be(0);

        using (ZipArchive zip = ZipFile.OpenRead(cmakerPath))
        {
            zip.Entries.Where(e => e.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty();
        }

        LoadedProjectSession loaded = pkgService.LoadPackage(cmakerPath);
        loaded.AssetCount.Should().Be(0);

        List<ShapePersonalizationElement> restoredShapes = loaded.Manager
            .ListElements()
            .OfType<ShapePersonalizationElement>()
            .ToList();
        restoredShapes.Should().HaveCount(5);

        ShapePersonalizationElement rRect = restoredShapes.Single(s => s.ShapeSpec!.Kind == GeometricShapeKind.Rectangle);
        rRect.ShapeSpec!.WidthMm.Should().BeApproximately(34.5, 1e-4);
        rRect.ShapeSpec.HeightMm.Should().BeApproximately(18.2, 1e-4);
        rRect.ShapeSpec.CornerRadiusMm.Should().BeApproximately(3.5, 1e-4);
        rRect.OperationMode.Should().Be(OperationMode.Cut);

        ShapePersonalizationElement rCircle = restoredShapes.Single(s => s.ShapeSpec!.Kind == GeometricShapeKind.Circle);
        rCircle.ShapeSpec!.DiameterMm.Should().BeApproximately(12.8, 1e-4);

        ShapePersonalizationElement rSlot = restoredShapes.Single(s => s.ShapeSpec!.Kind == GeometricShapeKind.Slot);
        rSlot.ShapeSpec!.WidthMm.Should().BeApproximately(28.0, 1e-4);
        rSlot.ShapeSpec.HeightMm.Should().BeApproximately(9.5, 1e-4);

        ShapePersonalizationElement rPoly = restoredShapes.Single(s => s.ShapeSpec!.Kind == GeometricShapeKind.Polygon);
        rPoly.ShapeSpec!.DiameterMm.Should().BeApproximately(11.0, 1e-4);
        rPoly.ShapeSpec.Sides.Should().Be(8);
        rPoly.ShapeSpec.FillHatch.Should().BeTrue();
        rPoly.OperationMode.Should().Be(OperationMode.Both);

        ShapePersonalizationElement rCross = restoredShapes.Single(s => s.ShapeSpec!.Kind == GeometricShapeKind.Crosshair);
        rCross.ShapeSpec!.DiameterMm.Should().BeApproximately(9.0, 1e-4);
        rCross.OperationMode.Should().Be(OperationMode.Engrave);
    }

    [Fact]
    public void PersonalizationWorkspaceViewModel_SupportsShapeInsertionParametricEditsUndoRedoAndInteractiveTransactions()
    {
        var calc = new BoxCalculator(3.0, 200.0, 150.0, 100.0);
        var model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var vm = new PersonalizationWorkspaceViewModel(calc, model);

        // 1. Inserção das 5 formas geométricas na chapa ativa
        ShapePersonalizationElement rect = vm.AddShape(GeometricShapeKind.Rectangle);
        rect.OperationMode.Should().Be(OperationMode.Cut);
        rect.Operation.Should().Be(CncOperationType.CutInternal);
        rect.Anchor.Should().Be(AnchorMode.Center);
        vm.IsShapeSelected.Should().BeTrue();
        vm.IsRectangleShapeSelected.Should().BeTrue();
        vm.IsCircleShapeSelected.Should().BeFalse();

        ShapePersonalizationElement circle = vm.AddShape(GeometricShapeKind.Circle);
        circle.OperationMode.Should().Be(OperationMode.Cut);
        vm.IsCircleShapeSelected.Should().BeTrue();

        ShapePersonalizationElement slot = vm.AddShape(GeometricShapeKind.Slot);
        slot.OperationMode.Should().Be(OperationMode.Cut);
        vm.IsSlotShapeSelected.Should().BeTrue();

        ShapePersonalizationElement poly = vm.AddShape(GeometricShapeKind.Polygon);
        poly.OperationMode.Should().Be(OperationMode.Cut);
        vm.IsPolygonShapeSelected.Should().BeTrue();

        ShapePersonalizationElement cross = vm.AddShape(GeometricShapeKind.Crosshair);
        cross.OperationMode.Should().Be(OperationMode.Engrave);
        vm.IsCrosshairShapeSelected.Should().BeTrue();

        vm.TotalElementCount.Should().Be(5);

        // 2. Edição paramétrica mantendo o centro fixo em AnchorMode.Center
        vm.SelectElement(circle.Id);
        Point2D centerBefore = circle.CenterMm();
        vm.SelectedShapeDiameterMm = 22.0;
        circle.WidthMm.Should().BeApproximately(22.0, 1e-4);
        circle.HeightMm.Should().BeApproximately(22.0, 1e-4);
        circle.CenterMm().X.Should().BeApproximately(centerBefore.X, 1e-3);
        circle.CenterMm().Y.Should().BeApproximately(centerBefore.Y, 1e-3);

        // 3. Transação interativa BeginInteractiveTransform / EndInteractiveTransform consolida em 1 passo de Undo
        vm.SelectElement(rect.Id);
        vm.FlushHistoryCoalescing();
        int undoBeforeInteractive = vm.UndoCount;

        vm.BeginInteractiveTransform();
        vm.IsInteractiveTransformActive.Should().BeTrue();
        vm.SelectedShapeWidthMm = 30.0;
        vm.SelectedShapeWidthMm = 36.0;
        vm.SelectedShapeHeightMm = 20.0;
        vm.SelectedShapeCornerRadiusMm = 4.0;
        bool committed = vm.EndInteractiveTransform();

        committed.Should().BeTrue();
        vm.IsInteractiveTransformActive.Should().BeFalse();
        vm.UndoCount.Should().Be(undoBeforeInteractive + 1);
        rect.WidthMm.Should().BeApproximately(36.0, 1e-4);
        rect.HeightMm.Should().BeApproximately(20.0, 1e-4);
        rect.ShapeSpec!.CornerRadiusMm.Should().BeApproximately(4.0, 1e-4);

        // 4. Undo e Redo restauram exatamente o estado anterior e posterior da forma
        vm.Undo();
        var rectAfterUndo = (ShapePersonalizationElement)vm.Manager.GetElement(rect.Id);
        rectAfterUndo.WidthMm.Should().BeApproximately(25.0, 1e-4);
        rectAfterUndo.HeightMm.Should().BeApproximately(15.0, 1e-4);
        rectAfterUndo.ShapeSpec!.CornerRadiusMm.Should().BeApproximately(0.0, 1e-4);

        vm.Redo();
        var rectAfterRedo = (ShapePersonalizationElement)vm.Manager.GetElement(rect.Id);
        rectAfterRedo.WidthMm.Should().BeApproximately(36.0, 1e-4);
        rectAfterRedo.HeightMm.Should().BeApproximately(20.0, 1e-4);
        rectAfterRedo.ShapeSpec!.CornerRadiusMm.Should().BeApproximately(4.0, 1e-4);

        // 5. Renderização 2D e 3D sem exceções
        var controller = new SurfaceCanvas2DController();
        using SKBitmap bmp2d = OffscreenViewportRenderer.RenderEditor2DBitmap(
            controller,
            vm.ActiveSurface!,
            vm.AllElements,
            null,
            new RenderEditor2DOptions { WidthPx = 480, HeightPx = 320, SelectedElementId = rectAfterRedo.Id });
        bmp2d.Width.Should().Be(480);

        BoxSheetGeometry2D sheet = model.BuildSheetGeometry(calc, model.CreateDrawer(calc));
        BoxScene3D scene3d = BoxScene3DBuilder.BuildScene(calc, model, sheet, vm.Manager);
        var camera = new Camera3DController();
        using SKBitmap bmp3d = OffscreenViewportRenderer.RenderScene3DBitmap(
            scene3d,
            camera,
            vm.Manager,
            new RenderScene3DOptions { WidthPx = 480, HeightPx = 320, ActiveSurfaceId = SurfaceId.Front });
        bmp3d.Width.Should().Be(480);
    }

    [Fact]
    public void Editor2DGizmoAndCadPanels_KeepShapeSpecInSyncAndExposeContextualCadControls()
    {
        var calc = new BoxCalculator(3.0, 200.0, 150.0, 100.0);
        var model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var vm = new PersonalizationWorkspaceViewModel(calc, model);

        ShapePersonalizationElement slot = vm.AddShape(GeometricShapeKind.Slot);
        slot.Anchor = AnchorMode.TopLeft;
        double startX = slot.XMm;
        double startY = slot.YMm;

        // Em AnchorMode.TopLeft, editar o comprimento preserva o canto superior esquerdo
        vm.SelectedShapeWidthMm = 42.0;
        vm.SelectedShapeHeightMm = 14.0;
        slot.XMm.Should().BeApproximately(startX, 1e-3);
        slot.YMm.Should().BeApproximately(startY, 1e-3);
        slot.WidthMm.Should().BeApproximately(42.0, 1e-3);
        slot.HeightMm.Should().BeApproximately(14.0, 1e-3);

        // Redimensionamento via gizmo 2D sincroniza ShapeSpec em milímetros
        var controller = new SurfaceCanvas2DController();
        controller.UpdateLayout(vm.ActiveSurface!, 640, 420);
        IReadOnlyDictionary<Editor2DHandleType, Point2D> handles = controller.GetHandlesMm(slot);
        (double eastPxX, double eastPxY) = controller.MmToPx(handles[Editor2DHandleType.East].X, handles[Editor2DHandleType.East].Y);

        controller.OnPointerPressed(vm, eastPxX, eastPxY, Editor2DMouseButton.Left).Should().Be(Editor2DHandleType.East);
        (double targetPxX, double targetPxY) = controller.MmToPx(handles[Editor2DHandleType.East].X + 8.0, handles[Editor2DHandleType.East].Y);
        controller.OnPointerMoved(vm, targetPxX, targetPxY, shiftHeld: false).Should().BeTrue();
        controller.OnPointerReleased(vm).Should().BeTrue();

        slot.ShapeSpec!.WidthMm.Should().BeApproximately(50.0, 0.2);

        // Verifica presença das seções e rótulos CAD nos painéis esquerdo e direito
        string repoRoot = FindNativeWinUiRoot();
        string leftXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "CaixaMaker.App", "Views", "Areas", "PersonalizationAreaLeftPanel.xaml"));
        string rightXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "CaixaMaker.App", "Views", "Areas", "PersonalizationAreaRightPanel.xaml"));

        leftXaml.Should().Contain("Formas Geométricas (1:1 mm)");
        leftXaml.Should().Contain("Retângulo");
        leftXaml.Should().Contain("Círculo / Furo");
        leftXaml.Should().Contain("Oblongo / Slot");
        leftXaml.Should().Contain("Polígono");
        leftXaml.Should().Contain("Cruz de Centro");

        rightXaml.Should().Contain("Parâmetros da Forma (mm)");
        rightXaml.Should().Contain("Largura (mm)");
        rightXaml.Should().Contain("Altura (mm)");
        rightXaml.Should().Contain("Raio de Canto (mm)");
        rightXaml.Should().Contain("Diâmetro (mm)");
        rightXaml.Should().Contain("Comprimento L (mm)");
        rightXaml.Should().Contain("Largura W (mm)");
        rightXaml.Should().Contain("Nº de Lados (3–24)");
        rightXaml.Should().Contain("Tamanho do Alvo (mm)");
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

        throw new DirectoryNotFoundException("Diretório native-winui/ não localizado.");
    }
}
