using System.Collections.ObjectModel;
using System.Diagnostics;
using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.Cam.Raster;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Cam.Text;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaixaMaker.App.ViewModels;

/// <summary>
/// ViewModel da área de Personalização CNC (superfícies, Texto/SVG/Raster, 7 âncoras, 5 alinhamentos rápidos,
/// histórico Undo/Redo de 40 passos, Clipboard e validador SAT em tempo real).
/// </summary>
public sealed partial class PersonalizationWorkspaceViewModel : ObservableObject
{
    private readonly FontResolver _fontResolver;
    private readonly TextVectorizer _textVectorizer;
    private readonly SvgImporter _svgImporter;
    private readonly RasterProcessor _rasterProcessor;
    private readonly PersonalizationHistory _history = new(PersonalizationHistory.DefaultMaxSteps);
    private static readonly TimeSpan HistoryCoalesceWindow = TimeSpan.FromMilliseconds(1200);
    private bool _suppressSelectedSync;
    private string? _lastCoalesceKey;
    private DateTimeOffset _lastCoalesceUtc;

    private bool _isInteractiveTransformActive;
    private PersonalizationHistoryEntry? _interactiveCheckpoint;
    private (string ElementId, double X, double Y, double W, double H, double Rot)? _interactiveStartState;
    private List<(string ElementId, double X, double Y)>? _interactiveGroupStartStates;
    private string? _interactiveStartShapeSignature;
    private string? _interactiveDrawingElementId;
    private readonly List<string> _selectedElementIds = new();
    private bool _updatingSelectionList;
    private IReadOnlyList<GeometryValidationIssue>? _nonActivePanelErrors;
    private IReadOnlyList<GeometryValidationIssue>? _nonActivePanelWarnings;
    private long _lastInteractiveValidationTimestamp;
    private static readonly long InteractiveValidationMinIntervalTicks =
        (long)(Stopwatch.Frequency * 0.033);
    private IRelayCommand<GeometricShapeKind>? _addShapeCommand;
    private IRelayCommand<Canvas2DToolMode>? _setActiveToolModeCommand;
    private IRelayCommand? _mirrorFourCornersCommand;
    private IRelayCommand? _createLinearArrayCommand;

    public SurfaceManager Manager { get; private set; }
    public IReadOnlyList<string> AvailableFontFamilies { get; }

    public IReadOnlyList<CncOperationType> AvailableOperations { get; } =
    [
        CncOperationType.EngraveVector,
        CncOperationType.EngraveFill,
        CncOperationType.Mark,
        CncOperationType.EngraveRaster,
        CncOperationType.CutInternal,
    ];

    public IReadOnlyList<OperationMode> AvailableOperationModes { get; } =
    [
        OperationMode.Cut,
        OperationMode.Engrave,
        OperationMode.Both,
    ];

    public IReadOnlyList<AnchorMode> AvailableAnchorModes { get; } =
    [
        AnchorMode.Absolute,
        AnchorMode.Relative,
        AnchorMode.Center,
        AnchorMode.TopLeft,
        AnchorMode.TopRight,
        AnchorMode.BottomLeft,
        AnchorMode.BottomRight,
    ];

    public IReadOnlyList<TextRenderMode> AvailableTextRenderModes { get; } =
    [
        TextRenderMode.Outline,
        TextRenderMode.Fill,
        TextRenderMode.Both,
        TextRenderMode.Hatch,
    ];

    public IReadOnlyList<HorizontalAlign> AvailableHorizontalAligns { get; } =
    [
        HorizontalAlign.Left,
        HorizontalAlign.Center,
        HorizontalAlign.Right,
    ];

    public IReadOnlyList<AlignmentReferenceMode> AvailableAlignmentReferences { get; } =
    [
        AlignmentReferenceMode.SurfaceSafeArea,
        AlignmentReferenceMode.FirstSelected,
        AlignmentReferenceMode.LastSelected,
        AlignmentReferenceMode.SelectionBounds,
    ];

    public ObservableCollection<SurfaceDefinition> Surfaces { get; } = new();
    public ObservableCollection<PersonalizationElement> ElementsOnActiveSurface { get; } = new();
    public ObservableCollection<GeometryValidationIssue> ValidationIssues { get; } = new();

    [ObservableProperty]
    private SurfaceId _activeSurfaceId = SurfaceId.Front;

    [ObservableProperty]
    private string? _selectedElementId;

    [ObservableProperty]
    private PersonalizationElement? _clipboardElement;

    [ObservableProperty]
    private double _safetyMarginMm = GeometryValidator.DefaultSafetyMarginMm;

    [ObservableProperty]
    private double _defaultHatchSpacingMm = 0.25;

    [ObservableProperty]
    private ValidationReport? _validationReport;

    // Draft / criação rápida de texto
    [ObservableProperty]
    private string _draftTextContent = "IFMaker Juruá";

    [ObservableProperty]
    private string _draftFontFamily = "Arial";

    [ObservableProperty]
    private double _draftFontSizeMm = 14.0;

    [ObservableProperty]
    private bool _draftBold;

    [ObservableProperty]
    private bool _draftItalic;

    [ObservableProperty]
    private TextRenderMode _draftRenderMode = TextRenderMode.Outline;

    [ObservableProperty]
    private double _draftHatchSpacingMm = 0.25;

    // Campos de inspeção do elemento selecionado
    [ObservableProperty]
    private string _selectedName = string.Empty;

    [ObservableProperty]
    private double _selectedXMm;

    [ObservableProperty]
    private double _selectedYMm;

    [ObservableProperty]
    private double _selectedWidthMm = 10.0;

    [ObservableProperty]
    private double _selectedHeightMm = 10.0;

    [ObservableProperty]
    private double _selectedRotationDeg;

    [ObservableProperty]
    private bool _selectedPreserveAspectRatio = true;

    [ObservableProperty]
    private CncOperationType _selectedOperation = CncOperationType.EngraveVector;

    [ObservableProperty]
    private OperationMode _selectedOperationMode = OperationMode.Cut;

    [ObservableProperty]
    private AnchorMode _selectedAnchor = AnchorMode.Absolute;

    [ObservableProperty]
    private bool _selectedVisible = true;

    [ObservableProperty]
    private bool _selectedLocked;

    // Campos de texto quando elemento selecionado é TextPersonalizationElement
    [ObservableProperty]
    private string _selectedTextContent = string.Empty;

    [ObservableProperty]
    private string _selectedTextFontFamily = "Arial";

    [ObservableProperty]
    private double _selectedTextFontSizeMm = 12.0;

    [ObservableProperty]
    private bool _selectedTextBold;

    [ObservableProperty]
    private bool _selectedTextItalic;

    [ObservableProperty]
    private HorizontalAlign _selectedTextHorizontalAlign = HorizontalAlign.Left;

    [ObservableProperty]
    private double _selectedTextLetterSpacingMm;

    [ObservableProperty]
    private double _selectedTextLineSpacing = 1.2;

    [ObservableProperty]
    private TextRenderMode _selectedTextRenderMode = TextRenderMode.Outline;

    [ObservableProperty]
    private double _selectedTextHatchSpacingMm = 0.25;

    // Campos de raster quando elemento selecionado é RasterImagePersonalizationElement
    [ObservableProperty]
    private bool _selectedRasterGrayscale = true;

    [ObservableProperty]
    private double _selectedRasterBrightness = 1.0;

    [ObservableProperty]
    private double _selectedRasterContrast = 1.0;

    [ObservableProperty]
    private bool _selectedRasterInvert;

    [ObservableProperty]
    private bool _selectedRasterMonochrome;

    [ObservableProperty]
    private int _selectedRasterThreshold = 128;

    [ObservableProperty]
    private bool _selectedRasterDithering;

    [ObservableProperty]
    private bool _selectedRasterVectorize;

    [ObservableProperty]
    private double _selectedRasterVectorizeEpsilonPx = 1.5;

    // Campos de forma geométrica paramétrica (ShapePersonalizationElement)
    [ObservableProperty]
    private GeometricShapeKind _selectedShapeKind = GeometricShapeKind.Rectangle;

    [ObservableProperty]
    private double _selectedShapeWidthMm = 25.0;

    [ObservableProperty]
    private double _selectedShapeHeightMm = 15.0;

    [ObservableProperty]
    private double _selectedShapeDiameterMm = 10.0;

    [ObservableProperty]
    private double _selectedShapeCornerRadiusMm;

    [ObservableProperty]
    private int _selectedShapeSides = 6;

    [ObservableProperty]
    private bool _selectedShapeFillHatch;

    [ObservableProperty]
    private double _selectedShapeHatchSpacingMm = ShapeGeometryBuilder.DefaultHatchSpacingMm;

    // Ferramenta CAD 2D ativa e repetição/simetria (Stage 23)
    [ObservableProperty]
    private Canvas2DToolMode _activeToolMode = Canvas2DToolMode.Select;

    [ObservableProperty]
    private int _arrayCount = 4;

    [ObservableProperty]
    private double _arrayStepXMm;

    [ObservableProperty]
    private double _arrayStepYMm = 10.0;

    [ObservableProperty]
    private AlignmentReferenceMode _selectedAlignmentReference = AlignmentReferenceMode.SurfaceSafeArea;

    public bool IsSelectToolActive => ActiveToolMode == Canvas2DToolMode.Select;
    public bool IsDrawRectangleToolActive => ActiveToolMode == Canvas2DToolMode.DrawRectangle;
    public bool IsDrawCircleToolActive => ActiveToolMode == Canvas2DToolMode.DrawCircle;
    public bool IsDrawSlotToolActive => ActiveToolMode == Canvas2DToolMode.DrawSlot;
    public bool IsDrawPolygonToolActive => ActiveToolMode == Canvas2DToolMode.DrawPolygon;
    public bool IsDrawCrosshairToolActive => ActiveToolMode == Canvas2DToolMode.DrawCrosshair;

    public SurfaceDefinition? ActiveSurface =>
        Surfaces.FirstOrDefault(s => s.PanelId == ActiveSurfaceId);

    public PersonalizationElement? SelectedElement =>
        SelectedElementId is not null && Manager.TryGetElement(SelectedElementId, out PersonalizationElement? el)
            ? el
            : null;

