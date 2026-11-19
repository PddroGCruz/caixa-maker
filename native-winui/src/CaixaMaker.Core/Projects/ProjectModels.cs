using System.Text.Json;
using System.Text.Json.Serialization;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.Core.Projects;

/// <summary>
/// Parâmetros dimensionais da caixa armazenados na raiz de projetos <c>.json</c> e pacotes <c>.caixamaker.json</c>
/// com as chaves canônicas da v1.2.0 (<c>box_thickness</c>, <c>box_width</c>, <c>box_depth</c>, <c>box_height</c>, <c>folga</c>).
/// Aceita também chaves <c>*_mm</c> na importação por compatibilidade.
/// </summary>
public sealed class ProjectBoxParameters
{
    [JsonPropertyName("box_thickness")]
    public double? BoxThickness { get; set; }

    [JsonPropertyName("box_width")]
    public double? BoxWidth { get; set; }

    [JsonPropertyName("box_depth")]
    public double? BoxDepth { get; set; }

    [JsonPropertyName("box_height")]
    public double? BoxHeight { get; set; }

    [JsonPropertyName("folga")]
    public double? Folga { get; set; }

    [JsonPropertyName("finger_joint_mode")]
    public string? FingerJointMode { get; set; }

    [JsonPropertyName("target_finger_width_mm")]
    public double? TargetFingerWidthMm { get; set; }

    [JsonPropertyName("fixed_finger_count")]
    public int? FixedFingerCount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static ProjectBoxParameters FromBoxParameters(BoxParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new ProjectBoxParameters
        {
            BoxThickness = PythonMath.Round(parameters.BoxThickness, 4),
            BoxWidth = PythonMath.Round(parameters.BoxWidth, 4),
            BoxDepth = PythonMath.Round(parameters.BoxDepth, 4),
            BoxHeight = PythonMath.Round(parameters.BoxHeight, 4),
            Folga = PythonMath.Round(parameters.Folga, 4),
            FingerJointMode = FormatFingerJointMode(parameters.FingerJointMode),
            TargetFingerWidthMm = PythonMath.Round(parameters.TargetFingerWidthMm, 4),
            FixedFingerCount = parameters.FixedFingerCount,
        };
    }

    public static ProjectBoxParameters FromCalculator(BoxCalculator calc)
    {
        ArgumentNullException.ThrowIfNull(calc);
        return new ProjectBoxParameters
        {
            BoxThickness = PythonMath.Round(calc.Thickness, 4),
            BoxWidth = PythonMath.Round(calc.Width, 4),
            BoxDepth = PythonMath.Round(calc.Depth, 4),
            BoxHeight = PythonMath.Round(calc.Height, 4),
            Folga = PythonMath.Round(calc.Folga, 4),
            FingerJointMode = FormatFingerJointMode(calc.FingerJointMode),
            TargetFingerWidthMm = PythonMath.Round(calc.TargetFingerWidthMm, 4),
            FixedFingerCount = calc.FixedFingerCount,
        };
    }

    public BoxParameters ToBoxParameters()
    {
        return new BoxParameters
        {
            BoxThickness = ResolveDouble(BoxThickness, "thickness_mm", BoxParameters.DefaultThickness),
            BoxWidth = ResolveDouble(BoxWidth, "width_mm", BoxParameters.DefaultWidth),
            BoxDepth = ResolveDouble(BoxDepth, "depth_mm", BoxParameters.DefaultDepth),
            BoxHeight = ResolveDouble(BoxHeight, "height_mm", BoxParameters.DefaultHeight),
            Folga = ResolveDouble(Folga, "folga_mm", BoxParameters.DefaultFolga),
            FingerJointMode = ParseFingerJointMode(FingerJointMode),
            TargetFingerWidthMm = Math.Clamp(
                ResolveDouble(TargetFingerWidthMm, "target_finger_width_mm", BoxParameters.DefaultTargetFingerWidthMm),
                BoxParameters.MinTargetFingerWidthMm,
                BoxParameters.MaxTargetFingerWidthMm),
            FixedFingerCount = Math.Clamp(
                FixedFingerCount ?? BoxParameters.DefaultFixedFingerCount,
                BoxParameters.MinFixedFingerCount,
                BoxParameters.MaxFixedFingerCount),
        };
    }

