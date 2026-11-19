using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.Personalization;

/// <summary>
/// Opções compartilhadas de serialização JSON para o domínio de personalização,
/// reproduzindo <c>ensure_ascii=False</c> do backend Python v1.2.0.
/// </summary>
public static class PersonalizationJsonOptions
{
    public static JsonSerializerOptions Default { get; } = Create(indented: false);
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);
    public static JsonSerializerOptions CmakerWithoutRawB64 { get; } = Create(indented: true, includeRawRasterB64: false);

    public static JsonSerializerOptions Create(bool indented = false, bool includeRawRasterB64 = true)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
        };
        if (!includeRawRasterB64)
        {
            opts.Converters.Add(new PersonalizationElementJsonConverter(includeRawRasterB64: false));
        }
        return opts;
    }
}

/// <summary>
/// Parâmetros da caixa serializados dentro de <c>personalization.box_parameters</c> (v1.2.0).
/// Aceita na desserialização tanto as chaves <c>*_mm</c> quanto <c>box_*</c> por compatibilidade.
/// </summary>
public sealed class PersonalizationBoxParametersSnapshot
{
    [JsonPropertyName("width_mm")]
    public double? WidthMm { get; set; }

    [JsonPropertyName("depth_mm")]
    public double? DepthMm { get; set; }

    [JsonPropertyName("height_mm")]
    public double? HeightMm { get; set; }

    [JsonPropertyName("thickness_mm")]
    public double? ThicknessMm { get; set; }

    [JsonPropertyName("folga_mm")]
    public double? FolgaMm { get; set; }

    [JsonPropertyName("finger_joint_mode")]
    public string? FingerJointMode { get; set; }

    [JsonPropertyName("target_finger_width_mm")]
    public double? TargetFingerWidthMm { get; set; }

    [JsonPropertyName("fixed_finger_count")]
    public int? FixedFingerCount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static PersonalizationBoxParametersSnapshot FromCalculator(BoxCalculator calc)
    {
        ArgumentNullException.ThrowIfNull(calc);
        return new PersonalizationBoxParametersSnapshot
        {
            WidthMm = PythonMath.Round(calc.Width, 4),
            DepthMm = PythonMath.Round(calc.Depth, 4),
            HeightMm = PythonMath.Round(calc.Height, 4),
            ThicknessMm = PythonMath.Round(calc.Thickness, 4),
            FolgaMm = PythonMath.Round(calc.Folga, 4),
            FingerJointMode = CaixaMaker.Core.Projects.ProjectBoxParameters.FormatFingerJointMode(calc.FingerJointMode),
            TargetFingerWidthMm = PythonMath.Round(calc.TargetFingerWidthMm, 4),
            FixedFingerCount = calc.FixedFingerCount,
        };
    }

    public static PersonalizationBoxParametersSnapshot FromBoxParameters(BoxParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new PersonalizationBoxParametersSnapshot
        {
            WidthMm = PythonMath.Round(parameters.BoxWidth, 4),
            DepthMm = PythonMath.Round(parameters.BoxDepth, 4),
            HeightMm = PythonMath.Round(parameters.BoxHeight, 4),
            ThicknessMm = PythonMath.Round(parameters.BoxThickness, 4),
            FolgaMm = PythonMath.Round(parameters.Folga, 4),
            FingerJointMode = CaixaMaker.Core.Projects.ProjectBoxParameters.FormatFingerJointMode(parameters.FingerJointMode),
            TargetFingerWidthMm = PythonMath.Round(parameters.TargetFingerWidthMm, 4),
            FixedFingerCount = parameters.FixedFingerCount,
        };
    }

