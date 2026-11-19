using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Core.Personalization;

/// <summary>
/// Gerencia dimensões, áreas seguras, zonas proibidas, offsets CNC e transformações 2D ↔ 3D
/// das faces ativas de um modelo de caixa. Porta integralmente <c>personalization/surfaces.py</c>.
/// </summary>
public sealed class SurfaceMapper
{
    private static readonly SurfaceId[] CanonicalOrder =
    [
        SurfaceId.Front,
        SurfaceId.Back,
        SurfaceId.Left,
        SurfaceId.Right,
        SurfaceId.Lid,
        SurfaceId.Bottom,
    ];

    private readonly Dictionary<SurfaceId, SurfaceDefinition> _surfaces;

    public BoxCalculator Calc { get; }
    public BoxDrawer Drawer { get; }
    public IBoxModelDefinition BoxModel { get; }

    public SurfaceMapper(
        BoxCalculator calculator,
        BoxDrawer? drawer = null,
        IBoxModelDefinition? boxModel = null)
    {
        Calc = calculator ?? throw new ArgumentNullException(nameof(calculator));
        Drawer = drawer ?? new BoxDrawer(calculator);
        BoxModel = boxModel ?? BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        _surfaces = BuildSurfaces();
    }

    public IReadOnlyList<SurfaceDefinition> ListSurfaces()
        => CanonicalOrder
            .Where(_surfaces.ContainsKey)
            .Select(id => _surfaces[id])
            .ToArray();

    public SurfaceDefinition GetSurface(SurfaceId surfaceId)
    {
        if (!_surfaces.TryGetValue(surfaceId, out SurfaceDefinition? surface))
        {
            throw new KeyNotFoundException(
                $"A superfície '{SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(surfaceId)}' não existe no modelo '{BoxModel.ModelId}'.");
        }

        return surface;
    }

    public SurfaceDefinition GetSurface(string surfaceId)
        => GetSurface(SnakeCaseEnumJsonConverter<SurfaceId>.ParseApiString(surfaceId));

    public bool TryGetSurface(SurfaceId surfaceId, out SurfaceDefinition? surface)
        => _surfaces.TryGetValue(surfaceId, out surface);

    /// <summary>
    /// Transforma um ponto <c>(xMm, yMm)</c> da superfície para coordenadas na peça individual ou folha SVG CNC,
    /// aplicando a rotação de 180° na tampa (<c>lid</c>) quando configurada.
    /// </summary>
    public PanelSvgTransformResult SurfaceToPanelSvg(
        SurfaceId surfaceId,
        double xMm,
        double yMm,
        double rotationDeg = 0.0,
        Point2D? originOffsetMm = null)
    {
        SurfaceDefinition surface = GetSurface(surfaceId);

        if (!originOffsetMm.HasValue)
        {
            double ox = surface.SvgBodyOffsetMm[0];
            double oy = surface.SvgBodyOffsetMm[1];
            if (Math.Abs(surface.CncRotationDeg - 180.0) < 1e-6)
            {
                return new PanelSvgTransformResult(
                    ox + (surface.WidthMm - xMm),
                    oy + (surface.HeightMm - yMm),
                    NormalizeAngle(rotationDeg + 180.0));
            }

            return new PanelSvgTransformResult(ox + xMm, oy + yMm, NormalizeAngle(rotationDeg));
        }

        // Caso de arquivo individual da peça (originOffsetMm relativo à origem local do painel).
        double earOffset = BoxModel.HasHandles ? 4.0 * Calc.Thickness : 0.0;
        double baseX = originOffsetMm.Value.X + (surface.PanelId == SurfaceId.Left ? earOffset : 0.0);
        double baseY = originOffsetMm.Value.Y;

        if (Math.Abs(surface.CncRotationDeg - 180.0) < 1e-6)
        {
            return new PanelSvgTransformResult(
                baseX + (surface.WidthMm - xMm),
                baseY + (surface.HeightMm - yMm),
                NormalizeAngle(rotationDeg + 180.0));
        }

        return new PanelSvgTransformResult(baseX + xMm, baseY + yMm, NormalizeAngle(rotationDeg));
    }

