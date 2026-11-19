using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.Personalization;

/// <summary>Superfícies físicas disponíveis para personalização nas chapas da caixa.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<SurfaceId>))]
public enum SurfaceId
{
    Front,
    Back,
    Left,
    Right,
    Lid,
    Bottom,
}

/// <summary>Categorias principais de elementos gráficos de personalização.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<ElementType>))]
public enum ElementType
{
    Text,
    VectorSvg,
    RasterImage,
    Shape,
}

/// <summary>Primitivas geométricas paramétricas 1:1 disponíveis para corte, furação e marcação CNC.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<GeometricShapeKind>))]
public enum GeometricShapeKind
{
    Rectangle,
    Circle,
    Slot,
    Polygon,
    Crosshair,
}

/// <summary>Modo de operação de alto nível para elementos e primitivas geométricas CNC.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<OperationMode>))]
public enum OperationMode
{
    Cut,
    Engrave,
    Both,
}

/// <summary>Categorias semânticas de operação CNC/CAM sem acoplar parâmetros de máquina.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<CncOperationType>))]
public enum CncOperationType
{
    CutExternal,
    CutInternal,
    EngraveVector,
    EngraveFill,
    Mark,
    EngraveRaster,
}

/// <summary>Estratégia vetorial para conversão de fontes em caminhos de gravação.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<TextRenderMode>))]
public enum TextRenderMode
{
    Outline,
    Fill,
    Both,
    Hatch,
}

/// <summary>Alinhamento horizontal de texto em caixa delimitadora.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<HorizontalAlign>))]
public enum HorizontalAlign
{
    Left,
    Center,
    Right,
}

/// <summary>Alinhamento vertical de texto em caixa delimitadora.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<VerticalAlign>))]
public enum VerticalAlign
{
    Top,
    Middle,
    Bottom,
}

/// <summary>Os 7 modos de reposicionamento automático ao redimensionar a caixa.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<AnchorMode>))]
public enum AnchorMode
{
    Absolute,
    Relative,
    Center,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>Categorias de zonas restritas ou estruturais sobre uma face.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<ForbiddenZoneType>))]
public enum ForbiddenZoneType
{
    FingerJoint,
    ThroughSlot,
    HingeEar,
    EdgeMargin,
}

/// <summary>Especificação visual e semântica de uma camada CNC no SVG/CAD.</summary>
public sealed record CncLayerSpec(
    CncOperationType Operation,
    string Id,
    string Label,
    string Stroke,
    string StrokeWidth,
    string Fill);

/// <summary>Catálogo canônico das 6 camadas CNC espelhando <c>LAYER_SPECS</c> de <c>personalization/exporter.py</c>.</summary>
public static class CncLayerSpecs
{
    public static CncLayerSpec CutInternal { get; } = new(
        CncOperationType.CutInternal,
        "layer_cut_internal",
        "Corte Interno (Rasgos e Furos)",
        "#B71C1C",
        "0.03mm",
        "none");

    public static CncLayerSpec CutExternal { get; } = new(
        CncOperationType.CutExternal,
        "layer_cut_external",
        "Corte Externo (Perimetro das Pecas)",
        "#000000",
        "0.03mm",
        "none");

    public static CncLayerSpec EngraveVector { get; } = new(
        CncOperationType.EngraveVector,
        "layer_engrave_vector",
        "Gravacao Vetorial (Contornos e Hachuras)",
        "#0D47A1",
        "0.1mm",
        "none");

    public static CncLayerSpec EngraveFill { get; } = new(
        CncOperationType.EngraveFill,
        "layer_engrave_fill",
        "Gravacao Preenchida (Superficie)",
        "none",
        "0",
        "#6A1B9A");

    public static CncLayerSpec Mark { get; } = new(
        CncOperationType.Mark,
        "layer_mark",
        "Marcacao / Risco Superficial",
        "#1B5E20",
        "0.1mm",
        "none");

    public static CncLayerSpec EngraveRaster { get; } = new(
        CncOperationType.EngraveRaster,
        "layer_engrave_raster",
        "Gravacao Raster (Imagem Varredura)",
        "none",
        "0",
        "none");

    public static IReadOnlyDictionary<CncOperationType, CncLayerSpec> ByOperation { get; } =
        new Dictionary<CncOperationType, CncLayerSpec>
        {
            [CncOperationType.CutInternal] = CutInternal,
            [CncOperationType.CutExternal] = CutExternal,
            [CncOperationType.EngraveVector] = EngraveVector,
            [CncOperationType.EngraveFill] = EngraveFill,
            [CncOperationType.Mark] = Mark,
            [CncOperationType.EngraveRaster] = EngraveRaster,
        };

    public static CncLayerSpec Get(CncOperationType operation) => ByOperation[operation];
}

/// <summary>Retângulo bidimensional em milímetros no plano de uma superfície.</summary>
public sealed record Rect2D
{
    [JsonPropertyName("x_mm")]
    public double XMm { get; init; }

    [JsonPropertyName("y_mm")]
    public double YMm { get; init; }

    [JsonPropertyName("width_mm")]
    public double WidthMm { get; init; }

    [JsonPropertyName("height_mm")]
    public double HeightMm { get; init; }

    [JsonConstructor]
    public Rect2D(double xMm, double yMm, double widthMm, double heightMm)
    {
        XMm = xMm;
        YMm = yMm;
        WidthMm = widthMm;
        HeightMm = heightMm;
    }

    [JsonIgnore]
    public double Right => XMm + WidthMm;

    [JsonIgnore]
    public double Bottom => YMm + HeightMm;

    [JsonIgnore]
    public Point2D Center => new(XMm + WidthMm / 2.0, YMm + HeightMm / 2.0);

    public bool ContainsRect(Rect2D other, double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.XMm >= XMm - tolerance
            && other.YMm >= YMm - tolerance
            && other.Right <= Right + tolerance
            && other.Bottom <= Bottom + tolerance;
    }

    public bool Intersects(Rect2D other, double tolerance = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (WidthMm <= 0.0 || HeightMm <= 0.0 || other.WidthMm <= 0.0 || other.HeightMm <= 0.0)
        {
            return false;
        }

        return XMm < other.Right - tolerance
            && Right > other.XMm + tolerance
            && YMm < other.Bottom - tolerance
            && Bottom > other.YMm + tolerance;
    }

    public Rect2D Shrink(double marginMm)
    {
        double margin = Math.Max(0.0, marginMm);
        return new Rect2D(
            XMm + margin,
            YMm + margin,
            Math.Max(0.0, WidthMm - 2.0 * margin),
            Math.Max(0.0, HeightMm - 2.0 * margin));
    }

    public IReadOnlyList<Point2D> ToCorners() =>
    [
        new(XMm, YMm),
        new(Right, YMm),
        new(Right, Bottom),
        new(XMm, Bottom),
    ];

    public Rect2D Rounded() => new(
        PythonMath.Round(XMm, 4),
        PythonMath.Round(YMm, 4),
        PythonMath.Round(WidthMm, 4),
        PythonMath.Round(HeightMm, 4));
}

/// <summary>Caminho vetorial normalizado em milímetros no espaço local do elemento.</summary>
public sealed record VectorPathItem
{
    [JsonPropertyName("d")]
    public required string D { get; init; }

    [JsonPropertyName("operation")]
    public CncOperationType Operation { get; init; } = CncOperationType.EngraveVector;

    [JsonPropertyName("is_closed")]
    public bool IsClosed { get; init; } = true;

    [JsonPropertyName("has_fill")]
    public bool HasFill { get; init; }

    [JsonPropertyName("has_stroke")]
    public bool HasStroke { get; init; } = true;

    [JsonPropertyName("layer_name")]
    public string? LayerName { get; init; }

    public VectorPathItem DeepClone() => this with { };
}

/// <summary>Parâmetros de pré-processamento para gravação raster ou vetorização.</summary>
public sealed class RasterProcessingOptions
{
    [JsonPropertyName("grayscale")]
    public bool Grayscale { get; set; } = true;