    public IReadOnlyList<PersonalizationElement> AllElements => Manager.ListElements();
    public int TotalElementCount => Manager.ListElements().Count;
    public IReadOnlyList<string> SelectedElementIds => _selectedElementIds;
    public int SelectedElementCount => _selectedElementIds.Count;
    public bool HasMultipleSelection => _selectedElementIds.Count > 1;
    public string? FirstSelectedElementId => _selectedElementIds.Count > 0 ? _selectedElementIds[0] : null;
    public string? LastSelectedElementId => _selectedElementIds.Count > 0 ? _selectedElementIds[^1] : null;
    public bool HasSelection => SelectedElement is not null;
    public bool HasSelectedElement => HasSelection;
    public bool CanAlignSelectedElements => SelectedAlignmentReference == AlignmentReferenceMode.SurfaceSafeArea
        ? SelectedElementCount >= 1
        : SelectedElementCount >= 2;
    public bool CanDistributeSelectedElements => SelectedElementCount >= 3;
    public bool IsElementSelected(string? elementId) =>
        !string.IsNullOrEmpty(elementId) &&
        _selectedElementIds.Contains(elementId, StringComparer.Ordinal);
    public bool CanEditSelection => SelectedElement is { Locked: false };
    public bool IsSelectedText => SelectedElement is TextPersonalizationElement;
    public bool IsSelectedVectorSvg => SelectedElement is VectorSvgPersonalizationElement;
    public bool IsSelectedRaster => SelectedElement is RasterImagePersonalizationElement;
    public bool IsShapeSelected => SelectedElement is ShapePersonalizationElement;
    public bool IsSelectedShape => IsShapeSelected;
    public bool IsRectangleShapeSelected => SelectedElement is ShapePersonalizationElement { ShapeSpec.Kind: GeometricShapeKind.Rectangle };
    public bool IsCircleShapeSelected => SelectedElement is ShapePersonalizationElement { ShapeSpec.Kind: GeometricShapeKind.Circle };
    public bool IsSlotShapeSelected => SelectedElement is ShapePersonalizationElement { ShapeSpec.Kind: GeometricShapeKind.Slot };
    public bool IsPolygonShapeSelected => SelectedElement is ShapePersonalizationElement { ShapeSpec.Kind: GeometricShapeKind.Polygon };
    public bool IsCrosshairShapeSelected => SelectedElement is ShapePersonalizationElement { ShapeSpec.Kind: GeometricShapeKind.Crosshair };
    public bool HasClipboard => ClipboardElement is not null;
    public bool HasElements => TotalElementCount > 0;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public int UndoCount => _history.UndoCount;
    public int RedoCount => _history.RedoCount;
    public bool IsValidationValid => ValidationReport?.IsValid ?? true;
    public int ValidationErrorCount => ValidationReport?.ErrorCount ?? 0;
    public int ValidationWarningCount => ValidationReport?.WarningCount ?? 0;

    public string ValidationSummaryBadge => ValidationReport switch
    {
        null => "Pronto",
        { IsValid: true, WarningCount: 0 } => "Válido CNC",
        { IsValid: true, WarningCount: > 0 } => $"{ValidationReport.WarningCount} aviso(s)",
        _ => $"{ValidationReport.ErrorCount} erro(s)",
    };

    public bool IsInteractiveTransformActive => _isInteractiveTransformActive;

    public event EventHandler? StateMutatedByUser;
    public event EventHandler? VisualStateChanged;
    public event EventHandler? InteractiveTransformChanged;

    public PersonalizationWorkspaceViewModel(
        BoxCalculator initialCalc,
        IBoxModelDefinition initialModel,
        FontResolver? fontResolver = null)
    {
        ArgumentNullException.ThrowIfNull(initialCalc);
        ArgumentNullException.ThrowIfNull(initialModel);

        _fontResolver = fontResolver ?? FontResolver.Default;
        _textVectorizer = new TextVectorizer(_fontResolver);
        _svgImporter = new SvgImporter();
        _rasterProcessor = new RasterProcessor();

        IReadOnlyList<string> families = _fontResolver.ListFamilies();
        AvailableFontFamilies = families.Count > 0 ? families : ["Arial", "Segoe UI", "Times New Roman"];
        if (!AvailableFontFamilies.Contains(DraftFontFamily, StringComparer.OrdinalIgnoreCase))
        {
            DraftFontFamily = AvailableFontFamilies[0];
        }

        Manager = new SurfaceManager(initialCalc, boxModel: initialModel, safetyMarginMm: SafetyMarginMm);
        RefreshAllViewsAndValidation();
    }