    public BoxParameters ToBoxParameters()
    {
        return new BoxParameters
        {
            BoxThickness = ResolveDouble(ThicknessMm, "box_thickness", BoxParameters.DefaultThickness),
            BoxWidth = ResolveDouble(WidthMm, "box_width", BoxParameters.DefaultWidth),
            BoxDepth = ResolveDouble(DepthMm, "box_depth", BoxParameters.DefaultDepth),
            BoxHeight = ResolveDouble(HeightMm, "box_height", BoxParameters.DefaultHeight),
            Folga = ResolveDouble(FolgaMm, "folga", BoxParameters.DefaultFolga),
            FingerJointMode = CaixaMaker.Core.Projects.ProjectBoxParameters.ParseFingerJointMode(FingerJointMode),
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

    public BoxCalculator ToCalculator(IBoxModelDefinition? model = null)
    {
        BoxParameters p = ToBoxParameters();
        return model is not null
            ? model.CreateCalculator(p)
            : new BoxCalculator(p);
    }

    private double ResolveDouble(double? primary, string legacyKey, double fallback)
    {
        if (primary.HasValue && double.IsFinite(primary.Value))
        {
            return primary.Value;
        }

        if (ExtensionData is not null && ExtensionData.TryGetValue(legacyKey, out JsonElement raw))
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
/// Snapshot completo e serializável do estado de personalização (paridade com <c>PersonalizationManager.to_dict</c>).
/// </summary>
public sealed class PersonalizationSnapshot
{
    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = BoxWithLidAndHandlesModel.Id;

    [JsonPropertyName("box_parameters")]
    public PersonalizationBoxParametersSnapshot BoxParameters { get; set; } = new();

    [JsonPropertyName("safety_margin_mm")]
    public double SafetyMarginMm { get; set; } = GeometryValidator.DefaultSafetyMarginMm;

    [JsonPropertyName("surfaces")]
    public List<SurfaceDefinition> Surfaces { get; set; } = new();

    [JsonPropertyName("elements")]
    public List<PersonalizationElement> Elements { get; set; } = new();

    [JsonPropertyName("validation")]
    public ValidationReport? Validation { get; set; }

    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(
            this,
            indented ? PersonalizationJsonOptions.Indented : PersonalizationJsonOptions.Default);

    public static PersonalizationSnapshot FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PersonalizationSnapshot>(json, PersonalizationJsonOptions.Default)
            ?? throw new JsonException("Snapshot de personalização inválido ou nulo.");
    }
}

/// <summary>Entrada da pilha de histórico Undo/Redo da personalização.</summary>
public sealed record PersonalizationHistoryEntry(
    PersonalizationSnapshot Snapshot,
    SurfaceId ActiveSurfaceId = SurfaceId.Front,
    string? SelectedElementId = null,
    IReadOnlyList<string>? SelectedElementIds = null);

/// <summary>
/// Pilha limitada de Undo/Redo (máximo de 40 passos, conforme <c>MAX_HISTORY_STEPS</c> da v1.2.0).
/// </summary>
public sealed class PersonalizationHistory
{
    public const int DefaultMaxSteps = 40;

    private readonly List<PersonalizationHistoryEntry> _undoStack = new();
    private readonly List<PersonalizationHistoryEntry> _redoStack = new();

    public int MaxSteps { get; }
    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public PersonalizationHistory(int maxSteps = DefaultMaxSteps)
    {
        if (maxSteps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSteps), "O limite de histórico deve ser positivo.");
        }

        MaxSteps = maxSteps;
    }

    public void Record(PersonalizationHistoryEntry stateBeforeChange)
    {
        ArgumentNullException.ThrowIfNull(stateBeforeChange);
        _redoStack.Clear();
        PushBounded(_undoStack, stateBeforeChange);
    }

    public PersonalizationHistoryEntry Undo(PersonalizationHistoryEntry currentState)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        if (_undoStack.Count == 0)
        {
            throw new InvalidOperationException("Não há estados anteriores na pilha de Undo.");
        }

        PersonalizationHistoryEntry previous = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        PushBounded(_redoStack, currentState);
        return previous;
    }

    public PersonalizationHistoryEntry Redo(PersonalizationHistoryEntry currentState)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        if (_redoStack.Count == 0)
        {
            throw new InvalidOperationException("Não há estados futuros na pilha de Redo.");
        }

        PersonalizationHistoryEntry next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        PushBounded(_undoStack, currentState);
        return next;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private void PushBounded(List<PersonalizationHistoryEntry> stack, PersonalizationHistoryEntry entry)
    {
        stack.Add(entry);
        if (stack.Count > MaxSteps)
        {
            stack.RemoveAt(0);
        }
    }
}

/// <summary>
/// Orquestra elementos gráficos nas superfícies da caixa, modos de ancoragem ao redimensionar,
/// cache de raster bruto e snapshots/restauração atômica.
/// Porta integralmente a camada de domínio de <c>personalization/manager.py</c>.
/// </summary>
public sealed class SurfaceManager
{
    private readonly List<string> _elementOrder = new();
    private readonly Dictionary<string, PersonalizationElement> _elements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rawRasterCache = new(StringComparer.Ordinal);
    private double _safetyMarginMm;