    [JsonPropertyName("brightness")]
    public double Brightness { get; set; } = 1.0;

    [JsonPropertyName("contrast")]
    public double Contrast { get; set; } = 1.0;

    [JsonPropertyName("invert")]
    public bool Invert { get; set; }

    [JsonPropertyName("monochrome")]
    public bool Monochrome { get; set; }

    [JsonPropertyName("threshold")]
    public int Threshold { get; set; } = 128;

    [JsonPropertyName("dithering")]
    public bool Dithering { get; set; }

    [JsonPropertyName("crop_box")]
    public double[]? CropBox { get; set; }

    [JsonPropertyName("resize_max_px")]
    public int? ResizeMaxPx { get; set; }

    public RasterProcessingOptions Clone() => new()
    {
        Grayscale = Grayscale,
        Brightness = Brightness,
        Contrast = Contrast,
        Invert = Invert,
        Monochrome = Monochrome,
        Threshold = Threshold,
        Dithering = Dithering,
        CropBox = CropBox is null ? null : (double[])CropBox.Clone(),
        ResizeMaxPx = ResizeMaxPx,
    };
}

/// <summary>
/// Especificação matemática 1:1 em milímetros de uma forma geométrica paramétrica CNC.
/// </summary>
public sealed class GeometricShapeSpec : IEquatable<GeometricShapeSpec>
{
    private GeometricShapeKind _kind = GeometricShapeKind.Rectangle;
    private double _widthMm = 25.0;
    private double _heightMm = 15.0;
    private double _diameterMm = 10.0;
    private double _rawCornerRadiusMm;
    private int _sides = 6;
    private bool _fillHatch;
    private double _hatchSpacingMm = ShapeGeometryBuilder.DefaultHatchSpacingMm;

    [JsonIgnore]
    internal Action? ChangedCallback { get; set; }

    [JsonPropertyName("kind")]
    public GeometricShapeKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            ChangedCallback?.Invoke();
        }
    }

    [JsonPropertyName("width_mm")]
    public double WidthMm
    {
        get => _widthMm;
        set
        {
            double clamped = Math.Max(ShapeGeometryBuilder.MinDimensionMm, double.IsFinite(value) ? value : 25.0);
            if (Math.Abs(_widthMm - clamped) <= 1e-9) return;
            _widthMm = clamped;
            ChangedCallback?.Invoke();
        }
    }

    [JsonPropertyName("height_mm")]
    public double HeightMm
    {
        get => _heightMm;
        set
        {
            double clamped = Math.Max(ShapeGeometryBuilder.MinDimensionMm, double.IsFinite(value) ? value : 15.0);
            if (Math.Abs(_heightMm - clamped) <= 1e-9) return;
            _heightMm = clamped;
            ChangedCallback?.Invoke();
        }
    }

    [JsonPropertyName("diameter_mm")]
    public double DiameterMm
    {
        get => _diameterMm;
        set
        {
            double clamped = Math.Max(ShapeGeometryBuilder.MinDimensionMm, double.IsFinite(value) ? value : 10.0);
            if (Math.Abs(_diameterMm - clamped) <= 1e-9) return;
            _diameterMm = clamped;
            ChangedCallback?.Invoke();
        }
    }

    [JsonPropertyName("corner_radius_mm")]
    public double CornerRadiusMm
    {
        get => Math.Clamp(_rawCornerRadiusMm, 0.0, Math.Min(_widthMm, _heightMm) * 0.5);
        set
        {
            double nonNeg = Math.Max(0.0, double.IsFinite(value) ? value : 0.0);
            double clamped = Math.Clamp(nonNeg, 0.0, Math.Min(_widthMm, _heightMm) * 0.5);
            if (Math.Abs(_rawCornerRadiusMm - nonNeg) <= 1e-9 && Math.Abs(CornerRadiusMm - clamped) <= 1e-9) return;
            _rawCornerRadiusMm = nonNeg;
            ChangedCallback?.Invoke();
        }
    }

    [JsonPropertyName("sides")]
    public int Sides
    {
        get => _sides;
        set
        {
            int clamped = Math.Clamp(value, ShapeGeometryBuilder.MinPolygonSides, ShapeGeometryBuilder.MaxPolygonSides);
            if (_sides == clamped) return;
            _sides = clamped;
            ChangedCallback?.Invoke();
        }
    }

    [JsonPropertyName("fill_hatch")]
    public bool FillHatch
    {
        get => _fillHatch;
        set
        {
            if (_fillHatch == value) return;
            _fillHatch = value;
            ChangedCallback?.Invoke();
        }
    }

    [JsonPropertyName("hatch_spacing_mm")]
    public double HatchSpacingMm
    {
        get => _hatchSpacingMm;
        set
        {
            double clamped = Math.Clamp(double.IsFinite(value) ? value : ShapeGeometryBuilder.DefaultHatchSpacingMm, 0.1, 10.0);
            if (Math.Abs(_hatchSpacingMm - clamped) <= 1e-9) return;
            _hatchSpacingMm = clamped;
            ChangedCallback?.Invoke();
        }
    }

    public GeometricShapeSpec Clone() => new()
    {
        _kind = _kind,
        _widthMm = _widthMm,
        _heightMm = _heightMm,
        _diameterMm = _diameterMm,
        _rawCornerRadiusMm = CornerRadiusMm,
        _sides = _sides,
        _fillHatch = _fillHatch,
        _hatchSpacingMm = _hatchSpacingMm,
    };

    public string GetSignature()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)_kind}:{_widthMm:F3}:{_heightMm:F3}:{_diameterMm:F3}:{CornerRadiusMm:F3}:{_sides}:{(_fillHatch ? 1 : 0)}:{_hatchSpacingMm:F2}");

    public bool Equals(GeometricShapeSpec? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _kind == other._kind
            && Math.Abs(_widthMm - other._widthMm) <= 1e-6
            && Math.Abs(_heightMm - other._heightMm) <= 1e-6
            && Math.Abs(_diameterMm - other._diameterMm) <= 1e-6
            && Math.Abs(CornerRadiusMm - other.CornerRadiusMm) <= 1e-6
            && _sides == other._sides
            && _fillHatch == other._fillHatch
            && Math.Abs(_hatchSpacingMm - other._hatchSpacingMm) <= 1e-6;
    }

    public override bool Equals(object? obj) => Equals(obj as GeometricShapeSpec);

    public override int GetHashCode()
        => HashCode.Combine(
            _kind,
            Math.Round(_widthMm, 4),
            Math.Round(_heightMm, 4),
            Math.Round(_diameterMm, 4),
            Math.Round(CornerRadiusMm, 4),
            _sides,
            _fillHatch);
}

/// <summary>
/// Modelo base para elementos gráficos de personalização aplicados a uma face da caixa.
/// Espelha integralmente <c>GraphicElement</c> de <c>personalization/models.py</c>.
/// </summary>
[JsonConverter(typeof(PersonalizationElementJsonConverter))]
public abstract class PersonalizationElement
{
    private double _widthMm = 10.0;
    private double _heightMm = 10.0;
    private CncOperationType _operation = CncOperationType.EngraveVector;
    private OperationMode _operationMode = OperationMode.Engrave;
    private GeometricShapeSpec? _shapeSpec;
    private bool _syncingOperationState;

    public string Id { get; set; } = GenerateNewId();
    public string Name { get; set; } = string.Empty;
    public abstract ElementType ElementType { get; }
    public SurfaceId PanelId { get; set; } = SurfaceId.Front;
    public double XMm { get; set; }
    public double YMm { get; set; }

    public double WidthMm
    {
        get => _widthMm;
        set => _widthMm = Math.Max(0.01, value);
    }

    public double HeightMm
    {
        get => _heightMm;
        set => _heightMm = Math.Max(0.01, value);
    }

    public double RotationDeg { get; set; }
    public bool PreserveAspectRatio { get; set; } = true;

