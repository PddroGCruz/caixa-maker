using System.Globalization;
using System.Text.Json.Serialization;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.Personalization;

/// <summary>Severidade de diagnóstico geométrico do validador SAT.</summary>
[JsonConverter(typeof(SnakeCaseEnumJsonConverter<ValidationSeverity>))]
public enum ValidationSeverity
{
    Error,
    Warning,
}

/// <summary>Ocorrência individual de validação espacial ou de manufatura CNC.</summary>
public sealed record GeometryValidationIssue
{
    [JsonPropertyName("severity")]
    public required ValidationSeverity Severity { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("panel_id")]
    public required string PanelId { get; init; }

    [JsonPropertyName("element_id")]
    public string? ElementId { get; init; }

    [JsonPropertyName("zone_id")]
    public string? ZoneId { get; init; }

    [JsonPropertyName("details")]
    public Dictionary<string, object?> Details { get; init; } = new();
}

/// <summary>Relatório consolidado da validação espacial SAT de uma personalização.</summary>
public sealed record ValidationReport
{
    [JsonPropertyName("is_valid")]
    public required bool IsValid { get; init; }

    [JsonPropertyName("error_count")]
    public int ErrorCount => Errors.Count;

    [JsonPropertyName("warning_count")]
    public int WarningCount => Warnings.Count;

    [JsonPropertyName("safety_margin_mm")]
    public required double SafetyMarginMm { get; init; }

    [JsonPropertyName("errors")]
    public required IReadOnlyList<GeometryValidationIssue> Errors { get; init; }

    [JsonPropertyName("warnings")]
    public required IReadOnlyList<GeometryValidationIssue> Warnings { get; init; }

    [JsonPropertyName("cam_notes")]
    public required IReadOnlyList<string> CamNotes { get; init; }

    /// <summary>Indica se o projeto está livre de erros bloqueantes para exportação CNC.</summary>
    [JsonIgnore]
    public bool CanExport => IsValid;
}

/// <summary>
/// Validador geométrico espacial baseado no Separating Axis Theorem (SAT).
/// Porta integralmente <c>personalization/validator.py</c>.
/// </summary>
public sealed class GeometryValidator
{
    public const double DefaultTolerance = 1e-4;
    public const double DefaultSafetyMarginMm = 2.0;

    private static readonly string[] DefaultCamNotes =
    [
        "Corte e gravação são exportados em camadas SVG distintas por operação CNC/CAM.",
        "Potência do laser, velocidade de avanço (feedrate) e profundidade de passe devem ser definidos no software CAM do laboratório conforme o MDF utilizado.",
    ];

    public double SafetyMarginMm { get; }

    public GeometryValidator(double safetyMarginMm = DefaultSafetyMarginMm)
    {
        SafetyMarginMm = Math.Max(0.0, safetyMarginMm);
    }

    /// <summary>
    /// Verifica interseção entre dois polígonos 2D convexos usando o Teorema do Eixo Separador (SAT).
    /// Retorna <c>false</c> quando há folga igual ou superior a <paramref name="tolerance"/> em algum eixo separador.
    /// </summary>
    public static bool PolygonsIntersectSat(
        IReadOnlyList<Point2D> polyA,
        IReadOnlyList<Point2D> polyB,
        double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(polyA);
        ArgumentNullException.ThrowIfNull(polyB);
        if (polyA.Count < 3 || polyB.Count < 3)
        {
            return false;
        }

        return !HasSeparatingAxis(polyA, polyA, polyB, tolerance)
            && !HasSeparatingAxis(polyB, polyA, polyB, tolerance);
    }

    /// <summary>Executa a validação completa de todos os elementos em relação às superfícies ativas.</summary>
    public ValidationReport Validate(
        SurfaceMapper surfaceMapper,
        IEnumerable<PersonalizationElement> elements,
        double? safetyMarginMm = null,
        bool strictZones = true)
    {
        ArgumentNullException.ThrowIfNull(surfaceMapper);
        ArgumentNullException.ThrowIfNull(elements);
        return ValidateCore(surfaceMapper, elements, filterPanelId: null, safetyMarginMm, strictZones);
    }