    public void SyncWithBoxCalculator(
        BoxCalculator newCalc,
        IBoxModelDefinition boxModel,
        BoxDrawer? drawer = null,
        bool notifyUserMutation = false)
    {
        ArgumentNullException.ThrowIfNull(newCalc);
        ArgumentNullException.ThrowIfNull(boxModel);

        Manager.UpdateBoxCalculator(
            newCalc,
            boxModel: boxModel,
            safetyMarginMm: SafetyMarginMm,
            drawer: drawer,
            dropUnsupportedSurfaceElements: true);

        if (!Manager.Surfaces.TryGetSurface(ActiveSurfaceId, out _))
        {
            ActiveSurfaceId = SurfaceId.Front;
        }

        if (SelectedElementId is not null && !Manager.TryGetElement(SelectedElementId, out _))
        {
            SelectedElementId = null;
        }

        RefreshAllViewsAndValidation();
        if (notifyUserMutation)
        {
            StateMutatedByUser?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ReplaceManager(SurfaceManager newManager, SurfaceId? activeSurface = null)
    {
        Manager = newManager ?? throw new ArgumentNullException(nameof(newManager));
        SafetyMarginMm = newManager.SafetyMarginMm;
        _history.Clear();
        FlushHistoryCoalescing();
        ClipboardElement = null;
        SelectedElementId = null;

        ActiveSurfaceId = activeSurface.HasValue && Manager.Surfaces.TryGetSurface(activeSurface.Value, out _)
            ? activeSurface.Value
            : SurfaceId.Front;

        RefreshAllViewsAndValidation();
    }

    public void ResetForNewProject(BoxCalculator calc, IBoxModelDefinition model, BoxDrawer? drawer = null)
    {
        _history.Clear();
        FlushHistoryCoalescing();
        ClipboardElement = null;
        SelectedElementId = null;
        Manager = new SurfaceManager(calc, drawer: drawer, boxModel: model, safetyMarginMm: SafetyMarginMm);
        ActiveSurfaceId = SurfaceId.Front;
        RefreshAllViewsAndValidation();
    }

    [RelayCommand]
    public void SelectSurface(SurfaceId panelId)
    {
        if (!Manager.Surfaces.TryGetSurface(panelId, out _))
        {
            return;
        }

        bool changedSurface = ActiveSurfaceId != panelId;
        ActiveSurfaceId = panelId;
        if (changedSurface || (SelectedElement is not null && SelectedElement.PanelId != panelId))
        {
            ApplyOrderedSelectionInternal([]);
        }

        RefreshActiveSurfaceElements();
        SyncSelectedFieldsFromElement();
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void SelectElement(string? elementId) => SelectSingleElement(elementId);

    public void SelectSingleElement(string? elementId)
    {
        if (string.IsNullOrWhiteSpace(elementId))
        {
            if (_selectedElementIds.Count == 0 && SelectedElementId is null)
            {
                return;
            }
            ApplyOrderedSelectionInternal([]);
        }
        else if (Manager.TryGetElement(elementId, out PersonalizationElement? el) && el is not null)
        {
            if (_selectedElementIds.Count == 1 &&
                string.Equals(_selectedElementIds[0], el.Id, StringComparison.Ordinal) &&
                el.PanelId == ActiveSurfaceId)
            {
                return;
            }

            if (el.PanelId != ActiveSurfaceId)
            {
                ActiveSurfaceId = el.PanelId;
                RefreshActiveSurfaceElements();
            }
            ApplyOrderedSelectionInternal([el.Id]);
        }
        else
        {
            return;
        }

        SyncSelectedFieldsFromElement();
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleElementSelection(string elementId)
    {
        if (string.IsNullOrWhiteSpace(elementId) ||
            !Manager.TryGetElement(elementId, out PersonalizationElement? el) ||
            el is null)
        {
            return;
        }

        if (el.PanelId != ActiveSurfaceId)
        {
            ActiveSurfaceId = el.PanelId;
            RefreshActiveSurfaceElements();
            ApplyOrderedSelectionInternal([el.Id]);
            SyncSelectedFieldsFromElement();
            VisualStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var next = new List<string>(_selectedElementIds);
        int existingIndex = next.FindIndex(id => string.Equals(id, el.Id, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            next.RemoveAt(existingIndex);
        }
        else
        {
            next.Add(el.Id);
        }

        ApplyOrderedSelectionInternal(next);
        SyncSelectedFieldsFromElement();
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSelectedElements(IEnumerable<string> elementIds)
    {
        ArgumentNullException.ThrowIfNull(elementIds);

        var filtered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in elementIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            if (Manager.TryGetElement(id, out PersonalizationElement? el) &&
                el is not null &&
                el.PanelId == ActiveSurfaceId)
            {
                filtered.Add(el.Id);
            }
        }

        ApplyOrderedSelectionInternal(filtered);
        SyncSelectedFieldsFromElement();
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void SelectAllElementsOnActiveSurface()
    {
        if (ElementsOnActiveSurface.Count == 0 && Manager.ListElements(ActiveSurfaceId).Count == 0)
        {
            return;
        }

        IReadOnlyList<PersonalizationElement> onSurface = Manager.ListElements(ActiveSurfaceId);
        ApplyOrderedSelectionInternal(onSurface.Select(e => e.Id));
        SyncSelectedFieldsFromElement();
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedElementIdChanged(string? value)
    {
        if (_updatingSelectionList)
        {
            return;
        }

        _selectedElementIds.Clear();
        if (!string.IsNullOrWhiteSpace(value) &&
            Manager.TryGetElement(value, out PersonalizationElement? el) &&
            el is not null)
        {
            _selectedElementIds.Add(el.Id);
        }

        NotifySelectionCollectionProperties();
    }

    private void ApplyOrderedSelectionInternal(IEnumerable<string> orderedIds)
    {
        _selectedElementIds.Clear();
        foreach (string id in orderedIds)
        {
            if (!string.IsNullOrWhiteSpace(id) &&
                !_selectedElementIds.Contains(id, StringComparer.Ordinal))
            {
                _selectedElementIds.Add(id);
            }
        }

        string? primaryId = _selectedElementIds.Count > 0 ? _selectedElementIds[0] : null;
        _updatingSelectionList = true;
        try
        {
            SelectedElementId = primaryId;
        }
        finally
        {
            _updatingSelectionList = false;
        }

        NotifySelectionCollectionProperties();
    }

    private void NotifySelectionCollectionProperties()
    {
        OnPropertyChanged(nameof(SelectedElementIds));
        OnPropertyChanged(nameof(SelectedElementCount));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(FirstSelectedElementId));
        OnPropertyChanged(nameof(LastSelectedElementId));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSelectedElement));
        OnPropertyChanged(nameof(CanAlignSelectedElements));
        OnPropertyChanged(nameof(CanDistributeSelectedElements));
    }

    partial void OnSelectedAlignmentReferenceChanged(AlignmentReferenceMode value)
    {
        OnPropertyChanged(nameof(CanAlignSelectedElements));
        OnPropertyChanged(nameof(CanDistributeSelectedElements));
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void AddDefaultText() => AddTextElement();

    public TextPersonalizationElement AddTextElement(string? overrideContent = null)
    {
        string text = string.IsNullOrWhiteSpace(overrideContent)
            ? (string.IsNullOrWhiteSpace(DraftTextContent) ? "Texto CNC" : DraftTextContent)
            : overrideContent;

        RecordUndoPoint();

        var element = new TextPersonalizationElement
        {
            Name = text.Length <= 24 ? $"Texto: {text}" : $"Texto: {text[..24]}...",
            PanelId = ActiveSurfaceId,
            Content = text,
            FontFamily = DraftFontFamily,
            FontSizeMm = Math.Clamp(DraftFontSizeMm, 1.0, 400.0),
            Bold = DraftBold,
            Italic = DraftItalic,
            RenderMode = DraftRenderMode,
            HatchSpacingMm = Math.Clamp(DraftHatchSpacingMm, 0.05, 5.0),
        };

        _textVectorizer.VectorizeElement(element, updateDimensions: true);
        Manager.AddElement(element, centerOnSurface: true);

        SelectedElementId = element.Id;
        CommitMutation();
        return element;
    }

    public VectorSvgPersonalizationElement ImportSvgFromString(
        string svgXml,
        string? sourceFilename = null,
        CncOperationType? operation = null)
    {
        RecordUndoPoint();

        SvgImportResult imported = _svgImporter.ImportFromString(
            svgXml,
            sourceFilename: sourceFilename,
            panelId: ActiveSurfaceId);

        if (operation.HasValue)
        {
            imported.Element.Operation = operation.Value;
        }

        Manager.AddElement(imported.Element, centerOnSurface: true);
        SelectedElementId = imported.Element.Id;
        CommitMutation();
        return imported.Element;
    }

    public VectorSvgPersonalizationElement ImportSvgFromFile(
        string filePath,
        CncOperationType? operation = null)
    {
        RecordUndoPoint();

        SvgImportResult imported = _svgImporter.ImportFromFile(
            filePath,
            panelId: ActiveSurfaceId);

        if (operation.HasValue)
        {
            imported.Element.Operation = operation.Value;
        }

        Manager.AddElement(imported.Element, centerOnSurface: true);
        SelectedElementId = imported.Element.Id;
        CommitMutation();
        return imported.Element;
    }

    public RasterImagePersonalizationElement ImportRasterFromBytes(
        byte[] rawBytes,
        string? sourceFilename = null,
        bool vectorize = false,
        RasterProcessingOptions? options = null)
    {
        RecordUndoPoint();

        CncOperationType op = vectorize ? CncOperationType.EngraveVector : CncOperationType.EngraveRaster;
        RasterImagePersonalizationElement element = _rasterProcessor.ProcessFromBytes(
            rawBytes: rawBytes,
            sourceFilename: sourceFilename,
            panelId: ActiveSurfaceId,
            options: options,
            vectorize: vectorize,
            operation: op);

        Manager.AddElement(element, centerOnSurface: true);
        SelectedElementId = element.Id;
        CommitMutation();
        return element;
    }

    public RasterImagePersonalizationElement ImportRasterFromFile(
        string filePath,
        bool vectorize = false,
        RasterProcessingOptions? options = null)
    {
        RecordUndoPoint();

        CncOperationType op = vectorize ? CncOperationType.EngraveVector : CncOperationType.EngraveRaster;
        RasterImagePersonalizationElement element = _rasterProcessor.ProcessFromFile(
            filePath: filePath,
            panelId: ActiveSurfaceId,
            options: options,
            vectorize: vectorize,
            operation: op);

        Manager.AddElement(element, centerOnSurface: true);
        SelectedElementId = element.Id;
        CommitMutation();
        return element;
    }

    public IRelayCommand<GeometricShapeKind> AddShapeCommand =>
        _addShapeCommand ??= new RelayCommand<GeometricShapeKind>(kind => AddShape(kind));

    public IRelayCommand<Canvas2DToolMode> SetActiveToolModeCommand =>
        _setActiveToolModeCommand ??= new RelayCommand<Canvas2DToolMode>(SetActiveToolMode);

    public void SetActiveToolMode(Canvas2DToolMode mode)
    {
        if (ActiveToolMode != mode)
        {
            ActiveToolMode = mode;
        }
    }

    partial void OnActiveToolModeChanged(Canvas2DToolMode value)
    {
        OnPropertyChanged(nameof(IsSelectToolActive));
        OnPropertyChanged(nameof(IsDrawRectangleToolActive));
        OnPropertyChanged(nameof(IsDrawCircleToolActive));
        OnPropertyChanged(nameof(IsDrawSlotToolActive));
        OnPropertyChanged(nameof(IsDrawPolygonToolActive));
        OnPropertyChanged(nameof(IsDrawCrosshairToolActive));
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnArrayCountChanged(int value)
    {
        int clamped = Math.Clamp(value, 2, 20);
        if (clamped != value)
        {
            ArrayCount = clamped;
        }
    }

    [RelayCommand]
    public void AddRectangleShape() => AddShape(GeometricShapeKind.Rectangle);

    [RelayCommand]
    public void AddCircleShape() => AddShape(GeometricShapeKind.Circle);

    [RelayCommand]
    public void AddSlotShape() => AddShape(GeometricShapeKind.Slot);

    [RelayCommand]
    public void AddPolygonShape() => AddShape(GeometricShapeKind.Polygon);

    [RelayCommand]
    public void AddCrosshairShape() => AddShape(GeometricShapeKind.Crosshair);

    public ShapePersonalizationElement AddShape(
        GeometricShapeKind kind,
        GeometricShapeSpec? customSpec = null)
    {
        RecordUndoPoint();

        GeometricShapeSpec spec = customSpec is not null
            ? customSpec.Clone()
            : new GeometricShapeSpec
            {
                Kind = kind,
                WidthMm = 25.0,
                HeightMm = 15.0,
                DiameterMm = 10.0,
                CornerRadiusMm = 0.0,
                Sides = 6,
            };
        spec.Kind = kind;

        OperationMode defaultMode = ShapePersonalizationElement.DefaultOperationModeForKind(kind);
        CncOperationType defaultOp = ShapePersonalizationElement.DefaultCncOperationForKind(kind, defaultMode, spec.FillHatch);

        var element = new ShapePersonalizationElement(spec, defaultMode, defaultOp)
        {
            PanelId = ActiveSurfaceId,
            Anchor = AnchorMode.Center,
            Name = ShapePersonalizationElement.DefaultDisplayNameForSpec(spec),
        };

        Manager.AddElement(element, centerOnSurface: true);
        SelectedElementId = element.Id;
        CommitActivePanelMutation();
        return element;
    }

    /// <summary>
    /// Inicia uma transação de transformação interativa (arraste 2D ou slider), mantendo
    /// histórico atômico (1 passo no término), validação SAT restrita à face ativa e eventos leves (60+ FPS).
    /// </summary>
    public void BeginInteractiveTransform(PersonalizationHistoryEntry? preCapturedCheckpoint = null)
    {
        if (_isInteractiveTransformActive)
        {
            return;
        }

        _isInteractiveTransformActive = true;
        PersonalizationHistoryEntry checkpoint = preCapturedCheckpoint ?? CaptureHistoryCheckpoint();
        _interactiveCheckpoint = checkpoint;
        _interactiveDrawingElementId = null;

        PersonalizationElement? selected = SelectedElement;
        _interactiveStartState = selected is not null
            ? (selected.Id, selected.XMm, selected.YMm, selected.WidthMm, selected.HeightMm, selected.RotationDeg)
            : null;
        _interactiveStartShapeSignature = selected is ShapePersonalizationElement startShape
            ? startShape.ShapeSpec?.GetSignature()
            : null;
        _interactiveGroupStartStates = CaptureGroupStartStates();

        InitializeInteractiveBaseline(checkpoint);
        OnPropertyChanged(nameof(IsInteractiveTransformActive));
    }

    internal void BeginInteractiveShapeDraw(
        ShapePersonalizationElement tempShape,
        PersonalizationHistoryEntry preCapturedCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(tempShape);
        ArgumentNullException.ThrowIfNull(preCapturedCheckpoint);

        _isInteractiveTransformActive = true;
        _interactiveCheckpoint = preCapturedCheckpoint;
        _interactiveStartState = null;
        _interactiveStartShapeSignature = null;
        _interactiveDrawingElementId = tempShape.Id;

        InitializeInteractiveBaseline(preCapturedCheckpoint);

        RefreshActiveSurfaceElements();
        SelectedElementId = tempShape.Id;
        OnPropertyChanged(nameof(IsInteractiveTransformActive));
    }

    internal void NotifyInteractiveShapeDrawStep(ShapePersonalizationElement shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (_isInteractiveTransformActive)
        {
            CommitInteractiveTransformStep(shape);
        }
    }

    internal void SelectElementAfterInteractiveDraw(string elementId)
    {
        RefreshActiveSurfaceElements();
        SelectedElementId = elementId;
    }

    internal void CancelInteractiveShapeDraw(string? tempElementId, string? restoreSelectedElementId)
    {
        if (!string.IsNullOrEmpty(tempElementId) && Manager.TryGetElement(tempElementId, out _))
        {
            Manager.RemoveElement(tempElementId);
        }

        _isInteractiveTransformActive = false;
        _interactiveCheckpoint = null;
        _interactiveStartState = null;
        _interactiveStartShapeSignature = null;
        _interactiveDrawingElementId = null;
        _nonActivePanelErrors = null;
        _nonActivePanelWarnings = null;
        _lastInteractiveValidationTimestamp = 0;

        RefreshActiveSurfaceElements();
        SelectedElementId = restoreSelectedElementId is not null && Manager.TryGetElement(restoreSelectedElementId, out _)
            ? restoreSelectedElementId
            : null;
        OnPropertyChanged(nameof(IsInteractiveTransformActive));
        RunIncrementalPanelValidation(ActiveSurfaceId, notifyVisualState: true);
    }

    private void InitializeInteractiveBaseline(PersonalizationHistoryEntry checkpoint)
    {
        ValidationReport baseline = ValidationReport
            ?? checkpoint.Snapshot.Validation
            ?? Manager.Validate(SafetyMarginMm, strictZones: true);
        string activePanel = SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(ActiveSurfaceId);
        _nonActivePanelErrors = baseline.Errors
            .Where(i => !string.Equals(i.PanelId, activePanel, StringComparison.Ordinal))
            .ToArray();
        _nonActivePanelWarnings = baseline.Warnings
            .Where(i => !string.Equals(i.PanelId, activePanel, StringComparison.Ordinal))
            .ToArray();
        _lastInteractiveValidationTimestamp = 0;
    }

    /// <summary>
    /// Finaliza a transação de transformação interativa, registrando exatamente 1 entrada no histórico
    /// caso tenha havido alteração geométrica e disparando sincronização completa + salvamento em disco.
    /// </summary>
    public bool EndInteractiveTransform(bool recordUndoIfChanged = true)
    {
        if (!_isInteractiveTransformActive)
        {
            return false;
        }

        _isInteractiveTransformActive = false;
        PersonalizationHistoryEntry? checkpoint = _interactiveCheckpoint;
        var startState = _interactiveStartState;
        List<(string ElementId, double X, double Y)>? groupStates = _interactiveGroupStartStates;
        string? startShapeSig = _interactiveStartShapeSignature;
        string? drawnId = _interactiveDrawingElementId;
        _interactiveCheckpoint = null;
        _interactiveStartState = null;
        _interactiveGroupStartStates = null;
        _interactiveStartShapeSignature = null;
        _interactiveDrawingElementId = null;
        _nonActivePanelErrors = null;
        _nonActivePanelWarnings = null;
        _lastInteractiveValidationTimestamp = 0;

        bool changed = drawnId is not null && Manager.TryGetElement(drawnId, out _);
        if (!changed &&
            startState is { } start &&
            Manager.TryGetElement(start.ElementId, out PersonalizationElement? current) &&
            current is not null)
        {
            changed =
                Math.Abs(current.XMm - start.X) > 0.005 ||
                Math.Abs(current.YMm - start.Y) > 0.005 ||
                Math.Abs(current.WidthMm - start.W) > 0.005 ||
                Math.Abs(current.HeightMm - start.H) > 0.005 ||
                Math.Abs(current.RotationDeg - start.Rot) > 0.02 ||
                (current is ShapePersonalizationElement curShape &&
                 !string.Equals(curShape.ShapeSpec?.GetSignature(), startShapeSig, StringComparison.Ordinal));
        }

        if (!changed && groupStates is { Count: > 0 })
        {
            foreach ((string id, double sx, double sy) in groupStates)
            {
                if (Manager.TryGetElement(id, out PersonalizationElement? gel) &&
                    gel is not null &&
                    (Math.Abs(gel.XMm - sx) > 0.005 || Math.Abs(gel.YMm - sy) > 0.005))
                {
                    changed = true;
                    break;
                }
            }
        }

        OnPropertyChanged(nameof(IsInteractiveTransformActive));

        if (changed)
        {
            if (recordUndoIfChanged && checkpoint is not null)
            {
                RecordHistoryEntryInternal(checkpoint, notifyVisualState: false);
            }

            CommitActivePanelMutation();
            return true;
        }

        RunIncrementalPanelValidation(ActiveSurfaceId, notifyVisualState: true);
        return false;
    }

    private List<(string ElementId, double X, double Y)>? CaptureGroupStartStates()
    {
        if (_selectedElementIds.Count <= 1)
        {
            return null;
        }

        var list = new List<(string ElementId, double X, double Y)>(_selectedElementIds.Count);
        foreach (string id in _selectedElementIds)
        {
            if (Manager.TryGetElement(id, out PersonalizationElement? el) && el is not null)
            {
                list.Add((el.Id, el.XMm, el.YMm));
            }
        }
        return list;
    }

    /// <summary>
    /// Desloca todos os elementos selecionados (<see cref="SelectedElementIds"/>) pelo mesmo delta em mm.
    /// Quando invocada fora de uma transação interativa ativa, encapsula automaticamente em Begin/EndInteractiveTransform
    /// produzindo exatamente 1 passo Undo/Redo e 1 chamada incremental a ValidatePanel.
    /// </summary>
    public void TranslateSelectedElements(double deltaXMm, double deltaYMm)
    {
        if (_selectedElementIds.Count == 0)
        {
            return;
        }

        if (Math.Abs(deltaXMm) < 1e-6 && Math.Abs(deltaYMm) < 1e-6)
        {
            return;
        }

        bool ownTransaction = !_isInteractiveTransformActive;
        if (ownTransaction)
        {
            BeginInteractiveTransform();
        }

        PersonalizationElement? primaryMoved = null;
        foreach (string id in _selectedElementIds)
        {
            if (Manager.TryGetElement(id, out PersonalizationElement? el) && el is not null && !el.Locked)
            {
                Manager.MoveElement(
                    el.Id,
                    Math.Round(el.XMm + deltaXMm, 2),
                    Math.Round(el.YMm + deltaYMm, 2));
                primaryMoved ??= el;
            }
        }

        if (ownTransaction)
        {
            EndInteractiveTransform(recordUndoIfChanged: true);
        }
        else if (primaryMoved is not null)
        {
            CommitInteractiveTransformStep(primaryMoved);
        }
    }

    internal void ApplyGroupPositionsInteractive(IReadOnlyList<(string ElementId, double XMm, double YMm)> positions)
    {
        PersonalizationElement? firstEl = null;
        for (int i = 0; i < positions.Count; i++)
        {
            (string id, double xMm, double yMm) = positions[i];
            if (Manager.TryGetElement(id, out PersonalizationElement? el) && el is not null && !el.Locked)
            {
                Manager.MoveElement(id, xMm, yMm);
                firstEl ??= el;
            }
        }

        if (firstEl is not null)
        {
            CommitInteractiveTransformStep(firstEl);
        }
    }

    public void UpdateSelectedElementTransform(
        double? xMm = null,
        double? yMm = null,
        double? widthMm = null,
        double? heightMm = null,
        double? rotationDeg = null,
        bool? preserveAspectRatio = null,
        bool recordUndo = true,
        string? coalesceKey = null)
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null || selected.Locked)
        {
            return;
        }

        if (recordUndo && !_isInteractiveTransformActive)
        {
            RecordUndoPoint(coalesceKey);
        }

        if (xMm.HasValue || yMm.HasValue)
        {
            Manager.MoveElement(selected.Id, xMm ?? selected.XMm, yMm ?? selected.YMm);
        }

        if (preserveAspectRatio.HasValue)
        {
            selected.PreserveAspectRatio = preserveAspectRatio.Value;
        }

        if (widthMm.HasValue || heightMm.HasValue)
        {
            OffscreenViewportRenderer.InvalidateElementVectorCache(selected);
            Manager.ResizeElement(
                selected.Id,
                widthMm: widthMm.HasValue ? Math.Max(0.1, widthMm.Value) : null,
                heightMm: heightMm.HasValue ? Math.Max(0.1, heightMm.Value) : null,
                preserveAspectRatio: preserveAspectRatio);
        }

        if (rotationDeg.HasValue)
        {
            Manager.RotateElement(selected.Id, rotationDeg.Value);
        }

        if (_isInteractiveTransformActive)
        {
            CommitInteractiveTransformStep(selected);
            return;
        }

        CommitMutation();
    }

    [RelayCommand]
    public void ApplySelectedInspectorChanges()
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        RecordUndoPoint();

        if (!string.IsNullOrWhiteSpace(SelectedName))
        {
            selected.Name = SelectedName.Trim();
        }

        selected.Visible = SelectedVisible;
        selected.Locked = SelectedLocked;
        selected.Operation = SelectedOperation;
        selected.Anchor = SelectedAnchor;
        selected.PreserveAspectRatio = SelectedPreserveAspectRatio;

        if (!selected.Locked)
        {
            Manager.MoveElement(selected.Id, SelectedXMm, SelectedYMm);
            Manager.ResizeElement(
                selected.Id,
                widthMm: Math.Max(0.1, SelectedWidthMm),
                heightMm: Math.Max(0.1, SelectedHeightMm),
                preserveAspectRatio: false);
            Manager.RotateElement(selected.Id, SelectedRotationDeg);
        }

        Manager.SyncRelativeCoords(selected);

        if (selected is TextPersonalizationElement textEl)
        {
            textEl.Content = SelectedTextContent;
            textEl.FontFamily = SelectedTextFontFamily;
            textEl.FontSizeMm = Math.Clamp(SelectedTextFontSizeMm, 1.0, 400.0);
            textEl.Bold = SelectedTextBold;
            textEl.Italic = SelectedTextItalic;
            textEl.HorizontalAlign = SelectedTextHorizontalAlign;
            textEl.LetterSpacingMm = SelectedTextLetterSpacingMm;
            textEl.LineSpacing = Math.Clamp(SelectedTextLineSpacing, 0.5, 4.0);
            textEl.RenderMode = SelectedTextRenderMode;
            textEl.HatchSpacingMm = Math.Clamp(SelectedTextHatchSpacingMm, 0.05, 5.0);

            RevectorizeTextElement(textEl);
        }
        else if (selected is RasterImagePersonalizationElement rasterEl)
        {
            ApplyRasterReprocessing(rasterEl);
        }

        CommitMutation();
    }

    [RelayCommand]
    public void ApplyQuickAlignment(QuickAlignmentPreset preset)
    {
        PersonalizationElement? selected = SelectedElement;
        SurfaceDefinition? surface = ActiveSurface;
        if (selected is null || surface is null || selected.Locked)
        {
            return;
        }

        RecordUndoPoint();

        Rect2D safe = surface.SafeAreaForMargin(SafetyMarginMm);
        double newX = selected.XMm;
        double newY = selected.YMm;

        switch (preset)
        {
            case QuickAlignmentPreset.CenterSurface:
                newX = safe.XMm + (safe.WidthMm - selected.WidthMm) * 0.5;
                newY = safe.YMm + (safe.HeightMm - selected.HeightMm) * 0.5;
                break;
            case QuickAlignmentPreset.AlignLeft:
                newX = safe.XMm;
                break;
            case QuickAlignmentPreset.AlignRight:
                newX = safe.Right - selected.WidthMm;
                break;
            case QuickAlignmentPreset.AlignTop:
                newY = safe.YMm;
                break;
            case QuickAlignmentPreset.AlignBottom:
                newY = safe.Bottom - selected.HeightMm;
                break;
        }

        Manager.MoveElement(selected.Id, PythonMath.Round(newX, 4), PythonMath.Round(newY, 4));
        CommitMutation();
    }

    [RelayCommand]
    public void AlignLeft() => AlignSelectedElements(AlignmentAction.AlignLeft);

    [RelayCommand]
    public void AlignCenterX() => AlignSelectedElements(AlignmentAction.AlignCenterX);

    [RelayCommand]
    public void AlignRight() => AlignSelectedElements(AlignmentAction.AlignRight);

    [RelayCommand]
    public void AlignTop() => AlignSelectedElements(AlignmentAction.AlignTop);

    [RelayCommand]
    public void AlignCenterY() => AlignSelectedElements(AlignmentAction.AlignCenterY);

    [RelayCommand]
    public void AlignBottom() => AlignSelectedElements(AlignmentAction.AlignBottom);

    [RelayCommand]
    public void DistributeHorizontal() => AlignSelectedElements(AlignmentAction.DistributeHorizontal);

    [RelayCommand]
    public void DistributeVertical() => AlignSelectedElements(AlignmentAction.DistributeVertical);

    /// <summary>
    /// Executa alinhamento ou distribuição CAD 1:1 (estilo Inkscape) sobre os elementos selecionados
    /// na chapa ativa em 1 único passo atômico de Undo/Redo com revalidação incremental <c>ValidatePanel</c>.
    /// </summary>
    public bool AlignSelectedElements(AlignmentAction action)
    {
        SurfaceDefinition? surface = ActiveSurface;
        if (surface is null)
        {
            return false;
        }

        var ordered = new List<PersonalizationElement>(_selectedElementIds.Count);
        foreach (string id in _selectedElementIds)
        {
            if (Manager.TryGetElement(id, out PersonalizationElement? el) &&
                el is not null &&
                el.PanelId == ActiveSurfaceId)
            {
                ordered.Add(el);
            }
        }

        if (ordered.Count == 0 && SelectedElement is { } fallback && fallback.PanelId == ActiveSurfaceId)
        {
            ordered.Add(fallback);
        }

        if (ordered.Count == 0)
        {
            return false;
        }

        IReadOnlyList<ElementAlignmentDelta> deltas = ElementAlignmentService.ComputeAlignedPositions(
            ordered,
            surface,
            action,
            SelectedAlignmentReference,
            SafetyMarginMm);

        if (deltas.Count == 0)
        {
            return false;
        }

        RecordUndoPoint();
        for (int i = 0; i < deltas.Count; i++)
        {
            ElementAlignmentDelta d = deltas[i];
            Manager.MoveElement(d.ElementId, d.NewXMm, d.NewYMm);
        }

        CommitActivePanelMutation();
        return true;
    }

    [RelayCommand]
    public void SetSelectedAnchor(AnchorMode anchor)
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        RecordUndoPoint();
        selected.Anchor = anchor;
        Manager.SyncRelativeCoords(selected);
        CommitMutation();
    }

    [RelayCommand]
    public void SetSelectedOperation(CncOperationType operation)
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        RecordUndoPoint();
        OffscreenViewportRenderer.InvalidateElementVectorCache(selected);
        selected.Operation = operation;
        CommitActivePanelMutation();
    }

    [RelayCommand]
    public void SetSelectedOperationMode(OperationMode mode)
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        RecordUndoPoint();
        OffscreenViewportRenderer.InvalidateElementVectorCache(selected);
        selected.OperationMode = mode;
        CommitActivePanelMutation();
    }

    [RelayCommand]
    public void MoveSelectedElementToSurface(SurfaceId targetPanelId)
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null || selected.Locked || selected.PanelId == targetPanelId)
        {
            return;
        }