    public CncOperationType Operation
    {
        get => _operation;
        set
        {
            if (_operation == value && !_syncingOperationState)
            {
                return;
            }

            _operation = value;
            if (!_syncingOperationState)
            {
                _syncingOperationState = true;
                try
                {
                    if (value is CncOperationType.CutInternal or CncOperationType.CutExternal)
                    {
                        if (_operationMode != OperationMode.Both)
                        {
                            _operationMode = OperationMode.Cut;
                        }
                    }
                    else
                    {
                        _operationMode = OperationMode.Engrave;
                    }

                    OnOperationStateChanged();
                }
                finally
                {
                    _syncingOperationState = false;
                }
            }
        }
    }

    public OperationMode OperationMode
    {
        get => _operationMode;
        set
        {
            if (_operationMode == value && !_syncingOperationState)
            {
                return;
            }

            _operationMode = value;
            if (!_syncingOperationState)
            {
                _syncingOperationState = true;
                try
                {
                    if (value is OperationMode.Cut or OperationMode.Both)
                    {
                        _operation = CncOperationType.CutInternal;
                    }
                    else if (_operation is CncOperationType.CutInternal or CncOperationType.CutExternal)
                    {
                        _operation = _shapeSpec?.Kind == GeometricShapeKind.Crosshair
                            ? CncOperationType.Mark
                            : (_shapeSpec?.FillHatch == true ? CncOperationType.EngraveFill : CncOperationType.EngraveVector);
                    }

                    OnOperationStateChanged();
                }
                finally
                {
                    _syncingOperationState = false;
                }
            }
        }
    }

    public GeometricShapeSpec? ShapeSpec
    {
        get => _shapeSpec;
        set
        {
            if (_shapeSpec is not null)
            {
                _shapeSpec.ChangedCallback = null;
            }

            _shapeSpec = value;
            if (_shapeSpec is not null)
            {
                _shapeSpec.ChangedCallback = OnShapeSpecMutated;
            }

            OnShapeSpecMutated();
        }
    }

    public AnchorMode Anchor { get; set; } = AnchorMode.Absolute;
    public double RelX { get; set; } = 0.5;
    public double RelY { get; set; } = 0.5;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public JsonObject Metadata { get; set; } = new();

    protected virtual void OnOperationStateChanged()
    {
    }

    protected virtual void OnShapeSpecMutated()
    {
    }

    public static string GenerateNewId() => Guid.NewGuid().ToString("N")[..12];

    public void MoveTo(double xMm, double yMm)
    {
        XMm = xMm;
        YMm = yMm;
    }

    public void Translate(double dxMm, double dyMm)
    {
        XMm += dxMm;
        YMm += dyMm;
    }

    public void Rotate(double rotationDeg)
    {
        double mod = rotationDeg % 360.0;
        RotationDeg = mod < 0.0 ? mod + 360.0 : mod;
    }

    public void RepositionSurface(SurfaceId newPanelId)
    {
        PanelId = newPanelId;
    }

    public virtual void Resize(double? widthMm = null, double? heightMm = null, bool? preserveAspectRatio = null)
    {
        bool keepRatio = preserveAspectRatio ?? PreserveAspectRatio;
        double oldW = Math.Max(WidthMm, 1e-6);
        double oldH = Math.Max(HeightMm, 1e-6);
        double aspect = oldW / oldH;

        if (widthMm.HasValue && widthMm.Value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMm), "A largura do elemento deve ser maior que zero.");
        }

        if (heightMm.HasValue && heightMm.Value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightMm), "A altura do elemento deve ser maior que zero.");
        }

        if (keepRatio)
        {
            if (widthMm.HasValue && !heightMm.HasValue)
            {
                WidthMm = widthMm.Value;
                HeightMm = Math.Max(0.01, WidthMm / aspect);
            }
            else if (heightMm.HasValue && !widthMm.HasValue)
            {
                HeightMm = heightMm.Value;
                WidthMm = Math.Max(0.01, HeightMm * aspect);
            }
            else if (widthMm.HasValue && heightMm.HasValue)
            {
                double scale = Math.Min(widthMm.Value / oldW, heightMm.Value / oldH);
                WidthMm = Math.Max(0.01, oldW * scale);
                HeightMm = Math.Max(0.01, oldH * scale);
            }
        }
        else
        {
            if (widthMm.HasValue)
            {
                WidthMm = widthMm.Value;
            }

            if (heightMm.HasValue)
            {
                HeightMm = heightMm.Value;
            }
        }
    }

    public Point2D CenterMm() => new(XMm + WidthMm / 2.0, YMm + HeightMm / 2.0);

    public Rect2D BoundingBoxLocal() => new(XMm, YMm, WidthMm, HeightMm);

    /// <summary>Retorna os 4 cantos reais do elemento rotacionado em torno do centro em milímetros.</summary>
    public IReadOnlyList<Point2D> Corners2D()
    {
        Point2D center = CenterMm();
        double hw = WidthMm / 2.0;
        double hh = HeightMm / 2.0;
        double rad = RotationDeg * (Math.PI / 180.0);
        double cosA = Math.Cos(rad);
        double sinA = Math.Sin(rad);

        return
        [
            RotateCorner(-hw, -hh, center, cosA, sinA),
            RotateCorner(hw, -hh, center, cosA, sinA),
            RotateCorner(hw, hh, center, cosA, sinA),
            RotateCorner(-hw, hh, center, cosA, sinA),
        ];
    }

    /// <summary>
    /// Retorna o polígono convexo real do elemento no sistema de coordenadas 2D da chapa para validação SAT.
    /// </summary>
    public virtual IReadOnlyList<Point2D> GetCollisionPolygon2D() => Corners2D();

    /// <summary>Retorna a caixa envolvente alinhada aos eixos após considerar a rotação.</summary>
    public Rect2D BoundingBoxOriented()
    {
        double mod = Math.Abs(RotationDeg % 360.0);
        if (mod < 1e-9 || Math.Abs(mod - 360.0) < 1e-9)
        {
            return BoundingBoxLocal();
        }

        IReadOnlyList<Point2D> corners = Corners2D();
        BoundingBox2D box = BoundingBox2D.FromPoints(corners);
        return new Rect2D(box.MinX, box.MinY, box.Width, box.Height);
    }

    public abstract PersonalizationElement DeepClone();

    public PersonalizationElement Duplicate(string? newId = null, double offsetXMm = 5.0, double offsetYMm = 5.0)
    {
        PersonalizationElement cloned = DeepClone();
        cloned.Id = string.IsNullOrWhiteSpace(newId) ? GenerateNewId() : newId;
        cloned.XMm += offsetXMm;
        cloned.YMm += offsetYMm;
        if (!string.IsNullOrEmpty(cloned.Name))
        {
            cloned.Name = $"{cloned.Name} (copia)";
        }

        return cloned;
    }

    protected void CopyBaseFieldsTo(PersonalizationElement target)
    {
        target.Id = Id;
        target.Name = Name;
        target.PanelId = PanelId;
        target.XMm = XMm;
        target.YMm = YMm;
        target.WidthMm = WidthMm;
        target.HeightMm = HeightMm;
        target.RotationDeg = RotationDeg;
        target.PreserveAspectRatio = PreserveAspectRatio;
        target.Operation = Operation;
        target.OperationMode = OperationMode;
        target.Anchor = Anchor;
        target.RelX = RelX;
        target.RelY = RelY;
        target.Visible = Visible;
        target.Locked = Locked;
        target.Metadata = (JsonObject)(Metadata.DeepClone());
        if (ShapeSpec is not null && target is not ShapePersonalizationElement)
        {
            target.ShapeSpec = ShapeSpec.Clone();
        }
    }

    protected static Point2D RotateCorner(double lx, double ly, Point2D center, double cosA, double sinA)
        => new(center.X + (lx * cosA) - (ly * sinA), center.Y + (lx * sinA) + (ly * cosA));
}

/// <summary>Elemento textual com propriedades tipográficas e caminhos vetoriais em mm.</summary>
public sealed class TextPersonalizationElement : PersonalizationElement
{
    public override ElementType ElementType => ElementType.Text;