    public static string FormatFingerJointMode(FingerJointSizingMode mode) => mode switch
    {
        FingerJointSizingMode.TargetSizeMm => "target_size_mm",
        FingerJointSizingMode.FixedCount => "fixed_count",
        _ => "auto",
    };

    public static FingerJointSizingMode ParseFingerJointMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return BoxParameters.DefaultFingerJointMode;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "target_size_mm" or "targetsizemm" or "target_size" => FingerJointSizingMode.TargetSizeMm,
            "fixed_count" or "fixedcount" or "fixed" => FingerJointSizingMode.FixedCount,
            _ => FingerJointSizingMode.Auto,
        };
    }

    private double ResolveDouble(double? primary, string altKey, double fallback)
    {
        if (primary.HasValue && double.IsFinite(primary.Value))
        {
            return primary.Value;
        }

        if (ExtensionData is not null && ExtensionData.TryGetValue(altKey, out JsonElement raw))
        {
            if (raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out double d) && double.IsFinite(d))
            {
                return d;
            }
        }

        return fallback;
    }
}

/// <summary>
/// Estado do visualizador 3D/2D salvo junto ao projeto (compatível com <c>viewer_state</c> v1.2.0 e v2.0 .cmaker).
/// </summary>
public sealed class ProjectViewerState
{
    [JsonPropertyName("exploded")]
    public bool Exploded { get; set; }

    [JsonPropertyName("lid_angle")]
    public double LidAngle { get; set; } = 35.0;

    [JsonPropertyName("show_dimensions")]
    public bool ShowDimensions { get; set; } = true;

    [JsonPropertyName("active_surface")]
    public string ActiveSurface { get; set; } = "front";

    [JsonPropertyName("dimension_mode")]
    public string? DimensionMode { get; set; }

    [JsonPropertyName("camera_yaw_deg")]
    public double? CameraYawDeg { get; set; }

    [JsonPropertyName("camera_pitch_deg")]
    public double? CameraPitchDeg { get; set; }

    [JsonPropertyName("camera_zoom")]
    public double? CameraZoom { get; set; }

    [JsonPropertyName("camera_pan_x")]
    public double? CameraPanX { get; set; }

    [JsonPropertyName("camera_pan_y")]
    public double? CameraPanY { get; set; }

    [JsonPropertyName("center_view_mode")]
    public string? CenterViewMode { get; set; }

    [JsonPropertyName("split_orientation")]
    public string? SplitOrientation { get; set; }

    [JsonPropertyName("split_viewport_ratio")]
    public double? SplitViewportRatio { get; set; }

    [JsonPropertyName("laser_bed_preset")]
    public string? LaserBedPreset { get; set; }

    [JsonPropertyName("laser_bed_width_mm")]
    public double? LaserBedWidthMm { get; set; }

    [JsonPropertyName("laser_bed_height_mm")]
    public double? LaserBedHeightMm { get; set; }

    [JsonPropertyName("kerf_slot_count")]
    public int? KerfSlotCount { get; set; }

