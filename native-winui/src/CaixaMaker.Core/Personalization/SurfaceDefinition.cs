using System.Text.Json.Serialization;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.Personalization;

/// <summary>Ponto tridimensional em milímetros no espaço local da caixa montada.</summary>
public readonly record struct Point3D(double X, double Y, double Z);

/// <summary>Resultado da transformação de coordenadas 2D de uma superfície para o plano SVG/CNC.</summary>
public readonly record struct PanelSvgTransformResult(double XMm, double YMm, double RotationDeg);

/// <summary>Região estrutural ou de encaixe inadequada para gravação em uma superfície.</summary>
public sealed record ForbiddenZone
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("zone_type")]
    public required ForbiddenZoneType ZoneType { get; init; }

    [JsonPropertyName("rect_mm")]
    public required Rect2D RectMm { get; init; }
}

/// <summary>Contrato geométrico completo de uma das faces personalizáveis da caixa.</summary>
public sealed record SurfaceDefinition
{
    [JsonPropertyName("panel_id")]
    public required SurfaceId PanelId { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("width_mm")]
    public required double WidthMm { get; init; }

    [JsonPropertyName("height_mm")]
    public required double HeightMm { get; init; }

    [JsonPropertyName("safe_rect_mm")]
    public required Rect2D SafeRectMm { get; init; }

    [JsonPropertyName("forbidden_zones")]
    public required IReadOnlyList<ForbiddenZone> ForbiddenZones { get; init; }

    [JsonPropertyName("svg_cell_origin_mm")]
    public required double[] SvgCellOriginMm { get; init; }

    [JsonPropertyName("svg_body_offset_mm")]
    public required double[] SvgBodyOffsetMm { get; init; }

    [JsonPropertyName("cnc_rotation_deg")]
    public double CncRotationDeg { get; init; }

    [JsonPropertyName("cnc_mirror_x")]
    public bool CncMirrorX { get; init; }

    [JsonPropertyName("normal_3d")]
    public required double[] Normal3D { get; init; }

    [JsonPropertyName("assembled_origin_3d_mm")]
    public required double[] AssembledOrigin3DMm { get; init; }

    [JsonPropertyName("orientation_notes")]
    public string OrientationNotes { get; init; } = string.Empty;

    public Rect2D BoundsRect() => new(0.0, 0.0, WidthMm, HeightMm);

    public Rect2D SafeAreaForMargin(double safetyMarginMm = 0.0)
        => SafeRectMm.Shrink(safetyMarginMm);
}