    public string Content { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Arial";
    public string? FontPath { get; set; }
    public double FontSizeMm { get; set; } = 12.0;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public HorizontalAlign HorizontalAlign { get; set; } = HorizontalAlign.Left;
    public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Top;
    public double LetterSpacingMm { get; set; }
    public double LineSpacing { get; set; } = 1.2;
    public TextRenderMode RenderMode { get; set; } = TextRenderMode.Outline;

    private double _hatchSpacingMm = 0.25;
    public double HatchSpacingMm
    {
        get => _hatchSpacingMm;
        set => _hatchSpacingMm = Math.Max(0.05, value);
    }

    public bool ConvertToPaths { get; set; } = true;
    public bool Vectorized { get; set; }

    private double _baseWidthMm = 10.0;
    public double BaseWidthMm
    {
        get => _baseWidthMm;
        set => _baseWidthMm = Math.Max(0.01, value);
    }

    private double _baseHeightMm = 10.0;
    public double BaseHeightMm
    {
        get => _baseHeightMm;
        set => _baseHeightMm = Math.Max(0.01, value);
    }

    public List<VectorPathItem> VectorPaths { get; set; } = new();
    public string? ResolvedFontName { get; set; }
    public bool FontFallbackUsed { get; set; }

    public static CncOperationType DefaultOperationForRenderMode(TextRenderMode mode)
        => mode is TextRenderMode.Fill or TextRenderMode.Hatch
            ? CncOperationType.EngraveFill
            : CncOperationType.EngraveVector;

    public override PersonalizationElement DeepClone()
    {
        var copy = new TextPersonalizationElement
        {
            Content = Content,
            FontFamily = FontFamily,
            FontPath = FontPath,
            FontSizeMm = FontSizeMm,
            Bold = Bold,
            Italic = Italic,
            HorizontalAlign = HorizontalAlign,
            VerticalAlign = VerticalAlign,
            LetterSpacingMm = LetterSpacingMm,
            LineSpacing = LineSpacing,
            RenderMode = RenderMode,
            HatchSpacingMm = HatchSpacingMm,
            ConvertToPaths = ConvertToPaths,
            Vectorized = Vectorized,
            BaseWidthMm = BaseWidthMm,
            BaseHeightMm = BaseHeightMm,
            VectorPaths = VectorPaths.Select(p => p.DeepClone()).ToList(),
            ResolvedFontName = ResolvedFontName,
            FontFallbackUsed = FontFallbackUsed,
        };
        CopyBaseFieldsTo(copy);
        return copy;
    }
}

/// <summary>Elemento vetorial importado e normalizado em milímetros a partir de SVG.</summary>
public sealed class VectorSvgPersonalizationElement : PersonalizationElement
{
    public override ElementType ElementType => ElementType.VectorSvg;

    public string? SourceFilename { get; set; }
    public string? OriginalFileName { get; set; }
    public string? AssetEntryPath { get; set; }
    [JsonIgnore]
    public string? RawSvgText { get; set; }

    [JsonIgnore]
    public byte[]? RawSvgBytes
    {
        get => string.IsNullOrEmpty(RawSvgText) ? null : Encoding.UTF8.GetBytes(RawSvgText);
        set => RawSvgText = value is { Length: > 0 } ? Encoding.UTF8.GetString(value) : null;
    }

    public string SourceUnit { get; set; } = "mm";
    public double OriginalWidthMm { get; set; } = 10.0;
    public double OriginalHeightMm { get; set; } = 10.0;

    private double _baseWidthMm = 10.0;
    public double BaseWidthMm
    {
        get => _baseWidthMm;
        set => _baseWidthMm = Math.Max(0.01, value);
    }

    private double _baseHeightMm = 10.0;
    public double BaseHeightMm
    {
        get => _baseHeightMm;
        set => _baseHeightMm = Math.Max(0.01, value);
    }

    public List<VectorPathItem> Paths { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    public override PersonalizationElement DeepClone()
    {
        var copy = new VectorSvgPersonalizationElement
        {
            SourceFilename = SourceFilename,
            OriginalFileName = OriginalFileName,
            AssetEntryPath = AssetEntryPath,
            RawSvgText = RawSvgText,
            SourceUnit = SourceUnit,
            OriginalWidthMm = OriginalWidthMm,
            OriginalHeightMm = OriginalHeightMm,
            BaseWidthMm = BaseWidthMm,
            BaseHeightMm = BaseHeightMm,
            Paths = Paths.Select(p => p.DeepClone()).ToList(),
            Warnings = new List<string>(Warnings),
        };
        CopyBaseFieldsTo(copy);
        return copy;
    }
}

/// <summary>Elemento de imagem rasterizada (PNG/JPG/BMP) com pré-processamento e vetorização opcional.</summary>
public sealed class RasterImagePersonalizationElement : PersonalizationElement
{
    public override ElementType ElementType => ElementType.RasterImage;

    public string? SourceFilename { get; set; }
    public string? OriginalFileName { get; set; }
    public string? AssetEntryPath { get; set; }
    [JsonIgnore]
    public byte[]? RawImageBytes { get; set; }
    public string SourceFormat { get; set; } = "PNG";
    public int[] OriginalPixelSize { get; set; } = [1, 1];
    public int[] ProcessedPixelSize { get; set; } = [1, 1];
    public RasterProcessingOptions ProcessingOptions { get; set; } = new();
    public string RawImageB64 { get; set; } = string.Empty;
    public string ProcessedImageB64 { get; set; } = string.Empty;
    public bool Vectorize { get; set; }
    public double VectorizeEpsilonPx { get; set; } = 1.5;

    private double _baseWidthMm = 10.0;
    public double BaseWidthMm
    {
        get => _baseWidthMm;
        set => _baseWidthMm = Math.Max(0.01, value);
    }

    private double _baseHeightMm = 10.0;
    public double BaseHeightMm
    {
        get => _baseHeightMm;
        set => _baseHeightMm = Math.Max(0.01, value);
    }

    public List<VectorPathItem> VectorPaths { get; set; } = new();

    public override PersonalizationElement DeepClone()
    {
        var copy = new RasterImagePersonalizationElement
        {
            SourceFilename = SourceFilename,
            OriginalFileName = OriginalFileName,
            AssetEntryPath = AssetEntryPath,
            RawImageBytes = RawImageBytes is { Length: > 0 } ? (byte[])RawImageBytes.Clone() : null,
            SourceFormat = SourceFormat,
            OriginalPixelSize = (int[])OriginalPixelSize.Clone(),
            ProcessedPixelSize = (int[])ProcessedPixelSize.Clone(),
            ProcessingOptions = ProcessingOptions.Clone(),
            RawImageB64 = RawImageB64,
            ProcessedImageB64 = ProcessedImageB64,
            Vectorize = Vectorize,
            VectorizeEpsilonPx = VectorizeEpsilonPx,
            BaseWidthMm = BaseWidthMm,
            BaseHeightMm = BaseHeightMm,
            VectorPaths = VectorPaths.Select(p => p.DeepClone()).ToList(),
        };
        CopyBaseFieldsTo(copy);
        return copy;
    }
}

/// <summary>
/// Elemento paramétrico de forma geométrica 1:1 em milímetros (Retângulo, Círculo/Furo, Oblongo/Slot, Polígono Regular e Cruz de Centro).
/// </summary>
public sealed class ShapePersonalizationElement : PersonalizationElement
{
    private bool _suppressGeometryRebuild;
    private double _baseWidthMm = 25.0;
    private double _baseHeightMm = 15.0;

    public override ElementType ElementType => ElementType.Shape;

    public double BaseWidthMm
    {
        get => _baseWidthMm;
        set => _baseWidthMm = Math.Max(0.01, value);
    }

    public double BaseHeightMm
    {
        get => _baseHeightMm;
        set => _baseHeightMm = Math.Max(0.01, value);
    }