        if (!Manager.Surfaces.TryGetSurface(targetPanelId, out _))
        {
            return;
        }

        RecordUndoPoint();
        Manager.RepositionElement(selected.Id, targetPanelId, centerOnSurface: true);
        ActiveSurfaceId = targetPanelId;
        SelectedElementId = selected.Id;
        CommitMutation();
    }

    [RelayCommand]
    public void RotateSelected90()
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null || selected.Locked)
        {
            return;
        }

        double nextRot = Math.Round((selected.RotationDeg + 90.0) % 360.0, 1);
        UpdateSelectedElementTransform(rotationDeg: nextRot, recordUndo: true);
    }

    [RelayCommand]
    public void ToggleSelectedVisibility()
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        RecordUndoPoint();
        selected.Visible = !selected.Visible;
        CommitMutation();
    }

    [RelayCommand]
    public void ToggleSelectedLock()
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        RecordUndoPoint();
        selected.Locked = !selected.Locked;
        CommitMutation();
    }

    [RelayCommand]
    public void DuplicateSelectedElement()
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        RecordUndoPoint();
        PersonalizationElement copy = Manager.DuplicateElement(selected.Id, offsetMm: 5.0);
        SelectedElementId = copy.Id;
        CommitMutation();
    }

    public IRelayCommand MirrorFourCornersCommand =>
        _mirrorFourCornersCommand ??= new RelayCommand(MirrorFourCornersSelectedElement);

    public IRelayCommand CreateLinearArrayCommand =>
        _createLinearArrayCommand ??= new RelayCommand(CreateLinearArraySelectedElement);

    /// <summary>
    /// Espelha o elemento selecionado nas até 4 posições simétricas em relação ao centro da chapa ativa,
    /// ignorando posições que já possuam elemento idêntico (tolerância 0,1 mm) e registrando 1 passo Undo.
    /// </summary>
    public void MirrorFourCornersSelectedElement()
    {
        PersonalizationElement? selected = SelectedElement;
        SurfaceDefinition? surface = ActiveSurface;
        if (selected is null || surface is null)
        {
            return;
        }

        double surfCx = surface.WidthMm * 0.5;
        double surfCy = surface.HeightMm * 0.5;
        Point2D selCenter = selected.CenterMm();
        double absRelX = Math.Abs(selCenter.X - surfCx);
        double absRelY = Math.Abs(selCenter.Y - surfCy);

        if (absRelX <= 0.5 && absRelY <= 0.5)
        {
            return;
        }

        double[] candRelXs = absRelX <= 0.5 ? [0.0] : [-absRelX, absRelX];
        double[] candRelYs = absRelY <= 0.5 ? [0.0] : [-absRelY, absRelY];

        var targets = new List<(double Cx, double Cy)>(4);
        foreach (double rx in candRelXs)
        {
            foreach (double ry in candRelYs)
            {
                targets.Add((surfCx + rx, surfCy + ry));
            }
        }

        IReadOnlyList<PersonalizationElement> existing = Manager.ListElements(ActiveSurfaceId);
        var toAdd = new List<PersonalizationElement>(4);
        int cloneIndex = 1;

        foreach ((double targetCx, double targetCy) in targets)
        {
            bool alreadyPresent = existing.Any(e => IsMirrorDuplicateAt(selected, e, targetCx, targetCy)) ||
                                  toAdd.Any(e => IsMirrorDuplicateAt(selected, e, targetCx, targetCy));
            if (alreadyPresent)
            {
                continue;
            }

            PersonalizationElement clone = selected.DeepClone();
            clone.Id = PersonalizationElement.GenerateNewId();
            clone.Name = $"{selected.Name} (Sim {cloneIndex++})";
            clone.XMm = Math.Round(targetCx - clone.WidthMm * 0.5, 4);
            clone.YMm = Math.Round(targetCy - clone.HeightMm * 0.5, 4);
            toAdd.Add(clone);
        }

        if (toAdd.Count == 0)
        {
            return;
        }

        RecordUndoPoint();
        foreach (PersonalizationElement clone in toAdd)
        {
            Manager.AddElement(clone, xMm: clone.XMm, yMm: clone.YMm, centerOnSurface: false);
        }

        CommitActivePanelMutation();
    }

    private static bool IsMirrorDuplicateAt(
        PersonalizationElement source,
        PersonalizationElement candidate,
        double targetCx,
        double targetCy)
    {
        if (candidate.ElementType != source.ElementType)
        {
            return false;
        }

        Point2D c = candidate.CenterMm();
        if (Math.Abs(c.X - targetCx) > 0.1 || Math.Abs(c.Y - targetCy) > 0.1)
        {
            return false;
        }

        return Math.Abs(candidate.WidthMm - source.WidthMm) <= 0.1 &&
               Math.Abs(candidate.HeightMm - source.HeightMm) <= 0.1;
    }

    /// <summary>
    /// Gera uma grade linear de repetições (<see cref="ArrayCount"/> total, incluindo o selecionado)
    /// deslocadas por (<see cref="ArrayStepXMm"/>, <see cref="ArrayStepYMm"/>), com 1 passo Undo e validação SAT.
    /// </summary>
    public void CreateLinearArraySelectedElement()
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        int count = Math.Clamp(ArrayCount, 2, 20);
        RecordUndoPoint();

        for (int i = 1; i < count; i++)
        {
            PersonalizationElement clone = selected.DeepClone();
            clone.Id = PersonalizationElement.GenerateNewId();
            clone.Name = $"{selected.Name} [{i + 1}]";
            double nextX = Math.Round(selected.XMm + i * ArrayStepXMm, 4);
            double nextY = Math.Round(selected.YMm + i * ArrayStepYMm, 4);
            Manager.AddElement(clone, xMm: nextX, yMm: nextY, centerOnSurface: false);
        }

        CommitActivePanelMutation();
    }

    [RelayCommand]
    public void CopySelectedElement()
    {
        PersonalizationElement? selected = SelectedElement;
        if (selected is null)
        {
            return;
        }

        PersonalizationElement clone = selected.DeepClone();
        if (clone is RasterImagePersonalizationElement raster &&
            string.IsNullOrEmpty(raster.RawImageB64) &&
            Manager.TryGetCachedRawRaster(selected.Id, out string? raw))
        {
            raster.RawImageB64 = raw ?? string.Empty;
        }

        ClipboardElement = clone;
        OnPropertyChanged(nameof(HasClipboard));
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void PasteElement() => PasteClipboardElement();

    public PersonalizationElement? PasteClipboardElement()
    {
        if (ClipboardElement is null)
        {
            return null;
        }

        RecordUndoPoint();

        PersonalizationElement pasted = ClipboardElement.DeepClone();
        pasted.Id = PersonalizationElement.GenerateNewId();
        pasted.PanelId = ActiveSurfaceId;
        pasted.Name = string.IsNullOrWhiteSpace(pasted.Name)
            ? "Elemento colado"
            : $"{pasted.Name} (cópia)";

        bool sameSurface = ClipboardElement.PanelId == ActiveSurfaceId;
        double? x = sameSurface ? pasted.XMm + 6.0 : null;
        double? y = sameSurface ? pasted.YMm + 6.0 : null;

        Manager.AddElement(pasted, xMm: x, yMm: y, centerOnSurface: !sameSurface);
        SelectedElementId = pasted.Id;
        CommitMutation();
        return pasted;
    }

    [RelayCommand]
    public void DeleteSelectedElement() => DeleteSelectedElements();

    [RelayCommand]
    public void DeleteSelectedElements()
    {
        List<string> toRemove = _selectedElementIds
            .Where(id => Manager.TryGetElement(id, out _))
            .ToList();

        if (toRemove.Count == 0 && SelectedElement is { } fallback)
        {
            toRemove.Add(fallback.Id);
        }

        if (toRemove.Count == 0)
        {
            return;
        }

        RecordUndoPoint();
        foreach (string id in toRemove)
        {
            Manager.RemoveElement(id);
        }

        ApplyOrderedSelectionInternal([]);
        CommitActivePanelMutation();
    }

    [RelayCommand]
    public void ClearAllElements()
    {
        if (!HasElements)
        {
            return;
        }

        RecordUndoPoint();
        Manager.ClearElements(clearRawRasterCache: false);
        SelectedElementId = null;
        CommitMutation();
    }

    [RelayCommand]
    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        FlushHistoryCoalescing();
        PersonalizationHistoryEntry current = CaptureHistoryCheckpoint();

        PersonalizationHistoryEntry target = _history.Undo(current);
        RestoreHistoryEntry(target);
    }

    [RelayCommand]
    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        FlushHistoryCoalescing();
        PersonalizationHistoryEntry current = CaptureHistoryCheckpoint();

        PersonalizationHistoryEntry target = _history.Redo(current);
        RestoreHistoryEntry(target);
    }

    [RelayCommand]
    public void ValidateSurface() => RunValidation();

    public ValidationReport RunValidation()
        => RunValidationCore(notifyVisualState: true);

    private ValidationReport RunValidationCore(bool notifyVisualState)
    {
        ValidationReport report = Manager.Validate(SafetyMarginMm, strictZones: true);
        ApplyValidationReport(report, onlyIfChanged: false);
        if (notifyVisualState)
        {
            VisualStateChanged?.Invoke(this, EventArgs.Empty);
        }
        return report;
    }

    /// <summary>
    /// Executa validação incremental via <see cref="SurfaceManager.ValidatePanel"/> restrita à chapa informada,
    /// mesclando com os diagnósticos existentes das demais chapas.
    /// </summary>
    public ValidationReport RunIncrementalPanelValidation(SurfaceId panelId, bool notifyVisualState = false)
    {
        ValidationReport panelReport = Manager.ValidatePanel(panelId, SafetyMarginMm, strictZones: true);
        ValidationReport? baseline = ValidationReport;

        if (baseline is null)
        {
            ApplyValidationReport(panelReport, onlyIfChanged: false);
            if (notifyVisualState)
            {
                VisualStateChanged?.Invoke(this, EventArgs.Empty);
            }
            return panelReport;
        }

        string panelName = SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(panelId);
        GeometryValidationIssue[] otherErrors = baseline.Errors
            .Where(i => !string.Equals(i.PanelId, panelName, StringComparison.Ordinal))
            .ToArray();
        GeometryValidationIssue[] otherWarnings = baseline.Warnings
            .Where(i => !string.Equals(i.PanelId, panelName, StringComparison.Ordinal))
            .ToArray();

        GeometryValidationIssue[] combinedErrors = otherErrors.Length == 0
            ? panelReport.Errors as GeometryValidationIssue[] ?? panelReport.Errors.ToArray()
            : [.. otherErrors, .. panelReport.Errors];
        GeometryValidationIssue[] combinedWarnings = otherWarnings.Length == 0
            ? panelReport.Warnings as GeometryValidationIssue[] ?? panelReport.Warnings.ToArray()
            : [.. otherWarnings, .. panelReport.Warnings];

        var merged = new ValidationReport
        {
            IsValid = combinedErrors.Length == 0,
            Errors = combinedErrors,
            Warnings = combinedWarnings,
            SafetyMarginMm = panelReport.SafetyMarginMm,
            CamNotes = panelReport.CamNotes,
        };

        ApplyValidationReport(merged, onlyIfChanged: false);
        if (notifyVisualState)
        {
            VisualStateChanged?.Invoke(this, EventArgs.Empty);
        }

        return merged;
    }

    private void CommitInteractiveTransformStep(PersonalizationElement selected)
    {
        _suppressSelectedSync = true;
        try
        {
            SyncSelectedTransformProperties(selected);
        }
        finally
        {
            _suppressSelectedSync = false;
        }

        long nowTicks = Stopwatch.GetTimestamp();
        if (_lastInteractiveValidationTimestamp == 0 ||
            (nowTicks - _lastInteractiveValidationTimestamp) >= InteractiveValidationMinIntervalTicks)
        {
            _lastInteractiveValidationTimestamp = nowTicks;
            RunInteractivePanelValidation();
        }

        InteractiveTransformChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RunInteractivePanelValidation()
    {
        ValidationReport panelReport = Manager.ValidatePanel(ActiveSurfaceId, SafetyMarginMm, strictZones: true);
        IReadOnlyList<GeometryValidationIssue> otherErrors = _nonActivePanelErrors ?? [];
        IReadOnlyList<GeometryValidationIssue> otherWarnings = _nonActivePanelWarnings ?? [];

        GeometryValidationIssue[] combinedErrors = otherErrors.Count == 0
            ? panelReport.Errors as GeometryValidationIssue[] ?? panelReport.Errors.ToArray()
            : [.. otherErrors, .. panelReport.Errors];
        GeometryValidationIssue[] combinedWarnings = otherWarnings.Count == 0
            ? panelReport.Warnings as GeometryValidationIssue[] ?? panelReport.Warnings.ToArray()
            : [.. otherWarnings, .. panelReport.Warnings];

        var merged = new ValidationReport
        {
            IsValid = combinedErrors.Length == 0,
            Errors = combinedErrors,
            Warnings = combinedWarnings,
            SafetyMarginMm = panelReport.SafetyMarginMm,
            CamNotes = panelReport.CamNotes,
        };

        ApplyValidationReport(merged, onlyIfChanged: true);
    }

    private void ApplyValidationReport(ValidationReport report, bool onlyIfChanged)
    {
        bool unchanged = onlyIfChanged && IssuesMatch(ValidationIssues, report.Errors, report.Warnings);
        ValidationReport = report;
        if (unchanged)
        {
            return;
        }

        ValidationIssues.Clear();
        foreach (GeometryValidationIssue err in report.Errors)
        {
            ValidationIssues.Add(err);
        }
        foreach (GeometryValidationIssue warn in report.Warnings)
        {
            ValidationIssues.Add(warn);
        }

        OnPropertyChanged(nameof(IsValidationValid));
        OnPropertyChanged(nameof(ValidationErrorCount));
        OnPropertyChanged(nameof(ValidationWarningCount));
        OnPropertyChanged(nameof(ValidationSummaryBadge));
    }

    private static bool IssuesMatch(
        IReadOnlyList<GeometryValidationIssue> current,
        IReadOnlyList<GeometryValidationIssue> errors,
        IReadOnlyList<GeometryValidationIssue> warnings)
    {
        if (current.Count != errors.Count + warnings.Count)
        {
            return false;
        }

        int idx = 0;
        for (int i = 0; i < errors.Count; i++, idx++)
        {
            if (!IssueEquals(current[idx], errors[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < warnings.Count; i++, idx++)
        {
            if (!IssueEquals(current[idx], warnings[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IssueEquals(GeometryValidationIssue a, GeometryValidationIssue b)
        => a.Severity == b.Severity &&
           string.Equals(a.Code, b.Code, StringComparison.Ordinal) &&
           string.Equals(a.ElementId, b.ElementId, StringComparison.Ordinal) &&
           string.Equals(a.PanelId, b.PanelId, StringComparison.Ordinal) &&
           string.Equals(a.ZoneId, b.ZoneId, StringComparison.Ordinal) &&
           string.Equals(a.Message, b.Message, StringComparison.Ordinal);

    private void SyncSelectedTransformProperties(PersonalizationElement el)
    {
        SelectedXMm = Math.Round(el.XMm, 3);
        SelectedYMm = Math.Round(el.YMm, 3);
        SelectedWidthMm = Math.Round(el.WidthMm, 3);
        SelectedHeightMm = Math.Round(el.HeightMm, 3);
        SelectedRotationDeg = Math.Round(el.RotationDeg, 2);
        SelectedPreserveAspectRatio = el.PreserveAspectRatio;

        if (el is ShapePersonalizationElement shape && shape.ShapeSpec is { } spec)
        {
            SelectedShapeKind = spec.Kind;
            SelectedShapeWidthMm = Math.Round(spec.WidthMm, 3);
            SelectedShapeHeightMm = Math.Round(spec.HeightMm, 3);
            SelectedShapeDiameterMm = Math.Round(spec.DiameterMm, 3);
            SelectedShapeCornerRadiusMm = Math.Round(spec.CornerRadiusMm, 3);
            SelectedShapeSides = spec.Sides;
            SelectedShapeFillHatch = spec.FillHatch;
            SelectedShapeHatchSpacingMm = Math.Round(spec.HatchSpacingMm, 3);
        }
    }

    partial void OnSafetyMarginMmChanged(double value)
    {
        double clamped = Math.Clamp(double.IsFinite(value) ? value : 2.0, 0.0, 20.0);
        if (Math.Abs(clamped - value) > 1e-6)
        {
            SafetyMarginMm = clamped;
            return;
        }

        Manager.SafetyMarginMm = clamped;
        RunValidation();
    }

    partial void OnDefaultHatchSpacingMmChanged(double value)
    {
        DraftHatchSpacingMm = Math.Clamp(double.IsFinite(value) ? value : 0.25, 0.05, 5.0);
    }

    /// <summary>Captura um checkpoint de histórico sem alterar a pilha (para uso no início de arrastos 2D).</summary>
    public PersonalizationHistoryEntry CaptureHistoryCheckpoint()
        => new(
            Manager.CreateSnapshot(includeRawBase64: false),
            ActiveSurfaceId,
            SelectedElementId,
            _selectedElementIds.Count > 0 ? _selectedElementIds.ToArray() : null);

    /// <summary>Encerra a sessão atual de coalescência de histórico para iniciar novo passo na próxima edição.</summary>
    public void FlushHistoryCoalescing()
    {
        _lastCoalesceKey = null;
        _lastCoalesceUtc = default;
    }

    /// <summary>Registra um checkpoint capturado antes de um arrasto contínuo como um único passo Undo.</summary>
    public void PushHistoryCheckpoint(PersonalizationHistoryEntry stateBeforeChange)
        => RecordHistoryEntryInternal(stateBeforeChange, notifyVisualState: true);

    private void RecordHistoryEntryInternal(PersonalizationHistoryEntry stateBeforeChange, bool notifyVisualState)
    {
        ArgumentNullException.ThrowIfNull(stateBeforeChange);
        FlushHistoryCoalescing();
        _history.Record(stateBeforeChange);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoCount));
        OnPropertyChanged(nameof(RedoCount));
        if (notifyVisualState)
        {
            VisualStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RecordUndoPoint(string? coalesceKey = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(coalesceKey) &&
            string.Equals(_lastCoalesceKey, coalesceKey, StringComparison.Ordinal) &&
            (now - _lastCoalesceUtc) <= HistoryCoalesceWindow &&
            _history.CanUndo)
        {
            _lastCoalesceUtc = now;
            return;
        }

        _history.Record(CaptureHistoryCheckpoint());
        _lastCoalesceKey = coalesceKey;
        _lastCoalesceUtc = now;
    }

    private void RestoreHistoryEntry(PersonalizationHistoryEntry target)
    {
        Manager.RestoreSnapshot(target.Snapshot);
        ActiveSurfaceId = Manager.Surfaces.TryGetSurface(target.ActiveSurfaceId, out _)
            ? target.ActiveSurfaceId
            : SurfaceId.Front;

        if (target.SelectedElementIds is { Count: > 0 } restoredIds)
        {
            ApplyOrderedSelectionInternal(restoredIds.Where(id => Manager.TryGetElement(id, out _)));
        }
        else if (target.SelectedElementId is not null && Manager.TryGetElement(target.SelectedElementId, out _))
        {
            ApplyOrderedSelectionInternal([target.SelectedElementId]);
        }
        else
        {
            ApplyOrderedSelectionInternal([]);
        }

        RefreshAllViewsAndValidation();
        StateMutatedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyRasterReprocessing(RasterImagePersonalizationElement rasterEl)
    {
        string rawB64 = !string.IsNullOrEmpty(rasterEl.RawImageB64)
            ? rasterEl.RawImageB64
            : (Manager.TryGetCachedRawRaster(rasterEl.Id, out string? cached) && !string.IsNullOrEmpty(cached)
                ? cached
                : rasterEl.ProcessedImageB64);

        if (string.IsNullOrEmpty(rawB64))
        {
            return;
        }

        byte[] rawBytes = Convert.FromBase64String(rawB64);
        var opts = new RasterProcessingOptions
        {
            Grayscale = SelectedRasterGrayscale,
            Brightness = Math.Clamp(SelectedRasterBrightness, 0.1, 3.0),
            Contrast = Math.Clamp(SelectedRasterContrast, 0.1, 3.0),
            Invert = SelectedRasterInvert,
            Monochrome = SelectedRasterMonochrome,
            Threshold = Math.Clamp(SelectedRasterThreshold, 0, 255),
            Dithering = SelectedRasterDithering,
            CropBox = rasterEl.ProcessingOptions.CropBox,
            ResizeMaxPx = rasterEl.ProcessingOptions.ResizeMaxPx,
        };

        RasterImagePersonalizationElement reprocessed = _rasterProcessor.ProcessFromBytes(
            rawBytes: rawBytes,
            sourceFilename: rasterEl.SourceFilename,
            panelId: rasterEl.PanelId,
            options: opts,
            targetWidthMm: rasterEl.WidthMm,
            targetHeightMm: rasterEl.HeightMm,
            vectorize: SelectedRasterVectorize,
            vectorizeEpsilonPx: Math.Clamp(SelectedRasterVectorizeEpsilonPx, 0.2, 10.0),
            operation: rasterEl.Operation,
            name: rasterEl.Name);

        rasterEl.ProcessingOptions = reprocessed.ProcessingOptions;
        rasterEl.ProcessedImageB64 = reprocessed.ProcessedImageB64;
        rasterEl.ProcessedPixelSize = reprocessed.ProcessedPixelSize;
        rasterEl.Vectorize = SelectedRasterVectorize;
        rasterEl.VectorizeEpsilonPx = SelectedRasterVectorizeEpsilonPx;
        rasterEl.VectorPaths = reprocessed.VectorPaths;
    }

    private void CommitMutation()
    {
        RefreshAllViewsAndValidation();
        StateMutatedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void CommitActivePanelMutation()
    {
        Surfaces.Clear();
        foreach (SurfaceDefinition surface in Manager.ListSurfaces())
        {
            Surfaces.Add(surface);
        }

        RefreshActiveSurfaceElements();
        SyncSelectedFieldsFromElement();
        RunIncrementalPanelValidation(ActiveSurfaceId, notifyVisualState: false);

        OnPropertyChanged(nameof(ActiveSurface));
        OnPropertyChanged(nameof(TotalElementCount));
        OnPropertyChanged(nameof(HasElements));
        OnPropertyChanged(nameof(HasClipboard));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoCount));
        OnPropertyChanged(nameof(RedoCount));
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
        StateMutatedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshAllViewsAndValidation()
    {
        Surfaces.Clear();
        foreach (SurfaceDefinition surface in Manager.ListSurfaces())
        {
            Surfaces.Add(surface);
        }

        RefreshActiveSurfaceElements();
        SyncSelectedFieldsFromElement();
        RunValidationCore(notifyVisualState: false);

        OnPropertyChanged(nameof(ActiveSurface));
        OnPropertyChanged(nameof(TotalElementCount));
        OnPropertyChanged(nameof(HasElements));
        OnPropertyChanged(nameof(HasClipboard));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoCount));
        OnPropertyChanged(nameof(RedoCount));
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshActiveSurfaceElements()
    {
        ElementsOnActiveSurface.Clear();
        foreach (PersonalizationElement el in Manager.ListElements(ActiveSurfaceId))
        {
            ElementsOnActiveSurface.Add(el);
        }
    }

    private void SyncSelectedFieldsFromElement()
    {
        _suppressSelectedSync = true;
        try
        {
            PersonalizationElement? el = SelectedElement;
            OnPropertyChanged(nameof(SelectedElement));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanEditSelection));
            OnPropertyChanged(nameof(IsSelectedText));
            OnPropertyChanged(nameof(IsSelectedVectorSvg));
            OnPropertyChanged(nameof(IsSelectedRaster));
            OnPropertyChanged(nameof(IsShapeSelected));
            OnPropertyChanged(nameof(IsSelectedShape));
            OnPropertyChanged(nameof(IsRectangleShapeSelected));
            OnPropertyChanged(nameof(IsCircleShapeSelected));
            OnPropertyChanged(nameof(IsSlotShapeSelected));
            OnPropertyChanged(nameof(IsPolygonShapeSelected));
            OnPropertyChanged(nameof(IsCrosshairShapeSelected));

            if (el is null)
            {
                SelectedName = string.Empty;
                SelectedXMm = 0;
                SelectedYMm = 0;
                SelectedWidthMm = 10;
                SelectedHeightMm = 10;
                SelectedRotationDeg = 0;
                return;
            }

            SelectedName = el.Name;
            SyncSelectedTransformProperties(el);
            SelectedOperation = el.Operation;
            SelectedOperationMode = el.OperationMode;
            SelectedAnchor = el.Anchor;
            SelectedVisible = el.Visible;
            SelectedLocked = el.Locked;

            if (el is TextPersonalizationElement text)
            {
                SelectedTextContent = text.Content;
                SelectedTextFontFamily = text.FontFamily;
                SelectedTextFontSizeMm = text.FontSizeMm;
                SelectedTextBold = text.Bold;
                SelectedTextItalic = text.Italic;
                SelectedTextHorizontalAlign = text.HorizontalAlign;
                SelectedTextLetterSpacingMm = text.LetterSpacingMm;
                SelectedTextLineSpacing = text.LineSpacing;
                SelectedTextRenderMode = text.RenderMode;
                SelectedTextHatchSpacingMm = text.HatchSpacingMm;
            }
            else if (el is RasterImagePersonalizationElement raster)
            {
                SelectedRasterGrayscale = raster.ProcessingOptions.Grayscale;
                SelectedRasterBrightness = raster.ProcessingOptions.Brightness;
                SelectedRasterContrast = raster.ProcessingOptions.Contrast;
                SelectedRasterInvert = raster.ProcessingOptions.Invert;
                SelectedRasterMonochrome = raster.ProcessingOptions.Monochrome;
                SelectedRasterThreshold = raster.ProcessingOptions.Threshold;
                SelectedRasterDithering = raster.ProcessingOptions.Dithering;
                SelectedRasterVectorize = raster.Vectorize;
                SelectedRasterVectorizeEpsilonPx = raster.VectorizeEpsilonPx;
            }
        }
        finally
        {
            _suppressSelectedSync = false;
        }
    }

    partial void OnSelectedAnchorChanged(AnchorMode value)
    {
        if (_suppressSelectedSync || SelectedElement is null) return;
        if (SelectedElement.Anchor != value)
        {
            SetSelectedAnchor(value);
        }
    }

    partial void OnSelectedOperationChanged(CncOperationType value)
    {
        if (_suppressSelectedSync || SelectedElement is null) return;
        if (SelectedElement.Operation != value)
        {
            SetSelectedOperation(value);
        }
    }

    partial void OnSelectedOperationModeChanged(OperationMode value)
    {
        if (_suppressSelectedSync || SelectedElement is null) return;
        if (SelectedElement.OperationMode != value)
        {
            SetSelectedOperationMode(value);
        }
    }

    partial void OnSelectedNameChanged(string value)
    {
        if (_suppressSelectedSync || SelectedElement is null) return;
        string trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(SelectedElement.Name, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        RecordUndoPoint($"name:{SelectedElement.Id}");
        SelectedElement.Name = trimmed;
        CommitMutation();
    }

    partial void OnSelectedXMmChanged(double value)
    {
        if (_suppressSelectedSync || SelectedElement is null || SelectedElement.Locked) return;
        if (Math.Abs(SelectedElement.XMm - value) > 1e-6)
        {
            UpdateSelectedElementTransform(xMm: value, recordUndo: true, coalesceKey: $"pos-x:{SelectedElement.Id}");
        }
    }

    partial void OnSelectedYMmChanged(double value)
    {
        if (_suppressSelectedSync || SelectedElement is null || SelectedElement.Locked) return;
        if (Math.Abs(SelectedElement.YMm - value) > 1e-6)
        {
            UpdateSelectedElementTransform(yMm: value, recordUndo: true, coalesceKey: $"pos-y:{SelectedElement.Id}");
        }
    }

    partial void OnSelectedWidthMmChanged(double value)
    {
        if (_suppressSelectedSync || SelectedElement is null || SelectedElement.Locked) return;
        double target = Math.Max(0.1, value);
        if (Math.Abs(SelectedElement.WidthMm - target) > 1e-6)
        {
            UpdateSelectedElementTransform(widthMm: target, recordUndo: true, coalesceKey: $"size-w:{SelectedElement.Id}");
        }
    }

    partial void OnSelectedHeightMmChanged(double value)
    {
        if (_suppressSelectedSync || SelectedElement is null || SelectedElement.Locked) return;
        double target = Math.Max(0.1, value);
        if (Math.Abs(SelectedElement.HeightMm - target) > 1e-6)
        {
            UpdateSelectedElementTransform(heightMm: target, recordUndo: true, coalesceKey: $"size-h:{SelectedElement.Id}");
        }
    }

    partial void OnSelectedRotationDegChanged(double value)
    {
        if (_suppressSelectedSync || SelectedElement is null || SelectedElement.Locked) return;
        if (Math.Abs(SelectedElement.RotationDeg - value) > 1e-6)
        {
            UpdateSelectedElementTransform(rotationDeg: value, recordUndo: true, coalesceKey: $"rot:{SelectedElement.Id}");
        }
    }

    partial void OnSelectedPreserveAspectRatioChanged(bool value)
    {
        if (_suppressSelectedSync || SelectedElement is null || SelectedElement.PreserveAspectRatio == value) return;
        UpdateSelectedElementTransform(preserveAspectRatio: value, recordUndo: true);
    }

    partial void OnSelectedVisibleChanged(bool value)
    {
        if (_suppressSelectedSync || SelectedElement is null || SelectedElement.Visible == value) return;
        RecordUndoPoint();
        SelectedElement.Visible = value;
        CommitMutation();
    }

    partial void OnSelectedLockedChanged(bool value)
    {
        if (_suppressSelectedSync || SelectedElement is null || SelectedElement.Locked == value) return;
        RecordUndoPoint();
        SelectedElement.Locked = value;
        CommitMutation();
    }

    partial void OnSelectedTextContentChanged(string value)
    {
        string normalized = value ?? string.Empty;
        ApplyLiveSelectedTextChange(
            coalesceKey: SelectedElement is { } el ? $"text-content:{el.Id}" : null,
            mutate: textEl => textEl.Content = normalized,
            shouldApply: textEl => !string.Equals(textEl.Content, normalized, StringComparison.Ordinal));
    }

    partial void OnSelectedTextFontFamilyChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        ApplyLiveSelectedTextChange(
            coalesceKey: null,
            mutate: textEl => textEl.FontFamily = value,
            shouldApply: textEl => !string.Equals(textEl.FontFamily, value, StringComparison.Ordinal));
    }

    partial void OnSelectedTextFontSizeMmChanged(double value)
    {
        double clamped = Math.Clamp(value, 1.0, 400.0);
        ApplyLiveSelectedTextChange(
            coalesceKey: SelectedElement is { } el ? $"text-fontsize:{el.Id}" : null,
            mutate: textEl => textEl.FontSizeMm = clamped,
            shouldApply: textEl => Math.Abs(textEl.FontSizeMm - clamped) > 1e-6);
    }

    partial void OnSelectedTextBoldChanged(bool value)
    {
        ApplyLiveSelectedTextChange(
            coalesceKey: null,
            mutate: textEl => textEl.Bold = value,
            shouldApply: textEl => textEl.Bold != value);
    }

    partial void OnSelectedTextItalicChanged(bool value)
    {
        ApplyLiveSelectedTextChange(
            coalesceKey: null,
            mutate: textEl => textEl.Italic = value,
            shouldApply: textEl => textEl.Italic != value);
    }

    partial void OnSelectedTextHorizontalAlignChanged(HorizontalAlign value)
    {
        ApplyLiveSelectedTextChange(
            coalesceKey: null,
            mutate: textEl => textEl.HorizontalAlign = value,
            shouldApply: textEl => textEl.HorizontalAlign != value);
    }

    partial void OnSelectedTextLetterSpacingMmChanged(double value)
    {
        ApplyLiveSelectedTextChange(
            coalesceKey: SelectedElement is { } el ? $"text-letter:{el.Id}" : null,
            mutate: textEl => textEl.LetterSpacingMm = value,
            shouldApply: textEl => Math.Abs(textEl.LetterSpacingMm - value) > 1e-6);
    }

    partial void OnSelectedTextLineSpacingChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.5, 4.0);
        ApplyLiveSelectedTextChange(
            coalesceKey: SelectedElement is { } el ? $"text-line:{el.Id}" : null,
            mutate: textEl => textEl.LineSpacing = clamped,
            shouldApply: textEl => Math.Abs(textEl.LineSpacing - clamped) > 1e-6);
    }

    partial void OnSelectedTextRenderModeChanged(TextRenderMode value)
    {
        ApplyLiveSelectedTextChange(
            coalesceKey: null,
            mutate: textEl => textEl.RenderMode = value,
            shouldApply: textEl => textEl.RenderMode != value);
    }

    partial void OnSelectedTextHatchSpacingMmChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.05, 5.0);
        ApplyLiveSelectedTextChange(
            coalesceKey: SelectedElement is { } el ? $"text-hatch:{el.Id}" : null,
            mutate: textEl => textEl.HatchSpacingMm = clamped,
            shouldApply: textEl => Math.Abs(textEl.HatchSpacingMm - clamped) > 1e-6);
    }

    partial void OnSelectedRasterGrayscaleChanged(bool value)
    {
        ApplyLiveSelectedRasterChange(
            coalesceKey: null,
            shouldApply: rasterEl => rasterEl.ProcessingOptions.Grayscale != value);
    }

    partial void OnSelectedRasterBrightnessChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.1, 3.0);
        ApplyLiveSelectedRasterChange(
            coalesceKey: SelectedElement is { } el ? $"raster-brightness:{el.Id}" : null,
            shouldApply: rasterEl => Math.Abs(rasterEl.ProcessingOptions.Brightness - clamped) > 1e-6);
    }

    partial void OnSelectedRasterContrastChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.1, 3.0);
        ApplyLiveSelectedRasterChange(
            coalesceKey: SelectedElement is { } el ? $"raster-contrast:{el.Id}" : null,
            shouldApply: rasterEl => Math.Abs(rasterEl.ProcessingOptions.Contrast - clamped) > 1e-6);
    }

    partial void OnSelectedRasterInvertChanged(bool value)
    {
        ApplyLiveSelectedRasterChange(
            coalesceKey: null,
            shouldApply: rasterEl => rasterEl.ProcessingOptions.Invert != value);
    }

    partial void OnSelectedRasterMonochromeChanged(bool value)
    {
        ApplyLiveSelectedRasterChange(
            coalesceKey: null,
            shouldApply: rasterEl => rasterEl.ProcessingOptions.Monochrome != value);
    }

    partial void OnSelectedRasterThresholdChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 255);
        ApplyLiveSelectedRasterChange(
            coalesceKey: SelectedElement is { } el ? $"raster-threshold:{el.Id}" : null,
            shouldApply: rasterEl => rasterEl.ProcessingOptions.Threshold != clamped);
    }

    partial void OnSelectedRasterDitheringChanged(bool value)
    {
        ApplyLiveSelectedRasterChange(
            coalesceKey: null,
            shouldApply: rasterEl => rasterEl.ProcessingOptions.Dithering != value);
    }

    partial void OnSelectedRasterVectorizeChanged(bool value)
    {
        ApplyLiveSelectedRasterChange(
            coalesceKey: null,
            shouldApply: rasterEl => rasterEl.Vectorize != value);
    }

    partial void OnSelectedRasterVectorizeEpsilonPxChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.2, 10.0);
        ApplyLiveSelectedRasterChange(
            coalesceKey: SelectedElement is { } el ? $"raster-epsilon:{el.Id}" : null,
            shouldApply: rasterEl => Math.Abs(rasterEl.VectorizeEpsilonPx - clamped) > 1e-6);
    }

    private void ApplyLiveSelectedTextChange(
        string? coalesceKey,
        Action<TextPersonalizationElement> mutate,
        Func<TextPersonalizationElement, bool> shouldApply)
    {
        if (_suppressSelectedSync || SelectedElement is not TextPersonalizationElement textEl || !shouldApply(textEl))
        {
            return;
        }

        RecordUndoPoint(coalesceKey);
        mutate(textEl);
        RevectorizeTextElement(textEl);
        CommitMutation();
    }

    private void RevectorizeTextElement(TextPersonalizationElement textEl)
    {
        double prevWidth = textEl.WidthMm;
        double prevHeight = textEl.HeightMm;
        _textVectorizer.VectorizeElement(textEl, updateDimensions: true);
        if (!textEl.PreserveAspectRatio)
        {
            textEl.WidthMm = Math.Max(0.1, prevWidth);
            textEl.HeightMm = Math.Max(0.1, prevHeight);
        }
        Manager.SyncRelativeCoords(textEl);
    }

    private void ApplyLiveSelectedRasterChange(
        string? coalesceKey,
        Func<RasterImagePersonalizationElement, bool> shouldApply)
    {
        if (_suppressSelectedSync || SelectedElement is not RasterImagePersonalizationElement rasterEl || !shouldApply(rasterEl))
        {
            return;
        }

        RecordUndoPoint(coalesceKey);
        ApplyRasterReprocessing(rasterEl);
        CommitMutation();
    }

    partial void OnSelectedShapeKindChanged(GeometricShapeKind value)
    {
        ApplyLiveSelectedShapeChange(
            coalesceKey: null,
            shouldApply: (_, spec) => spec.Kind != value,
            mutate: (shapeEl, spec) =>
            {
                spec.Kind = value;
                shapeEl.PreserveAspectRatio = value is GeometricShapeKind.Circle or GeometricShapeKind.Polygon or GeometricShapeKind.Crosshair;
                if (value == GeometricShapeKind.Crosshair && shapeEl.OperationMode == OperationMode.Cut)
                {
                    shapeEl.OperationMode = OperationMode.Engrave;
                    shapeEl.Operation = CncOperationType.Mark;
                }
            });
    }

    partial void OnSelectedShapeWidthMmChanged(double value)
    {
        double clamped = Math.Max(ShapeGeometryBuilder.MinDimensionMm, double.IsFinite(value) ? value : 25.0);
        ApplyLiveSelectedShapeChange(
            coalesceKey: SelectedElement is { } el ? $"shape-w:{el.Id}" : null,
            shouldApply: (_, spec) => Math.Abs(spec.WidthMm - clamped) > 1e-6,
            mutate: (_, spec) => spec.WidthMm = clamped);
    }

    partial void OnSelectedShapeHeightMmChanged(double value)
    {
        double clamped = Math.Max(ShapeGeometryBuilder.MinDimensionMm, double.IsFinite(value) ? value : 15.0);
        ApplyLiveSelectedShapeChange(
            coalesceKey: SelectedElement is { } el ? $"shape-h:{el.Id}" : null,
            shouldApply: (_, spec) => Math.Abs(spec.HeightMm - clamped) > 1e-6,
            mutate: (_, spec) => spec.HeightMm = clamped);
    }

    partial void OnSelectedShapeDiameterMmChanged(double value)
    {
        double clamped = Math.Max(ShapeGeometryBuilder.MinDimensionMm, double.IsFinite(value) ? value : 10.0);
        ApplyLiveSelectedShapeChange(
            coalesceKey: SelectedElement is { } el ? $"shape-diam:{el.Id}" : null,
            shouldApply: (_, spec) => Math.Abs(spec.DiameterMm - clamped) > 1e-6,
            mutate: (_, spec) => spec.DiameterMm = clamped);
    }

    partial void OnSelectedShapeCornerRadiusMmChanged(double value)
    {
        double nonNeg = Math.Max(0.0, double.IsFinite(value) ? value : 0.0);
        ApplyLiveSelectedShapeChange(
            coalesceKey: SelectedElement is { } el ? $"shape-radius:{el.Id}" : null,
            shouldApply: (_, spec) =>
            {
                double clamped = Math.Clamp(nonNeg, 0.0, Math.Min(spec.WidthMm, spec.HeightMm) * 0.5);
                return Math.Abs(spec.CornerRadiusMm - clamped) > 1e-6;
            },
            mutate: (_, spec) => spec.CornerRadiusMm = nonNeg);
    }

    partial void OnSelectedShapeSidesChanged(int value)
    {
        int clamped = Math.Clamp(value, ShapeGeometryBuilder.MinPolygonSides, ShapeGeometryBuilder.MaxPolygonSides);
        ApplyLiveSelectedShapeChange(
            coalesceKey: SelectedElement is { } el ? $"shape-sides:{el.Id}" : null,
            shouldApply: (_, spec) => spec.Sides != clamped,
            mutate: (_, spec) => spec.Sides = clamped);
    }

    partial void OnSelectedShapeFillHatchChanged(bool value)
    {
        ApplyLiveSelectedShapeChange(
            coalesceKey: null,
            shouldApply: (_, spec) => spec.FillHatch != value,
            mutate: (_, spec) => spec.FillHatch = value);
    }

    partial void OnSelectedShapeHatchSpacingMmChanged(double value)
    {
        double clamped = Math.Clamp(double.IsFinite(value) ? value : ShapeGeometryBuilder.DefaultHatchSpacingMm, 0.1, 10.0);
        ApplyLiveSelectedShapeChange(
            coalesceKey: SelectedElement is { } el ? $"shape-hatch:{el.Id}" : null,
            shouldApply: (_, spec) => Math.Abs(spec.HatchSpacingMm - clamped) > 1e-6,
            mutate: (_, spec) => spec.HatchSpacingMm = clamped);
    }

    private void ApplyLiveSelectedShapeChange(
        string? coalesceKey,
        Func<ShapePersonalizationElement, GeometricShapeSpec, bool> shouldApply,
        Action<ShapePersonalizationElement, GeometricShapeSpec> mutate)
    {
        if (_suppressSelectedSync || SelectedElement is not ShapePersonalizationElement shapeEl || shapeEl.Locked)
        {
            return;
        }

        GeometricShapeSpec spec = shapeEl.ShapeSpec ??= new GeometricShapeSpec();
        if (!shouldApply(shapeEl, spec))
        {
            _suppressSelectedSync = true;
            try
            {
                SyncSelectedTransformProperties(shapeEl);
            }
            finally
            {
                _suppressSelectedSync = false;
            }
            return;
        }

        if (!_isInteractiveTransformActive)
        {
            RecordUndoPoint(coalesceKey);
        }

        OffscreenViewportRenderer.InvalidateElementVectorCache(shapeEl);
        shapeEl.MutateSpec(s => mutate(shapeEl, s), preserveAnchorPosition: true);
        Manager.SyncRelativeCoords(shapeEl);

        if (_isInteractiveTransformActive)
        {
            CommitInteractiveTransformStep(shapeEl);
            return;
        }

        CommitActivePanelMutation();
    }
}
