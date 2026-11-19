using System.Globalization;
using System.Security;
using System.Text;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Cam.Text;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.Cam.Calibration;

/// <summary>Parâmetros configuráveis do pente paramétrico de calibração de kerf/folga em MDF.</summary>
public sealed record KerfCalibrationCombOptions
{
    public double ThicknessMm { get; init; } = BoxParameters.DefaultThickness;
    public double CenterFolgaMm { get; init; } = BoxParameters.DefaultFolga;
    public int SlotCount { get; init; } = 7;
    public double StepMm { get; init; } = 0.05;
    public double SlotDepthMm { get; init; } = 10.0;
    public string FontFamily { get; init; } = "Arial";
    public IReadOnlyList<double>? CustomDeltasMm { get; init; }
}

/// <summary>Especificação geométrica individual de um rasgo calibrado no pente de kerf.</summary>
public sealed record KerfCalibrationSlotSpec(
    int Index,
    double FolgaMm,
    double EffectiveWidthMm,
    double DepthMm,
    double XStartMm,
    double XEndMm,
    double CenterXMm,
    string LabelText);

/// <summary>Resultado completo do pente de calibração em 1:1 (metadados + SVG multicamadas).</summary>
public sealed record KerfCalibrationCombResult(
    double ThicknessMm,
    double CenterFolgaMm,
    double TenonWidthMm,
    double TenonLengthMm,
    double SheetWidthMm,
    double SheetHeightMm,
    IReadOnlyList<KerfCalibrationSlotSpec> Slots,
    string SvgContent)
{
    /// <summary>Converte o gabarito gerado para SVG, DXF R2000 (<c>AC1015</c>) ou PDF 1.4 em escala 1:1.</summary>
    public byte[] ToExportBytes(VectorExportFormat format)
        => VectorConverter.SvgToExportBytes(SvgContent, format);
}

/// <summary>
/// Gerador paramétrico 1:1 de Pente de Calibração de Kerf para corte a laser em MDF,
/// produzindo rasgos calibrados (<c>max(0.10, t + folga_i)</c>), espiga padrão lateral de largura <c>t</c>,
/// furo de chaveiro em corte interno e marcações vetoriais graváveis exportáveis em SVG, DXF e PDF.
/// </summary>
public sealed class KerfCalibrationCombGenerator
{
    private const double OuterMarginMm = 4.0;
    private const double CornerChamferMm = 2.0;
    private const double LeftGripWidthMm = 12.5;
    private const double RightShoulderWidthMm = 9.0;
    private const double KeyholeRadiusMm = 2.0;

    private readonly TextVectorizer _textVectorizer;

    public KerfCalibrationCombGenerator(TextVectorizer? textVectorizer = null)
    {
        _textVectorizer = textVectorizer ?? new TextVectorizer();
    }

    public static KerfCalibrationCombGenerator Default { get; } = new();

    public KerfCalibrationCombResult Generate(
        double thicknessMm,
        double centerFolgaMm = 0.0,
        int slotCount = 7,
        double stepMm = 0.05)
        => Generate(new KerfCalibrationCombOptions
        {
            ThicknessMm = thicknessMm,
            CenterFolgaMm = centerFolgaMm,
            SlotCount = slotCount,
            StepMm = stepMm,
        });

    public KerfCalibrationCombResult Generate(KerfCalibrationCombOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        double t = options.ThicknessMm;
        if (!double.IsFinite(t) || t < 0.2 || t > 30.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                t,
                "A espessura do material para calibração deve estar entre 0,2 mm e 30,0 mm.");
        }