    public string SvgPathData { get; set; } = string.Empty;
    public List<VectorPathItem> VectorPaths { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyList<Point2D> PrimaryContour { get; private set; } = Array.Empty<Point2D>();

    [JsonIgnore]
    public IReadOnlyList<IReadOnlyList<Point2D>> Polylines { get; private set; } = Array.Empty<IReadOnlyList<Point2D>>();

    public ShapePersonalizationElement()
        : this(new GeometricShapeSpec())
    {
    }

    public ShapePersonalizationElement(
        GeometricShapeSpec spec,
        OperationMode? initialMode = null,
        CncOperationType? initialOperation = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        _suppressGeometryRebuild = true;
        try
        {
            Anchor = AnchorMode.Center;
            OperationMode defaultMode = initialMode ?? DefaultOperationModeForKind(spec.Kind);
            CncOperationType defaultOp = initialOperation ?? DefaultCncOperationForKind(spec.Kind, defaultMode, spec.FillHatch);
            OperationMode = defaultMode;
            Operation = defaultOp;
            PreserveAspectRatio = spec.Kind is GeometricShapeKind.Circle or GeometricShapeKind.Polygon or GeometricShapeKind.Crosshair;
            ShapeSpec = spec;
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = DefaultDisplayNameForSpec(spec);
            }
        }
        finally
        {
            _suppressGeometryRebuild = false;
        }

        RebuildGeometry(preserveAnchorPosition: false);
    }

    public static OperationMode DefaultOperationModeForKind(GeometricShapeKind kind)
        => kind == GeometricShapeKind.Crosshair ? OperationMode.Engrave : OperationMode.Cut;

    public static CncOperationType DefaultCncOperationForKind(
        GeometricShapeKind kind,
        OperationMode mode,
        bool fillHatch = false)
    {
        if (mode is OperationMode.Cut or OperationMode.Both)
        {
            return CncOperationType.CutInternal;
        }

        if (kind == GeometricShapeKind.Crosshair)
        {
            return CncOperationType.Mark;
        }

        return fillHatch ? CncOperationType.EngraveFill : CncOperationType.EngraveVector;
    }

    public static string DefaultDisplayNameForSpec(GeometricShapeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Kind switch
        {
            GeometricShapeKind.Rectangle => string.Format(
                CultureInfo.InvariantCulture,
                "Retângulo {0:0.#}×{1:0.#} mm",
                spec.WidthMm,
                spec.HeightMm),
            GeometricShapeKind.Circle => string.Format(
                CultureInfo.InvariantCulture,
                "Furo Ø{0:0.#} mm",
                spec.DiameterMm),
            GeometricShapeKind.Slot => string.Format(
                CultureInfo.InvariantCulture,
                "Oblongo {0:0.#}×{1:0.#} mm",
                Math.Max(spec.WidthMm, spec.HeightMm),
                Math.Min(spec.WidthMm, spec.HeightMm)),
            GeometricShapeKind.Polygon => string.Format(
                CultureInfo.InvariantCulture,
                "Polígono {0}L Ø{1:0.#} mm",
                spec.Sides,
                spec.DiameterMm),
            GeometricShapeKind.Crosshair => string.Format(
                CultureInfo.InvariantCulture,
                "Cruz de Centro Ø{0:0.#} mm",
                spec.DiameterMm),
            _ => "Forma Geométrica",
        };
    }

    public ShapeGeometryResult RebuildGeometry(bool preserveAnchorPosition = false)
        => ShapeGeometryBuilder.SynchronizeElement(this, preserveAnchorPosition);

    public ShapeGeometryResult MutateSpec(Action<GeometricShapeSpec> mutator, bool preserveAnchorPosition = true)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        GeometricShapeSpec spec = ShapeSpec ??= new GeometricShapeSpec();

        _suppressGeometryRebuild = true;
        try
        {
            mutator(spec);
        }
        finally
        {
            _suppressGeometryRebuild = false;
        }

        return RebuildGeometry(preserveAnchorPosition);
    }

    internal void ApplyBuiltGeometry(ShapeGeometryResult built)
    {
        WidthMm = built.WidthMm;
        HeightMm = built.HeightMm;
        BaseWidthMm = built.WidthMm;
        BaseHeightMm = built.HeightMm;
        SvgPathData = built.SvgPathData;
        PrimaryContour = built.PrimaryContour;
        Polylines = built.Polylines;
        VectorPaths = built.VectorPaths.Select(p => p.DeepClone()).ToList();
    }

    protected override void OnOperationStateChanged()
    {
        if (_suppressGeometryRebuild || ShapeSpec is null)
        {
            return;
        }

        RebuildGeometry(preserveAnchorPosition: false);
    }

    protected override void OnShapeSpecMutated()
    {
        if (_suppressGeometryRebuild || ShapeSpec is null)
        {
            return;
        }

        RebuildGeometry(preserveAnchorPosition: false);
    }

    public override void Resize(double? widthMm = null, double? heightMm = null, bool? preserveAspectRatio = null)
    {
        double prevW = WidthMm;
        double prevH = HeightMm;
        base.Resize(widthMm, heightMm, preserveAspectRatio);

        if (ShapeSpec is null)
        {
            return;
        }

        _suppressGeometryRebuild = true;
        try
        {
            switch (ShapeSpec.Kind)
            {
                case GeometricShapeKind.Rectangle:
                    ShapeSpec.WidthMm = Math.Max(ShapeGeometryBuilder.MinDimensionMm, WidthMm);
                    ShapeSpec.HeightMm = Math.Max(ShapeGeometryBuilder.MinDimensionMm, HeightMm);
                    break;

                case GeometricShapeKind.Slot:
                    ShapeSpec.WidthMm = Math.Max(ShapeGeometryBuilder.MinDimensionMm, Math.Max(WidthMm, HeightMm));
                    ShapeSpec.HeightMm = Math.Max(ShapeGeometryBuilder.MinDimensionMm, Math.Min(WidthMm, HeightMm));
                    break;

                case GeometricShapeKind.Circle:
                case GeometricShapeKind.Polygon:
                case GeometricShapeKind.Crosshair:
                {
                    double nextDiam;
                    if (widthMm.HasValue && !heightMm.HasValue)
                    {
                        nextDiam = WidthMm;
                    }
                    else if (heightMm.HasValue && !widthMm.HasValue)
                    {
                        nextDiam = HeightMm;
                    }
                    else
                    {
                        double dw = Math.Abs(WidthMm - prevW);
                        double dh = Math.Abs(HeightMm - prevH);
                        nextDiam = dw >= dh ? WidthMm : HeightMm;
                    }

                    ShapeSpec.DiameterMm = Math.Max(ShapeGeometryBuilder.MinDimensionMm, nextDiam);
                    break;
                }
            }
        }
        finally
        {
            _suppressGeometryRebuild = false;
        }

        RebuildGeometry(preserveAnchorPosition: false);
    }

    public override IReadOnlyList<Point2D> GetCollisionPolygon2D()
    {
        if (PrimaryContour.Count < 3 ||
            ShapeSpec?.Kind is GeometricShapeKind.Crosshair ||
            (ShapeSpec?.Kind == GeometricShapeKind.Rectangle && ShapeSpec.CornerRadiusMm <= 1e-4))
        {
            return Corners2D();
        }

        Point2D center = CenterMm();
        double hw = WidthMm * 0.5;
        double hh = HeightMm * 0.5;
        double rad = RotationDeg * (Math.PI / 180.0);
        double cosA = Math.Cos(rad);
        double sinA = Math.Sin(rad);

        var transformed = new Point2D[PrimaryContour.Count];
        for (int i = 0; i < PrimaryContour.Count; i++)
        {
            Point2D local = PrimaryContour[i];
            double lx = local.X - hw;
            double ly = local.Y - hh;
            transformed[i] = RotateCorner(lx, ly, center, cosA, sinA);
        }

        return transformed;
    }

