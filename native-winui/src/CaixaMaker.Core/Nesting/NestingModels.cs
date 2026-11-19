namespace CaixaMaker.Core.Nesting;

/// <summary>Camadas semânticas CNC/CAM suportadas no motor de nesting 1:1.</summary>
public enum NestingSemanticLayer
{
    CutExternal,
    CutInternal,
    EngraveVector,
    EngraveFill,
    Mark,
    EngraveRaster,
}

/// <summary>Política de rotação permitida para uma peça durante o aproveitamento da chapa.</summary>
public enum NestingRotationPolicy
{
    /// <summary>Mantém 0° fixo (respeita direção de veio do MDF/compensado).</summary>
    Locked0,

    /// <summary>Permite rotações em incrementos de 180° (0°, 180°).</summary>
    Step180,

    /// <summary>Permite rotações ortogonais de 90° (0°, 90°, 180°, 270°).</summary>
    Step90,

    /// <summary>Permite rotações em passos de 45° (0°, 45°, ..., 315°).</summary>
    Step45,
}

/// <summary>Polilinha 2D em milímetros pertencente a uma camada semântica de corte ou gravação.</summary>
public sealed record NestingPolyline2D(
    IReadOnlyList<(double X, double Y)> Points,
    bool IsClosed,
    NestingSemanticLayer Layer,
    double StrokeWidthMm = 0.1,
    string? SourceLayerName = null,
    string? CompoundGroupId = null,
    bool IsFilled = true);

/// <summary>
/// Sobreposição raster em milímetros rigidamente acoplada a uma <see cref="NestingPart"/>.
/// <para><see cref="X"/> e <see cref="Y"/> correspondem ao canto superior esquerdo da imagem antes de girar <see cref="RotationDeg"/> ao redor de seu centro.</para>
/// </summary>
public sealed record NestingRasterOverlay(
    double X,
    double Y,
    double WidthMm,
    double HeightMm,
    double RotationDeg,
    byte[] ImageBytes,
    string? SourceElementId = null);

/// <summary>
/// Peça individual normalizada para nesting 2D 1:1, com contorno externo fechado e origem local em <c>(0, 0)</c>
/// e todas as feições internas (furos, textos, vetores e rasters) acopladas ao seu sistema de referência.
/// </summary>
public sealed class NestingPart
{
    private readonly double _holesAreaMm2;

    public string Id { get; }
    public string Name { get; }
    public string SourceTag { get; }
    public string? SourcePanelKey { get; }
    public IReadOnlyList<(double X, double Y)> OuterContour { get; }
    public IReadOnlyList<NestingPolyline2D> InternalFeatures { get; }
    public IReadOnlyList<NestingRasterOverlay> RasterOverlays { get; }

    public int Quantity { get; set; }
    public NestingRotationPolicy RotationPolicy { get; set; }
    public bool Enabled { get; set; }

    public double WidthMm { get; }
    public double HeightMm { get; }
    public double OuterAreaMm2 { get; }
    public double NetAreaMm2 => Math.Max(0.0, OuterAreaMm2 - _holesAreaMm2);

    public NestingPart(
        string id,
        string name,
        IReadOnlyList<(double X, double Y)> outerContour,
        IReadOnlyList<NestingPolyline2D>? internalFeatures = null,
        IReadOnlyList<NestingRasterOverlay>? rasterOverlays = null,
        string sourceTag = "Externo",
        string? sourcePanelKey = null,
        int quantity = 1,
        NestingRotationPolicy rotationPolicy = NestingRotationPolicy.Step90,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(outerContour);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Identificador da peça não pode ser vazio.", nameof(id));
        }

        IReadOnlyList<(double X, double Y)> normalizedOuter = PolygonNestingGeometry.NormalizeClosedPolygon(outerContour, ensureCcw: true);
        (double minX, double minY, double maxX, double maxY) = PolygonNestingGeometry.ComputeBounds(normalizedOuter);

        var shiftedOuter = new (double X, double Y)[normalizedOuter.Count];
        for (int i = 0; i < normalizedOuter.Count; i++)
        {
            shiftedOuter[i] = (normalizedOuter[i].X - minX, normalizedOuter[i].Y - minY);
        }

        var shiftedFeatures = new List<NestingPolyline2D>();
        double holesArea = 0.0;
        if (internalFeatures is not null)
        {
            foreach (NestingPolyline2D feat in internalFeatures)
            {
                if (feat.Points.Count < 2)
                {
                    continue;
                }

                IReadOnlyList<(double X, double Y)> cleanPts = feat.IsClosed && feat.Points.Count >= 3
                    ? PolygonNestingGeometry.NormalizeClosedPolygon(feat.Points, ensureCcw: true)
                    : PolygonNestingGeometry.NormalizeOpenPolyline(feat.Points);

                if (cleanPts.Count < 2)
                {
                    continue;
                }

                var movedPts = new (double X, double Y)[cleanPts.Count];
                for (int i = 0; i < cleanPts.Count; i++)
                {
                    movedPts[i] = (cleanPts[i].X - minX, cleanPts[i].Y - minY);
                }

                if (feat.IsClosed && feat.Layer == NestingSemanticLayer.CutInternal && movedPts.Length >= 3)
                {
                    holesArea += PolygonNestingGeometry.AbsArea(movedPts);
                }

                shiftedFeatures.Add(feat with { Points = movedPts });
            }
        }