    /// <summary>
    /// Executa a validação geométrica SAT restrita a uma única superfície ativa,
    /// utilizada como caminho rápido em manipulações interativas 60+ FPS.
    /// </summary>
    public ValidationReport ValidatePanel(
        SurfaceMapper surfaceMapper,
        SurfaceId panelId,
        IEnumerable<PersonalizationElement> elements,
        double? safetyMarginMm = null,
        bool strictZones = true)
    {
        ArgumentNullException.ThrowIfNull(surfaceMapper);
        ArgumentNullException.ThrowIfNull(elements);
        return ValidateCore(surfaceMapper, elements, panelId, safetyMarginMm, strictZones);
    }

    private ValidationReport ValidateCore(
        SurfaceMapper surfaceMapper,
        IEnumerable<PersonalizationElement> elements,
        SurfaceId? filterPanelId,
        double? safetyMarginMm,
        bool strictZones)
    {
        double margin = safetyMarginMm.HasValue ? Math.Max(0.0, safetyMarginMm.Value) : SafetyMarginMm;
        var errors = new List<GeometryValidationIssue>();
        var warnings = new List<GeometryValidationIssue>();
        var byPanel = new Dictionary<SurfaceId, List<PersonalizationElement>>();
        var panelBoundsCache = new Dictionary<SurfaceId, (SurfaceDefinition Surface, string PanelName, Rect2D Bounds, IReadOnlyList<Point2D> BoundsCorners, Rect2D SafeArea)>();

        foreach (PersonalizationElement el in elements)
        {
            if (filterPanelId.HasValue && el.PanelId != filterPanelId.Value)
            {
                continue;
            }

            if (!byPanel.TryGetValue(el.PanelId, out List<PersonalizationElement>? list))
            {
                list = new List<PersonalizationElement>();
                byPanel[el.PanelId] = list;
            }

            list.Add(el);

            if (!panelBoundsCache.TryGetValue(el.PanelId, out var panelCtx))
            {
                SurfaceDefinition surface = surfaceMapper.GetSurface(el.PanelId);
                string panelName = SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(surface.PanelId);
                Rect2D bounds = surface.BoundsRect();
                panelCtx = (surface, panelName, bounds, bounds.ToCorners(), surface.SafeAreaForMargin(margin));
                panelBoundsCache[el.PanelId] = panelCtx;
            }

            SurfaceDefinition surf = panelCtx.Surface;
            string pName = panelCtx.PanelName;
            Rect2D surfBounds = panelCtx.Bounds;
            IReadOnlyList<Point2D> boundsCorners = panelCtx.BoundsCorners;
            Rect2D safeArea = panelCtx.SafeArea;

            IReadOnlyList<Point2D> corners = el.GetCollisionPolygon2D();

            // 1. Limites físicos da chapa (sem alocação de arrays intermediários)
            bool anyInside = false;
            bool allInside = corners.Count > 0;
            for (int c = 0; c < corners.Count; c++)
            {
                Point2D pt = corners[c];
                bool ptInside = pt.X >= -DefaultTolerance && pt.X <= surfBounds.WidthMm + DefaultTolerance
                    && pt.Y >= -DefaultTolerance && pt.Y <= surfBounds.HeightMm + DefaultTolerance;
                anyInside |= ptInside;
                allInside &= ptInside;
            }

            if (!anyInside && !PolygonsIntersectSat(corners, boundsCorners))
            {
                errors.Add(new GeometryValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Code = "OUT_OF_BOUNDS_COMPLETE",
                    Message = string.Format(
                        CultureInfo.InvariantCulture,
                        "Elemento '{0}' está totalmente fora dos limites úteis da superfície '{1}' ({2:F2} x {3:F2} mm).",
                        el.Name,
                        pName,
                        surfBounds.WidthMm,
                        surfBounds.HeightMm),
                    PanelId = pName,
                    ElementId = el.Id,
                });
            }
            else if (!allInside)
            {
                errors.Add(new GeometryValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Code = "OUT_OF_BOUNDS_PARTIAL",
                    Message = string.Format(
                        CultureInfo.InvariantCulture,
                        "Elemento '{0}' ultrapassa parcialmente as bordas da superfície '{1}' ({2:F2} x {3:F2} mm).",
                        el.Name,
                        pName,
                        surfBounds.WidthMm,
                        surfBounds.HeightMm),
                    PanelId = pName,
                    ElementId = el.Id,
                });
            }

