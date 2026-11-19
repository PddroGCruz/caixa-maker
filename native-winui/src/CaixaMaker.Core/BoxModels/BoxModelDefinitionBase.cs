using System.Globalization;
using System.Text;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.BoxModels;

/// <summary>
/// Implementação base comum para os modelos paramétricos de caixa do Caixa Maker.
/// </summary>
public abstract class BoxModelDefinitionBase : IBoxModelDefinition
{
    public abstract string ModelId { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<string> Features { get; }
    public abstract bool HasLid { get; }
    public abstract bool HasHandles { get; }
    public abstract bool SupportsLidArticulation { get; }
    public abstract int TotalPanels { get; }
    public abstract IReadOnlyList<string> SurfaceIds { get; }

    public virtual IReadOnlyList<string> SupportedParameters { get; } =
    [
        "box_thickness",
        "box_width",
        "box_depth",
        "box_height",
        "folga",
    ];

    public virtual IReadOnlyDictionary<string, double> DefaultParameters { get; } =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["box_thickness"] = BoxParameters.DefaultThickness,
            ["box_width"] = BoxParameters.DefaultWidth,
            ["box_depth"] = BoxParameters.DefaultDepth,
            ["box_height"] = BoxParameters.DefaultHeight,
            ["folga"] = BoxParameters.DefaultFolga,
        };

    public BoxCalculator CreateCalculator(
        double boxThickness,
        double boxWidth,
        double boxDepth,
        double boxHeight,
        double folga = 0.0)
        => new(boxThickness, boxWidth, boxDepth, boxHeight, folga);

    public BoxCalculator CreateCalculator(BoxParameters parameters)
        => new(parameters);

    public BoxDrawer CreateDrawer(BoxCalculator calc)
        => new(calc);

    public abstract BoxLayoutMetrics GetLayoutMetrics(BoxCalculator calc, BoxDrawer drawer);

    public abstract IReadOnlyList<PartSummary> BuildPartsSummary(
        BoxCalculator calc,
        BoxDrawer drawer,
        BoxLayoutMetrics layout,
        JointsSummary joints,
        string folgaNote,
        double effectiveSlotWidth);

    public JointsSummary BuildJointsSummary(BoxCalculator calc)
    {
        ArgumentNullException.ThrowIfNull(calc);

        double decreasedWidth = calc.Width - 2.0 * calc.Thickness;
        double decreasedDepthInner = calc.Depth - 2.0 * calc.Thickness;
        double verticalSpan = calc.CalcHeightJointSpan();

        return new JointsSummary
        {
            Vertical = BuildJointSpec(calc, calc.GetVerticalJoints(), verticalSpan),
            FrontBottom = BuildJointSpec(calc, calc.GetFrontBottomJoints(), decreasedWidth),
            SideBottom = BuildJointSpec(calc, calc.GetSideBottomJoints(), decreasedDepthInner),
        };
    }

