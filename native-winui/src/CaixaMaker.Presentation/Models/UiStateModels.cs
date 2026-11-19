using System.Text.Json.Serialization;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CaixaMaker.App.Models;

/// <summary>Preferências persistidas da interface em <c>ui_settings.json</c>.</summary>
public sealed class UiSettingsData
{
    public const int SchemaVersionCurrent = 1;
    public const double MinLeftSidebarWidth = 240.0;
    public const double MaxLeftSidebarWidth = 440.0;
    public const double DefaultLeftSidebarWidth = 320.0;
    public const double MinRightSidebarWidth = 260.0;
    public const double MaxRightSidebarWidth = 480.0;
    public const double DefaultRightSidebarWidth = 340.0;
    public const double MinSplitViewportRatio = 0.25;
    public const double MaxSplitViewportRatio = 0.75;
    public const double DefaultSplitViewportRatio = 0.52;
    public const double MinLaserBedDimensionMm = 50.0;
    public const double MaxLaserBedDimensionMm = 5000.0;
    public const double DefaultCustomBedWidthMm = 500.0;
    public const double DefaultCustomBedHeightMm = 300.0;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = SchemaVersionCurrent;

    [JsonPropertyName("theme_preference")]
    public string ThemePreference { get; set; } = "system";

    [JsonPropertyName("left_sidebar_width")]
    public double LeftSidebarWidth { get; set; } = DefaultLeftSidebarWidth;

    [JsonPropertyName("right_sidebar_width")]
    public double RightSidebarWidth { get; set; } = DefaultRightSidebarWidth;

    [JsonPropertyName("left_sidebar_open")]
    public bool LeftSidebarOpen { get; set; } = true;

    [JsonPropertyName("right_sidebar_open")]
    public bool RightSidebarOpen { get; set; } = true;

    [JsonPropertyName("toolbar_visible")]
    public bool ToolbarVisible { get; set; } = true;

    [JsonPropertyName("split_orientation")]
    public string SplitOrientation { get; set; } = nameof(SplitViewportOrientation.VerticalStacked);

    [JsonPropertyName("split_viewport_ratio")]
    public double SplitViewportRatio { get; set; } = DefaultSplitViewportRatio;

    [JsonPropertyName("selected_laser_bed_preset")]
    public string SelectedLaserBedPreset { get; set; } = nameof(LaserBedPreset.Laser600x400);

    [JsonPropertyName("custom_bed_width_mm")]
    public double CustomBedWidthMm { get; set; } = DefaultCustomBedWidthMm;

    [JsonPropertyName("custom_bed_height_mm")]
    public double CustomBedHeightMm { get; set; } = DefaultCustomBedHeightMm;

    [JsonPropertyName("show_dimensions")]
    public bool ShowDimensions { get; set; } = true;

    [JsonPropertyName("default_safety_margin_mm")]
    public double DefaultSafetyMarginMm { get; set; } = GeometryValidator.DefaultSafetyMarginMm;

    [JsonPropertyName("default_hatch_spacing_mm")]
    public double DefaultHatchSpacingMm { get; set; } = 0.25;

    [JsonPropertyName("autosave_enabled")]
    public bool AutosaveEnabled { get; set; } = true;

    [JsonPropertyName("recent_project_ids")]
    public List<string> RecentProjectIds { get; set; } = new();
}

/// <summary>Rascunho automático salvo em <c>autosave_draft.json</c> para recuperação de sessão.</summary>
public sealed class AutosaveDraftRecord
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("saved_at")]
    public string SavedAt { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = string.Empty;

    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("saved_project_id")]
    public string? SavedProjectId { get; set; }

    [JsonPropertyName("active_area")]
    public string ActiveArea { get; set; } = "Modeling";

    [JsonPropertyName("center_view_mode")]
    public string CenterViewMode { get; set; } = "Mode3D";

    [JsonPropertyName("safety_margin_mm")]
    public double SafetyMarginMm { get; set; } = GeometryValidator.DefaultSafetyMarginMm;

    [JsonPropertyName("box_parameters")]
    public ProjectBoxParameters BoxParameters { get; set; } = new();

    [JsonPropertyName("viewer_state")]
    public ProjectViewerState ViewerState { get; set; } = new();

    [JsonPropertyName("personalization")]
    public PersonalizationSnapshot Personalization { get; set; } = new();
}

/// <summary>Preset rápido de dimensões para modelagem paramétrica.</summary>
public sealed record BoxSizePreset(
    string Id,
    string Label,
    string Subtitle,
    double WidthMm,
    double DepthMm,
    double HeightMm,
    double ThicknessMm);

/// <summary>Entrada da tabela de atalhos de teclado exibida no modal Sobre e Atalhos.</summary>
public sealed record ShortcutReferenceItem(
    string Category,
    string Shortcut,
    string Action,
    string Scope);

/// <summary>Filtro tipado para diálogos nativos de abertura e gravação de arquivos.</summary>
public sealed record NativeFileFilter(
    string Description,
    IReadOnlyList<string> Extensions);

/// <summary>Estado de visibilidade e contagem de uma camada CNC na área de Fabricação.</summary>
public sealed partial class CncLayerItemViewModel : ObservableObject
{
    public CncOperationType Operation { get; }
    public string LayerId { get; }
    public string Label { get; }
    public string HexColor { get; }
    public string StrokeWidthDisplay { get; }

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private int _elementCount;

    public CncLayerItemViewModel(CncLayerSpec spec, int elementCount = 0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Operation = spec.Operation;
        LayerId = spec.Id;
        Label = spec.Label;
        HexColor = spec.Stroke != "none" ? spec.Stroke : (spec.Fill != "none" ? spec.Fill : "#7C8591");
        StrokeWidthDisplay = spec.StrokeWidth;
        _elementCount = elementCount;
    }
}

/// <summary>Registro de um arquivo gerado ou exportado na sessão atual.</summary>
public sealed record GeneratedOutputRecord(
    string Filename,
    string FormatLabel,
    string FullPath,
    long SizeBytes,
    string TimestampText);