    public BoxCalculator Calc { get; private set; }
    public BoxDrawer Drawer { get; private set; }
    public IBoxModelDefinition BoxModel { get; private set; }
    public SurfaceMapper Surfaces { get; private set; }
    public GeometryValidator Validator { get; private set; }

    public double SafetyMarginMm
    {
        get => _safetyMarginMm;
        set
        {
            _safetyMarginMm = Math.Max(0.0, value);
            Validator = new GeometryValidator(_safetyMarginMm);
        }
    }

    public SurfaceManager(
        BoxCalculator calculator,
        IBoxModelDefinition? boxModel = null,
        double safetyMarginMm = GeometryValidator.DefaultSafetyMarginMm,
        BoxDrawer? drawer = null)
    {
        Calc = calculator ?? throw new ArgumentNullException(nameof(calculator));
        BoxModel = boxModel ?? BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        Drawer = drawer ?? BoxModel.CreateDrawer(Calc);
        _safetyMarginMm = Math.Max(0.0, safetyMarginMm);
        Surfaces = new SurfaceMapper(Calc, Drawer, BoxModel);
        Validator = new GeometryValidator(_safetyMarginMm);
    }

    public static SurfaceManager FromParameters(
        BoxParameters parameters,
        string? modelId = null,
        double safetyMarginMm = GeometryValidator.DefaultSafetyMarginMm,
        BoxModelRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        BoxModelRegistry activeRegistry = registry ?? BoxModelRegistry.Default;
        IBoxModelDefinition model = activeRegistry.Get(
            string.IsNullOrWhiteSpace(modelId) ? BoxWithLidAndHandlesModel.Id : modelId);
        BoxCalculator calc = model.CreateCalculator(parameters);
        return new SurfaceManager(calc, model, safetyMarginMm);
    }

    public IReadOnlyList<SurfaceDefinition> ListSurfaces() => Surfaces.ListSurfaces();

    public int ElementCount => _elements.Count;

    public PersonalizationElement GetElement(string elementId)
    {
        if (string.IsNullOrWhiteSpace(elementId) || !_elements.TryGetValue(elementId, out PersonalizationElement? el))
        {
            throw new KeyNotFoundException($"Elemento de personalização '{elementId}' não encontrado.");
        }

        return el;
    }

    public bool TryGetElement(string elementId, out PersonalizationElement? element)
    {
        if (string.IsNullOrWhiteSpace(elementId))
        {
            element = null;
            return false;
        }

        return _elements.TryGetValue(elementId, out element);
    }

    public IReadOnlyList<PersonalizationElement> ListElements(SurfaceId? panelId = null)
    {
        var result = new List<PersonalizationElement>(_elementOrder.Count);
        foreach (string id in _elementOrder)
        {
            PersonalizationElement el = _elements[id];
            if (!panelId.HasValue || el.PanelId == panelId.Value)
            {
                result.Add(el);
            }
        }

        return result;
    }

    /// <summary>
    /// Adiciona e posiciona um elemento gráfico em uma superfície válida da caixa.
    /// </summary>
    public PersonalizationElement AddElement(
        PersonalizationElement element,
        double? xMm = null,
        double? yMm = null,
        bool centerOnSurface = true)
    {
        ArgumentNullException.ThrowIfNull(element);

        // Garante que a superfície pertence ao modelo atual.
        _ = Surfaces.GetSurface(element.PanelId);

        if (string.IsNullOrWhiteSpace(element.Id))
        {
            element.Id = PersonalizationElement.GenerateNewId();
        }

        PlaceElement(element, xMm, yMm, centerOnSurface);

        if (element is RasterImagePersonalizationElement raster && !string.IsNullOrEmpty(raster.RawImageB64))
        {
            _rawRasterCache[raster.Id] = raster.RawImageB64;
        }

        if (!_elements.ContainsKey(element.Id))
        {
            _elementOrder.Add(element.Id);
        }

        _elements[element.Id] = element;
        return element;
    }

