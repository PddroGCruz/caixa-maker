using System.Numerics;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.App.Editor2D;

/// <summary>Alças e regiões interativas do gizmo CAD 2D ao redor do elemento selecionado.</summary>
public enum Editor2DHandleType
{
    None,
    Body,
    Rotate,
    NorthWest,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
}

/// <summary>Botões de ponteiro suportados no editor CAD 2D.</summary>
public enum Editor2DMouseButton
{
    Left,
    Middle,
    Right,
}

/// <summary>Modos de ferramenta interativa do editor CAD 2D.</summary>
public enum Canvas2DToolMode
{
    Select,
    DrawRectangle,
    DrawCircle,
    DrawSlot,
    DrawPolygon,
    DrawCrosshair,
}

/// <summary>Contorno real da chapa convertido para o sistema de coordenadas 2D da superfície ativa.</summary>
public sealed record SurfaceContour2D(
    Polygon2D OuterContour,
    IReadOnlyList<Polygon2D> InternalHoles,
    IReadOnlyList<Circle2D> CircularHoles,
    BoundingBox2D Bounds);

/// <summary>
/// Controlador CAD 2D desacoplado de UI: gerencia escala em mm, zoom no cursor, pan,
/// mapeamento do contorno real da chapa, hit-testing OBB com rotação, gizmo de 8 alças + rotação,
/// snap magnético no centro e commit atômico de 1 passo na pilha Undo/Redo.
/// </summary>
public sealed class SurfaceCanvas2DController : IDisposable
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 8.0;
    public const double SnapThresholdMm = 1.5;
    public const double RotateHandleOffsetPx = 22.0;
    public const double HandleHitRadiusPx = 8.5;
    public const double MinElementSizeMm = 3.0;
    public const double RulerThicknessPx = 18.0;
    private const double FitPaddingPx = 28.0;

    private enum ActiveDragMode
    {
        None,
        Move,
        Resize,
        Rotate,
        Pan,
        DrawShape,
        Marquee,
        MoveGroup,
    }

    private ActiveDragMode _dragMode = ActiveDragMode.None;
    private string? _dragElementId;
    private Editor2DHandleType _dragHandle = Editor2DHandleType.None;
    private PersonalizationHistoryEntry? _preDragCheckpoint;

    private double _dragStartPxX;
    private double _dragStartPxY;
    private double _dragStartMmX;
    private double _dragStartMmY;
    private double _dragOrigPanX;
    private double _dragOrigPanY;

    private double _origX;
    private double _origY;
    private double _origW;
    private double _origH;
    private double _origCenterX;
    private double _origCenterY;
    private double _origRotDeg;
    private bool _origKeepAspect;
    private double _rotateStartAngleRad;

    private GeometricShapeKind _drawKind;
    private double _drawStartMmX;
    private double _drawStartMmY;
    private double _drawCurrentMmX;
    private double _drawCurrentMmY;
    private double _maxDrawDistanceMm;
    private string? _prevSelectedIdBeforeDraw;

    private bool _marqueeShiftHeld;
    private readonly List<(string Id, double OrigX, double OrigY)> _groupDragBaselines = new();
    private readonly List<(string Id, double X, double Y)> _groupMoveWorkBuffer = new();
    private readonly HashSet<string> _groupSelectedIdSet = new(StringComparer.Ordinal);
    private BoundingBox2D _groupOrigBounds;
    private string? _pendingCollapseElementId;
    private double _maxGroupDragDistMm;

    public double ViewportWidthPx { get; private set; } = 640.0;
    public double ViewportHeightPx { get; private set; } = 400.0;
    public double UserZoom { get; private set; } = 1.0;
    public double PanPxX { get; private set; }
    public double PanPxY { get; private set; }
    public double FitScalePxPerMm { get; private set; } = 2.0;
    public double EffectiveScalePxPerMm => Math.Max(1e-4, FitScalePxPerMm * UserZoom);
    public double OriginPxX { get; private set; }
    public double OriginPxY { get; private set; }

    public Canvas2DToolMode ActiveToolMode { get; set; } = Canvas2DToolMode.Select;
    public bool SnapGuideXActive { get; private set; }
    public bool SnapGuideYActive { get; private set; }
    public double? SnapGuideX { get; private set; }
    public double? SnapGuideY { get; private set; }
    public bool IsDragging => _dragMode != ActiveDragMode.None;
    public bool IsDrawingShape => _dragMode == ActiveDragMode.DrawShape;
    public bool IsMarqueeSelecting => _dragMode == ActiveDragMode.Marquee;
    public bool IsDraggingGroup => _dragMode == ActiveDragMode.MoveGroup;
    public Point2D MarqueeStartMm { get; private set; }
    public Point2D MarqueeCurrentMm { get; private set; }
    public double GroupDragDeltaXMm { get; private set; }
    public double GroupDragDeltaYMm { get; private set; }
    public string? ActiveDrawingElementId => _dragMode == ActiveDragMode.DrawShape ? _dragElementId : null;
    public int StaticRenderCacheRebuildCount { get; internal set; }
    public Editor2DHandleType ActiveHandle => _dragHandle;
    public Editor2DHandleType HoveredHandle { get; private set; } = Editor2DHandleType.None;
    public double LastPointerMmX { get; private set; }
    public double LastPointerMmY { get; private set; }

    internal IDisposable? StaticRenderCache { get; set; }

    public void Dispose()
    {
        StaticRenderCache?.Dispose();
        StaticRenderCache = null;
    }

    public void UpdateLayout(
        SurfaceDefinition surface,
        double viewportWidthPx,
        double viewportHeightPx,
        SurfaceContour2D? contour = null)
    {
        ArgumentNullException.ThrowIfNull(surface);

        ViewportWidthPx = Math.Max(60.0, viewportWidthPx);
        ViewportHeightPx = Math.Max(60.0, viewportHeightPx);

        BoundingBox2D ext = ComputeCombinedBounds(surface, contour);
        double spanW = Math.Max(1.0, ext.Width);
        double spanH = Math.Max(1.0, ext.Height);

        double availW = Math.Max(40.0, ViewportWidthPx - RulerThicknessPx - FitPaddingPx * 1.6);
        double availH = Math.Max(40.0, ViewportHeightPx - RulerThicknessPx - FitPaddingPx * 1.6);
        FitScalePxPerMm = Math.Max(0.1, Math.Min(availW / spanW, availH / spanH));

        double scale = EffectiveScalePxPerMm;
        OriginPxX = RulerThicknessPx + ((ViewportWidthPx - RulerThicknessPx) - (ext.MinX + ext.MaxX) * scale) * 0.5 + PanPxX;
        OriginPxY = RulerThicknessPx + ((ViewportHeightPx - RulerThicknessPx) - (ext.MinY + ext.MaxY) * scale) * 0.5 + PanPxY;
    }

    public void FitToView(
        SurfaceDefinition surface,
        double viewportWidthPx,
        double viewportHeightPx,
        SurfaceContour2D? contour = null)
    {
        UserZoom = 1.0;
        PanPxX = 0.0;
        PanPxY = 0.0;
        UpdateLayout(surface, viewportWidthPx, viewportHeightPx, contour);
    }

    public void ZoomAtPoint(
        SurfaceDefinition surface,
        double factor,
        double anchorPxX,
        double anchorPxY,
        SurfaceContour2D? contour = null)
    {
        ArgumentNullException.ThrowIfNull(surface);

        (double mmX, double mmY) = PxToMm(anchorPxX, anchorPxY);
        double nextZoom = Math.Clamp(UserZoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(nextZoom - UserZoom) < 1e-6)
        {
            return;
        }

        UserZoom = nextZoom;
        UpdateLayout(surface, ViewportWidthPx, ViewportHeightPx, contour);

        // Ajusta o pan para manter o ponto em mm exatamente sob o cursor
        (double newPxX, double newPxY) = MmToPx(mmX, mmY);
        PanPxX += anchorPxX - newPxX;
        PanPxY += anchorPxY - newPxY;
        UpdateLayout(surface, ViewportWidthPx, ViewportHeightPx, contour);
    }

    public void PanBy(double deltaPxX, double deltaPxY)
    {
        PanPxX += deltaPxX;
        PanPxY += deltaPxY;
        OriginPxX += deltaPxX;
        OriginPxY += deltaPxY;
    }

    public (double PxX, double PxY) MmToPx(double mmX, double mmY)
    {
        double scale = EffectiveScalePxPerMm;
        return (OriginPxX + mmX * scale, OriginPxY + mmY * scale);
    }

    public (double MmX, double MmY) PxToMm(double pxX, double pxY)
    {
        double scale = EffectiveScalePxPerMm;
        return ((pxX - OriginPxX) / scale, (pxY - OriginPxY) / scale);
    }

    /// <summary>
    /// Converte o <see cref="PanelGeometry2D"/> (coordenadas SVG da folha) para o plano 2D de <see cref="SurfaceDefinition"/>
    /// preservando dentes Finger Joint, orelha de dobradiça, furos circulares e rasgos internos.
    /// </summary>
    public static SurfaceContour2D MapPanelContourToSurface(
        PanelGeometry2D panel,
        SurfaceDefinition surface)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(surface);

        double s = BoxDrawer.StartingCoordinate;

        Point2D MapPoint(Point2D p)
        {
            double lx = p.X - s;
            double ly = p.Y - s;

            return surface.PanelId switch
            {
                SurfaceId.Left => new Point2D(surface.WidthMm - lx, ly),
                SurfaceId.Lid => new Point2D(surface.WidthMm - lx, surface.HeightMm - ly),
                _ => new Point2D(lx, ly),
            };
        }

        Polygon2D outer = panel.OuterContour.Transform(MapPoint);
        Polygon2D[] holes = panel.InternalHoles.Select(h => h.Transform(MapPoint)).ToArray();
        Circle2D[] circles = panel.CircularHoles
            .Select(c =>
            {
                Point2D mc = MapPoint(new Point2D(c.CenterX, c.CenterY));
                return new Circle2D(mc.X, mc.Y, c.Radius);
            })
            .ToArray();

        return new SurfaceContour2D(outer, holes, circles, outer.Bounds);
    }

    /// <summary>Retorna as coordenadas (em mm no espaço da chapa) das 8 alças de resize + alça de rotação.</summary>
    public IReadOnlyDictionary<Editor2DHandleType, Point2D> GetHandlesMm(PersonalizationElement el)
    {
        ArgumentNullException.ThrowIfNull(el);

        Point2D center = el.CenterMm();
        double hw = el.WidthMm * 0.5;
        double hh = el.HeightMm * 0.5;
        double rotOffsetMm = RotateHandleOffsetPx / Math.Max(0.2, EffectiveScalePxPerMm);

        var localHandles = new (Editor2DHandleType Handle, double Lx, double Ly)[]
        {
            (Editor2DHandleType.NorthWest, -hw, -hh),
            (Editor2DHandleType.North, 0.0, -hh),
            (Editor2DHandleType.NorthEast, hw, -hh),
            (Editor2DHandleType.East, hw, 0.0),
            (Editor2DHandleType.SouthEast, hw, hh),
            (Editor2DHandleType.South, 0.0, hh),
            (Editor2DHandleType.SouthWest, -hw, hh),
            (Editor2DHandleType.West, -hw, 0.0),
            (Editor2DHandleType.Rotate, 0.0, -hh - rotOffsetMm),
        };

        double rad = el.RotationDeg * Math.PI / 180.0;
        double cosA = Math.Cos(rad);
        double sinA = Math.Sin(rad);

        var map = new Dictionary<Editor2DHandleType, Point2D>(localHandles.Length);
        foreach ((Editor2DHandleType handle, double lx, double ly) in localHandles)
        {
            double wx = center.X + lx * cosA - ly * sinA;
            double wy = center.Y + lx * sinA + ly * cosA;
            map[handle] = new Point2D(wx, wy);
        }

        return map;
    }

    public Editor2DHandleType HitTestHandle(PersonalizationElement? selectedElement, double pxX, double pxY)
    {
        if (selectedElement is null || !selectedElement.Visible)
        {
            return Editor2DHandleType.None;
        }

        IReadOnlyDictionary<Editor2DHandleType, Point2D> handles = GetHandlesMm(selectedElement);
        Editor2DHandleType best = Editor2DHandleType.None;
        double bestDistSq = double.PositiveInfinity;

        foreach ((Editor2DHandleType type, Point2D mmPos) in handles)
        {
            (double hx, double hy) = MmToPx(mmPos.X, mmPos.Y);
            double dx = pxX - hx;
            double dy = pxY - hy;
            double distSq = dx * dx + dy * dy;
            double hitRadius = type == Editor2DHandleType.Rotate ? HandleHitRadiusPx + 2.0 : HandleHitRadiusPx;
            if (distSq <= hitRadius * hitRadius && distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = type;
            }
        }

        return best;
    }

    public PersonalizationElement? HitTestElement(
        IEnumerable<PersonalizationElement> elements,
        double mmX,
        double mmY)
    {
        ArgumentNullException.ThrowIfNull(elements);

        double hitPadMm = 2.0 / Math.Max(0.2, EffectiveScalePxPerMm);
        PersonalizationElement[] ordered = elements as PersonalizationElement[] ?? elements.ToArray();

        for (int i = ordered.Length - 1; i >= 0; i--)
        {
            PersonalizationElement el = ordered[i];
            if (!el.Visible) continue;

            Point2D center = el.CenterMm();
            double dx = mmX - center.X;
            double dy = mmY - center.Y;
            double rad = -el.RotationDeg * Math.PI / 180.0;
            double cosA = Math.Cos(rad);
            double sinA = Math.Sin(rad);
            double lx = dx * cosA - dy * sinA;
            double ly = dx * sinA + dy * cosA;

            double hw = el.WidthMm * 0.5 + hitPadMm;
            double hh = el.HeightMm * 0.5 + hitPadMm;
            if (Math.Abs(lx) <= hw && Math.Abs(ly) <= hh)
            {
                return el;
            }
        }

        return null;
    }

    /// <summary>Processa início de clique ou arraste CAD em coordenadas de pixel do canvas.</summary>
    public Editor2DHandleType OnPointerPressed(
        PersonalizationWorkspaceViewModel vm,
        double pxX,
        double pxY,
        Editor2DMouseButton button = Editor2DMouseButton.Left,
        bool spaceHeld = false,
        bool shiftHeld = false)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _ = shiftHeld;

        (double mmX, double mmY) = PxToMm(pxX, pxY);
        LastPointerMmX = mmX;
        LastPointerMmY = mmY;

        if (button == Editor2DMouseButton.Middle || (button == Editor2DMouseButton.Left && spaceHeld))
        {
            _dragMode = ActiveDragMode.Pan;
            _dragHandle = Editor2DHandleType.None;
            _dragStartPxX = pxX;
            _dragStartPxY = pxY;
            _dragOrigPanX = PanPxX;
            _dragOrigPanY = PanPxY;
            return Editor2DHandleType.None;
        }

        if (button == Editor2DMouseButton.Right)
        {
            if (IsDrawingShape || ResolveEffectiveToolMode(vm) != Canvas2DToolMode.Select)
            {
                CancelActiveDrawing(vm);
            }
            else if (IsMarqueeSelecting)
            {
                CancelActiveMarquee();
            }
            return Editor2DHandleType.None;
        }

        if (button != Editor2DMouseButton.Left)
        {
            return Editor2DHandleType.None;
        }

        Canvas2DToolMode effectiveTool = ResolveEffectiveToolMode(vm);
        if (effectiveTool != Canvas2DToolMode.Select)
        {
            BeginShapeDrawDrag(vm, effectiveTool, pxX, pxY, mmX, mmY);
            return Editor2DHandleType.Body;
        }

        // 1. Verifica alças do elemento único já selecionado na face ativa (desabilitado durante seleção múltipla ou Shift+Clique)
        PersonalizationElement? currentSelected = vm.SelectedElement;
        if (!shiftHeld &&
            !vm.HasMultipleSelection &&
            currentSelected is not null &&
            currentSelected.Visible &&
            currentSelected.PanelId == vm.ActiveSurfaceId)
        {
            Editor2DHandleType handle = HitTestHandle(currentSelected, pxX, pxY);
            if (handle != Editor2DHandleType.None)
            {
                HoveredHandle = handle;
                if (currentSelected.Locked)
                {
                    return handle;
                }

                CaptureDragBaseline(vm, currentSelected, pxX, pxY, mmX, mmY);
                _dragHandle = handle;

                if (handle == Editor2DHandleType.Rotate)
                {
                    _dragMode = ActiveDragMode.Rotate;
                    _rotateStartAngleRad = Math.Atan2(mmY - _origCenterY, mmX - _origCenterX);
                }
                else
                {
                    _dragMode = ActiveDragMode.Resize;
                }

                return handle;
            }
        }

        // 2. Hit-test OBB dos elementos visíveis na superfície ativa
        PersonalizationElement? hit = HitTestElement(vm.ElementsOnActiveSurface, mmX, mmY);
        if (hit is not null)
        {
            if (shiftHeld)
            {
                vm.ToggleElementSelection(hit.Id);
                HoveredHandle = Editor2DHandleType.Body;
                return Editor2DHandleType.Body;
            }

            if (vm.HasMultipleSelection && vm.IsElementSelected(hit.Id))
            {
                BeginGroupMoveDrag(vm, hit.Id, pxX, pxY, mmX, mmY);
                HoveredHandle = Editor2DHandleType.Body;
                return Editor2DHandleType.Body;
            }

            vm.SelectSingleElement(hit.Id);
            if (!hit.Locked)
            {
                CaptureDragBaseline(vm, hit, pxX, pxY, mmX, mmY);
                _dragMode = ActiveDragMode.Move;
                _dragHandle = Editor2DHandleType.Body;
                HoveredHandle = Editor2DHandleType.Body;
                return Editor2DHandleType.Body;
            }

            HoveredHandle = Editor2DHandleType.Body;
            return Editor2DHandleType.Body;
        }

        // 3. Clique no fundo vazio inicia caixa de seleção Marquee
        _dragMode = ActiveDragMode.Marquee;
        _dragHandle = Editor2DHandleType.None;
        _marqueeShiftHeld = shiftHeld;
        _dragStartPxX = pxX;
        _dragStartPxY = pxY;
        _dragStartMmX = mmX;
        _dragStartMmY = mmY;
        MarqueeStartMm = new Point2D(mmX, mmY);
        MarqueeCurrentMm = new Point2D(mmX, mmY);
        HoveredHandle = Editor2DHandleType.None;
        return Editor2DHandleType.None;
    }

    /// <summary>Atualiza movimento contínuo sem poluir a pilha Undo/Redo.</summary>
    public bool OnPointerMoved(
        PersonalizationWorkspaceViewModel vm,
        double pxX,
        double pxY,
        bool shiftHeld = false,
        bool altHeld = false)
    {
        ArgumentNullException.ThrowIfNull(vm);

        (double mmX, double mmY) = PxToMm(pxX, pxY);
        LastPointerMmX = mmX;
        LastPointerMmY = mmY;

        if (_dragMode == ActiveDragMode.None)
        {
            Canvas2DToolMode idleTool = ResolveEffectiveToolMode(vm);
            if (idleTool != Canvas2DToolMode.Select)
            {
                HoveredHandle = Editor2DHandleType.None;
                return false;
            }

            PersonalizationElement? sel = vm.SelectedElement;
            if (!vm.HasMultipleSelection && sel is not null && sel.Visible && sel.PanelId == vm.ActiveSurfaceId)
            {
                Editor2DHandleType h = HitTestHandle(sel, pxX, pxY);
                if (h != Editor2DHandleType.None)
                {
                    HoveredHandle = h;
                    return false;
                }
            }

            HoveredHandle = HitTestElement(vm.ElementsOnActiveSurface, mmX, mmY) is not null
                ? Editor2DHandleType.Body
                : Editor2DHandleType.None;
            return false;
        }

        if (_dragMode == ActiveDragMode.Pan)
        {
            double nextPanX = _dragOrigPanX + (pxX - _dragStartPxX);
            double nextPanY = _dragOrigPanY + (pxY - _dragStartPxY);
            PanBy(nextPanX - PanPxX, nextPanY - PanPxY);
            return true;
        }

        if (_dragMode == ActiveDragMode.Marquee)
        {
            MarqueeCurrentMm = new Point2D(mmX, mmY);
            return true;
        }

        if (_dragMode == ActiveDragMode.MoveGroup)
        {
            ApplyGroupMoveDrag(vm, mmX, mmY);
            return true;
        }

        PersonalizationElement? el = _dragElementId is not null &&
                                     vm.Manager.TryGetElement(_dragElementId, out PersonalizationElement? found)
            ? found
            : vm.SelectedElement;
        if (el is null || el.Locked)
        {
            return false;
        }

        switch (_dragMode)
        {
            case ActiveDragMode.Move:
                ApplyMoveDrag(vm, mmX, mmY);
                return true;

            case ActiveDragMode.Resize:
                ApplyResizeDrag(vm, el, mmX, mmY, shiftHeld);
                return true;

            case ActiveDragMode.Rotate:
                ApplyRotateDrag(vm, mmX, mmY, shiftHeld);
                return true;

            case ActiveDragMode.DrawShape when el is ShapePersonalizationElement shapeEl:
                ApplyDrawShapeMove(vm, shapeEl, mmX, mmY, shiftHeld, altHeld);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Cancela o desenho ativo (Esc / Botão Direito) sem gravar entrada no Undo/Redo e retorna ao modo Seleção.</summary>
    public bool CancelActiveDrawing(PersonalizationWorkspaceViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        bool wasDrawing = _dragMode == ActiveDragMode.DrawShape;
        string? drawingId = wasDrawing ? _dragElementId : null;
        string? prevSelected = _prevSelectedIdBeforeDraw;

        if (wasDrawing)
        {
            _dragMode = ActiveDragMode.None;
            _dragHandle = Editor2DHandleType.None;
            _dragElementId = null;
            _preDragCheckpoint = null;
            _prevSelectedIdBeforeDraw = null;
            SnapGuideXActive = false;
            SnapGuideYActive = false;
            SnapGuideX = null;
            SnapGuideY = null;

            vm.CancelInteractiveShapeDraw(drawingId, prevSelected);
        }

        ActiveToolMode = Canvas2DToolMode.Select;
        if (vm.ActiveToolMode != Canvas2DToolMode.Select)
        {
            vm.SetActiveToolMode(Canvas2DToolMode.Select);
        }

        return wasDrawing;
    }

    /// <summary>Cancela a caixa marquee ativa (Esc / Botão Direito) sem alterar a seleção existente.</summary>
    public bool CancelActiveMarquee()
    {
        if (_dragMode != ActiveDragMode.Marquee)
        {
            return false;
        }

        _dragMode = ActiveDragMode.None;
        _dragHandle = Editor2DHandleType.None;
        _marqueeShiftHeld = false;
        SnapGuideXActive = false;
        SnapGuideYActive = false;
        SnapGuideX = null;
        SnapGuideY = null;
        return true;
    }

    /// <summary>Finaliza a manipulação e grava exatamente 1 entrada atômica no histórico Undo/Redo.</summary>
    public bool OnPointerReleased(PersonalizationWorkspaceViewModel vm, bool? shiftHeld = null)
    {
        ArgumentNullException.ThrowIfNull(vm);

        ActiveDragMode finishedMode = _dragMode;
        PersonalizationHistoryEntry? checkpoint = _preDragCheckpoint;
        string? draggedId = _dragElementId;

        _dragMode = ActiveDragMode.None;
        _dragHandle = Editor2DHandleType.None;
        _dragElementId = null;
        _preDragCheckpoint = null;
        _prevSelectedIdBeforeDraw = null;
        SnapGuideXActive = false;
        SnapGuideYActive = false;
        SnapGuideX = null;
        SnapGuideY = null;

        if (finishedMode == ActiveDragMode.Marquee)
        {
            return FinalizeMarqueeRelease(vm, shiftHeld ?? _marqueeShiftHeld);
        }

        if (finishedMode == ActiveDragMode.MoveGroup)
        {
            return FinalizeGroupMoveRelease(vm);
        }

        if (finishedMode == ActiveDragMode.DrawShape)
        {
            if (draggedId is not null &&
                vm.Manager.TryGetElement(draggedId, out PersonalizationElement? created) &&
                created is ShapePersonalizationElement drawnShape)
            {
                FinalizeDrawShapeRelease(vm, drawnShape);
            }

            ActiveToolMode = Canvas2DToolMode.Select;
            vm.SetActiveToolMode(Canvas2DToolMode.Select);
            return vm.EndInteractiveTransform(recordUndoIfChanged: true);
        }

        if (finishedMode is ActiveDragMode.Move or ActiveDragMode.Resize or ActiveDragMode.Rotate)
        {
            if (draggedId is not null &&
                vm.Manager.TryGetElement(draggedId, out PersonalizationElement? current) &&
                current is not null)
            {
                // Restaura PreserveAspectRatio caso tenha sido mantido durante o resize livre/proporcional
                current.PreserveAspectRatio = _origKeepAspect;
            }

            bool committed = vm.EndInteractiveTransform(recordUndoIfChanged: true);
            if (!committed && checkpoint is not null && draggedId is not null &&
                vm.Manager.TryGetElement(draggedId, out PersonalizationElement? cur) &&
                cur is not null)
            {
                bool fallbackChanged =
                    Math.Abs(cur.XMm - _origX) > 0.01 ||
                    Math.Abs(cur.YMm - _origY) > 0.01 ||
                    Math.Abs(cur.WidthMm - _origW) > 0.01 ||
                    Math.Abs(cur.HeightMm - _origH) > 0.01 ||
                    Math.Abs(cur.RotationDeg - _origRotDeg) > 0.05;
                if (fallbackChanged)
                {
                    vm.PushHistoryCheckpoint(checkpoint);
                    return true;
                }
            }

            return committed;
        }

        return false;
    }

    private void CaptureDragBaseline(
        PersonalizationWorkspaceViewModel vm,
        PersonalizationElement el,
        double pxX,
        double pxY,
        double mmX,
        double mmY)
    {
        _preDragCheckpoint = vm.CaptureHistoryCheckpoint();
        vm.BeginInteractiveTransform(_preDragCheckpoint);
        _dragElementId = el.Id;
        _dragStartPxX = pxX;
        _dragStartPxY = pxY;
        _dragStartMmX = mmX;
        _dragStartMmY = mmY;
        _origX = el.XMm;
        _origY = el.YMm;
        _origW = el.WidthMm;
        _origH = el.HeightMm;
        Point2D center = el.CenterMm();
        _origCenterX = center.X;
        _origCenterY = center.Y;
        _origRotDeg = el.RotationDeg;
        _origKeepAspect = el.PreserveAspectRatio;
    }

    private void ApplyMoveDrag(PersonalizationWorkspaceViewModel vm, double mmX, double mmY)
    {
        double dxMm = mmX - _dragStartMmX;
        double dyMm = mmY - _dragStartMmY;
        double nextX = _origX + dxMm;
        double nextY = _origY + dyMm;

        bool snappedX = false;
        bool snappedY = false;
        double? guideX = null;
        double? guideY = null;

        if (vm.ActiveSurface is SurfaceDefinition surf)
        {
            Rect2D safe = surf.SafeAreaForMargin(vm.SafetyMarginMm);
            Point2D safeCenter = safe.Center;
            double curCx = nextX + _origW * 0.5;
            double curCy = nextY + _origH * 0.5;

            if (Math.Abs(curCx - safeCenter.X) <= SnapThresholdMm)
            {
                nextX = safeCenter.X - _origW * 0.5;
                snappedX = true;
                guideX = safeCenter.X;
            }

            if (Math.Abs(curCy - safeCenter.Y) <= SnapThresholdMm)
            {
                nextY = safeCenter.Y - _origH * 0.5;
                snappedY = true;
                guideY = safeCenter.Y;
            }
        }

        SnapGuideXActive = snappedX;
        SnapGuideYActive = snappedY;
        SnapGuideX = guideX;
        SnapGuideY = guideY;

        vm.UpdateSelectedElementTransform(
            xMm: Math.Round(nextX, 2),
            yMm: Math.Round(nextY, 2),
            recordUndo: false);
    }

    private Canvas2DToolMode ResolveEffectiveToolMode(PersonalizationWorkspaceViewModel vm)
        => vm.ActiveToolMode != Canvas2DToolMode.Select ? vm.ActiveToolMode : ActiveToolMode;

    private static GeometricShapeKind MapToolToShapeKind(Canvas2DToolMode mode) => mode switch
    {
        Canvas2DToolMode.DrawCircle => GeometricShapeKind.Circle,
        Canvas2DToolMode.DrawSlot => GeometricShapeKind.Slot,
        Canvas2DToolMode.DrawPolygon => GeometricShapeKind.Polygon,
        Canvas2DToolMode.DrawCrosshair => GeometricShapeKind.Crosshair,
        _ => GeometricShapeKind.Rectangle,
    };

    private (double X, double Y, double? GuideX, double? GuideY) SnapCoordinate(
        PersonalizationWorkspaceViewModel vm,
        double rawX,
        double rawY,
        string? excludeElementId)
    {
        double bestDistX = SnapThresholdMm + 1e-6;
        double bestDistY = SnapThresholdMm + 1e-6;
        double? snapX = null;
        double? snapY = null;

        void ConsiderX(double candidate)
        {
            double dist = Math.Abs(rawX - candidate);
            if (dist <= SnapThresholdMm && dist < bestDistX)
            {
                bestDistX = dist;
                snapX = candidate;
            }
        }

        void ConsiderY(double candidate)
        {
            double dist = Math.Abs(rawY - candidate);
            if (dist <= SnapThresholdMm && dist < bestDistY)
            {
                bestDistY = dist;
                snapY = candidate;
            }
        }

        ConsiderX(0.0);
        ConsiderY(0.0);

        if (vm.ActiveSurface is SurfaceDefinition surf)
        {
            Rect2D safe = surf.SafeAreaForMargin(vm.SafetyMarginMm);
            ConsiderX(safe.Center.X);
            ConsiderY(safe.Center.Y);
        }

        foreach (PersonalizationElement other in vm.ElementsOnActiveSurface)
        {
            if (!other.Visible || string.Equals(other.Id, excludeElementId, StringComparison.Ordinal))
            {
                continue;
            }

            Point2D c = other.CenterMm();
            ConsiderX(other.XMm);
            ConsiderX(c.X);
            ConsiderX(other.XMm + other.WidthMm);

            ConsiderY(other.YMm);
            ConsiderY(c.Y);
            ConsiderY(other.YMm + other.HeightMm);
        }

        return (snapX ?? rawX, snapY ?? rawY, snapX, snapY);
    }

    private void BeginShapeDrawDrag(
        PersonalizationWorkspaceViewModel vm,
        Canvas2DToolMode tool,
        double pxX,
        double pxY,
        double mmX,
        double mmY)
    {
        (double startX, double startY, double? snapX, double? snapY) = SnapCoordinate(vm, mmX, mmY, excludeElementId: null);
        SnapGuideX = snapX;
        SnapGuideY = snapY;
        SnapGuideXActive = snapX.HasValue;
        SnapGuideYActive = snapY.HasValue;

        GeometricShapeKind kind = MapToolToShapeKind(tool);
        PersonalizationHistoryEntry checkpoint = vm.CaptureHistoryCheckpoint();
        _prevSelectedIdBeforeDraw = vm.SelectedElementId;

        var spec = new GeometricShapeSpec
        {
            Kind = kind,
            WidthMm = 1.0,
            HeightMm = 1.0,
            DiameterMm = 1.0,
            CornerRadiusMm = 0.0,
            Sides = 6,
        };
        OperationMode defaultMode = ShapePersonalizationElement.DefaultOperationModeForKind(kind);
        CncOperationType defaultOp = ShapePersonalizationElement.DefaultCncOperationForKind(kind, defaultMode, spec.FillHatch);
        var shape = new ShapePersonalizationElement(spec, defaultMode, defaultOp)
        {
            PanelId = vm.ActiveSurfaceId,
            Anchor = AnchorMode.Center,
            Name = ShapePersonalizationElement.DefaultDisplayNameForSpec(spec),
        };

        vm.Manager.AddElement(
            shape,
            xMm: startX - shape.WidthMm * 0.5,
            yMm: startY - shape.HeightMm * 0.5,
            centerOnSurface: false);

        vm.BeginInteractiveShapeDraw(shape, checkpoint);

        ActiveToolMode = tool;
        _dragMode = ActiveDragMode.DrawShape;
        _dragHandle = Editor2DHandleType.None;
        _dragElementId = shape.Id;
        _preDragCheckpoint = checkpoint;
        _dragStartPxX = pxX;
        _dragStartPxY = pxY;
        _dragStartMmX = startX;
        _dragStartMmY = startY;
        _drawKind = kind;
        _drawStartMmX = startX;
        _drawStartMmY = startY;
        _drawCurrentMmX = startX;
        _drawCurrentMmY = startY;
        _maxDrawDistanceMm = 0.0;
    }

    private void ApplyDrawShapeMove(
        PersonalizationWorkspaceViewModel vm,
        ShapePersonalizationElement shape,
        double rawMmX,
        double rawMmY,
        bool shiftHeld,
        bool altHeld)
    {
        (double curX, double curY, double? snapX, double? snapY) = SnapCoordinate(vm, rawMmX, rawMmY, shape.Id);
        SnapGuideX = snapX;
        SnapGuideY = snapY;
        SnapGuideXActive = snapX.HasValue;
        SnapGuideYActive = snapY.HasValue;

        _drawCurrentMmX = curX;
        _drawCurrentMmY = curY;
        _maxDrawDistanceMm = Math.Max(_maxDrawDistanceMm, double.Hypot(curX - _drawStartMmX, curY - _drawStartMmY));

        CaixaMaker.App.Rendering.OffscreenViewportRenderer.InvalidateElementVectorCache(shape);

        switch (_drawKind)
        {
            case GeometricShapeKind.Rectangle:
            case GeometricShapeKind.Slot:
            {
                double dx = curX - _drawStartMmX;
                double dy = curY - _drawStartMmY;
                double w;
                double h;
                double minX;
                double minY;

                if (altHeld)
                {
                    double halfW = Math.Abs(dx);
                    double halfH = Math.Abs(dy);
                    if (shiftHeld && _drawKind == GeometricShapeKind.Rectangle)
                    {
                        double side = Math.Max(halfW, halfH);
                        halfW = side;
                        halfH = side;
                    }

                    w = Math.Max(1.0, Math.Round(halfW * 2.0, 1));
                    h = Math.Max(1.0, Math.Round(halfH * 2.0, 1));
                    minX = _drawStartMmX - w * 0.5;
                    minY = _drawStartMmY - h * 0.5;
                }
                else
                {
                    double absW = Math.Abs(dx);
                    double absH = Math.Abs(dy);
                    if (shiftHeld && _drawKind == GeometricShapeKind.Rectangle)
                    {
                        double side = Math.Max(absW, absH);
                        absW = side;
                        absH = side;
                    }

                    w = Math.Max(1.0, Math.Round(absW, 1));
                    h = Math.Max(1.0, Math.Round(absH, 1));
                    minX = dx >= 0 ? _drawStartMmX : _drawStartMmX - w;
                    minY = dy >= 0 ? _drawStartMmY : _drawStartMmY - h;
                }

                if (_drawKind == GeometricShapeKind.Rectangle)
                {
                    shape.RotationDeg = 0.0;
                    shape.MutateSpec(s =>
                    {
                        s.WidthMm = w;
                        s.HeightMm = h;
                    }, preserveAnchorPosition: false);
                    vm.Manager.MoveElement(shape.Id, Math.Round(minX, 2), Math.Round(minY, 2));
                }
                else
                {
                    double major = Math.Max(w, h);
                    double minor = Math.Min(w, h);
                    double cx = minX + w * 0.5;
                    double cy = minY + h * 0.5;
                    shape.RotationDeg = h > w ? 90.0 : 0.0;
                    shape.MutateSpec(s =>
                    {
                        s.WidthMm = major;
                        s.HeightMm = minor;
                    }, preserveAnchorPosition: false);
                    vm.Manager.MoveElement(
                        shape.Id,
                        Math.Round(cx - shape.WidthMm * 0.5, 2),
                        Math.Round(cy - shape.HeightMm * 0.5, 2));
                }
                break;
            }

            case GeometricShapeKind.Circle:
            case GeometricShapeKind.Polygon:
            case GeometricShapeKind.Crosshair:
            {
                double radius = double.Hypot(curX - _drawStartMmX, curY - _drawStartMmY);
                double diameter = Math.Max(1.0, Math.Round(radius * 2.0, 1));
                shape.MutateSpec(s => s.DiameterMm = diameter, preserveAnchorPosition: false);
                vm.Manager.MoveElement(
                    shape.Id,
                    Math.Round(_drawStartMmX - shape.WidthMm * 0.5, 2),
                    Math.Round(_drawStartMmY - shape.HeightMm * 0.5, 2));
                break;
            }
        }

        shape.Name = ShapePersonalizationElement.DefaultDisplayNameForSpec(shape.ShapeSpec ?? new GeometricShapeSpec());
        vm.NotifyInteractiveShapeDrawStep(shape);
    }

    private void FinalizeDrawShapeRelease(PersonalizationWorkspaceViewModel vm, ShapePersonalizationElement shape)
    {
        double distMm = double.Hypot(_drawCurrentMmX - _drawStartMmX, _drawCurrentMmY - _drawStartMmY);
        if (distMm < 1.0 && _maxDrawDistanceMm < 1.0)
        {
            CaixaMaker.App.Rendering.OffscreenViewportRenderer.InvalidateElementVectorCache(shape);
            shape.RotationDeg = 0.0;
            shape.MutateSpec(s =>
            {
                s.WidthMm = 25.0;
                s.HeightMm = 15.0;
                s.DiameterMm = 10.0;
                s.CornerRadiusMm = 0.0;
                s.Sides = 6;
            }, preserveAnchorPosition: false);

            vm.Manager.MoveElement(
                shape.Id,
                Math.Round(_drawStartMmX - shape.WidthMm * 0.5, 2),
                Math.Round(_drawStartMmY - shape.HeightMm * 0.5, 2));
        }

        shape.Name = ShapePersonalizationElement.DefaultDisplayNameForSpec(shape.ShapeSpec ?? new GeometricShapeSpec());
        vm.SelectElementAfterInteractiveDraw(shape.Id);
    }

    private void ApplyResizeDrag(
        PersonalizationWorkspaceViewModel vm,
        PersonalizationElement el,
        double mmX,
        double mmY,
        bool shiftHeld)
    {
        double dxWorld = mmX - _dragStartMmX;
        double dyWorld = mmY - _dragStartMmY;

        double rad = _origRotDeg * Math.PI / 180.0;
        double cosA = Math.Cos(rad);
        double sinA = Math.Sin(rad);

        // Converte deslocamento para o espaço local do elemento rotacionado
        double dxLocal = dxWorld * cosA + dyWorld * sinA;
        double dyLocal = -dxWorld * sinA + dyWorld * cosA;

        double left = -_origW * 0.5;
        double right = _origW * 0.5;
        double top = -_origH * 0.5;
        double bottom = _origH * 0.5;

        bool hasWest = _dragHandle is Editor2DHandleType.NorthWest or Editor2DHandleType.West or Editor2DHandleType.SouthWest;
        bool hasEast = _dragHandle is Editor2DHandleType.NorthEast or Editor2DHandleType.East or Editor2DHandleType.SouthEast;
        bool hasNorth = _dragHandle is Editor2DHandleType.NorthWest or Editor2DHandleType.North or Editor2DHandleType.NorthEast;
        bool hasSouth = _dragHandle is Editor2DHandleType.SouthWest or Editor2DHandleType.South or Editor2DHandleType.SouthEast;

        if (hasWest) left = Math.Min(right - MinElementSizeMm, left + dxLocal);
        if (hasEast) right = Math.Max(left + MinElementSizeMm, right + dxLocal);
        if (hasNorth) top = Math.Min(bottom - MinElementSizeMm, top + dyLocal);
        if (hasSouth) bottom = Math.Max(top + MinElementSizeMm, bottom + dyLocal);

        double newW = right - left;
        double newH = bottom - top;
        bool keepAspect = _origKeepAspect && !shiftHeld;

        if (keepAspect)
        {
            double aspect = Math.Max(0.05, _origW / Math.Max(0.05, _origH));

            if ((hasWest || hasEast) && (hasNorth || hasSouth))
            {
                if (Math.Abs(dxLocal) >= Math.Abs(dyLocal))
                {
                    newH = Math.Max(MinElementSizeMm, newW / aspect);
                    if (hasNorth) top = bottom - newH;
                    else bottom = top + newH;
                }
                else
                {
                    newW = Math.Max(MinElementSizeMm, newH * aspect);
                    if (hasWest) left = right - newW;
                    else right = left + newW;
                }
            }
            else if (hasWest || hasEast)
            {
                newH = Math.Max(MinElementSizeMm, newW / aspect);
                double cyLocal = (top + bottom) * 0.5;
                top = cyLocal - newH * 0.5;
                bottom = cyLocal + newH * 0.5;
            }
            else if (hasNorth || hasSouth)
            {
                newW = Math.Max(MinElementSizeMm, newH * aspect);
                double cxLocal = (left + right) * 0.5;
                left = cxLocal - newW * 0.5;
                right = cxLocal + newW * 0.5;
            }
        }

        double localCx = (left + right) * 0.5;
        double localCy = (top + bottom) * 0.5;
        double newWorldCx = _origCenterX + localCx * cosA - localCy * sinA;
        double newWorldCy = _origCenterY + localCx * sinA + localCy * cosA;
        double newX = newWorldCx - newW * 0.5;
        double newY = newWorldCy - newH * 0.5;

        bool savedAspect = el.PreserveAspectRatio;
        vm.UpdateSelectedElementTransform(
            xMm: Math.Round(newX, 2),
            yMm: Math.Round(newY, 2),
            widthMm: Math.Round(newW, 2),
            heightMm: Math.Round(newH, 2),
            preserveAspectRatio: false,
            recordUndo: false);
        el.PreserveAspectRatio = savedAspect;
    }

    private void ApplyRotateDrag(
        PersonalizationWorkspaceViewModel vm,
        double mmX,
        double mmY,
        bool shiftHeld)
    {
        double currentAngleRad = Math.Atan2(mmY - _origCenterY, mmX - _origCenterX);
        double deltaDeg = (currentAngleRad - _rotateStartAngleRad) * 180.0 / Math.PI;
        double nextDeg = (_origRotDeg + deltaDeg) % 360.0;
        if (nextDeg < 0.0) nextDeg += 360.0;

        if (shiftHeld)
        {
            nextDeg = Math.Round(nextDeg / 15.0) * 15.0;
            if (nextDeg >= 360.0) nextDeg = 0.0;
        }

        vm.UpdateSelectedElementTransform(
            rotationDeg: Math.Round(nextDeg, 1),
            recordUndo: false);
    }

    private static BoundingBox2D ComputeCombinedBounds(SurfaceDefinition surface, SurfaceContour2D? contour)
    {
        double minX = 0.0;
        double minY = 0.0;
        double maxX = surface.WidthMm;
        double maxY = surface.HeightMm;

        if (contour is not null)
        {
            minX = Math.Min(minX, contour.Bounds.MinX);
            minY = Math.Min(minY, contour.Bounds.MinY);
            maxX = Math.Max(maxX, contour.Bounds.MaxX);
            maxY = Math.Max(maxY, contour.Bounds.MaxY);
        }

        return new BoundingBox2D(minX, minY, maxX, maxY);
    }

    private void BeginGroupMoveDrag(
        PersonalizationWorkspaceViewModel vm,
        string clickedElementId,
        double pxX,
        double pxY,
        double mmX,
        double mmY)
    {
        PersonalizationHistoryEntry checkpoint = vm.CaptureHistoryCheckpoint();
        vm.BeginInteractiveTransform(checkpoint);

        _dragMode = ActiveDragMode.MoveGroup;
        _dragHandle = Editor2DHandleType.Body;
        _dragElementId = clickedElementId;
        _pendingCollapseElementId = clickedElementId;
        _preDragCheckpoint = checkpoint;
        _dragStartPxX = pxX;
        _dragStartPxY = pxY;
        _dragStartMmX = mmX;
        _dragStartMmY = mmY;
        _maxGroupDragDistMm = 0.0;
        GroupDragDeltaXMm = 0.0;
        GroupDragDeltaYMm = 0.0;

        _groupDragBaselines.Clear();
        _groupSelectedIdSet.Clear();

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (string id in vm.SelectedElementIds)
        {
            if (!vm.Manager.TryGetElement(id, out PersonalizationElement? el) ||
                el is null ||
                el.PanelId != vm.ActiveSurfaceId ||
                !el.Visible)
            {
                continue;
            }

            _groupSelectedIdSet.Add(el.Id);
            if (!el.Locked)
            {
                _groupDragBaselines.Add((el.Id, el.XMm, el.YMm));
            }

            IReadOnlyList<Point2D> corners = el.Corners2D();
            for (int c = 0; c < corners.Count; c++)
            {
                Point2D pt = corners[c];
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }
        }

        _groupOrigBounds = minX <= maxX
            ? new BoundingBox2D(minX, minY, maxX, maxY)
            : new BoundingBox2D(mmX, mmY, mmX, mmY);
    }

    private void ApplyGroupMoveDrag(PersonalizationWorkspaceViewModel vm, double mmX, double mmY)
    {
        double rawDx = mmX - _dragStartMmX;
        double rawDy = mmY - _dragStartMmY;
        _maxGroupDragDistMm = Math.Max(_maxGroupDragDistMm, double.Hypot(rawDx, rawDy));

        (double snappedDx, double snappedDy, double? guideX, double? guideY) = SnapGroupDelta(vm, rawDx, rawDy);
        SnapGuideX = guideX;
        SnapGuideY = guideY;
        SnapGuideXActive = guideX.HasValue;
        SnapGuideYActive = guideY.HasValue;

        GroupDragDeltaXMm = Math.Round(snappedDx, 2);
        GroupDragDeltaYMm = Math.Round(snappedDy, 2);

        _groupMoveWorkBuffer.Clear();
        for (int i = 0; i < _groupDragBaselines.Count; i++)
        {
            (string id, double origX, double origY) = _groupDragBaselines[i];
            _groupMoveWorkBuffer.Add((id, Math.Round(origX + snappedDx, 2), Math.Round(origY + snappedDy, 2)));
        }

        vm.ApplyGroupPositionsInteractive(_groupMoveWorkBuffer);
    }

    private (double Dx, double Dy, double? GuideX, double? GuideY) SnapGroupDelta(
        PersonalizationWorkspaceViewModel vm,
        double rawDx,
        double rawDy)
    {
        double gMinX = _groupOrigBounds.MinX + rawDx;
        double gMaxX = _groupOrigBounds.MaxX + rawDx;
        double gCx = (gMinX + gMaxX) * 0.5;

        double gMinY = _groupOrigBounds.MinY + rawDy;
        double gMaxY = _groupOrigBounds.MaxY + rawDy;
        double gCy = (gMinY + gMaxY) * 0.5;

        double bestDistX = SnapThresholdMm + 1e-6;
        double bestAdjustX = 0.0;
        double? snapGuideX = null;

        double bestDistY = SnapThresholdMm + 1e-6;
        double bestAdjustY = 0.0;
        double? snapGuideY = null;

        void ConsiderX(double target)
        {
            double dLeft = Math.Abs(gMinX - target);
            if (dLeft <= SnapThresholdMm && dLeft < bestDistX)
            {
                bestDistX = dLeft;
                bestAdjustX = target - gMinX;
                snapGuideX = target;
            }
            double dCenter = Math.Abs(gCx - target);
            if (dCenter <= SnapThresholdMm && dCenter < bestDistX)
            {
                bestDistX = dCenter;
                bestAdjustX = target - gCx;
                snapGuideX = target;
            }
            double dRight = Math.Abs(gMaxX - target);
            if (dRight <= SnapThresholdMm && dRight < bestDistX)
            {
                bestDistX = dRight;
                bestAdjustX = target - gMaxX;
                snapGuideX = target;
            }
        }

        void ConsiderY(double target)
        {
            double dTop = Math.Abs(gMinY - target);
            if (dTop <= SnapThresholdMm && dTop < bestDistY)
            {
                bestDistY = dTop;
                bestAdjustY = target - gMinY;
                snapGuideY = target;
            }
            double dCenter = Math.Abs(gCy - target);
            if (dCenter <= SnapThresholdMm && dCenter < bestDistY)
            {
                bestDistY = dCenter;
                bestAdjustY = target - gCy;
                snapGuideY = target;
            }
            double dBottom = Math.Abs(gMaxY - target);
            if (dBottom <= SnapThresholdMm && dBottom < bestDistY)
            {
                bestDistY = dBottom;
                bestAdjustY = target - gMaxY;
                snapGuideY = target;
            }
        }

        ConsiderX(0.0);
        ConsiderY(0.0);

        if (vm.ActiveSurface is SurfaceDefinition surf)
        {
            Rect2D safe = surf.SafeAreaForMargin(vm.SafetyMarginMm);
            ConsiderX(safe.Center.X);
            ConsiderY(safe.Center.Y);
        }

        foreach (PersonalizationElement other in vm.ElementsOnActiveSurface)
        {
            if (!other.Visible || _groupSelectedIdSet.Contains(other.Id))
            {
                continue;
            }

            Point2D c = other.CenterMm();
            ConsiderX(other.XMm);
            ConsiderX(c.X);
            ConsiderX(other.XMm + other.WidthMm);

            ConsiderY(other.YMm);
            ConsiderY(c.Y);
            ConsiderY(other.YMm + other.HeightMm);
        }

        return (rawDx + bestAdjustX, rawDy + bestAdjustY, snapGuideX, snapGuideY);
    }

    private bool FinalizeMarqueeRelease(PersonalizationWorkspaceViewModel vm, bool shiftHeld)
    {
        double minX = Math.Min(MarqueeStartMm.X, MarqueeCurrentMm.X);
        double maxX = Math.Max(MarqueeStartMm.X, MarqueeCurrentMm.X);
        double minY = Math.Min(MarqueeStartMm.Y, MarqueeCurrentMm.Y);
        double maxY = Math.Max(MarqueeStartMm.Y, MarqueeCurrentMm.Y);
        double spanW = maxX - minX;
        double spanH = maxY - minY;

        if (spanW < 1.0 && spanH < 1.0)
        {
            if (!shiftHeld)
            {
                vm.SelectSingleElement(null);
            }
            return true;
        }

        var captured = new List<string>();
        foreach (PersonalizationElement el in vm.ElementsOnActiveSurface)
        {
            if (!el.Visible)
            {
                continue;
            }

            IReadOnlyList<Point2D> corners = el.Corners2D();
            double eMinX = double.PositiveInfinity;
            double eMaxX = double.NegativeInfinity;
            double eMinY = double.PositiveInfinity;
            double eMaxY = double.NegativeInfinity;
            for (int i = 0; i < corners.Count; i++)
            {
                Point2D p = corners[i];
                if (p.X < eMinX) eMinX = p.X;
                if (p.X > eMaxX) eMaxX = p.X;
                if (p.Y < eMinY) eMinY = p.Y;
                if (p.Y > eMaxY) eMaxY = p.Y;
            }

            bool intersects = eMinX <= maxX && eMaxX >= minX && eMinY <= maxY && eMaxY >= minY;
            if (intersects)
            {
                captured.Add(el.Id);
            }
        }

        if (shiftHeld)
        {
            var merged = new List<string>(vm.SelectedElementIds.Count + captured.Count);
            merged.AddRange(vm.SelectedElementIds);
            foreach (string id in captured)
            {
                if (!merged.Contains(id, StringComparer.Ordinal))
                {
                    merged.Add(id);
                }
            }
            vm.SetSelectedElements(merged);
        }
        else
        {
            vm.SetSelectedElements(captured);
        }

        return true;
    }

    private bool FinalizeGroupMoveRelease(PersonalizationWorkspaceViewModel vm)
    {
        string? collapseId = _pendingCollapseElementId;
        _pendingCollapseElementId = null;
        double movedDist = Math.Max(
            _maxGroupDragDistMm,
            double.Hypot(GroupDragDeltaXMm, GroupDragDeltaYMm));

        if (_maxGroupDragDistMm < 1.0 && movedDist < 1.0)
        {
            _groupMoveWorkBuffer.Clear();
            for (int i = 0; i < _groupDragBaselines.Count; i++)
            {
                (string id, double origX, double origY) = _groupDragBaselines[i];
                _groupMoveWorkBuffer.Add((id, origX, origY));
            }
            if (_groupMoveWorkBuffer.Count > 0)
            {
                vm.ApplyGroupPositionsInteractive(_groupMoveWorkBuffer);
            }

            _groupDragBaselines.Clear();
            _groupSelectedIdSet.Clear();
            GroupDragDeltaXMm = 0.0;
            GroupDragDeltaYMm = 0.0;

            vm.EndInteractiveTransform(recordUndoIfChanged: false);
            if (!string.IsNullOrEmpty(collapseId))
            {
                vm.SelectSingleElement(collapseId);
            }
            return true;
        }

        _groupDragBaselines.Clear();
        _groupSelectedIdSet.Clear();
        GroupDragDeltaXMm = 0.0;
        GroupDragDeltaYMm = 0.0;
        return vm.EndInteractiveTransform(recordUndoIfChanged: true);
    }
}
