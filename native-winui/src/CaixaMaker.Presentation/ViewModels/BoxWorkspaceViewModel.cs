using System.Collections.ObjectModel;
using System.Globalization;
using CaixaMaker.App.Models;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaixaMaker.App.ViewModels;

/// <summary>
/// ViewModel da área de Modelagem Paramétrica e estado do visualizador 3D.
/// Substitui <c>useBoxStore.ts</c> em arquitetura MVVM nativa.
/// </summary>
public sealed partial class BoxWorkspaceViewModel : ObservableObject
{
    private readonly BoxModelRegistry _registry;
    private bool _suppressRebuild;
    private int _interactiveUpdateDepth;
    private bool _hasDeferredInteractiveCommit;

    public bool IsInteractiveUpdate => _interactiveUpdateDepth > 0;

    public void BeginInteractiveUpdate() => _interactiveUpdateDepth++;

    public void EndInteractiveUpdate()
    {
        if (_interactiveUpdateDepth <= 0)
        {
            _interactiveUpdateDepth = 0;
            return;
        }

        _interactiveUpdateDepth--;
        if (_interactiveUpdateDepth == 0 && _hasDeferredInteractiveCommit)
        {
            _hasDeferredInteractiveCommit = false;
            ViewerStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<BoxModelCatalogItem> CatalogModels { get; }

    public IReadOnlyList<BoxSizePreset> SizePresets { get; } =
    [
        new("compacta", "Compacta", "150 × 120 × 80 mm (MDF 3 mm)", 150.0, 120.0, 80.0, 3.0),
        new("padrao", "Padrão", "200 × 150 × 100 mm (MDF 3 mm)", 200.0, 150.0, 100.0, 3.0),
        new("organizadora", "Organizadora", "280 × 200 × 130 mm (MDF 3 mm)", 280.0, 200.0, 130.0, 3.0),
        new("alta", "Alta / Ferramentas", "180 × 180 × 160 mm (MDF 4 mm)", 180.0, 180.0, 160.0, 4.0),
    ];

    public IReadOnlyList<double> QuickThicknesses { get; } = [2.5, 3.0, 4.0, 6.0];

    [ObservableProperty]
    private string _selectedModelId = BoxWithLidAndHandlesModel.Id;

    [ObservableProperty]
    private double _boxThickness = BoxParameters.DefaultThickness;

    [ObservableProperty]
    private double _boxWidth = BoxParameters.DefaultWidth;

    [ObservableProperty]
    private double _boxDepth = BoxParameters.DefaultDepth;

    [ObservableProperty]
    private double _boxHeight = BoxParameters.DefaultHeight;

    [ObservableProperty]
    private double _folga = BoxParameters.DefaultFolga;

    [ObservableProperty]
    private DimensionReferenceMode _dimensionMode = DimensionReferenceMode.External;

    [ObservableProperty]
    private bool _explodedView;

    [ObservableProperty]
    private double _lidAngle = 35.0;

    [ObservableProperty]
    private bool _showDimensions = true;

    [ObservableProperty]
    private CameraPreset _activeCameraPreset = CameraPreset.Isometric;

    [ObservableProperty]
    private double _cameraYawDeg = 38.0;

    [ObservableProperty]
    private double _cameraPitchDeg = 26.0;

    [ObservableProperty]
    private double _cameraZoom = 1.0;

    [ObservableProperty]
    private double _cameraPanX;

    [ObservableProperty]
    private double _cameraPanY;

    [ObservableProperty]
    private string? _selectedPartId;

    [ObservableProperty]
    private string? _validationError;

    [ObservableProperty]
    private bool _isParametersValid = true;

    [ObservableProperty]
    private BoxCalculationSummary? _summary;

    [ObservableProperty]
    private BoxSheetGeometry2D? _sheetGeometry;

    [ObservableProperty]
    private string _generatedBaseSvg = string.Empty;

    public ObservableCollection<PartSummary> Parts { get; } = new();
    public ObservableCollection<string> Advisories { get; } = new();

    public IBoxModelDefinition SelectedModel => _registry.Get(SelectedModelId);
    public BoxModelCatalogItem SelectedModelItem => SelectedModel.ToCatalogItem();
    public bool HasLid => SelectedModel.HasLid;
    public bool HasHandles => SelectedModel.HasHandles;
    public bool SupportsLidArticulation => SelectedModel.SupportsLidArticulation;
    public int TotalPartUnits => Parts.Sum(p => p.Quantity);
    public double EstimatedCuttingAreaCm2 => Summary?.EstimatedCuttingAreaCm2 ?? 0.0;
    public double TotalCutPerimeterMm => Summary?.TotalCutPerimeterMm ?? 0.0;
    public double TotalCutPerimeterMeters => Summary?.TotalCutPerimeterMeters ?? 0.0;
    public bool IsExternalDimensionMode => DimensionMode == DimensionReferenceMode.External;
    public bool IsInternalDimensionMode => DimensionMode == DimensionReferenceMode.InternalUsable;

    public double InputWidth
    {
        get => DimensionMode == DimensionReferenceMode.InternalUsable
            ? Math.Round(BoxWidth - 2.0 * BoxThickness, 4)
            : BoxWidth;
        set
        {
            double next = DimensionMode == DimensionReferenceMode.InternalUsable
                ? Math.Round(value + 2.0 * BoxThickness, 4)
                : value;
            if (Math.Abs(BoxWidth - next) > 1e-9)
            {
                BoxWidth = next;
            }
        }
    }

    public double InputDepth
    {
        get => DimensionMode == DimensionReferenceMode.InternalUsable
            ? Math.Round(BoxDepth - 2.0 * BoxThickness, 4)
            : BoxDepth;
        set
        {
            double next = DimensionMode == DimensionReferenceMode.InternalUsable
                ? Math.Round(value + 2.0 * BoxThickness, 4)
                : value;
            if (Math.Abs(BoxDepth - next) > 1e-9)
            {
                BoxDepth = next;
            }
        }
    }

    public double InputHeight
    {
        get => DimensionMode == DimensionReferenceMode.InternalUsable
            ? Math.Round(BoxHeight - BoxThickness, 4)
            : BoxHeight;
        set
        {
            double next = DimensionMode == DimensionReferenceMode.InternalUsable
                ? Math.Round(value + BoxThickness, 4)
                : value;
            if (Math.Abs(BoxHeight - next) > 1e-9)
            {
                BoxHeight = next;
            }
        }
    }

    public double InnerWidthMm => Summary?.ComputedDimensions.InnerWidth ?? Math.Max(0.0, BoxWidth - 2.0 * BoxThickness);
    public double InnerDepthMm => Summary?.ComputedDimensions.InnerDepth ?? Math.Max(0.0, BoxDepth - 2.0 * BoxThickness);
    public double InnerHeightMm => Summary?.ComputedDimensions.InnerHeight ?? Math.Max(0.0, BoxHeight - BoxThickness);

    public string DimensionsBadgeText => string.Format(
        CultureInfo.InvariantCulture,
        "{0:0}×{1:0}×{2:0} mm",
        Math.Round(BoxWidth),
        Math.Round(BoxDepth),
        Math.Round(BoxHeight));

    public string InnerDimensionsDisplay => string.Format(
        CultureInfo.InvariantCulture,
        "{0:F1} × {1:F1} × {2:F1} mm",
        InnerWidthMm,
        InnerDepthMm,
        InnerHeightMm);

    public string EstimatedAreaDisplay => string.Format(
        CultureInfo.InvariantCulture,
        "{0:F1} cm²",
        EstimatedCuttingAreaCm2);

    public string TotalCutPerimeterDisplay => Summary is null
        ? "—"
        : string.Format(
            CultureInfo.InvariantCulture,
            "{0:F2} m ({1:F0} mm)",
            TotalCutPerimeterMeters,
            TotalCutPerimeterMm);

    public string SvgCanvasSizeDisplay => Summary is null
        ? "—"
        : string.Format(
            CultureInfo.InvariantCulture,
            "{0:F1} × {1:F1} mm",
            Summary.SvgCanvas.WidthMm,
            Summary.SvgCanvas.HeightMm);

    public string StructuralModelSummaryDisplay => FormatModelSummary(SelectedModel);

    public static string FormatModelSummary(IBoxModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string lidSummary = model.SupportsLidArticulation
            ? "Tampa articulada e alças"
            : "Topo livre sem tampa";
        return $"{model.TotalPanels} chapas paramétricas • {lidSummary}";
    }

    public BoxCalculator? CurrentCalculator { get; private set; }
    public BoxDrawer? CurrentDrawer { get; private set; }

    public event EventHandler? GeometryRebuilt;
    public event EventHandler? ParameterEditedByUser;
    public event EventHandler? ViewerStateChanged;

    public BoxWorkspaceViewModel(BoxModelRegistry? registry = null)
    {
        _registry = registry ?? BoxModelRegistry.Default;
        CatalogModels = _registry.ListCatalogItems();
        RebuildGeometry(notifyEdit: false);
    }

    public BoxParameters CurrentParameters => new()
    {
        BoxThickness = BoxThickness,
        BoxWidth = BoxWidth,
        BoxDepth = BoxDepth,
        BoxHeight = BoxHeight,
        Folga = Folga,
    };

    public ProjectViewerState ToViewerState(string activeSurfaceApiName) => new()
    {
        Exploded = ExplodedView,
        LidAngle = Math.Round(LidAngle, 2),
        ShowDimensions = ShowDimensions,
        ActiveSurface = activeSurfaceApiName,
        DimensionMode = DimensionMode == DimensionReferenceMode.InternalUsable ? "internal_usable" : "external",
        CameraYawDeg = Math.Round(CameraYawDeg, 3),
        CameraPitchDeg = Math.Round(CameraPitchDeg, 3),
        CameraZoom = Math.Round(CameraZoom, 4),
        CameraPanX = Math.Round(CameraPanX, 3),
        CameraPanY = Math.Round(CameraPanY, 3),
    };

    public void LoadModel(string modelId, bool applyDefaults = true, bool markAsEdit = false)
    {
        if (!_registry.Contains(modelId))
        {
            throw new ArgumentException($"Modelo '{modelId}' não encontrado no catálogo.", nameof(modelId));
        }

        _suppressRebuild = true;
        try
        {
            SelectedModelId = modelId;
            if (applyDefaults)
            {
                IBoxModelDefinition def = _registry.Get(modelId);
                BoxThickness = def.DefaultParameters.GetValueOrDefault("box_thickness", BoxParameters.DefaultThickness);
                BoxWidth = def.DefaultParameters.GetValueOrDefault("box_width", BoxParameters.DefaultWidth);
                BoxDepth = def.DefaultParameters.GetValueOrDefault("box_depth", BoxParameters.DefaultDepth);
                BoxHeight = def.DefaultParameters.GetValueOrDefault("box_height", BoxParameters.DefaultHeight);
                Folga = def.DefaultParameters.GetValueOrDefault("folga", BoxParameters.DefaultFolga);
            }

            if (!SupportsLidArticulation)
            {
                LidAngle = 0.0;
            }
            else if (LidAngle <= 0.0)
            {
                LidAngle = 35.0;
            }

            SelectedPartId = null;
        }
        finally
        {
            _suppressRebuild = false;
        }

        NotifyModelMetadataChanged();
        RebuildGeometry(notifyEdit: markAsEdit);
    }

    public void ApplyParameters(BoxParameters parameters, bool markAsEdit = false)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _suppressRebuild = true;
        try
        {
            BoxThickness = parameters.BoxThickness;
            BoxWidth = parameters.BoxWidth;
            BoxDepth = parameters.BoxDepth;
            BoxHeight = parameters.BoxHeight;
            Folga = parameters.Folga;
        }
        finally
        {
            _suppressRebuild = false;
        }

        RebuildGeometry(notifyEdit: markAsEdit);
    }

    public void ApplySessionState(string modelId, BoxParameters parameters, ProjectViewerState viewerState)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(viewerState);

        _suppressRebuild = true;
        try
        {
            SelectedModelId = _registry.Contains(modelId) ? modelId : BoxWithLidAndHandlesModel.Id;
            BoxThickness = parameters.BoxThickness;
            BoxWidth = parameters.BoxWidth;
            BoxDepth = parameters.BoxDepth;
            BoxHeight = parameters.BoxHeight;
            Folga = parameters.Folga;
            ExplodedView = viewerState.Exploded;
            ShowDimensions = viewerState.ShowDimensions;
            LidAngle = SupportsLidArticulation
                ? Math.Clamp(double.IsFinite(viewerState.LidAngle) ? viewerState.LidAngle : 35.0, 0.0, 110.0)
                : 0.0;
            DimensionMode = string.Equals(viewerState.DimensionMode, "internal_usable", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(viewerState.DimensionMode, "InternalUsable", StringComparison.OrdinalIgnoreCase)
                ? DimensionReferenceMode.InternalUsable
                : DimensionReferenceMode.External;

            if (viewerState.CameraYawDeg is double yaw && double.IsFinite(yaw))
            {
                CameraYawDeg = yaw;
            }
            if (viewerState.CameraPitchDeg is double pitch && double.IsFinite(pitch))
            {
                CameraPitchDeg = Math.Clamp(pitch, -89.0, 89.0);
            }
            if (viewerState.CameraZoom is double zoom && double.IsFinite(zoom) && zoom > 0.0)
            {
                CameraZoom = Math.Clamp(zoom, 0.2, 6.0);
            }
            if (viewerState.CameraPanX is double panX && double.IsFinite(panX))
            {
                CameraPanX = panX;
            }
            if (viewerState.CameraPanY is double panY && double.IsFinite(panY))
            {
                CameraPanY = panY;
            }
            SelectedPartId = null;
        }
        finally
        {
            _suppressRebuild = false;
        }

        NotifyModelMetadataChanged();
        RebuildGeometry(notifyEdit: false);
        ViewerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ApplySizePreset(string presetId)
    {
        BoxSizePreset? preset = SizePresets.FirstOrDefault(
            p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            return;
        }

        _suppressRebuild = true;
        try
        {
            BoxWidth = preset.WidthMm;
            BoxDepth = preset.DepthMm;
            BoxHeight = preset.HeightMm;
            BoxThickness = preset.ThicknessMm;
        }
        finally
        {
            _suppressRebuild = false;
        }

        RebuildGeometry(notifyEdit: true);
    }

    public void ApplyCustomPresetDimensions(double widthMm, double depthMm, double heightMm)
    {
        _suppressRebuild = true;
        try
        {
            BoxWidth = Math.Clamp(widthMm, BoxParameters.MinWidth, BoxParameters.MaxWidth);
            BoxDepth = Math.Clamp(depthMm, BoxParameters.MinDepth, BoxParameters.MaxDepth);
            BoxHeight = Math.Clamp(heightMm, BoxParameters.MinHeight, BoxParameters.MaxHeight);
        }
        finally
        {
            _suppressRebuild = false;
        }

        RebuildGeometry(notifyEdit: true);
    }

    [RelayCommand]
    public void SetDimensionReferenceMode(DimensionReferenceMode mode)
        => DimensionMode = mode;

    /// <summary>Retorna o próximo eixo dimensional na sequência cíclica (<c>Largura → Profundidade → Altura</c>).</summary>
    public static BoxDimensionAxis GetNextDimensionAxis(BoxDimensionAxis current, bool reverse = false)
    {
        return (current, reverse) switch
        {
            (BoxDimensionAxis.WidthX, false) => BoxDimensionAxis.DepthZ,
            (BoxDimensionAxis.DepthZ, false) => BoxDimensionAxis.HeightY,
            (BoxDimensionAxis.HeightY, false) => BoxDimensionAxis.WidthX,
            (BoxDimensionAxis.WidthX, true) => BoxDimensionAxis.HeightY,
            (BoxDimensionAxis.DepthZ, true) => BoxDimensionAxis.WidthX,
            (BoxDimensionAxis.HeightY, true) => BoxDimensionAxis.DepthZ,
            _ => BoxDimensionAxis.WidthX,
        };
    }

    /// <summary>Título contextual da cota inline refletindo o eixo e o modo dimensional ativo.</summary>
    public string GetDimensionAxisEditHeader(BoxDimensionAxis axis)
    {
        string axisLabel = axis switch
        {
            BoxDimensionAxis.WidthX => "LARGURA (X)",
            BoxDimensionAxis.DepthZ => "PROFUNDIDADE (Z)",
            BoxDimensionAxis.HeightY => "ALTURA (Y)",
            _ => "DIMENSÃO",
        };
        string modeLabel = DimensionMode == DimensionReferenceMode.InternalUsable
            ? "INTERNA ÚTIL (MM)"
            : "EXTERNA (MM)";
        return $"{axisLabel} • {modeLabel}";
    }

    /// <summary>Formata o valor numérico inicial da cota no modo dimensional ativo para edição inline.</summary>
    public string FormatDimensionAxisEditValue(BoxDimensionAxis axis)
    {
        double value = axis switch
        {
            BoxDimensionAxis.WidthX => InputWidth,
            BoxDimensionAxis.DepthZ => InputDepth,
            BoxDimensionAxis.HeightY => InputHeight,
            _ => InputWidth,
        };
        return Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Avalia uma expressão matemática ou valor para o eixo informado respeitando o <see cref="DimensionMode"/> ativo
    /// e os limites mínimo/máximo paramétricos.
    /// </summary>
    public bool TryApplyDimensionAxisExpression(BoxDimensionAxis axis, string? expressionOrValue)
    {
        if (!CadMathExpressionEvaluator.TryEvaluate(expressionOrValue, out double rawInput) ||
            !double.IsFinite(rawInput) ||
            rawInput <= 0.0)
        {
            return false;
        }

        switch (axis)
        {
            case BoxDimensionAxis.WidthX:
            {
                double ext = DimensionMode == DimensionReferenceMode.InternalUsable
                    ? Math.Round(rawInput + 2.0 * BoxThickness, 4)
                    : Math.Round(rawInput, 4);
                double clamped = Math.Clamp(ext, BoxParameters.MinWidth, BoxParameters.MaxWidth);
                if (Math.Abs(BoxWidth - clamped) > 1e-9)
                {
                    BoxWidth = clamped;
                }
                return true;
            }
            case BoxDimensionAxis.DepthZ:
            {
                double ext = DimensionMode == DimensionReferenceMode.InternalUsable
                    ? Math.Round(rawInput + 2.0 * BoxThickness, 4)
                    : Math.Round(rawInput, 4);
                double clamped = Math.Clamp(ext, BoxParameters.MinDepth, BoxParameters.MaxDepth);
                if (Math.Abs(BoxDepth - clamped) > 1e-9)
                {
                    BoxDepth = clamped;
                }
                return true;
            }
            case BoxDimensionAxis.HeightY:
            {
                double ext = DimensionMode == DimensionReferenceMode.InternalUsable
                    ? Math.Round(rawInput + BoxThickness, 4)
                    : Math.Round(rawInput, 4);
                double clamped = Math.Clamp(ext, BoxParameters.MinHeight, BoxParameters.MaxHeight);
                if (Math.Abs(BoxHeight - clamped) > 1e-9)
                {
                    BoxHeight = clamped;
                }
                return true;
            }
            default:
                return false;
        }
    }

    public void SetInputDimensions(double widthMm, double depthMm, double heightMm)
    {
        (double extW, double extD, double extH) = DimensionMode == DimensionReferenceMode.InternalUsable
            ? DimensionReferenceConverter.ToExternal(widthMm, depthMm, heightMm, BoxThickness)
            : (widthMm, depthMm, heightMm);

        _suppressRebuild = true;
        try
        {
            BoxWidth = extW;
            BoxDepth = extD;
            BoxHeight = extH;
        }
        finally
        {
            _suppressRebuild = false;
        }

        RebuildGeometry(notifyEdit: true);
    }

    [RelayCommand]
    public void SetQuickThickness(double thicknessMm)
    {
        BoxThickness = Math.Clamp(thicknessMm, BoxParameters.MinThickness, BoxParameters.MaxThickness);
    }

    [RelayCommand]
    public void SetQuickFolga(double folgaMm)
    {
        Folga = Math.Clamp(folgaMm, BoxParameters.MinFolga, BoxParameters.MaxFolga);
    }

    [RelayCommand]
    public void ResetDefaultParameters()
    {
        IBoxModelDefinition def = SelectedModel;
        ApplyParameters(
            new BoxParameters
            {
                BoxThickness = def.DefaultParameters.GetValueOrDefault("box_thickness", BoxParameters.DefaultThickness),
                BoxWidth = def.DefaultParameters.GetValueOrDefault("box_width", BoxParameters.DefaultWidth),
                BoxDepth = def.DefaultParameters.GetValueOrDefault("box_depth", BoxParameters.DefaultDepth),
                BoxHeight = def.DefaultParameters.GetValueOrDefault("box_height", BoxParameters.DefaultHeight),
                Folga = def.DefaultParameters.GetValueOrDefault("folga", BoxParameters.DefaultFolga),
            },
            markAsEdit: true);
    }

    [RelayCommand]
    public void ToggleAlternativeModel()
    {
        string nextId = SelectedModelId == BoxWithLidAndHandlesModel.Id
            ? OpenBoxModel.Id
            : BoxWithLidAndHandlesModel.Id;
        LoadModel(nextId, applyDefaults: false, markAsEdit: true);
    }

    [RelayCommand]
    public void SetCameraPreset(CameraPreset preset)
    {
        ActiveCameraPreset = preset;
        CameraPanX = 0.0;
        CameraPanY = 0.0;
        (CameraYawDeg, CameraPitchDeg) = preset switch
        {
            CameraPreset.Front => (0.0, 6.0),
            CameraPreset.Back => (180.0, 6.0),
            CameraPreset.Left => (-90.0, 6.0),
            CameraPreset.Right => (90.0, 6.0),
            CameraPreset.Top => (0.0, 86.0),
            _ => (38.0, 26.0),
        };
        ViewerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ResetCamera()
    {
        CameraZoom = 1.0;
        CameraPanX = 0.0;
        CameraPanY = 0.0;
        SetCameraPreset(CameraPreset.Isometric);
    }

    [RelayCommand]
    public void ToggleExplodedView()
    {
        ExplodedView = !ExplodedView;
    }

    [RelayCommand]
    public void ToggleLidOpenClose()
    {
        if (!SupportsLidArticulation)
        {
            return;
        }

        LidAngle = LidAngle > 1.0 ? 0.0 : 95.0;
    }

    [RelayCommand]
    public void ToggleDimensions()
    {
        ShowDimensions = !ShowDimensions;
    }

    public void SelectPart(string? partId)
    {
        SelectedPartId = SelectedPartId == partId ? null : partId;
        ViewerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedModelIdChanged(string value)
    {
        if (_suppressRebuild) return;
        NotifyModelMetadataChanged();
        RebuildGeometry(notifyEdit: true);
    }

    partial void OnBoxThicknessChanged(double oldValue, double newValue)
    {
        if (_suppressRebuild) return;
        if (IsInteractiveUpdate)
        {
            _hasDeferredInteractiveCommit = true;
        }

        if (DimensionMode == DimensionReferenceMode.InternalUsable)
        {
            (double innerW, double innerD, double innerH) =
                DimensionReferenceConverter.ToInternalUsable(BoxWidth, BoxDepth, BoxHeight, oldValue);
            (double nextW, double nextD, double nextH) =
                DimensionReferenceConverter.ToExternal(innerW, innerD, innerH, newValue);

            _suppressRebuild = true;
            try
            {
                BoxWidth = nextW;
                BoxDepth = nextD;
                BoxHeight = nextH;
            }
            finally
            {
                _suppressRebuild = false;
            }
        }

        RebuildGeometry(notifyEdit: true);
    }

    partial void OnBoxWidthChanged(double value) => HandleParameterPropertyChanged();
    partial void OnBoxDepthChanged(double value) => HandleParameterPropertyChanged();
    partial void OnBoxHeightChanged(double value) => HandleParameterPropertyChanged();
    partial void OnFolgaChanged(double value) => HandleParameterPropertyChanged();

    partial void OnDimensionModeChanged(DimensionReferenceMode value)
    {
        OnPropertyChanged(nameof(IsExternalDimensionMode));
        OnPropertyChanged(nameof(IsInternalDimensionMode));
        OnPropertyChanged(nameof(InputWidth));
        OnPropertyChanged(nameof(InputDepth));
        OnPropertyChanged(nameof(InputHeight));
        ViewerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnExplodedViewChanged(bool value) => RaiseViewerStateChanged();
    partial void OnLidAngleChanged(double value) => RaiseViewerStateChanged();
    partial void OnShowDimensionsChanged(bool value) => RaiseViewerStateChanged();

    private void RaiseViewerStateChanged()
    {
        if (IsInteractiveUpdate)
        {
            _hasDeferredInteractiveCommit = true;
        }
        ViewerStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleParameterPropertyChanged()
    {
        if (_suppressRebuild) return;
        if (IsInteractiveUpdate)
        {
            _hasDeferredInteractiveCommit = true;
        }
        RebuildGeometry(notifyEdit: true);
    }

    private void NotifyModelMetadataChanged()
    {
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedModelItem));
        OnPropertyChanged(nameof(HasLid));
        OnPropertyChanged(nameof(HasHandles));
        OnPropertyChanged(nameof(SupportsLidArticulation));
    }

    private void RebuildGeometry(bool notifyEdit)
    {
        BoxParameters parameters = CurrentParameters;
        BoxValidationResult validation = BoxParametersValidator.Validate(parameters);

        Advisories.Clear();
        foreach (BoxValidationIssue advisory in BoxParametersValidator.GetRecommendedRangeAdvisories(parameters))
        {
            Advisories.Add(advisory.Message);
        }

        if (!validation.IsValid)
        {
            IsParametersValid = false;
            ValidationError = string.Join(" ", validation.Issues.Select(i => i.Message));
            OnPropertyChanged(nameof(InputWidth));
            OnPropertyChanged(nameof(InputDepth));
            OnPropertyChanged(nameof(InputHeight));
            OnPropertyChanged(nameof(DimensionsBadgeText));
            return;
        }

        IsParametersValid = true;
        ValidationError = null;

        IBoxModelDefinition model = SelectedModel;
        BoxCalculator calc = model.CreateCalculator(parameters);
        BoxDrawer drawer = model.CreateDrawer(calc);
        BoxCalculationSummary summary = model.BuildSummary(calc, drawer);
        BoxSheetGeometry2D sheetGeometry = model.BuildSheetGeometry(calc, drawer);
        string svg = model.DrawBoxSvg(calc, drawer);

        CurrentCalculator = calc;
        CurrentDrawer = drawer;
        Summary = summary;
        SheetGeometry = sheetGeometry;
        GeneratedBaseSvg = svg;

        Parts.Clear();
        foreach (PartSummary part in summary.Parts)
        {
            Parts.Add(part);
        }

        foreach (string warning in summary.Warnings)
        {
            if (!Advisories.Contains(warning))
            {
                Advisories.Add(warning);
            }
        }

        OnPropertyChanged(nameof(TotalPartUnits));
        OnPropertyChanged(nameof(EstimatedCuttingAreaCm2));
        OnPropertyChanged(nameof(TotalCutPerimeterMm));
        OnPropertyChanged(nameof(TotalCutPerimeterMeters));
        OnPropertyChanged(nameof(InputWidth));
        OnPropertyChanged(nameof(InputDepth));
        OnPropertyChanged(nameof(InputHeight));
        OnPropertyChanged(nameof(InnerWidthMm));
        OnPropertyChanged(nameof(InnerDepthMm));
        OnPropertyChanged(nameof(InnerHeightMm));
        OnPropertyChanged(nameof(DimensionsBadgeText));
        OnPropertyChanged(nameof(InnerDimensionsDisplay));
        OnPropertyChanged(nameof(EstimatedAreaDisplay));
        OnPropertyChanged(nameof(TotalCutPerimeterDisplay));
        OnPropertyChanged(nameof(SvgCanvasSizeDisplay));

        GeometryRebuilt?.Invoke(this, EventArgs.Empty);
        if (notifyEdit)
        {
            ParameterEditedByUser?.Invoke(this, EventArgs.Empty);
        }
    }
}