    /// <summary>
    /// Posiciona um elemento em uma face: centralizado na área segura quando solicitado
    /// ou usando coordenadas explícitas parciais/completas, sincronizando <see cref="PersonalizationElement.RelX"/>/<see cref="PersonalizationElement.RelY"/>.
    /// </summary>
    public void PlaceElement(
        PersonalizationElement element,
        double? xMm = null,
        double? yMm = null,
        bool centerOnSurface = true)
    {
        ArgumentNullException.ThrowIfNull(element);
        SurfaceDefinition surface = Surfaces.GetSurface(element.PanelId);
        Rect2D safe = surface.SafeRectMm;
        Point2D safeCenter = safe.Center;

        if (centerOnSurface || (!xMm.HasValue && !yMm.HasValue))
        {
            element.XMm = PythonMath.Round(safeCenter.X - element.WidthMm * 0.5, 4);
            element.YMm = PythonMath.Round(safeCenter.Y - element.HeightMm * 0.5, 4);
        }
        else
        {
            element.XMm = xMm.HasValue
                ? xMm.Value
                : PythonMath.Round(safeCenter.X - element.WidthMm * 0.5, 4);
            element.YMm = yMm.HasValue
                ? yMm.Value
                : PythonMath.Round(safeCenter.Y - element.HeightMm * 0.5, 4);
        }

        SyncRelativeCoords(element, surface);
    }

    /// <summary>
    /// Sincroniza as coordenadas normalizadas do centro (<c>rel_x</c>, <c>rel_y</c>)
    /// relativas às dimensões totais da superfície.
    /// </summary>
    public void SyncRelativeCoords(PersonalizationElement element, SurfaceDefinition? surface = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        SurfaceDefinition targetSurface = surface ?? Surfaces.GetSurface(element.PanelId);
        Point2D center = element.CenterMm();
        element.RelX = center.X / Math.Max(1e-6, targetSurface.WidthMm);
        element.RelY = center.Y / Math.Max(1e-6, targetSurface.HeightMm);
    }

    public PersonalizationElement UpdateElement(string elementId, Action<PersonalizationElement> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        PersonalizationElement element = GetElement(elementId);
        updater(element);
        SurfaceDefinition surface = Surfaces.GetSurface(element.PanelId);
        SyncRelativeCoords(element, surface);

        if (element is RasterImagePersonalizationElement raster && !string.IsNullOrEmpty(raster.RawImageB64))
        {
            _rawRasterCache[raster.Id] = raster.RawImageB64;
        }

        return element;
    }

    public PersonalizationElement MoveElement(string elementId, double xMm, double yMm)
    {
        PersonalizationElement element = GetElement(elementId);
        element.MoveTo(xMm, yMm);
        SyncRelativeCoords(element);
        return element;
    }

    public PersonalizationElement ResizeElement(
        string elementId,
        double? widthMm = null,
        double? heightMm = null,
        bool? preserveAspectRatio = null)
    {
        PersonalizationElement element = GetElement(elementId);
        element.Resize(widthMm, heightMm, preserveAspectRatio);
        SyncRelativeCoords(element);
        return element;
    }

    public PersonalizationElement RotateElement(string elementId, double rotationDeg)
    {
        PersonalizationElement element = GetElement(elementId);
        element.Rotate(rotationDeg);
        return element;
    }

    public PersonalizationElement RepositionElement(
        string elementId,
        SurfaceId targetPanelId,
        double? xMm = null,
        double? yMm = null,
        bool centerOnSurface = true)
    {
        PersonalizationElement element = GetElement(elementId);
        SurfaceDefinition targetSurface = Surfaces.GetSurface(targetPanelId);
        element.RepositionSurface(targetPanelId);
        PlaceElement(element, xMm, yMm, centerOnSurface);
        SyncRelativeCoords(element, targetSurface);
        return element;
    }

