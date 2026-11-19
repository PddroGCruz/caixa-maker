using System.Text.Json;
using System.Text.Json.Serialization;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;

namespace CaixaMaker.App.Models;

/// <summary>Entrada verificável de integridade no <c>manifest.json</c> de um pacote <c>.cmaker</c>.</summary>
public sealed class CmakerManifestEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Manifesto raiz <c>manifest.json</c> dentro do contêiner ZIP <c>.cmaker</c>.</summary>
public sealed class CmakerManifest
{
    public const string CanonicalFormat = "cmaker";
    public const string CurrentSchemaVersion = "2.0.0";

    [JsonPropertyName("format")]
    public string Format { get; set; } = CanonicalFormat;

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("created_utc")]
    public string CreatedUtc { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<CmakerManifestEntry> Entries { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Trio dimensional em milímetros (largura × profundidade × altura).</summary>
public sealed class CmakerDimensionsDto
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("depth")]
    public double Depth { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

/// <summary>Metadados pesquisáveis e telemetria de engenharia em <c>metadata.json</c>.</summary>
public sealed class CmakerMetadata
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

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

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("model_display_name")]
    public string ModelDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("external_dimensions_mm")]
    public CmakerDimensionsDto ExternalDimensionsMm { get; set; } = new();

    [JsonPropertyName("internal_usable_dimensions_mm")]
    public CmakerDimensionsDto InternalUsableDimensionsMm { get; set; } = new();

    [JsonPropertyName("dimension_reference_mode")]
    public string DimensionReferenceMode { get; set; } = "external";

    [JsonPropertyName("thickness_mm")]
    public double ThicknessMm { get; set; }

    [JsonPropertyName("folga_mm")]
    public double FolgaMm { get; set; }

    [JsonPropertyName("safety_margin_mm")]
    public double SafetyMarginMm { get; set; } = GeometryValidator.DefaultSafetyMarginMm;

    [JsonPropertyName("panel_count")]
    public int PanelCount { get; set; }

    [JsonPropertyName("total_cut_perimeter_meters")]
    public double TotalCutPerimeterMeters { get; set; }

    [JsonPropertyName("element_count")]
    public int ElementCount { get; set; }

    [JsonPropertyName("asset_count")]
    public int AssetCount { get; set; }

    [JsonPropertyName("validation_valid")]
    public bool ValidationValid { get; set; } = true;

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warning_count")]
    public int WarningCount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Configurações de fabricação/mesa CNC armazenadas dentro de <c>model.json</c>.</summary>
public sealed class CmakerManufacturingState
{
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

    [JsonPropertyName("include_engrave_only_in_zip")]
    public bool? IncludeEngraveOnlyInZip { get; set; }

    [JsonPropertyName("include_full_sheet_in_zip")]
    public bool? IncludeFullSheetInZip { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Parâmetros construtivos e estado de viewport em <c>model.json</c>.</summary>
public sealed class CmakerModelState
{
    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("box_parameters")]
    public ProjectBoxParameters BoxParameters { get; set; } = new();

    [JsonPropertyName("dimension_reference_mode")]
    public string DimensionReferenceMode { get; set; } = "external";

    [JsonPropertyName("safety_margin_mm")]
    public double SafetyMarginMm { get; set; } = GeometryValidator.DefaultSafetyMarginMm;

    [JsonPropertyName("viewer_state")]
    public ProjectViewerState ViewerState { get; set; } = new();

    [JsonPropertyName("manufacturing_state")]
    public CmakerManufacturingState ManufacturingState { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Resumo de uma chapa dentro de <c>personalization/surfaces.json</c>.</summary>
public sealed class CmakerSurfaceHeader
{
    [JsonPropertyName("panel_id")]
    public string PanelId { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("width_mm")]
    public double WidthMm { get; set; }

    [JsonPropertyName("height_mm")]
    public double HeightMm { get; set; }

    [JsonPropertyName("element_count")]
    public int ElementCount { get; set; }
}

/// <summary>Índice de superfícies em <c>personalization/surfaces.json</c>.</summary>
public sealed class CmakerSurfacesIndex
{
    [JsonPropertyName("safety_margin_mm")]
    public double SafetyMarginMm { get; set; } = GeometryValidator.DefaultSafetyMarginMm;

    [JsonPropertyName("surface_ids")]
    public List<string> SurfaceIds { get; set; } = [];

    [JsonPropertyName("surfaces")]
    public List<CmakerSurfaceHeader> Surfaces { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Documento de elementos de uma face específica em <c>personalization/&lt;surface_id&gt;.json</c>.</summary>
public sealed class CmakerSurfaceDocument
{
    [JsonPropertyName("panel_id")]
    public string PanelId { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("elements")]
    public List<PersonalizationElement> Elements { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Resultado em memória da construção de um pacote autocontido <c>.cmaker</c>.</summary>
public sealed class CmakerPackageBuildResult
{
    public required string FileName { get; init; }
    public required byte[] PackageBytes { get; init; }
    public required CmakerManifest Manifest { get; init; }
    public required CmakerMetadata Metadata { get; init; }
    public required ProjectSummary Summary { get; init; }
}

/// <summary>Relatório consolidado da migração de projetos legados <c>.json</c> para <c>.cmaker</c>.</summary>
public sealed class LegacyCmakerMigrationReport
{
    public int MigratedCount { get; init; }
    public int SkippedExistingCount { get; init; }
    public int ErrorCount => Errors.Count;
    public IReadOnlyList<string> MigratedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