    /// <summary>
    /// Mapeia coordenadas 2D da superfície <c>(xMm, yMm)</c> para coordenadas locais 3D da caixa montada.
    /// </summary>
    public Point3D SurfaceTo3DLocal(SurfaceId surfaceId, double xMm, double yMm)
    {
        SurfaceDefinition surf = GetSurface(surfaceId);
        double ox = surf.AssembledOrigin3DMm[0];
        double oy = surf.AssembledOrigin3DMm[1];
        double oz = surf.AssembledOrigin3DMm[2];

        return surfaceId switch
        {
            SurfaceId.Front => new Point3D(ox + xMm, oy - yMm, oz),
            SurfaceId.Back => new Point3D(ox - xMm, oy - yMm, oz),
            SurfaceId.Right => new Point3D(ox, oy - yMm, oz - xMm),
            SurfaceId.Left => new Point3D(ox, oy - yMm, oz + xMm),
            SurfaceId.Lid => new Point3D(ox + xMm, oy, oz + yMm),
            SurfaceId.Bottom => new Point3D(ox + xMm, oy, oz + yMm),
            _ => throw new ArgumentOutOfRangeException(nameof(surfaceId)),
        };
    }

    private Dictionary<SurfaceId, SurfaceDefinition> BuildSurfaces()
    {
        double t = Calc.Thickness;
        double w = Calc.Width;
        double depth = Calc.Depth;
        double h = Calc.Height;
        double folga = Calc.Folga;
        double halfFolga = Math.Max(0.0, folga / 2.0);
        const double s = BoxDrawer.StartingCoordinate;

        BoxLayoutMetrics layout = BoxModel.GetLayoutMetrics(Calc, Drawer);
        IReadOnlyDictionary<string, PanelPlacement2D> panelsOrigin = layout.Panels;
        double earOffset = BoxModel.HasHandles ? 4.0 * t : 0.0;
        double lidCoverDepth = depth + 1.5 * t;

        var surfaces = new Dictionary<SurfaceId, SurfaceDefinition>();

        // 1. FRONT e 2. BACK
        ForbiddenZone[] frontBackForbidden =
        [
            new ForbiddenZone
            {
                Id = "left_corner_tabs",
                Label = "Encaixe lateral esquerdo",
                Reason = "Regiao das espigas verticais e topo da parede lateral esquerda.",
                ZoneType = ForbiddenZoneType.FingerJoint,
                RectMm = new Rect2D(0.0, 0.0, t, h),
            },
            new ForbiddenZone
            {
                Id = "right_corner_tabs",
                Label = "Encaixe lateral direito",
                Reason = "Regiao das espigas verticais e topo da parede lateral direita.",
                ZoneType = ForbiddenZoneType.FingerJoint,
                RectMm = new Rect2D(w - t, 0.0, t, h),
            },
            new ForbiddenZone
            {
                Id = "bottom_tabs",
                Label = "Espigas inferiores",
                Reason = "Regiao das espigas que encaixam nos rasgos da base.",
                ZoneType = ForbiddenZoneType.FingerJoint,
                RectMm = new Rect2D(t, h - t, Math.Max(0.0, w - 2.0 * t), t),
            },
        ];

        PanelPlacement2D frontOrigin = panelsOrigin["front"];
        surfaces[SurfaceId.Front] = new SurfaceDefinition
        {
            PanelId = SurfaceId.Front,
            DisplayName = "Face Frontal (+Z)",
            WidthMm = w,
            HeightMm = h,
            SafeRectMm = new Rect2D(t, 0.0, Math.Max(0.0, w - 2.0 * t), Math.Max(0.0, h - t)),
            ForbiddenZones = frontBackForbidden,
            SvgCellOriginMm = [Round4(frontOrigin.X), Round4(frontOrigin.Y)],
            SvgBodyOffsetMm = [Round4(frontOrigin.X + s), Round4(frontOrigin.Y + s)],
            CncRotationDeg = 0.0,
            CncMirrorX = false,
            Normal3D = [0.0, 0.0, 1.0],
            AssembledOrigin3DMm = [Round4(-w / 2.0), Round4(h), Round4(depth / 2.0)],
            OrientationNotes = "Correspondencia direta 1:1 entre vista frontal externa (+Z) e mesa CNC (0 deg).",
        };

        PanelPlacement2D backOrigin = panelsOrigin["back"];
        surfaces[SurfaceId.Back] = new SurfaceDefinition
        {
            PanelId = SurfaceId.Back,
            DisplayName = "Face Traseira (-Z)",
            WidthMm = w,
            HeightMm = h,
            SafeRectMm = new Rect2D(t, 0.0, Math.Max(0.0, w - 2.0 * t), Math.Max(0.0, h - t)),
            ForbiddenZones = frontBackForbidden,
            SvgCellOriginMm = [Round4(backOrigin.X), Round4(backOrigin.Y)],
            SvgBodyOffsetMm = [Round4(backOrigin.X + s), Round4(backOrigin.Y + s)],
            CncRotationDeg = 0.0,
            CncMirrorX = false,
            Normal3D = [0.0, 0.0, -1.0],
            AssembledOrigin3DMm = [Round4(w / 2.0), Round4(h), Round4(-depth / 2.0)],
            OrientationNotes = "Visto externamente de tras (-Z), esquerda->direita equivale diretamente ao X da mesa CNC sem espelhar (0 deg).",
        };

        // 3. RIGHT
        PanelPlacement2D rightOrigin = panelsOrigin["right"];
        double sideW = depth + 2.0 * t;
        surfaces[SurfaceId.Right] = new SurfaceDefinition
        {
            PanelId = SurfaceId.Right,
            DisplayName = "Lateral Direita (+X)",
            WidthMm = sideW,
            HeightMm = h,
            SafeRectMm = new Rect2D(
                2.0 * t + halfFolga,
                0.0,
                Math.Max(0.0, depth - 2.0 * t - 2.0 * halfFolga),
                Math.Max(0.0, h - t)),
            ForbiddenZones =
            [
                new ForbiddenZone
                {
                    Id = "front_slot_band",
                    Label = "Borda e rasgos frontais",
                    Reason = "Borda de contato e coluna de rasgos verticais da parede frontal.",
                    ZoneType = ForbiddenZoneType.ThroughSlot,
                    RectMm = new Rect2D(0.0, 0.0, 2.0 * t + halfFolga, h),
                },
                new ForbiddenZone
                {
                    Id = "rear_slot_band",
                    Label = "Borda e rasgos traseiros",
                    Reason = "Coluna de rasgos da parede traseira e inicio da orelha da dobradica.",
                    ZoneType = ForbiddenZoneType.ThroughSlot,
                    RectMm = new Rect2D(depth - halfFolga, 0.0, 2.0 * t + halfFolga, h),
                },
                new ForbiddenZone
                {
                    Id = "bottom_tabs",
                    Label = "Espigas inferiores",
                    Reason = "Espigas inferiores que encaixam na base.",
                    ZoneType = ForbiddenZoneType.FingerJoint,
                    RectMm = new Rect2D(2.0 * t, h - t, Math.Max(0.0, depth - 2.0 * t), t),
                },
            ],
            SvgCellOriginMm = [Round4(rightOrigin.X), Round4(rightOrigin.Y)],
            SvgBodyOffsetMm = [Round4(rightOrigin.X + s), Round4(rightOrigin.Y + s)],
            CncRotationDeg = 0.0,
            CncMirrorX = false,
            Normal3D = [1.0, 0.0, 0.0],
            AssembledOrigin3DMm = [Round4(w / 2.0), Round4(h), Round4(depth / 2.0 + t)],
            OrientationNotes = "Correspondencia direta 1:1 com draw_side(inversor=0): frente (+Z) a esquerda e orelha traseira (-Z) a direita.",
        };

        // 4. LEFT
        PanelPlacement2D leftOrigin = panelsOrigin["left"];
        surfaces[SurfaceId.Left] = new SurfaceDefinition
        {
            PanelId = SurfaceId.Left,
            DisplayName = "Lateral Esquerda (-X)",
            WidthMm = sideW,
            HeightMm = h,
            SafeRectMm = new Rect2D(
                2.0 * t + halfFolga,
                0.0,
                Math.Max(0.0, depth - 2.0 * t - 2.0 * halfFolga),
                Math.Max(0.0, h - t)),
            ForbiddenZones =
            [
                new ForbiddenZone
                {
                    Id = "rear_slot_band",
                    Label = "Borda e rasgos traseiros",
                    Reason = "Coluna de rasgos da parede traseira junto a orelha da dobradica.",
                    ZoneType = ForbiddenZoneType.ThroughSlot,
                    RectMm = new Rect2D(0.0, 0.0, 2.0 * t + halfFolga, h),
                },
                new ForbiddenZone
                {
                    Id = "front_slot_band",
                    Label = "Borda e rasgos frontais",
                    Reason = "Coluna de rasgos da parede frontal e borda dianteira.",
                    ZoneType = ForbiddenZoneType.ThroughSlot,
                    RectMm = new Rect2D(depth - halfFolga, 0.0, 2.0 * t + halfFolga, h),
                },
                new ForbiddenZone
                {
                    Id = "bottom_tabs",
                    Label = "Espigas inferiores",
                    Reason = "Espigas inferiores que encaixam na base.",
                    ZoneType = ForbiddenZoneType.FingerJoint,
                    RectMm = new Rect2D(2.0 * t, h - t, Math.Max(0.0, depth - 2.0 * t), t),
                },
            ],
            SvgCellOriginMm = [Round4(leftOrigin.X), Round4(leftOrigin.Y)],
            SvgBodyOffsetMm = [Round4(leftOrigin.X + s + earOffset), Round4(leftOrigin.Y + s)],
            CncRotationDeg = 0.0,
            CncMirrorX = false,
            Normal3D = [-1.0, 0.0, 0.0],
            AssembledOrigin3DMm = [Round4(-w / 2.0), Round4(h), Round4(-(depth / 2.0 + t))],
            OrientationNotes = "draw_side(inversor=1) com gravacao aplicada sem espelhamento para leitura normal pelo lado externo (-X).",
        };

        // 5. LID (somente quando o modelo possui tampa)
        if (BoxModel.HasLid && panelsOrigin.TryGetValue("lid", out PanelPlacement2D? lidOrigin))
        {
            double lidW = w + 2.0 * t;
            surfaces[SurfaceId.Lid] = new SurfaceDefinition
            {
                PanelId = SurfaceId.Lid,
                DisplayName = "Tampa Superior (+Y)",
                WidthMm = lidW,
                HeightMm = lidCoverDepth,
                SafeRectMm = new Rect2D(t, 0.5 * t, w, Math.Max(0.0, depth)),
                ForbiddenZones =
                [
                    new ForbiddenZone
                    {
                        Id = "lid_left_margin",
                        Label = "Aba lateral esquerda da tampa",
                        Reason = "Margem externa arredondada de apoio da tampa.",
                        ZoneType = ForbiddenZoneType.EdgeMargin,
                        RectMm = new Rect2D(0.0, 0.0, t, lidCoverDepth),
                    },
                    new ForbiddenZone
                    {
                        Id = "lid_right_margin",
                        Label = "Aba lateral direita da tampa",
                        Reason = "Margem externa arredondada de apoio da tampa.",
                        ZoneType = ForbiddenZoneType.EdgeMargin,
                        RectMm = new Rect2D(w + t, 0.0, t, lidCoverDepth),
                    },
                    new ForbiddenZone
                    {
                        Id = "lid_rear_neck_clearance",
                        Label = "Transicao do pescoco da dobradica",
                        Reason = "Regiao traseira proxima ao recorte de articulacao entre as alcas.",
                        ZoneType = ForbiddenZoneType.HingeEar,
                        RectMm = new Rect2D(0.0, 0.0, lidW, 0.5 * t),
                    },
                    new ForbiddenZone
                    {
                        Id = "lid_front_lip",
                        Label = "Aba frontal arredondada",
                        Reason = "Borda dianteira da tampa com cantos arredondados de raio t.",
                        ZoneType = ForbiddenZoneType.EdgeMargin,
                        RectMm = new Rect2D(0.0, depth + 0.5 * t, lidW, t),
                    },
                ],
                SvgCellOriginMm = [Round4(lidOrigin.X), Round4(lidOrigin.Y)],
                SvgBodyOffsetMm = [Round4(lidOrigin.X + s), Round4(lidOrigin.Y + s)],
                CncRotationDeg = 180.0,
                CncMirrorX = false,
                Normal3D = [0.0, 1.0, 0.0],
                AssembledOrigin3DMm = [Round4(-lidW / 2.0), Round4(h + t), Round4(-(depth / 2.0 + 0.5 * t))],
                OrientationNotes = "Visto de frente olhando de cima (+Y), a dobradica fica em y=0 (topo) e a borda frontal embaixo; rotacionado 180 deg em draw_lid na folha CNC.",
            };
        }

        // 6. BOTTOM
        PanelPlacement2D bottomOrigin = panelsOrigin["bottom"];
        double botW = w + 2.0 * t;
        double botD = depth + 2.0 * t;
        surfaces[SurfaceId.Bottom] = new SurfaceDefinition
        {
            PanelId = SurfaceId.Bottom,
            DisplayName = "Fundo / Base",
            WidthMm = botW,
            HeightMm = botD,
            SafeRectMm = new Rect2D(
                2.0 * t + halfFolga,
                2.0 * t + halfFolga,
                Math.Max(0.0, w - 2.0 * t - 2.0 * halfFolga),
                Math.Max(0.0, depth - 2.0 * t - 2.0 * halfFolga)),
            ForbiddenZones =
            [
                new ForbiddenZone
                {
                    Id = "bottom_top_slots",
                    Label = "Rasgos da parede traseira",
                    Reason = "Borda e fileira de rasgos passantes da parede traseira.",
                    ZoneType = ForbiddenZoneType.ThroughSlot,
                    RectMm = new Rect2D(0.0, 0.0, botW, 2.0 * t + halfFolga),
                },
                new ForbiddenZone
                {
                    Id = "bottom_bottom_slots",
                    Label = "Rasgos da parede frontal",
                    Reason = "Borda e fileira de rasgos passantes da parede frontal.",
                    ZoneType = ForbiddenZoneType.ThroughSlot,
                    RectMm = new Rect2D(0.0, depth - halfFolga, botW, 2.0 * t + halfFolga),
                },
                new ForbiddenZone
                {
                    Id = "bottom_left_slots",
                    Label = "Rasgos da lateral esquerda",
                    Reason = "Borda e fileira de rasgos passantes da lateral esquerda.",
                    ZoneType = ForbiddenZoneType.ThroughSlot,
                    RectMm = new Rect2D(
                        0.0,
                        2.0 * t + halfFolga,
                        2.0 * t + halfFolga,
                        Math.Max(0.0, depth - 2.0 * t - 2.0 * halfFolga)),
                },
                new ForbiddenZone
                {
                    Id = "bottom_right_slots",
                    Label = "Rasgos da lateral direita",
                    Reason = "Borda e fileira de rasgos passantes da lateral direita.",
                    ZoneType = ForbiddenZoneType.ThroughSlot,
                    RectMm = new Rect2D(
                        w - halfFolga,
                        2.0 * t + halfFolga,
                        2.0 * t + halfFolga,
                        Math.Max(0.0, depth - 2.0 * t - 2.0 * halfFolga)),
                },
            ],
            SvgCellOriginMm = [Round4(bottomOrigin.X), Round4(bottomOrigin.Y)],
            SvgBodyOffsetMm = [Round4(bottomOrigin.X + s), Round4(bottomOrigin.Y + s)],
            CncRotationDeg = 0.0,
            CncMirrorX = false,
            Normal3D = [0.0, -1.0, 0.0],
            AssembledOrigin3DMm = [Round4(-botW / 2.0), 0.0, Round4(-botD / 2.0)],
            OrientationNotes = "Correspondencia direta 1:1 no retangulo externo da base (0 deg), com area util interna delimitada pelas 4 fileiras de rasgos passantes.",
        };

        return surfaces;
    }

    private static double Round4(double value) => PythonMath.Round(value, 4);

    private static double NormalizeAngle(double angle)
    {
        double mod = angle % 360.0;
        return mod < 0.0 ? mod + 360.0 : mod;
    }
}