    public override PersonalizationElement DeepClone()
    {
        GeometricShapeSpec clonedSpec = (ShapeSpec ?? new GeometricShapeSpec()).Clone();
        var copy = new ShapePersonalizationElement(clonedSpec, OperationMode, Operation)
        {
            BaseWidthMm = BaseWidthMm,
            BaseHeightMm = BaseHeightMm,
            SvgPathData = SvgPathData,
            VectorPaths = VectorPaths.Select(p => p.DeepClone()).ToList(),
            PrimaryContour = PrimaryContour.ToArray(),
            Polylines = Polylines.Select(pl => (IReadOnlyList<Point2D>)pl.ToArray()).ToArray(),
        };
        CopyBaseFieldsTo(copy);
        copy.ShapeSpec = clonedSpec;
        return copy;
    }
}

/// <summary>Conversor System.Text.Json snake_case para enums compatíveis com a API Python v1.2.0.</summary>
public sealed class SnakeCaseEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> NameToEnum = BuildReadMap();
    private static readonly Dictionary<TEnum, string> EnumToName = BuildWriteMap();

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Esperada string para o enum {typeof(TEnum).Name}.");
        }

        string raw = reader.GetString() ?? string.Empty;
        if (NameToEnum.TryGetValue(raw.Trim(), out TEnum value))
        {
            return value;
        }

        throw new JsonException($"Valor inválido '{raw}' para o enum {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (EnumToName.TryGetValue(value, out string? name))
        {
            writer.WriteStringValue(name);
            return;
        }

        writer.WriteStringValue(ToSnakeCase(value.ToString()));
    }

    public static string ToApiString(TEnum value)
        => EnumToName.TryGetValue(value, out string? s) ? s : ToSnakeCase(value.ToString());

    public static TEnum ParseApiString(string raw)
    {
        if (NameToEnum.TryGetValue(raw.Trim(), out TEnum value))
        {
            return value;
        }

        throw new ArgumentException($"Valor inválido '{raw}' para {typeof(TEnum).Name}.", nameof(raw));
    }

    private static Dictionary<string, TEnum> BuildReadMap()
    {
        var map = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
        foreach (TEnum val in Enum.GetValues<TEnum>())
        {
            string pascal = val.ToString();
            map[pascal] = val;
            map[ToSnakeCase(pascal)] = val;
        }

        if (typeof(TEnum) == typeof(ElementType))
        {
            map["svg"] = (TEnum)(object)ElementType.VectorSvg;
            map["raster"] = (TEnum)(object)ElementType.RasterImage;
            map["parametric_shape"] = (TEnum)(object)ElementType.Shape;
        }

        return map;
    }

    private static Dictionary<TEnum, string> BuildWriteMap()
    {
        var map = new Dictionary<TEnum, string>();
        foreach (TEnum val in Enum.GetValues<TEnum>())
        {
            map[val] = ToSnakeCase(val.ToString());
        }

        return map;
    }

    private static string ToSnakeCase(string pascalName)
    {
        var chars = new List<char>(pascalName.Length + 4);
        for (int i = 0; i < pascalName.Length; i++)
        {
            char c = pascalName[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    chars.Add('_');
                }

                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add(c);
            }
        }

        return new string(chars.ToArray());
    }
}

/// <summary>
/// Serializador polimórfico plano para <see cref="PersonalizationElement"/> preservando
/// o contrato exato (<c>element_type</c>, <c>bounding_box</c>, etc.) dos JSONs v1.2.0.
/// </summary>
public sealed class PersonalizationElementJsonConverter : JsonConverter<PersonalizationElement>
{
    private readonly bool _includeRawRasterB64;

    public PersonalizationElementJsonConverter()
        : this(includeRawRasterB64: true)
    {
    }

    public PersonalizationElementJsonConverter(bool includeRawRasterB64)
    {
        _includeRawRasterB64 = includeRawRasterB64;
    }