    public ComputedDimensionsSummary BuildComputedDimensions(
        BoxCalculator calc,
        BoxDrawer drawer,
        BoxLayoutMetrics layout)
    {
        ArgumentNullException.ThrowIfNull(calc);
        ArgumentNullException.ThrowIfNull(drawer);
        ArgumentNullException.ThrowIfNull(layout);

        double t = calc.Thickness;
        double w = calc.Width;
        double d = calc.Depth;
        double h = calc.Height;
        double sideDepth = layout.SideWidth;

        JointDivision verticalJ = calc.GetVerticalJoints();
        JointDivision frontBaseJ = calc.GetFrontBottomJoints();
        JointDivision sideBaseJ = calc.GetSideBottomJoints();

        return new ComputedDimensionsSummary
        {
            IncreasedWidth = PythonMath.Round(calc.IncreasedWidth, 4),
            IncreasedDepth = PythonMath.Round(calc.IncreasedDepth, 4),
            DecreasedHeight = PythonMath.Round(calc.DecreasedHeight, 4),
            InnerWidth = PythonMath.Round(w - 2.0 * t, 4),
            InnerDepth = PythonMath.Round(d - 2.0 * t, 4),
            InnerHeight = PythonMath.Round(calc.DecreasedHeight, 4),
            JointlessSpan = PythonMath.Round(calc.CalcJointlessSpan(), 4),
            VerticalJointSpan = PythonMath.Round(calc.CalcHeightJointSpan(), 4),
            OuterWidthWithFlange = PythonMath.Round(calc.IncreasedWidth, 4),
            OuterDepthWithHinge = PythonMath.Round(sideDepth, 4),
            TotalHeightNominal = PythonMath.Round(h, 4),
            TotalHeightWithLid = PythonMath.Round(HasLid ? h + t : h, 4),
            FolgaMm = PythonMath.Round(calc.Folga, 4),
            EffectiveSlotWidthMm = PythonMath.Round(calc.CalcSlotWidth(), 4),
            HingeHoleRadius = HasHandles ? PythonMath.Round(t * BoxDrawer.HoleFactor, 4) : null,
            LidWidth = HasLid && layout.LidDepth.HasValue ? PythonMath.Round(calc.IncreasedWidth, 4) : null,
            LidDepth = HasLid && layout.LidDepth.HasValue ? PythonMath.Round(layout.LidDepth.Value, 4) : null,
            VerticalFingerWidthMm = PythonMath.Round(calc.CalcTabLength(verticalJ.ActualLength), 4),
            BaseFrontFingerWidthMm = PythonMath.Round(calc.CalcTabLength(frontBaseJ.ActualLength), 4),
            BaseSideFingerWidthMm = PythonMath.Round(calc.CalcTabLength(sideBaseJ.ActualLength), 4),
            VerticalFingerCount = verticalJ.Count,
            BaseFrontFingerCount = frontBaseJ.Count / 2,
            BaseSideFingerCount = sideBaseJ.Count / 2,
        };
    }