    public PersonalizationElement DuplicateElement(
        string elementId,
        SurfaceId? targetPanelId = null,
        double offsetMm = 5.0)
    {
        PersonalizationElement original = GetElement(elementId);
        SurfaceId destinationPanel = targetPanelId ?? original.PanelId;
        SurfaceDefinition destinationSurface = Surfaces.GetSurface(destinationPanel);

        PersonalizationElement clone = original.Duplicate();
        if (clone is RasterImagePersonalizationElement rasterClone)
        {
            if (string.IsNullOrEmpty(rasterClone.RawImageB64) &&
                _rawRasterCache.TryGetValue(original.Id, out string? cachedRaw))
            {
                rasterClone.RawImageB64 = cachedRaw;
            }

            if (!string.IsNullOrEmpty(rasterClone.RawImageB64))
            {
                _rawRasterCache[rasterClone.Id] = rasterClone.RawImageB64;
            }
        }

        if (destinationPanel != original.PanelId)
        {
            clone.PanelId = destinationPanel;
            PlaceElement(clone, centerOnSurface: true);
        }
        else
        {
            clone.MoveTo(original.XMm + offsetMm, original.YMm + offsetMm);
            SyncRelativeCoords(clone, destinationSurface);
        }

        _elementOrder.Add(clone.Id);
        _elements[clone.Id] = clone;
        return clone;
    }

    public bool RemoveElement(string elementId)
    {
        if (string.IsNullOrWhiteSpace(elementId) || !_elements.Remove(elementId))
        {
            return false;
        }

        _elementOrder.Remove(elementId);
        // Mantém _rawRasterCache em memória para permitir restauração via Undo/Redo.
        return true;
    }

    public void ClearElements(bool clearRawRasterCache = false)
    {
        _elementOrder.Clear();
        _elements.Clear();
        if (clearRawRasterCache)
        {
            _rawRasterCache.Clear();
        }
    }

    public bool TryGetCachedRawRaster(string elementId, out string? rawImageB64)
        => _rawRasterCache.TryGetValue(elementId, out rawImageB64);

    /// <summary>
    /// Atualiza dimensões/modelo da caixa e reposiciona elementos conforme os 7 modos de <see cref="AnchorMode"/>
    /// com paridade exata ao método <c>update_box_calculator</c> de <c>personalization/manager.py</c>.
    /// </summary>
    public void UpdateBoxCalculator(
        BoxCalculator newCalc,
        IBoxModelDefinition? boxModel = null,
        double? safetyMarginMm = null,
        BoxDrawer? drawer = null,
        bool dropUnsupportedSurfaceElements = false)
    {
        ArgumentNullException.ThrowIfNull(newCalc);

        IBoxModelDefinition effectiveModel = boxModel ?? BoxModel;
        BoxDrawer newDrawer = drawer ?? effectiveModel.CreateDrawer(newCalc);
        SurfaceMapper oldSurfaces = Surfaces;
        SurfaceMapper newSurfaces = new(newCalc, newDrawer, effectiveModel);

        var unsupportedIds = new List<string>();
        foreach (string id in _elementOrder)
        {
            PersonalizationElement el = _elements[id];
            if (!newSurfaces.TryGetSurface(el.PanelId, out _))
            {
                if (!dropUnsupportedSurfaceElements)
                {
                    throw new KeyNotFoundException(
                        $"A superfície '{SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(el.PanelId)}' do elemento '{el.Id}' não existe no modelo '{effectiveModel.ModelId}'.");
                }

                unsupportedIds.Add(id);
            }
        }

        foreach (string id in unsupportedIds)
        {
            _elements.Remove(id);
            _elementOrder.Remove(id);
        }

        foreach (string id in _elementOrder)
        {
            PersonalizationElement el = _elements[id];
            SurfaceDefinition oldSurf = oldSurfaces.GetSurface(el.PanelId);
            SurfaceDefinition newSurf = newSurfaces.GetSurface(el.PanelId);
            Rect2D oldSafe = oldSurf.SafeRectMm;
            Rect2D newSafe = newSurf.SafeRectMm;

            switch (el.Anchor)
            {
                case AnchorMode.Center:
                {
                    Point2D newCenter = newSafe.Center;
                    el.MoveTo(
                        PythonMath.Round(newCenter.X - el.WidthMm * 0.5, 4),
                        PythonMath.Round(newCenter.Y - el.HeightMm * 0.5, 4));
                    break;
                }
                case AnchorMode.Relative:
                {
                    double targetCx = el.RelX * newSurf.WidthMm;
                    double targetCy = el.RelY * newSurf.HeightMm;
                    el.MoveTo(
                        PythonMath.Round(targetCx - el.WidthMm * 0.5, 4),
                        PythonMath.Round(targetCy - el.HeightMm * 0.5, 4));
                    break;
                }
                case AnchorMode.TopLeft:
                {
                    double dx = el.XMm - oldSafe.XMm;
                    double dy = el.YMm - oldSafe.YMm;
                    el.MoveTo(
                        PythonMath.Round(newSafe.XMm + dx, 4),
                        PythonMath.Round(newSafe.YMm + dy, 4));
                    break;
                }
                case AnchorMode.TopRight:
                {
                    double dxRight = oldSafe.Right - (el.XMm + el.WidthMm);
                    double dy = el.YMm - oldSafe.YMm;
                    el.MoveTo(
                        PythonMath.Round(newSafe.Right - dxRight - el.WidthMm, 4),
                        PythonMath.Round(newSafe.YMm + dy, 4));
                    break;
                }
                case AnchorMode.BottomLeft:
                {
                    double dx = el.XMm - oldSafe.XMm;
                    double dyBottom = oldSafe.Bottom - (el.YMm + el.HeightMm);
                    el.MoveTo(
                        PythonMath.Round(newSafe.XMm + dx, 4),
                        PythonMath.Round(newSafe.Bottom - dyBottom - el.HeightMm, 4));
                    break;
                }
                case AnchorMode.BottomRight:
                {
                    double dxRight = oldSafe.Right - (el.XMm + el.WidthMm);
                    double dyBottom = oldSafe.Bottom - (el.YMm + el.HeightMm);
                    el.MoveTo(
                        PythonMath.Round(newSafe.Right - dxRight - el.WidthMm, 4),
                        PythonMath.Round(newSafe.Bottom - dyBottom - el.HeightMm, 4));
                    break;
                }
                case AnchorMode.Absolute:
                default:
                    break;
            }

            if (el.Anchor != AnchorMode.Relative)
            {
                SyncRelativeCoords(el, newSurf);
            }
        }

        Calc = newCalc;
        Drawer = newDrawer;
        BoxModel = effectiveModel;
        Surfaces = newSurfaces;
        if (safetyMarginMm.HasValue)
        {
            SafetyMarginMm = safetyMarginMm.Value;
        }
    }