        if (!double.IsFinite(options.CenterFolgaMm))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A folga central deve ser finita.");
        }

        if (!double.IsFinite(options.SlotDepthMm) || options.SlotDepthMm < 2.0 || options.SlotDepthMm > 40.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SlotDepthMm,
                "A profundidade dos rasgos deve estar entre 2,0 mm e 40,0 mm.");
        }

        IReadOnlyList<double> folgas = ResolveFolgaSeries(options);
        double slotDepth = Math.Round(options.SlotDepthMm, 4);
        double tenonLength = slotDepth;
        double tenonWidth = Math.Round(t, 4);

        var labels = new string[folgas.Count];
        double maxLabelWidth = 0.0;
        for (int i = 0; i < folgas.Count; i++)
        {
            double roundedFolga = Math.Round(folgas[i], 4);
            string label = Math.Abs(roundedFolga) < 1e-6
                ? "0.00"
                : roundedFolga.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            labels[i] = label;

            TextVectorizationResult measured = _textVectorizer.Vectorize(
                label,
                fontFamily: options.FontFamily,
                fontSizeMm: 2.3,
                renderMode: TextRenderMode.Outline);
            if (measured.WidthMm > maxLabelWidth)
            {
                maxLabelWidth = measured.WidthMm;
            }
        }

        double bridgeWidth = Math.Max(8.5, Math.Round(Math.Max(t + 5.0, maxLabelWidth + 2.4), 2));
        double topHeaderHeight = Math.Round(Math.Max(19.0, t + 10.0), 4);
        double bodyHeight = Math.Round(topHeaderHeight + slotDepth, 4);

        double x0 = OuterMarginMm;
        double y0 = OuterMarginMm;

        var slots = new List<KerfCalibrationSlotSpec>(folgas.Count);
        double cursorX = x0 + LeftGripWidthMm;
        for (int i = 0; i < folgas.Count; i++)
        {
            double folga = Math.Round(folgas[i], 4);
            double slotWidth = Math.Round(Math.Max(0.10, t + folga), 4);
            double xStart = Math.Round(cursorX, 4);
            double xEnd = Math.Round(xStart + slotWidth, 4);
            double xCenter = Math.Round((xStart + xEnd) / 2.0, 4);

            slots.Add(new KerfCalibrationSlotSpec(
                Index: i,
                FolgaMm: folga,
                EffectiveWidthMm: slotWidth,
                DepthMm: slotDepth,
                XStartMm: xStart,
                XEndMm: xEnd,
                CenterXMm: xCenter,
                LabelText: labels[i]));

            cursorX = xEnd + bridgeWidth;
        }

        double rightWallX = Math.Round(slots[^1].XEndMm + ResolveRightShoulderWidth(t), 4);
        double bodyWidth = Math.Round(rightWallX - x0, 4);
        double bottomY = Math.Round(y0 + bodyHeight, 4);
        double slotTopY = Math.Round(bottomY - slotDepth, 4);

        double tenonTopY = Math.Round(y0 + ((topHeaderHeight - tenonWidth) / 2.0), 4);
        double tenonBottomY = Math.Round(tenonTopY + tenonWidth, 4);

        double sheetWidth = Math.Round(bodyWidth + tenonLength + (2.0 * OuterMarginMm), 4);
        double sheetHeight = Math.Round(bodyHeight + (2.0 * OuterMarginMm), 4);

        string outlinePath = BuildCombOutlinePath(
            x0,
            y0,
            rightWallX,
            bottomY,
            slotTopY,
            tenonTopY,
            tenonBottomY,
            tenonLength,
            slots);

        double keyholeCenterX = Math.Round(x0 + 5.5, 4);
        double keyholeCenterY = Math.Round(y0 + 5.5, 4);

        string engraveSvg = BuildEngraveLayerContent(
            t,
            x0,
            y0,
            rightWallX,
            slotTopY,
            tenonTopY,
            tenonBottomY,
            slots,
            options.FontFamily);

        string svg = AssembleSvgDocument(
            sheetWidth,
            sheetHeight,
            outlinePath,
            keyholeCenterX,
            keyholeCenterY,
            engraveSvg);

        return new KerfCalibrationCombResult(
            ThicknessMm: Math.Round(t, 4),
            CenterFolgaMm: Math.Round(options.CenterFolgaMm, 4),
            TenonWidthMm: tenonWidth,
            TenonLengthMm: tenonLength,
            SheetWidthMm: sheetWidth,
            SheetHeightMm: sheetHeight,
            Slots: slots,
            SvgContent: svg);
    }

    public byte[] ExportBytes(
        double thicknessMm,
        VectorExportFormat format,
        double centerFolgaMm = 0.0,
        int slotCount = 7,
        double stepMm = 0.05)
        => Generate(thicknessMm, centerFolgaMm, slotCount, stepMm).ToExportBytes(format);

    private static double ResolveRightShoulderWidth(double thicknessMm)
        => Math.Max(RightShoulderWidthMm, thicknessMm + 5.0);

    private static IReadOnlyList<double> ResolveFolgaSeries(KerfCalibrationCombOptions options)
    {
        if (options.CustomDeltasMm is { Count: > 0 } custom)
        {
            return custom;
        }

        if (options.SlotCount < 1 || options.SlotCount > 21)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SlotCount,
                "A quantidade de rasgos do pente deve ficar entre 1 e 21.");
        }

        if (!double.IsFinite(options.StepMm) || options.StepMm < 0.01 || options.StepMm > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.StepMm,
                "O passo entre rasgos deve estar entre 0,01 mm e 1,00 mm.");
        }

        var list = new double[options.SlotCount];
        double half = (options.SlotCount - 1) / 2.0;
        for (int i = 0; i < options.SlotCount; i++)
        {
            list[i] = Math.Round(options.CenterFolgaMm + ((i - half) * options.StepMm), 4);
        }

        return list;
    }

    private static string BuildCombOutlinePath(
        double x0,
        double y0,
        double rightWallX,
        double bottomY,
        double slotTopY,
        double tenonTopY,
        double tenonBottomY,
        double tenonLength,
        IReadOnlyList<KerfCalibrationSlotSpec> slots)
    {
        double c = CornerChamferMm;
        double tenonTipX = Math.Round(rightWallX + tenonLength, 4);

        var sb = new StringBuilder(1024);
        sb.Append(CultureInfo.InvariantCulture, $"M {Fmt(x0 + c)},{Fmt(y0)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(rightWallX - c)},{Fmt(y0)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(rightWallX)},{Fmt(y0 + c)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(rightWallX)},{Fmt(tenonTopY)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(tenonTipX)},{Fmt(tenonTopY)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(tenonTipX)},{Fmt(tenonBottomY)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(rightWallX)},{Fmt(tenonBottomY)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(rightWallX)},{Fmt(bottomY - c)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(rightWallX - c)},{Fmt(bottomY)} ");

        // Percorre os rasgos da direita para a esquerda na borda inferior fechando o contorno sem auto-interseções.
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            KerfCalibrationSlotSpec slot = slots[i];
            sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(slot.XEndMm)},{Fmt(bottomY)} ");
            sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(slot.XEndMm)},{Fmt(slotTopY)} ");
            sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(slot.XStartMm)},{Fmt(slotTopY)} ");
            sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(slot.XStartMm)},{Fmt(bottomY)} ");
        }

        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(x0 + c)},{Fmt(bottomY)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(x0)},{Fmt(bottomY - c)} ");
        sb.Append(CultureInfo.InvariantCulture, $"L {Fmt(x0)},{Fmt(y0 + c)} ");
        sb.Append('Z');
        return sb.ToString();
    }

    private string BuildEngraveLayerContent(
        double thicknessMm,
        double x0,
        double y0,
        double rightWallX,
        double slotTopY,
        double tenonTopY,
        double tenonBottomY,
        IReadOnlyList<KerfCalibrationSlotSpec> slots,
        string fontFamily)
    {
        CncLayerSpec engraveSpec = CncLayerSpecs.EngraveVector;
        var sb = new StringBuilder(4096);

        string title = string.Format(
            CultureInfo.InvariantCulture,
            "MDF {0:0.#}mm - KERF",
            thicknessMm);
        AppendVectorizedText(
            sb,
            title,
            fontFamily,
            fontSizeMm: 3.0,
            xLeftMm: x0 + 11.5,
            yTopMm: y0 + 2.5,
            engraveSpec);

        string tenonText = string.Format(CultureInfo.InvariantCulture, "t={0:0.#}", thicknessMm);
        TextVectorizationResult tenonVec = _textVectorizer.Vectorize(
            tenonText,
            fontFamily: fontFamily,
            fontSizeMm: 2.3,
            renderMode: TextRenderMode.Outline);
        double tenonLabelX = rightWallX - tenonVec.WidthMm - 1.5;
        double tenonLabelY = ((tenonTopY + tenonBottomY) / 2.0) - (tenonVec.HeightMm / 2.0);
        if (tenonLabelX > (slots[^1].XEndMm + 1.0))
        {
            AppendVectorizedPaths(sb, tenonVec.Paths, tenonLabelX, tenonLabelY, engraveSpec);
        }

        foreach (KerfCalibrationSlotSpec slot in slots)
        {
            TextVectorizationResult slotVec = _textVectorizer.Vectorize(
                slot.LabelText,
                fontFamily: fontFamily,
                fontSizeMm: 2.3,
                renderMode: TextRenderMode.Outline);

            double labelX = slot.CenterXMm - (slotVec.WidthMm / 2.0);
            double labelY = slotTopY - slotVec.HeightMm - 2.0;
            AppendVectorizedPaths(sb, slotVec.Paths, labelX, labelY, engraveSpec);
        }

        return sb.ToString();
    }

    private void AppendVectorizedText(
        StringBuilder sb,
        string text,
        string fontFamily,
        double fontSizeMm,
        double xLeftMm,
        double yTopMm,
        CncLayerSpec spec)
    {
        TextVectorizationResult vec = _textVectorizer.Vectorize(
            text,
            fontFamily: fontFamily,
            fontSizeMm: fontSizeMm,
            renderMode: TextRenderMode.Outline);
        AppendVectorizedPaths(sb, vec.Paths, xLeftMm, yTopMm, spec);
    }

    private static void AppendVectorizedPaths(
        StringBuilder sb,
        IReadOnlyList<VectorPathItem> paths,
        double offsetXMm,
        double offsetYMm,
        CncLayerSpec spec)
    {
        if (paths.Count == 0)
        {
            return;
        }

        sb.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({Fmt(offsetXMm)},{Fmt(offsetYMm)})\">");
        foreach (VectorPathItem path in paths)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<path d=\"{Esc(path.D)}\" fill=\"none\" stroke=\"{Esc(spec.Stroke)}\" stroke-width=\"{Esc(spec.StrokeWidth)}\" />");
        }
        sb.Append("</g>");
    }

    private static string AssembleSvgDocument(
        double sheetWidthMm,
        double sheetHeightMm,
        string outlinePathData,
        double keyholeCenterX,
        double keyholeCenterY,
        string engraveLayerContent)
    {
        string wStr = Fmt(sheetWidthMm);
        string hStr = Fmt(sheetHeightMm);
        CncLayerSpec extSpec = CncLayerSpecs.CutExternal;
        CncLayerSpec intSpec = CncLayerSpecs.CutInternal;
        CncLayerSpec engraveSpec = CncLayerSpecs.EngraveVector;

        var sb = new StringBuilder(8192);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" width=\"{wStr}mm\" height=\"{hStr}mm\" viewBox=\"0 0 {wStr} {hStr}\">");
        sb.Append("<defs />");

        sb.Append(CultureInfo.InvariantCulture,
            $"<g id=\"{Esc(engraveSpec.Id)}\" inkscape:label=\"{Esc(engraveSpec.Label)}\" inkscape:groupmode=\"layer\" stroke=\"{Esc(engraveSpec.Stroke)}\" stroke-width=\"{Esc(engraveSpec.StrokeWidth)}\" fill=\"{Esc(engraveSpec.Fill)}\">");
        sb.Append(engraveLayerContent);
        sb.Append("</g>");

        sb.Append(CultureInfo.InvariantCulture,
            $"<g id=\"{Esc(intSpec.Id)}\" inkscape:label=\"{Esc(intSpec.Label)}\" inkscape:groupmode=\"layer\" stroke=\"{Esc(intSpec.Stroke)}\" stroke-width=\"{Esc(intSpec.StrokeWidth)}\" fill=\"{Esc(intSpec.Fill)}\">");
        sb.Append(CultureInfo.InvariantCulture,
            $"<circle cx=\"{Fmt(keyholeCenterX)}\" cy=\"{Fmt(keyholeCenterY)}\" r=\"{Fmt(KeyholeRadiusMm)}\" fill=\"none\" stroke=\"{Esc(intSpec.Stroke)}\" stroke-width=\"{Esc(intSpec.StrokeWidth)}\" />");
        sb.Append("</g>");

        sb.Append(CultureInfo.InvariantCulture,
            $"<g id=\"{Esc(extSpec.Id)}\" inkscape:label=\"{Esc(extSpec.Label)}\" inkscape:groupmode=\"layer\" stroke=\"{Esc(extSpec.Stroke)}\" stroke-width=\"{Esc(extSpec.StrokeWidth)}\" fill=\"{Esc(extSpec.Fill)}\">");
        sb.Append(CultureInfo.InvariantCulture,
            $"<path d=\"{Esc(outlinePathData)}\" fill=\"none\" stroke=\"{Esc(extSpec.Stroke)}\" stroke-width=\"{Esc(extSpec.StrokeWidth)}\" />");
        sb.Append("</g>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Fmt(double value)
    {
        if (Math.Abs(value) < 5e-5)
        {
            return "0";
        }

        string s = value.ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static string Esc(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