            // 2. Colisões SAT com zonas estruturais proibidas (finger_joint, through_slot, hinge_ear, edge_margin)
            foreach (ForbiddenZone zone in surf.ForbiddenZones)
            {
                if (PolygonsIntersectSat(corners, zone.RectMm.ToCorners()))
                {
                    var issue = new GeometryValidationIssue
                    {
                        Severity = strictZones ? ValidationSeverity.Error : ValidationSeverity.Warning,
                        Code = "COLLISION_FORBIDDEN_ZONE",
                        Message = $"Elemento '{el.Name}' colide com região estrutural '{zone.Label}' em '{pName}': {zone.Reason}",
                        PanelId = pName,
                        ElementId = el.Id,
                        ZoneId = zone.Id,
                        Details = new Dictionary<string, object?>
                        {
                            ["zone_type"] = SnakeCaseEnumJsonConverter<ForbiddenZoneType>.ToApiString(zone.ZoneType),
                            ["zone_rect"] = zone.RectMm.Rounded(),
                        },
                    };

                    if (strictZones)
                    {
                        errors.Add(issue);
                    }
                    else
                    {
                        warnings.Add(issue);
                    }
                }
            }

            // 3. Margem de segurança interna
            if (safeArea.WidthMm <= 0.0 || safeArea.HeightMm <= 0.0)
            {
                warnings.Add(new GeometryValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Code = "SAFETY_MARGIN_EXCEEDS_PANEL",
                    Message = string.Format(
                        CultureInfo.InvariantCulture,
                        "Margem de segurança ({0:F1} mm) excede a área útil livre da peça '{1}'.",
                        margin,
                        pName),
                    PanelId = pName,
                    ElementId = el.Id,
                });
            }
            else if (allInside)
            {
                bool allInSafe = true;
                for (int c = 0; c < corners.Count; c++)
                {
                    Point2D pt = corners[c];
                    if (pt.X < safeArea.XMm - DefaultTolerance
                        || pt.X > safeArea.Right + DefaultTolerance
                        || pt.Y < safeArea.YMm - DefaultTolerance
                        || pt.Y > safeArea.Bottom + DefaultTolerance)
                    {
                        allInSafe = false;
                        break;
                    }
                }

                if (!allInSafe)
                {
                    warnings.Add(new GeometryValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Code = "SAFETY_MARGIN_VIOLATION",
                        Message = string.Format(
                            CultureInfo.InvariantCulture,
                            "Elemento '{0}' invade a margem de segurança de {1:F1} mm próxima às bordas/encaixes em '{2}'.",
                            el.Name,
                            margin,
                            pName),
                        PanelId = pName,
                        ElementId = el.Id,
                        Details = new Dictionary<string, object?>
                        {
                            ["safe_rect_mm"] = safeArea.Rounded(),
                        },
                    });
                }
            }

            // 4. Legibilidade e manufatura CNC
            if (el.WidthMm < 1.0 || el.HeightMm < 1.0)
            {
                warnings.Add(new GeometryValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Code = "ELEMENT_TOO_SMALL",
                    Message = string.Format(
                        CultureInfo.InvariantCulture,
                        "Elemento '{0}' possui dimensão muito reduzida ({1:F2} x {2:F2} mm), podendo comprometer a legibilidade no MDF.",
                        el.Name,
                        el.WidthMm,
                        el.HeightMm),
                    PanelId = pName,
                    ElementId = el.Id,
                });
            }

            if (el is TextPersonalizationElement textEl && textEl.FontFallbackUsed)
            {
                warnings.Add(new GeometryValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Code = "FONT_FALLBACK_USED",
                    Message = $"Fonte solicitada '{textEl.FontFamily}' não localizada; substituída por '{textEl.ResolvedFontName}'.",
                    PanelId = pName,
                    ElementId = el.Id,
                });
            }

            if (el is VectorSvgPersonalizationElement svgEl && svgEl.Warnings.Count > 0)
            {
                foreach (string wMsg in svgEl.Warnings)
                {
                    warnings.Add(new GeometryValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Code = "SVG_IMPORT_WARNING",
                        Message = $"{el.Name}: {wMsg}",
                        PanelId = pName,
                        ElementId = el.Id,
                    });
                }
            }

            if (el is RasterImagePersonalizationElement rasterEl && !rasterEl.Vectorize)
            {
                warnings.Add(new GeometryValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Code = "RASTER_NOT_VECTORIZED",
                    Message =
                        $"Imagem '{el.Name}' está em modo raster (varredura). Ideal para gravação a laser em camada 'layer_engrave_raster'; para fresagem vetorial, ative a vetorização de contornos.",
                    PanelId = pName,
                    ElementId = el.Id,
                });
            }
        }

        // 5. Sobreposição par-a-par na mesma face (via SAT)
        foreach ((SurfaceId panelId, List<PersonalizationElement> panelElems) in byPanel)
        {
            string panelName = SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(panelId);
            for (int i = 0; i < panelElems.Count; i++)
            {
                for (int j = i + 1; j < panelElems.Count; j++)
                {
                    PersonalizationElement ea = panelElems[i];
                    PersonalizationElement eb = panelElems[j];
                    if (PolygonsIntersectSat(ea.GetCollisionPolygon2D(), eb.GetCollisionPolygon2D()))
                    {
                        warnings.Add(new GeometryValidationIssue
                        {
                            Severity = ValidationSeverity.Warning,
                            Code = "ELEMENT_OVERLAP",
                            Message = $"Elementos '{ea.Name}' e '{eb.Name}' se sobrepõem na superfície '{panelName}'.",
                            PanelId = panelName,
                            ElementId = ea.Id,
                            Details = new Dictionary<string, object?>
                            {
                                ["other_element_id"] = eb.Id,
                                ["other_element_name"] = eb.Name,
                            },
                        });
                    }
                }
            }
        }

        return new ValidationReport
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            SafetyMarginMm = PythonMath.Round(margin, 4),
            CamNotes = DefaultCamNotes,
        };
    }

    private static bool HasSeparatingAxis(
        IReadOnlyList<Point2D> edgeSource,
        IReadOnlyList<Point2D> polyA,
        IReadOnlyList<Point2D> polyB,
        double tolerance)
    {
        int count = edgeSource.Count;
        for (int i = 0; i < count; i++)
        {
            Point2D p1 = edgeSource[i];
            Point2D p2 = edgeSource[(i + 1) % count];
            double nx = -(p2.Y - p1.Y);
            double ny = p2.X - p1.X;
            double lengthSq = (nx * nx) + (ny * ny);
            if (lengthSq < 1e-12)
            {
                continue;
            }

            (double minA, double maxA) = ProjectPolygon(polyA, nx, ny);
            (double minB, double maxB) = ProjectPolygon(polyB, nx, ny);
            double scale = Math.Sqrt(lengthSq);

            if ((maxA - minB) <= tolerance * scale || (maxB - minA) <= tolerance * scale)
            {
                return true;
            }
        }

        return false;
    }

    private static (double Min, double Max) ProjectPolygon(IReadOnlyList<Point2D> poly, double nx, double ny)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int i = 0; i < poly.Count; i++)
        {
            double proj = (poly[i].X * nx) + (poly[i].Y * ny);
            if (proj < min) min = proj;
            if (proj > max) max = proj;
        }

        return (min, max);
    }
}