    public ValidationReport Validate(double? safetyMarginMm = null, bool strictZones = true)
        => Validator.Validate(
            Surfaces,
            ListElements(),
            safetyMarginMm ?? _safetyMarginMm,
            strictZones);

    /// <summary>Executa validação SAT apenas dos elementos pertencentes à face informada (sem alocar clone de toda a cena).</summary>
    public ValidationReport ValidatePanel(SurfaceId panelId, double? safetyMarginMm = null, bool strictZones = true)
    {
        var panelElements = new List<PersonalizationElement>();
        foreach (string id in _elementOrder)
        {
            PersonalizationElement el = _elements[id];
            if (el.PanelId == panelId)
            {
                panelElements.Add(el);
            }
        }

        return Validator.ValidatePanel(
            Surfaces,
            panelId,
            panelElements,
            safetyMarginMm ?? _safetyMarginMm,
            strictZones);
    }

    /// <summary>
    /// Gera um snapshot profundo e independente do estado atual para persistência ou Undo/Redo.
    /// </summary>
    public PersonalizationSnapshot CreateSnapshot(bool includeRawBase64 = false)
    {
        var clonedElements = new List<PersonalizationElement>(_elementOrder.Count);
        foreach (string id in _elementOrder)
        {
            PersonalizationElement copy = _elements[id].DeepClone();
            if (copy is RasterImagePersonalizationElement rasterCopy)
            {
                if (includeRawBase64)
                {
                    if (string.IsNullOrEmpty(rasterCopy.RawImageB64) &&
                        _rawRasterCache.TryGetValue(rasterCopy.Id, out string? cachedRaw))
                    {
                        rasterCopy.RawImageB64 = cachedRaw;
                    }
                }
                else
                {
                    rasterCopy.RawImageB64 = string.Empty;
                }
            }

            clonedElements.Add(copy);
        }

        return new PersonalizationSnapshot
        {
            ModelId = BoxModel.ModelId,
            BoxParameters = PersonalizationBoxParametersSnapshot.FromCalculator(Calc),
            SafetyMarginMm = _safetyMarginMm,
            Surfaces = Surfaces.ListSurfaces().ToList(),
            Elements = clonedElements,
            Validation = Validate(),
        };
    }

    public string ToJson(bool indented = true, bool includeRawBase64 = false)
        => CreateSnapshot(includeRawBase64).ToJson(indented);