    public override PersonalizationElement Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Elemento de personalização deve ser um objeto JSON.");
        }

        string rawType = GetString(root, "element_type")
            ?? throw new JsonException("Campo obrigatório 'element_type' ausente no elemento.");
        ElementType elementType = SnakeCaseEnumJsonConverter<ElementType>.ParseApiString(rawType);

        PersonalizationElement element = elementType switch
        {
            ElementType.Text => ReadTextElement(root, options),
            ElementType.VectorSvg => ReadVectorSvgElement(root, options),
            ElementType.RasterImage => ReadRasterElement(root, options),
            ElementType.Shape => ReadShapeElement(root, options),
            _ => throw new JsonException($"Tipo de elemento não suportado: '{rawType}'."),
        };

        ReadBaseFields(root, element);
        if (element is ShapePersonalizationElement shapeEl)
        {
            if (GetString(root, "operation_mode") is { } opModeStr)
            {
                shapeEl.OperationMode = SnakeCaseEnumJsonConverter<OperationMode>.ParseApiString(opModeStr);
            }

            shapeEl.RebuildGeometry(preserveAnchorPosition: false);
        }

        return element;
    }

    public override void Write(
        Utf8JsonWriter writer,
        PersonalizationElement value,
        JsonSerializerOptions options)
    {
        WriteElement(writer, value, options, includeRawRasterB64: _includeRawRasterB64);
    }

    internal static void WriteElement(
        Utf8JsonWriter writer,
        PersonalizationElement value,
        JsonSerializerOptions options,
        bool includeRawRasterB64)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        writer.WriteString("element_type", SnakeCaseEnumJsonConverter<ElementType>.ToApiString(value.ElementType));
        writer.WriteString("panel_id", SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(value.PanelId));
        writer.WriteNumber("x_mm", PythonMath.Round(value.XMm, 4));
        writer.WriteNumber("y_mm", PythonMath.Round(value.YMm, 4));
        writer.WriteNumber("width_mm", PythonMath.Round(value.WidthMm, 4));
        writer.WriteNumber("height_mm", PythonMath.Round(value.HeightMm, 4));
        writer.WriteNumber("rotation_deg", PythonMath.Round(value.RotationDeg, 4));
        writer.WriteBoolean("preserve_aspect_ratio", value.PreserveAspectRatio);
        writer.WriteString("operation", SnakeCaseEnumJsonConverter<CncOperationType>.ToApiString(value.Operation));
        writer.WriteString("anchor", SnakeCaseEnumJsonConverter<AnchorMode>.ToApiString(value.Anchor));
        writer.WriteNumber("rel_x", PythonMath.Round(value.RelX, 4));
        writer.WriteNumber("rel_y", PythonMath.Round(value.RelY, 4));
        if (!value.Visible)
        {
            writer.WriteBoolean("visible", value.Visible);
        }

        if (value.Locked)
        {
            writer.WriteBoolean("locked", value.Locked);
        }

        writer.WritePropertyName("bounding_box");
        JsonSerializer.Serialize(writer, value.BoundingBoxOriented().Rounded(), options);

        writer.WritePropertyName("metadata");
        value.Metadata.WriteTo(writer, options);

        switch (value)
        {
            case TextPersonalizationElement text:
                WriteTextFields(writer, text, options);
                break;
            case VectorSvgPersonalizationElement svg:
                WriteVectorSvgFields(writer, svg, options);
                break;
            case RasterImagePersonalizationElement raster:
                WriteRasterFields(writer, raster, options, includeRawRasterB64);
                break;
            case ShapePersonalizationElement shape:
                WriteShapeFields(writer, shape, options);
                break;
        }

        writer.WriteEndObject();
    }

    private static void ReadBaseFields(JsonElement root, PersonalizationElement target)
    {
        if (GetString(root, "id") is { Length: > 0 } id)
        {
            target.Id = id;
        }

        target.Name = GetString(root, "name") ?? target.Name;
        if (GetString(root, "panel_id") is { } panelStr)
        {
            target.PanelId = SnakeCaseEnumJsonConverter<SurfaceId>.ParseApiString(panelStr);
        }

        target.XMm = GetDouble(root, "x_mm", 0.0);
        target.YMm = GetDouble(root, "y_mm", 0.0);
        target.WidthMm = GetDouble(root, "width_mm", 10.0);
        target.HeightMm = GetDouble(root, "height_mm", 10.0);
        target.RotationDeg = GetDouble(root, "rotation_deg", 0.0);
        target.PreserveAspectRatio = GetBool(root, "preserve_aspect_ratio", true);

        if (GetString(root, "operation") is { } opStr)
        {
            target.Operation = SnakeCaseEnumJsonConverter<CncOperationType>.ParseApiString(opStr);
        }

        if (GetString(root, "anchor") is { } anchorStr)
        {
            target.Anchor = SnakeCaseEnumJsonConverter<AnchorMode>.ParseApiString(anchorStr);
        }

        target.RelX = GetDouble(root, "rel_x", 0.5);
        target.RelY = GetDouble(root, "rel_y", 0.5);
        target.Visible = GetBool(root, "visible", true);
        target.Locked = GetBool(root, "locked", false);

        if (root.TryGetProperty("metadata", out JsonElement metaProp) && metaProp.ValueKind == JsonValueKind.Object)
        {
            target.Metadata = JsonNode.Parse(metaProp.GetRawText())?.AsObject() ?? new JsonObject();
        }
    }

    private static TextPersonalizationElement ReadTextElement(JsonElement root, JsonSerializerOptions options)
    {
        TextRenderMode renderMode = GetString(root, "render_mode") is { } rm
            ? SnakeCaseEnumJsonConverter<TextRenderMode>.ParseApiString(rm)
            : TextRenderMode.Outline;

        double widthMm = GetDouble(root, "width_mm", 10.0);
        double heightMm = GetDouble(root, "height_mm", 10.0);
        string content = GetString(root, "content") ?? string.Empty;

        var el = new TextPersonalizationElement
        {
            Content = content,
            Name = string.IsNullOrEmpty(GetString(root, "name"))
                ? $"Texto: {content[..Math.Min(20, content.Length)]}"
                : GetString(root, "name")!,
            FontFamily = GetString(root, "font_family") ?? "Arial",
            FontPath = GetString(root, "font_path"),
            FontSizeMm = GetDouble(root, "font_size_mm", 12.0),
            Bold = GetBool(root, "bold", false),
            Italic = GetBool(root, "italic", false),
            HorizontalAlign = GetString(root, "horizontal_align") is { } ha
                ? SnakeCaseEnumJsonConverter<HorizontalAlign>.ParseApiString(ha)
                : HorizontalAlign.Left,
            VerticalAlign = GetString(root, "vertical_align") is { } va
                ? SnakeCaseEnumJsonConverter<VerticalAlign>.ParseApiString(va)
                : VerticalAlign.Top,
            LetterSpacingMm = GetDouble(root, "letter_spacing_mm", 0.0),
            LineSpacing = GetDouble(root, "line_spacing", 1.2),
            RenderMode = renderMode,
            HatchSpacingMm = GetDouble(root, "hatch_spacing_mm", 0.25),
            ConvertToPaths = GetBool(root, "convert_to_paths", true),
            Vectorized = GetBool(root, "vectorized", false),
            BaseWidthMm = GetDouble(root, "base_width_mm", widthMm),
            BaseHeightMm = GetDouble(root, "base_height_mm", heightMm),
            ResolvedFontName = GetString(root, "resolved_font_name"),
            FontFallbackUsed = GetBool(root, "font_fallback_used", false),
            Operation = TextPersonalizationElement.DefaultOperationForRenderMode(renderMode),
            VectorPaths = ReadVectorPaths(root, "vector_paths", options),
        };
        return el;
    }

    private static VectorSvgPersonalizationElement ReadVectorSvgElement(JsonElement root, JsonSerializerOptions options)
    {
        double widthMm = GetDouble(root, "width_mm", 10.0);
        double heightMm = GetDouble(root, "height_mm", 10.0);
        double origW = GetDouble(root, "original_width_mm", widthMm);
        double origH = GetDouble(root, "original_height_mm", heightMm);

        var warnings = new List<string>();
        if (root.TryGetProperty("warnings", out JsonElement warnProp) && warnProp.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in warnProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } w)
                {
                    warnings.Add(w);
                }
            }
        }

        string? srcName = GetString(root, "source_filename");
        string? origName = GetString(root, "original_file_name") ?? srcName;
        string? assetEntryPath = GetString(root, "asset_entry_path");
        return new VectorSvgPersonalizationElement
        {
            Name = GetString(root, "name") ?? srcName ?? "Grafico Vetorial SVG",
            SourceFilename = srcName,
            OriginalFileName = origName,
            AssetEntryPath = assetEntryPath,
            SourceUnit = GetString(root, "source_unit") ?? "mm",
            OriginalWidthMm = origW,
            OriginalHeightMm = origH,
            BaseWidthMm = GetDouble(root, "base_width_mm", origW),
            BaseHeightMm = GetDouble(root, "base_height_mm", origH),
            Paths = ReadVectorPaths(root, "paths", options),
            Warnings = warnings,
        };
    }

    private static RasterImagePersonalizationElement ReadRasterElement(JsonElement root, JsonSerializerOptions options)
    {
        double widthMm = GetDouble(root, "width_mm", 10.0);
        double heightMm = GetDouble(root, "height_mm", 10.0);
        bool vectorize = GetBool(root, "vectorize", false);
        string processedB64 = GetString(root, "processed_image_b64") ?? string.Empty;
        string rawB64 = GetString(root, "raw_image_b64") ?? string.Empty;

        RasterProcessingOptions procOpts = new();
        if (root.TryGetProperty("processing_options", out JsonElement optsProp) && optsProp.ValueKind == JsonValueKind.Object)
        {
            procOpts = JsonSerializer.Deserialize<RasterProcessingOptions>(optsProp.GetRawText(), options) ?? new();
        }

        string? srcName = GetString(root, "source_filename");
        string? origName = GetString(root, "original_file_name") ?? srcName;
        string? assetEntryPath = GetString(root, "asset_entry_path");
        string srcFmt = GetString(root, "source_format") ?? "PNG";

        return new RasterImagePersonalizationElement
        {
            Name = GetString(root, "name") ?? srcName ?? $"Imagem {srcFmt}",
            SourceFilename = srcName,
            OriginalFileName = origName,
            AssetEntryPath = assetEntryPath,
            SourceFormat = srcFmt,
            OriginalPixelSize = ReadIntPair(root, "original_pixel_size"),
            ProcessedPixelSize = ReadIntPair(root, "processed_pixel_size"),
            ProcessingOptions = procOpts,
            ProcessedImageB64 = processedB64,
            RawImageB64 = rawB64,
            Vectorize = vectorize,
            VectorizeEpsilonPx = GetDouble(root, "vectorize_epsilon_px", 1.5),
            BaseWidthMm = GetDouble(root, "base_width_mm", widthMm),
            BaseHeightMm = GetDouble(root, "base_height_mm", heightMm),
            VectorPaths = ReadVectorPaths(root, "vector_paths", options),
            Operation = vectorize ? CncOperationType.EngraveVector : CncOperationType.EngraveRaster,
        };
    }

    private static void WriteTextFields(Utf8JsonWriter writer, TextPersonalizationElement text, JsonSerializerOptions options)
    {
        writer.WriteString("content", text.Content);
        writer.WriteString("font_family", text.FontFamily);
        writer.WriteString("font_path", text.FontPath);
        writer.WriteNumber("font_size_mm", PythonMath.Round(text.FontSizeMm, 4));
        writer.WriteBoolean("bold", text.Bold);
        writer.WriteBoolean("italic", text.Italic);
        writer.WriteString("horizontal_align", SnakeCaseEnumJsonConverter<HorizontalAlign>.ToApiString(text.HorizontalAlign));
        writer.WriteString("vertical_align", SnakeCaseEnumJsonConverter<VerticalAlign>.ToApiString(text.VerticalAlign));
        writer.WriteNumber("letter_spacing_mm", PythonMath.Round(text.LetterSpacingMm, 4));
        writer.WriteNumber("line_spacing", PythonMath.Round(text.LineSpacing, 4));
        writer.WriteString("render_mode", SnakeCaseEnumJsonConverter<TextRenderMode>.ToApiString(text.RenderMode));
        writer.WriteNumber("hatch_spacing_mm", PythonMath.Round(text.HatchSpacingMm, 4));
        writer.WriteBoolean("convert_to_paths", text.ConvertToPaths);
        writer.WriteBoolean("vectorized", text.Vectorized);
        writer.WriteString("resolved_font_name", text.ResolvedFontName);
        writer.WriteBoolean("font_fallback_used", text.FontFallbackUsed);
        writer.WriteNumber("base_width_mm", PythonMath.Round(text.BaseWidthMm, 4));
        writer.WriteNumber("base_height_mm", PythonMath.Round(text.BaseHeightMm, 4));
        writer.WritePropertyName("vector_paths");
        JsonSerializer.Serialize(writer, text.VectorPaths, options);
    }

    private static void WriteVectorSvgFields(Utf8JsonWriter writer, VectorSvgPersonalizationElement svg, JsonSerializerOptions options)
    {
        writer.WriteString("source_filename", svg.SourceFilename);
        if (!string.IsNullOrWhiteSpace(svg.OriginalFileName))
        {
            writer.WriteString("original_file_name", svg.OriginalFileName);
        }
        if (!string.IsNullOrWhiteSpace(svg.AssetEntryPath))
        {
            writer.WriteString("asset_entry_path", svg.AssetEntryPath);
        }
        writer.WriteString("source_unit", svg.SourceUnit);
        writer.WriteNumber("original_width_mm", PythonMath.Round(svg.OriginalWidthMm, 4));
        writer.WriteNumber("original_height_mm", PythonMath.Round(svg.OriginalHeightMm, 4));
        writer.WriteNumber("base_width_mm", PythonMath.Round(svg.BaseWidthMm, 4));
        writer.WriteNumber("base_height_mm", PythonMath.Round(svg.BaseHeightMm, 4));
        writer.WriteNumber("path_count", svg.Paths.Count);
        writer.WritePropertyName("paths");
        JsonSerializer.Serialize(writer, svg.Paths, options);
        writer.WritePropertyName("warnings");
        JsonSerializer.Serialize(writer, svg.Warnings, options);
    }

    private static void WriteRasterFields(
        Utf8JsonWriter writer,
        RasterImagePersonalizationElement raster,
        JsonSerializerOptions options,
        bool includeRawB64)
    {
        writer.WriteString("source_filename", raster.SourceFilename);
        if (!string.IsNullOrWhiteSpace(raster.OriginalFileName))
        {
            writer.WriteString("original_file_name", raster.OriginalFileName);
        }
        if (!string.IsNullOrWhiteSpace(raster.AssetEntryPath))
        {
            writer.WriteString("asset_entry_path", raster.AssetEntryPath);
        }
        writer.WriteString("source_format", raster.SourceFormat);
        writer.WritePropertyName("original_pixel_size");
        JsonSerializer.Serialize(writer, raster.OriginalPixelSize, options);
        writer.WritePropertyName("processed_pixel_size");
        JsonSerializer.Serialize(writer, raster.ProcessedPixelSize, options);
        writer.WritePropertyName("processing_options");
        JsonSerializer.Serialize(writer, raster.ProcessingOptions, options);
        writer.WriteString("processed_image_b64", raster.ProcessedImageB64);
        writer.WriteBoolean("vectorize", raster.Vectorize);
        writer.WriteNumber("vectorize_epsilon_px", raster.VectorizeEpsilonPx);
        writer.WriteNumber("base_width_mm", PythonMath.Round(raster.BaseWidthMm, 4));
        writer.WriteNumber("base_height_mm", PythonMath.Round(raster.BaseHeightMm, 4));
        writer.WriteNumber("vector_path_count", raster.VectorPaths.Count);
        writer.WritePropertyName("vector_paths");
        JsonSerializer.Serialize(writer, raster.VectorPaths, options);
        if (includeRawB64 && !string.IsNullOrEmpty(raster.RawImageB64))
        {
            writer.WriteString("raw_image_b64", raster.RawImageB64);
        }
    }

    private static ShapePersonalizationElement ReadShapeElement(JsonElement root, JsonSerializerOptions options)
    {
        GeometricShapeSpec spec = new();
        if (root.TryGetProperty("shape_spec", out JsonElement specProp) && specProp.ValueKind == JsonValueKind.Object)
        {
            spec = JsonSerializer.Deserialize<GeometricShapeSpec>(specProp.GetRawText(), options) ?? new();
        }
        else
        {
            if (GetString(root, "shape_kind") is { } kindStr)
            {
                spec.Kind = SnakeCaseEnumJsonConverter<GeometricShapeKind>.ParseApiString(kindStr);
            }

            spec.WidthMm = GetDouble(root, "shape_width_mm", GetDouble(root, "width_mm", 25.0));
            spec.HeightMm = GetDouble(root, "shape_height_mm", GetDouble(root, "height_mm", 15.0));
            spec.DiameterMm = GetDouble(root, "diameter_mm", 10.0);
            spec.CornerRadiusMm = GetDouble(root, "corner_radius_mm", 0.0);
            if (root.TryGetProperty("sides", out JsonElement sidesEl) && sidesEl.TryGetInt32(out int s))
            {
                spec.Sides = s;
            }
            spec.FillHatch = GetBool(root, "fill_hatch", false);
            spec.HatchSpacingMm = GetDouble(root, "hatch_spacing_mm", ShapeGeometryBuilder.DefaultHatchSpacingMm);
        }

        OperationMode? mode = GetString(root, "operation_mode") is { } om
            ? SnakeCaseEnumJsonConverter<OperationMode>.ParseApiString(om)
            : null;
        CncOperationType? op = GetString(root, "operation") is { } opStr
            ? SnakeCaseEnumJsonConverter<CncOperationType>.ParseApiString(opStr)
            : null;

        var shape = new ShapePersonalizationElement(spec, mode, op);
        if (GetString(root, "name") is { Length: > 0 } customName)
        {
            shape.Name = customName;
        }

        return shape;
    }

    private static void WriteShapeFields(
        Utf8JsonWriter writer,
        ShapePersonalizationElement shape,
        JsonSerializerOptions options)
    {
        GeometricShapeSpec spec = shape.ShapeSpec ?? new GeometricShapeSpec();
        writer.WriteString("operation_mode", SnakeCaseEnumJsonConverter<OperationMode>.ToApiString(shape.OperationMode));
        writer.WritePropertyName("shape_spec");
        JsonSerializer.Serialize(writer, spec, options);
        writer.WriteNumber("base_width_mm", PythonMath.Round(shape.BaseWidthMm, 4));
        writer.WriteNumber("base_height_mm", PythonMath.Round(shape.BaseHeightMm, 4));
        writer.WriteString("svg_path_data", shape.SvgPathData);
        writer.WriteNumber("vector_path_count", shape.VectorPaths.Count);
        writer.WritePropertyName("vector_paths");
        JsonSerializer.Serialize(writer, shape.VectorPaths, options);
    }

    private static List<VectorPathItem> ReadVectorPaths(JsonElement root, string propName, JsonSerializerOptions options)
    {
        if (!root.TryGetProperty(propName, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return new List<VectorPathItem>();
        }

        return JsonSerializer.Deserialize<List<VectorPathItem>>(arr.GetRawText(), options) ?? new List<VectorPathItem>();
    }

    private static int[] ReadIntPair(JsonElement root, string propName)
    {
        if (!root.TryGetProperty(propName, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [1, 1];
        }

        var list = new List<int>(2);
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.TryGetInt32(out int v))
            {
                list.Add(v);
            }
        }

        return list.Count >= 2 ? [list[0], list[1]] : [1, 1];
    }

    private static string? GetString(JsonElement root, string propName)
    {
        if (root.TryGetProperty(propName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }

        return null;
    }

    private static double GetDouble(JsonElement root, string propName, double defaultValue)
    {
        if (!root.TryGetProperty(propName, out JsonElement el))
        {
            return defaultValue;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
        {
            return d;
        }

        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static bool GetBool(JsonElement root, string propName, bool defaultValue)
    {
        if (!root.TryGetProperty(propName, out JsonElement el))
        {
            return defaultValue;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }
}