    public BoxCalculationSummary BuildSummary(BoxCalculator calc, BoxDrawer? drawer = null)
    {
        ArgumentNullException.ThrowIfNull(calc);

        BoxDrawer activeDrawer = drawer ?? CreateDrawer(calc);
        BoxLayoutMetrics layout = GetLayoutMetrics(calc, activeDrawer);
        JointsSummary joints = BuildJointsSummary(calc);

        var warnings = new List<string>();
        if (joints.Vertical.SegmentLengthMm < 3.0)
        {
            warnings.Add(
                "Os dentes verticais ficaram abaixo de 3,0 mm devido à altura reduzida em relação à espessura.");
        }

        if (joints.FrontBottom.SegmentLengthMm < 3.0 || joints.SideBottom.SegmentLengthMm < 3.0)
        {
            warnings.Add(
                "Os dentes da base ficaram abaixo de 3,0 mm devido às dimensões reduzidas da caixa.");
        }

        if (joints.Vertical.WasReducedByLimit || joints.FrontBottom.WasReducedByLimit || joints.SideBottom.WasReducedByLimit)
        {
            warnings.Add(
                "A quantidade fixa de espigas foi reduzida automaticamente em uma ou mais arestas curtas para manter o mínimo estrutural (≥ 3,0 mm).");
        }

        double folgaVal = calc.Folga;
        double effectiveSlotWidth = calc.CalcSlotWidth();
        string folgaNote = folgaVal != 0.0
            ? string.Format(
                CultureInfo.InvariantCulture,
                " (folga {0:+0.00;-0.00;+0.00} mm, largura do rasgo {1:F2} mm)",
                folgaVal,
                effectiveSlotWidth)
            : string.Empty;

        IReadOnlyList<PartSummary> parts = BuildPartsSummary(
            calc,
            activeDrawer,
            layout,
            joints,
            folgaNote,
            effectiveSlotWidth);

        double estimatedAreaCm2 = parts.Sum(p => p.EstimatedAreaCm2);
        BoxSheetGeometry2D sheetGeometry = activeDrawer.BuildSheetGeometry(layout, hasHingeEar: HasHandles, modelId: ModelId);

        return new BoxCalculationSummary
        {
            ModelId = ModelId,
            ModelName = DisplayName,
            InputParameters = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["box_thickness"] = PythonMath.Round(calc.Thickness, 4),
                ["box_width"] = PythonMath.Round(calc.Width, 4),
                ["box_depth"] = PythonMath.Round(calc.Depth, 4),
                ["box_height"] = PythonMath.Round(calc.Height, 4),
                ["folga"] = PythonMath.Round(folgaVal, 4),
            },
            ComputedDimensions = BuildComputedDimensions(calc, activeDrawer, layout),
            Joints = joints,
            Parts = parts,
            SvgCanvas = new SvgCanvasSummary
            {
                WidthMm = PythonMath.Round(layout.SheetWidth, 4),
                HeightMm = PythonMath.Round(layout.SheetHeight, 4),
                SingleColumnWidthMm = PythonMath.Round(layout.CanvasWidth, 4),
            },
            Warnings = warnings,
            EstimatedCuttingAreaCm2 = PythonMath.Round(estimatedAreaCm2, 4),
            OuterCutPerimeterMm = PythonMath.Round(sheetGeometry.OuterCutPerimeterMm, 3),
            InternalCutPerimeterMm = PythonMath.Round(sheetGeometry.InternalCutPerimeterMm, 3),
            TotalCutPerimeterMm = PythonMath.Round(sheetGeometry.TotalCutPerimeterMm, 3),
        };
    }

    public BoxCalculationSummary BuildSummary(BoxParameters parameters)
    {
        BoxCalculator calc = BoxParametersValidator.ValidateAndCreateCalculator(parameters);
        return BuildSummary(calc);
    }

    public BoxSheetGeometry2D BuildSheetGeometry(BoxCalculator calc, BoxDrawer? drawer = null)
    {
        ArgumentNullException.ThrowIfNull(calc);

        BoxDrawer activeDrawer = drawer ?? CreateDrawer(calc);
        BoxLayoutMetrics layout = GetLayoutMetrics(calc, activeDrawer);
        return activeDrawer.BuildSheetGeometry(layout, hasHingeEar: HasHandles, modelId: ModelId);
    }

    public BoxSheetGeometry2D BuildSheetGeometry(BoxParameters parameters)
    {
        BoxCalculator calc = BoxParametersValidator.ValidateAndCreateCalculator(parameters);
        return BuildSheetGeometry(calc);
    }

    public string DrawBoxSvg(BoxCalculator calc, BoxDrawer? drawer = null)
    {
        BoxSheetGeometry2D sheet = BuildSheetGeometry(calc, drawer);
        return BoxDrawer.RenderSheetSvg(sheet);
    }

    public string DrawBoxSvg(BoxParameters parameters)
    {
        BoxCalculator calc = BoxParametersValidator.ValidateAndCreateCalculator(parameters);
        return DrawBoxSvg(calc);
    }

    public void ExportSvg(BoxCalculator calc, string outputPath, BoxDrawer? drawer = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("O caminho de saída SVG não pode estar vazio.", nameof(outputPath));
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string svgContent = DrawBoxSvg(calc, drawer);
        File.WriteAllText(outputPath, svgContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public BoxModelCatalogItem ToCatalogItem()
    {
        return new BoxModelCatalogItem
        {
            Id = ModelId,
            Name = DisplayName,
            Description = Description,
            Features = Features,
            HasLid = HasLid,
            HasHandles = HasHandles,
            SupportsLidArticulation = SupportsLidArticulation,
            TotalPanels = TotalPanels,
            SurfaceIds = SurfaceIds,
            SupportedParameters = SupportedParameters,
            DefaultParameters = DefaultParameters,
        };
    }

    protected static JointSpec BuildJointSpec(BoxCalculator calc, JointDivision division, double availableSpan)
    {
        int tabCount = division.Count / 2;
        int slotCount = division.Count - tabCount;

        return new JointSpec
        {
            Count = division.Count,
            SegmentLengthMm = PythonMath.Round(division.ActualLength, 4),
            TabLengthMm = PythonMath.Round(calc.CalcTabLength(division.ActualLength), 4),
            SlotLengthMm = PythonMath.Round(calc.CalcSlotLength(division.ActualLength), 4),
            TabCount = tabCount,
            SlotCount = slotCount,
            AvailableSpanMm = PythonMath.Round(availableSpan, 4),
            WasReducedByLimit = division.WasReducedByLimit,
        };
    }

    protected static PartSummary CreateFrontBackPart(
        BoxCalculator calc,
        JointsSummary joints,
        string folgaNote)
    {
        double t = calc.Thickness;
        double decreasedWidth = calc.Width - 2.0 * t;
        JointSpec verticalJ = joints.Vertical;
        JointSpec bottomJ = joints.FrontBottom;

        string notes = string.Format(
            CultureInfo.InvariantCulture,
            "Corpo {0:F1} × {1:F1} mm com espigas laterais ({2} por lado, {3:F2} mm) e inferiores ({4}, {5:F2} mm){6}.",
            decreasedWidth,
            calc.DecreasedHeight,
            verticalJ.TabCount,
            verticalJ.TabLengthMm,
            bottomJ.TabCount,
            bottomJ.TabLengthMm,
            folgaNote);

        return new PartSummary
        {
            Id = "front_back",
            Name = "Painel Frontal / Traseiro",
            Quantity = 2,
            WidthMm = PythonMath.Round(calc.Width, 3),
            HeightMm = PythonMath.Round(calc.Height, 3),
            ThicknessMm = PythonMath.Round(t, 3),
            Notes = notes,
        };
    }

    protected PartSummary CreateSidePart(
        BoxCalculator calc,
        JointsSummary joints,
        double effectiveSlotWidth)
    {
        double t = calc.Thickness;
        JointSpec verticalJ = joints.Vertical;

        if (HasHandles)
        {
            string notesWithHandles = string.Format(
                CultureInfo.InvariantCulture,
                "Profundidade base {0:F1} mm + orelha traseira com furo r={1:F2} mm e rasgos verticais ({2:F2} × {3:F2} mm, 2ª peça espelhada).",
                calc.IncreasedDepth,
                t * BoxDrawer.HoleFactor,
                verticalJ.SlotLengthMm,
                effectiveSlotWidth);

            return new PartSummary
            {
                Id = "side",
                Name = "Painel Lateral (c/ articulação)",
                Quantity = 2,
                WidthMm = PythonMath.Round(calc.IncreasedDepth + 4.0 * t, 3),
                HeightMm = PythonMath.Round(calc.Height + 2.5 * t, 3),
                ThicknessMm = PythonMath.Round(t, 3),
                Notes = notesWithHandles,
            };
        }

        string straightNotes = string.Format(
            CultureInfo.InvariantCulture,
            "Lateral limpa {0:F1} × {1:F1} mm sem alça, com rasgos verticais ({2:F2} × {3:F2} mm) e espigas inferiores para encaixe direto na base (2ª peça espelhada).",
            calc.IncreasedDepth,
            calc.Height,
            verticalJ.SlotLengthMm,
            effectiveSlotWidth);

        return new PartSummary
        {
            Id = "side",
            Name = "Painel Lateral (borda reta)",
            Quantity = 2,
            WidthMm = PythonMath.Round(calc.IncreasedDepth, 3),
            HeightMm = PythonMath.Round(calc.Height, 3),
            ThicknessMm = PythonMath.Round(t, 3),
            Notes = straightNotes,
        };
    }

    protected static PartSummary CreateBottomPart(BoxCalculator calc, double effectiveSlotWidth)
    {
        double t = calc.Thickness;
        string notes = string.Format(
            CultureInfo.InvariantCulture,
            "Cantos arredondados r={0:F1} mm com 4 fileiras de rasgos passantes (largura efetiva {1:F2} mm) para encaixe das 4 paredes.",
            t,
            effectiveSlotWidth);

        return new PartSummary
        {
            Id = "bottom",
            Name = "Fundo / Base",
            Quantity = 1,
            WidthMm = PythonMath.Round(calc.IncreasedWidth, 3),
            HeightMm = PythonMath.Round(calc.IncreasedDepth, 3),
            ThicknessMm = PythonMath.Round(t, 3),
            Notes = notes,
        };
    }
}