    /// <summary>
    /// Restaura atomicamente dimensões, modelo, margem e elementos a partir de um <see cref="PersonalizationSnapshot"/>.
    /// Em caso de falha na reconstrução dos dados do snapshot, mantém o estado anterior intacto.
    /// </summary>
    public void RestoreSnapshot(
        PersonalizationSnapshot snapshot,
        BoxModelRegistry? registry = null,
        IBoxModelDefinition? explicitBoxModel = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BoxModelRegistry activeRegistry = registry ?? BoxModelRegistry.Default;
        IBoxModelDefinition targetModel = explicitBoxModel ?? activeRegistry.Get(
            string.IsNullOrWhiteSpace(snapshot.ModelId) ? BoxModel.ModelId : snapshot.ModelId);

        BoxParameters targetParams = snapshot.BoxParameters.ToBoxParameters();
        BoxCalculator targetCalc = targetModel.CreateCalculator(targetParams);
        BoxDrawer targetDrawer = targetModel.CreateDrawer(targetCalc);
        SurfaceMapper targetSurfaces = new(targetCalc, targetDrawer, targetModel);
        double targetMargin = Math.Max(0.0, snapshot.SafetyMarginMm);

        var restoredOrder = new List<string>(snapshot.Elements.Count);
        var restoredMap = new Dictionary<string, PersonalizationElement>(StringComparer.Ordinal);
        var newRawEntries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (PersonalizationElement source in snapshot.Elements)
        {
            PersonalizationElement el = source.DeepClone();
            SurfaceDefinition surface = targetSurfaces.GetSurface(el.PanelId);

            if (el is RasterImagePersonalizationElement raster)
            {
                if (!string.IsNullOrEmpty(raster.RawImageB64))
                {
                    newRawEntries[raster.Id] = raster.RawImageB64;
                }
                else if (_rawRasterCache.TryGetValue(raster.Id, out string? cachedRaw))
                {
                    raster.RawImageB64 = cachedRaw;
                }
                else if (!string.IsNullOrEmpty(raster.ProcessedImageB64))
                {
                    raster.RawImageB64 = raster.ProcessedImageB64;
                }
            }

            if (el.Anchor != AnchorMode.Relative && el.RelX == 0.5 && el.RelY == 0.5)
            {
                Point2D center = el.CenterMm();
                el.RelX = center.X / Math.Max(1e-6, surface.WidthMm);
                el.RelY = center.Y / Math.Max(1e-6, surface.HeightMm);
            }

            if (!restoredMap.ContainsKey(el.Id))
            {
                restoredOrder.Add(el.Id);
            }

            restoredMap[el.Id] = el;
        }

        // Commit atômico somente após validar modelo, geometria e todos os elementos.
        Calc = targetCalc;
        Drawer = targetDrawer;
        BoxModel = targetModel;
        Surfaces = targetSurfaces;
        SafetyMarginMm = targetMargin;

        _elementOrder.Clear();
        _elementOrder.AddRange(restoredOrder);
        _elements.Clear();
        foreach (KeyValuePair<string, PersonalizationElement> kv in restoredMap)
        {
            _elements[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, string> kv in newRawEntries)
        {
            _rawRasterCache[kv.Key] = kv.Value;
        }
    }

    public static SurfaceManager FromSnapshot(
        PersonalizationSnapshot snapshot,
        BoxModelRegistry? registry = null,
        IBoxModelDefinition? explicitBoxModel = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BoxModelRegistry activeRegistry = registry ?? BoxModelRegistry.Default;
        IBoxModelDefinition targetModel = explicitBoxModel ?? activeRegistry.Get(
            string.IsNullOrWhiteSpace(snapshot.ModelId) ? BoxWithLidAndHandlesModel.Id : snapshot.ModelId);

        BoxCalculator calc = targetModel.CreateCalculator(snapshot.BoxParameters.ToBoxParameters());
        var manager = new SurfaceManager(calc, targetModel, snapshot.SafetyMarginMm);
        manager.RestoreSnapshot(snapshot, activeRegistry, targetModel);
        return manager;
    }

    public static SurfaceManager FromJson(
        string json,
        BoxModelRegistry? registry = null,
        IBoxModelDefinition? explicitBoxModel = null)
        => FromSnapshot(PersonalizationSnapshot.FromJson(json), registry, explicitBoxModel);
}