    [JsonPropertyName("kerf_step_mm")]
    public double? KerfStepMm { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Registro completo persistido em <c>.json</c> legado ou reconstruído a partir de <c>.cmaker</c>.</summary>
public sealed class ProjectRecord
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = string.Empty;

    [JsonPropertyName("box_parameters")]
    public ProjectBoxParameters BoxParameters { get; set; } = new();

    [JsonPropertyName("viewer_state")]
    public ProjectViewerState ViewerState { get; set; } = new();

    [JsonPropertyName("personalization")]
    public PersonalizationSnapshot Personalization { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Resumo leve de um projeto salvo para listagem e cartões da galeria.</summary>
public sealed class ProjectSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public required string UpdatedAt { get; init; }

    [JsonPropertyName("model_id")]
    public required string ModelId { get; init; }

    [JsonPropertyName("model_name")]
    public required string ModelName { get; init; }

    [JsonPropertyName("box_parameters")]
    public required ProjectBoxParameters BoxParameters { get; init; }

    [JsonPropertyName("viewer_state")]
    public required ProjectViewerState ViewerState { get; init; }

    [JsonPropertyName("element_count")]
    public int ElementCount { get; init; }

    [JsonPropertyName("asset_count")]
    public int AssetCount { get; init; }

    [JsonPropertyName("total_cut_perimeter_meters")]
    public double TotalCutPerimeterMeters { get; init; }

    [JsonPropertyName("validation_valid")]
    public bool ValidationValid { get; init; } = true;

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; init; }

    [JsonPropertyName("warning_count")]
    public int WarningCount { get; init; }

    [JsonPropertyName("package_format")]
    public string PackageFormat { get; init; } = "json";

    [JsonPropertyName("package_schema_version")]
    public string? PackageSchemaVersion { get; init; }

    [JsonPropertyName("package_size_bytes")]
    public long PackageSizeBytes { get; init; }

    [JsonIgnore]
    public string? PackageFilePath { get; init; }

    [JsonIgnore]
    public byte[]? ThumbnailPngBytes { get; init; }
}

/// <summary>Dados de entrada para criação ou atualização de um projeto salvo.</summary>
public sealed class ProjectSaveRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Notes { get; init; }
    public required string ModelId { get; init; }
    public string? ModelName { get; init; }
    public required BoxParameters BoxParameters { get; init; }
    public ProjectViewerState? ViewerState { get; init; }
    public SurfaceManager? Manager { get; init; }
    public PersonalizationSnapshot? PersonalizationSnapshot { get; init; }
    public double SafetyMarginMm { get; init; } = GeometryValidator.DefaultSafetyMarginMm;
    public string? SavedId { get; init; }
    public string? CreatedAtOverride { get; init; }
}

/// <summary>Resultado completo da abertura de um projeto salvo reconstruído com seu <see cref="SurfaceManager"/>.</summary>
public sealed class LoadedProjectSession
{
    public required ProjectRecord Record { get; init; }
    public required ProjectSummary Summary { get; init; }
    public required BoxParameters BoxParameters { get; init; }
    public required ProjectViewerState ViewerState { get; init; }
    public required SurfaceManager Manager { get; init; }
    public string SourceFormat { get; init; } = "json";
    public string? PackageSchemaVersion { get; init; }
    public string? PackageFilePath { get; init; }
    public string? MigrationNotice { get; init; }
    public int AssetCount { get; init; }
    public long PackageSizeBytes { get; init; }
}

/// <summary>Pacote portátil <c>.caixamaker.json</c> (v1.2.0 schema_version = 1).</summary>
public sealed class PortableProjectBundle
{
    public const string CanonicalFormat = "caixa-maker-project";
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("format")]
    public string Format { get; set; } = CanonicalFormat;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("exported_at")]
    public string ExportedAt { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = string.Empty;

    [JsonPropertyName("box_parameters")]
    public ProjectBoxParameters BoxParameters { get; set; } = new();

    [JsonPropertyName("viewer_state")]
    public ProjectViewerState ViewerState { get; set; } = new();

    [JsonPropertyName("safety_margin_mm")]
    public double SafetyMarginMm { get; set; } = GeometryValidator.DefaultSafetyMarginMm;

    [JsonPropertyName("personalization")]
    public PersonalizationSnapshot Personalization { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Resultado da exportação em memória ou arquivo de um pacote portátil <c>.caixamaker.json</c>.</summary>
public sealed class PortablePackageExportResult
{
    public required string Filename { get; init; }
    public required PortableProjectBundle Bundle { get; init; }
    public required string Json { get; init; }
}