        var shiftedRasters = new List<NestingRasterOverlay>();
        if (rasterOverlays is not null)
        {
            foreach (NestingRasterOverlay raster in rasterOverlays)
            {
                shiftedRasters.Add(raster with
                {
                    X = raster.X - minX,
                    Y = raster.Y - minY,
                });
            }
        }

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        SourceTag = string.IsNullOrWhiteSpace(sourceTag) ? "Externo" : sourceTag;
        SourcePanelKey = sourcePanelKey;
        OuterContour = shiftedOuter;
        InternalFeatures = shiftedFeatures;
        RasterOverlays = shiftedRasters;
        Quantity = Math.Max(1, quantity);
        RotationPolicy = rotationPolicy;
        Enabled = enabled;

        WidthMm = Math.Max(0.0, maxX - minX);
        HeightMm = Math.Max(0.0, maxY - minY);
        OuterAreaMm2 = PolygonNestingGeometry.AbsArea(shiftedOuter);
        _holesAreaMm2 = Math.Min(OuterAreaMm2, holesArea);
    }
}

/// <summary>Parâmetros da chapa ou retalho útil para o motor de nesting 2D.</summary>
public sealed record NestingSheetConfig(
    double SheetWidthMm = 600.0,
    double SheetHeightMm = 400.0,
    double MarginMm = 5.0,
    double PartSpacingMm = 3.0,
    bool AllowNestingInHoles = true,
    NestingRotationPolicy? GlobalRotationOverride = null,
    double PlacementStepMm = 0.0)
{
    public double UsableWidthMm => Math.Max(0.0, SheetWidthMm - (2.0 * MarginMm));
    public double UsableHeightMm => Math.Max(0.0, SheetHeightMm - (2.0 * MarginMm));
}

/// <summary>Instância de peça posicionada em uma chapa específica com geometria já transformada em mm.</summary>
public sealed record PlacedNestingPart
{
    public required NestingPart Part { get; init; }
    public int InstanceIndex { get; init; }
    public int SheetIndex { get; init; }
    public double OffsetXMm { get; init; }
    public double OffsetYMm { get; init; }
    public double RotationDeg { get; init; }
    public required IReadOnlyList<(double X, double Y)> TransformedOuterContour { get; init; }
    public IReadOnlyList<NestingPolyline2D> TransformedInternalFeatures { get; init; } = Array.Empty<NestingPolyline2D>();
    public IReadOnlyList<NestingRasterOverlay> TransformedRasterOverlays { get; init; } = Array.Empty<NestingRasterOverlay>();
    public double MinXMm { get; init; }
    public double MinYMm { get; init; }
    public double MaxXMm { get; init; }
    public double MaxYMm { get; init; }
    public bool PlacedInsideHole { get; init; }

    public double BoundsWidthMm => Math.Max(0.0, MaxXMm - MinXMm);
    public double BoundsHeightMm => Math.Max(0.0, MaxYMm - MinYMm);
}

/// <summary>Layout completo de uma chapa/retalho resultante do nesting.</summary>
public sealed record NestingSheetLayout
{
    public int SheetIndex { get; init; }
    public double SheetWidthMm { get; init; }
    public double SheetHeightMm { get; init; }
    public double MarginMm { get; init; }
    public required IReadOnlyList<PlacedNestingPart> Placements { get; init; }
    public double UsedOuterAreaMm2 { get; init; }
    public double UsableSheetAreaMm2 { get; init; }
    public double UtilizationPercent { get; init; }
    public double UsedBoundsWidthMm { get; init; }
    public double UsedBoundsHeightMm { get; init; }
}

/// <summary>Peça que não pôde ser alocada no retalho configurado.</summary>
public sealed record NestingUnplacedPart(
    string PartId,
    string PartName,
    int InstanceIndex,
    string Reason);

/// <summary>Relato incremental de progresso durante a execução assíncrona do motor de nesting.</summary>
public sealed record NestingProgress(
    int ProcessedInstances,
    int TotalInstances,
    int CurrentSheetCount,
    double BestUtilizationPercent,
    string StatusMessage);

/// <summary>Resultado consolidado da otimização de nesting 2D em múltiplas chapas.</summary>
public sealed record NestingResult
{
    public required NestingSheetConfig Config { get; init; }
    public required IReadOnlyList<NestingSheetLayout> Sheets { get; init; }
    public required IReadOnlyList<NestingUnplacedPart> UnplacedParts { get; init; }
    public double OverallUtilizationPercent { get; init; }
    public int TotalRequestedInstances { get; init; }
    public int TotalPlacedInstances { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string WinningStrategy { get; init; } = string.Empty;
}
