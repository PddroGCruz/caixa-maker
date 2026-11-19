using System.Diagnostics;

namespace CaixaMaker.Core.Nesting;

/// <summary>
/// Motor determinístico de aproveitamento e encaixe 2D <i>True-Shape</i> multi-estratégia
/// em memória (thread-safe, compatível com execução em background e cancelamento).
/// </summary>
public sealed class TrueShapeNestingEngine
{
    private const double MinCompactionEpsMm = 0.2;

    private sealed record ExpandedInstance(NestingPart Part, int InstanceIndex);

    private sealed record PartOrientation(
        double AngleDeg,
        double WidthMm,
        double HeightMm,
        IReadOnlyList<(double X, double Y)> OuterContour,
        IReadOnlyList<NestingPolyline2D> InternalFeatures,
        IReadOnlyList<NestingRasterOverlay> RasterOverlays,
        IReadOnlyList<double> DistinctVertexX,
        IReadOnlyList<double> DistinctVertexY);

    private sealed record StrategyEvaluation(
        string StrategyName,
        List<NestingSheetLayout> Sheets,
        List<NestingUnplacedPart> UnplacedParts,
        int TotalPlaced,
        double OverallUtilizationPercent,
        double LastSheetUsedAreaScore);

    private sealed class SheetWorkspace
    {
        public int SheetIndex { get; init; }
        public double MinX { get; init; }
        public double MinY { get; init; }
        public double MaxX { get; init; }
        public double MaxY { get; init; }
        public double Spacing { get; init; }
        public List<PlacedEntry> Entries { get; } = new();
        public double UsedBoundsMaxX { get; set; }
        public double UsedBoundsMaxY { get; set; }
    }

    private sealed class PlacedEntry
    {
        public required PlacedNestingPart Placement { get; init; }
        public required IReadOnlyList<HolePolygonInfo> Holes { get; init; }
    }

    private sealed record HolePolygonInfo(
        IReadOnlyList<(double X, double Y)> Points,
        double MinX,
        double MinY,
        double MaxX,
        double MaxY);

    private readonly struct PlacementCandidate
    {
        public double ScorePrimary { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
    }

    public NestingResult Optimize(
        IReadOnlyList<NestingPart> parts,
        NestingSheetConfig config,
        IProgress<NestingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(config);

        if (config.SheetWidthMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "Largura da chapa deve ser positiva.");
        }

        if (config.SheetHeightMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "Altura da chapa deve ser positiva.");
        }

        if (config.MarginMm < 0.0 || config.PartSpacingMm < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "Margem e espaçamento não podem ser negativos.");
        }

        var sw = Stopwatch.StartNew();

        var instances = new List<ExpandedInstance>();
        var immediateInvalid = new List<NestingUnplacedPart>();

        foreach (NestingPart part in parts)
        {
            if (!part.Enabled || part.Quantity <= 0)
            {
                continue;
            }

            if (part.OuterContour.Count < 3 || part.OuterAreaMm2 <= 1e-6)
            {
                for (int q = 1; q <= part.Quantity; q++)
                {
                    immediateInvalid.Add(new NestingUnplacedPart(
                        part.Id,
                        part.Name,
                        q,
                        "Contorno externo inválido ou com área nula."));
                }

                continue;
            }

            for (int q = 1; q <= part.Quantity; q++)
            {
                instances.Add(new ExpandedInstance(part, q));
            }
        }

        int totalRequested = instances.Count + immediateInvalid.Count;
        if (instances.Count == 0)
        {
            sw.Stop();
            return new NestingResult
            {
                Config = config,
                Sheets = Array.Empty<NestingSheetLayout>(),
                UnplacedParts = immediateInvalid,
                OverallUtilizationPercent = 0.0,
                TotalRequestedInstances = totalRequested,
                TotalPlacedInstances = 0,
                Elapsed = sw.Elapsed,
                WinningStrategy = "None",
            };
        }

        // Pré-calcula orientações válidas para cada peça única
        var orientationCache = new Dictionary<string, IReadOnlyList<PartOrientation>>(StringComparer.Ordinal);
        foreach (ExpandedInstance inst in instances)
        {
            if (!orientationCache.ContainsKey(inst.Part.Id))
            {
                NestingRotationPolicy policy = config.GlobalRotationOverride ?? inst.Part.RotationPolicy;
                orientationCache[inst.Part.Id] = BuildUniqueOrientations(inst.Part, policy);
            }
        }

        (string Name, Func<List<ExpandedInstance>, List<ExpandedInstance>> Sort)[] strategies =
        [
            ("AreaDescending", list => list
                .OrderByDescending(i => i.Part.OuterAreaMm2)
                .ThenByDescending(i => Math.Max(i.Part.WidthMm, i.Part.HeightMm))
                .ThenBy(i => i.Part.Id, StringComparer.Ordinal)
                .ThenBy(i => i.InstanceIndex)
                .ToList()),
            ("MaxDimensionDescending", list => list
                .OrderByDescending(i => Math.Max(i.Part.WidthMm, i.Part.HeightMm))
                .ThenByDescending(i => i.Part.OuterAreaMm2)
                .ThenBy(i => i.Part.Id, StringComparer.Ordinal)
                .ThenBy(i => i.InstanceIndex)
                .ToList()),
            ("BoundingAreaDescending", list => list
                .OrderByDescending(i => i.Part.WidthMm * i.Part.HeightMm)
                .ThenByDescending(i => i.Part.WidthMm)
                .ThenBy(i => i.Part.Id, StringComparer.Ordinal)
                .ThenBy(i => i.InstanceIndex)
                .ToList()),
        ];

        StrategyEvaluation? bestEval = null;
        int totalStepsAllStrategies = strategies.Length * instances.Count;
        int completedSteps = 0;

        foreach ((string strategyName, var sorter) in strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<ExpandedInstance> ordered = sorter(instances);
            StrategyEvaluation eval = RunSingleStrategy(
                strategyName,
                ordered,
                orientationCache,
                config,
                immediateInvalid,
                () =>
                {
                    completedSteps++;
                    if (progress is not null && (completedSteps % 3 == 0 || completedSteps == totalStepsAllStrategies))
                    {
                        progress.Report(new NestingProgress(
                            completedSteps,
                            totalStepsAllStrategies,
                            bestEval?.Sheets.Count ?? 0,
                            bestEval?.OverallUtilizationPercent ?? 0.0,
                            $"Avaliando estratégia '{strategyName}' ({completedSteps}/{totalStepsAllStrategies})..."));
                    }
                },
                cancellationToken);

            if (IsBetterEvaluation(eval, bestEval))
            {
                bestEval = eval;
            }
        }

        sw.Stop();
        StrategyEvaluation winner = bestEval!;
        progress?.Report(new NestingProgress(
            totalStepsAllStrategies,
            totalStepsAllStrategies,
            winner.Sheets.Count,
            winner.OverallUtilizationPercent,
            $"Otimização concluída ({FormatWinningSummary(winner)})."));

        return new NestingResult
        {
            Config = config,
            Sheets = winner.Sheets,
            UnplacedParts = winner.UnplacedParts,
            OverallUtilizationPercent = winner.OverallUtilizationPercent,
            TotalRequestedInstances = totalRequested,
            TotalPlacedInstances = winner.TotalPlaced,
            Elapsed = sw.Elapsed,
            WinningStrategy = winner.StrategyName,
        };
    }

    private static bool IsBetterEvaluation(StrategyEvaluation candidate, StrategyEvaluation? currentBest)
    {
        if (currentBest is null)
        {
            return true;
        }

        if (candidate.TotalPlaced != currentBest.TotalPlaced)
        {
            return candidate.TotalPlaced > currentBest.TotalPlaced;
        }

        if (candidate.Sheets.Count != currentBest.Sheets.Count)
        {
            return candidate.Sheets.Count < currentBest.Sheets.Count;
        }

        if (Math.Abs(candidate.OverallUtilizationPercent - currentBest.OverallUtilizationPercent) > 1e-4)
        {
            return candidate.OverallUtilizationPercent > currentBest.OverallUtilizationPercent;
        }

        return candidate.LastSheetUsedAreaScore < currentBest.LastSheetUsedAreaScore;
    }

    private static StrategyEvaluation RunSingleStrategy(
        string strategyName,
        List<ExpandedInstance> ordered,
        Dictionary<string, IReadOnlyList<PartOrientation>> orientationCache,
        NestingSheetConfig config,
        List<NestingUnplacedPart> immediateInvalid,
        Action onInstanceProcessed,
        CancellationToken cancellationToken)
    {
        var sheets = new List<SheetWorkspace>();
        var unplaced = new List<NestingUnplacedPart>(immediateInvalid);

        double usableMinX = config.MarginMm;
        double usableMinY = config.MarginMm;
        double usableMaxX = config.SheetWidthMm - config.MarginMm;
        double usableMaxY = config.SheetHeightMm - config.MarginMm;
        double usableW = Math.Max(0.0, usableMaxX - usableMinX);
        double usableH = Math.Max(0.0, usableMaxY - usableMinY);

        foreach (ExpandedInstance inst in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<PartOrientation> orientations = orientationCache[inst.Part.Id];
            bool fitsAnyRotation = orientations.Any(o =>
                o.WidthMm <= usableW + 1e-6 &&
                o.HeightMm <= usableH + 1e-6);

            if (!fitsAnyRotation)
            {
                unplaced.Add(new NestingUnplacedPart(
                    inst.Part.Id,
                    inst.Part.Name,
                    inst.InstanceIndex,
                    $"Excede a área útil do retalho ({usableW:0.#} × {usableH:0.#} mm) considerando as rotações permitidas."));
                onInstanceProcessed();
                continue;
            }

            PlacedEntry? chosen = null;
            SheetWorkspace? targetSheet = null;

            foreach (SheetWorkspace sheet in sheets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (config.AllowNestingInHoles &&
                    TryPlaceInsideAnyHole(sheet, inst, orientations, config, cancellationToken, out PlacedEntry? holePlacement))
                {
                    chosen = holePlacement;
                    targetSheet = sheet;
                    break;
                }

                if (TryPlaceOnSheet(sheet, inst, orientations, config, cancellationToken, out PlacedEntry? sheetPlacement))
                {
                    chosen = sheetPlacement;
                    targetSheet = sheet;
                    break;
                }
            }

            if (chosen is null)
            {
                var newSheet = new SheetWorkspace
                {
                    SheetIndex = sheets.Count + 1,
                    MinX = usableMinX,
                    MinY = usableMinY,
                    MaxX = usableMaxX,
                    MaxY = usableMaxY,
                    Spacing = config.PartSpacingMm,
                    UsedBoundsMaxX = usableMinX,
                    UsedBoundsMaxY = usableMinY,
                };

                if (TryPlaceOnSheet(newSheet, inst, orientations, config, cancellationToken, out PlacedEntry? newPlacement))
                {
                    sheets.Add(newSheet);
                    chosen = newPlacement;
                    targetSheet = newSheet;
                }
                else
                {
                    unplaced.Add(new NestingUnplacedPart(
                        inst.Part.Id,
                        inst.Part.Name,
                        inst.InstanceIndex,
                        "Não foi possível acomodar o contorno na área útil disponível."));
                }
            }

            if (chosen is not null && targetSheet is not null)
            {
                targetSheet.Entries.Add(chosen);
                targetSheet.UsedBoundsMaxX = Math.Max(targetSheet.UsedBoundsMaxX, chosen.Placement.MaxXMm);
                targetSheet.UsedBoundsMaxY = Math.Max(targetSheet.UsedBoundsMaxY, chosen.Placement.MaxYMm);
            }

            onInstanceProcessed();
        }

        double usableSheetArea = Math.Max(1.0, usableW * usableH);
        var builtSheets = new List<NestingSheetLayout>(sheets.Count);
        double totalNetAreaUsed = 0.0;
        int totalPlaced = 0;

        foreach (SheetWorkspace sheet in sheets)
        {
            double topLevelOuterArea = 0.0;
            double sheetNetArea = 0.0;
            double maxUsedX = sheet.MinX;
            double maxUsedY = sheet.MinY;
            var placements = new List<PlacedNestingPart>(sheet.Entries.Count);

            foreach (PlacedEntry entry in sheet.Entries)
            {
                placements.Add(entry.Placement);
                if (!entry.Placement.PlacedInsideHole)
                {
                    topLevelOuterArea += entry.Placement.Part.OuterAreaMm2;
                }

                sheetNetArea += entry.Placement.Part.NetAreaMm2;
                maxUsedX = Math.Max(maxUsedX, entry.Placement.MaxXMm);
                maxUsedY = Math.Max(maxUsedY, entry.Placement.MaxYMm);
            }

            totalPlaced += placements.Count;
            totalNetAreaUsed += sheetNetArea;
            double util = Math.Clamp((sheetNetArea / usableSheetArea) * 100.0, 0.0, 100.0);

            builtSheets.Add(new NestingSheetLayout
            {
                SheetIndex = sheet.SheetIndex,
                SheetWidthMm = config.SheetWidthMm,
                SheetHeightMm = config.SheetHeightMm,
                MarginMm = config.MarginMm,
                Placements = placements,
                UsedOuterAreaMm2 = Math.Round(topLevelOuterArea, 2),
                UsableSheetAreaMm2 = Math.Round(usableSheetArea, 2),
                UtilizationPercent = Math.Round(util, 2),
                UsedBoundsWidthMm = placements.Count == 0 ? 0.0 : Math.Round(maxUsedX - sheet.MinX, 2),
                UsedBoundsHeightMm = placements.Count == 0 ? 0.0 : Math.Round(maxUsedY - sheet.MinY, 2),
            });
        }

        double overallUtil = builtSheets.Count == 0
            ? 0.0
            : Math.Clamp((totalNetAreaUsed / (builtSheets.Count * usableSheetArea)) * 100.0, 0.0, 100.0);
        double lastScore = builtSheets.Count == 0
            ? 0.0
            : builtSheets[^1].UsedBoundsWidthMm * builtSheets[^1].UsedBoundsHeightMm;

        return new StrategyEvaluation(
            strategyName,
            builtSheets,
            unplaced,
            totalPlaced,
            Math.Round(overallUtil, 2),
            lastScore);
    }

    private static bool TryPlaceInsideAnyHole(
        SheetWorkspace sheet,
        ExpandedInstance inst,
        IReadOnlyList<PartOrientation> orientations,
        NestingSheetConfig config,
        CancellationToken cancellationToken,
        out PlacedEntry? placement)
    {
        placement = null;
        double spacing = config.PartSpacingMm;

        foreach (PlacedEntry host in sheet.Entries)
        {
            if (host.Holes.Count == 0)
            {
                continue;
            }

            foreach (HolePolygonInfo hole in host.Holes)
            {
                double holeW = hole.MaxX - hole.MinX - (2.0 * spacing);
                double holeH = hole.MaxY - hole.MinY - (2.0 * spacing);
                if (holeW <= 1.0 || holeH <= 1.0)
                {
                    continue;
                }

                foreach (PartOrientation orient in orientations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (orient.WidthMm > holeW + 1e-6 || orient.HeightMm > holeH + 1e-6)
                    {
                        continue;
                    }

                    double xStart = hole.MinX + spacing;
                    double yStart = hole.MinY + spacing;
                    double xEnd = hole.MaxX - spacing - orient.WidthMm;
                    double yEnd = hole.MaxY - spacing - orient.HeightMm;

                    double step = ResolveStep(config, orient, holeW, holeH);
                    for (double x = xStart; x <= xEnd + 1e-6; x = AdvanceGridCoord(x, xEnd, step))
                    {
                        double cx = Math.Min(x, xEnd);
                        for (double y = yStart; y <= yEnd + 1e-6; y = AdvanceGridCoord(y, yEnd, step))
                        {
                            double cy = Math.Min(y, yEnd);
                            if (CanFitInsideSpecificHole(sheet, host, hole, orient, cx, cy, spacing, config.AllowNestingInHoles, out (double X, double Y)[]? placedPoly))
                            {
                                (double compX, double compY, (double X, double Y)[] compPoly) = CompactHolePlacement(
                                    sheet,
                                    host,
                                    hole,
                                    orient,
                                    cx,
                                    cy,
                                    xStart,
                                    yStart,
                                    spacing,
                                    config.AllowNestingInHoles,
                                    placedPoly!);

                                placement = BuildPlacedEntry(inst, sheet.SheetIndex, orient, compX, compY, compPoly, placedInsideHole: true);
                                return true;
                            }

                            if (y >= yEnd - 1e-6) break;
                        }

                        if (x >= xEnd - 1e-6) break;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryPlaceOnSheet(
        SheetWorkspace sheet,
        ExpandedInstance inst,
        IReadOnlyList<PartOrientation> orientations,
        NestingSheetConfig config,
        CancellationToken cancellationToken,
        out PlacedEntry? placement)
    {
        placement = null;
        double spacing = config.PartSpacingMm;
        double usableW = sheet.MaxX - sheet.MinX;
        double usableH = sheet.MaxY - sheet.MinY;

        if (sheet.Entries.Count == 0)
        {
            // Na chapa vazia, escolhe a orientação que cabe e deixa melhor aproveitamento / menor altura/largura
            PartOrientation? firstFit = orientations
                .Where(o => o.WidthMm <= usableW + 1e-6 && o.HeightMm <= usableH + 1e-6)
                .OrderBy(o => o.WidthMm * o.HeightMm)
                .ThenBy(o => o.WidthMm)
                .FirstOrDefault();
            if (firstFit is null)
            {
                return false;
            }

            (double X, double Y)[] poly = PolygonNestingGeometry.TranslatePoints(firstFit.OuterContour, sheet.MinX, sheet.MinY);
            placement = BuildPlacedEntry(inst, sheet.SheetIndex, firstFit, sheet.MinX, sheet.MinY, poly, placedInsideHole: false);
            return true;
        }

        double bestScore = double.PositiveInfinity;
        PartOrientation? bestOrient = null;
        double bestX = 0.0;
        double bestY = 0.0;
        (double X, double Y)[]? bestPoly = null;

        foreach (PartOrientation orient in orientations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (orient.WidthMm > usableW + 1e-6 || orient.HeightMm > usableH + 1e-6)
            {
                continue;
            }

            double maxAllowX = sheet.MaxX - orient.WidthMm;
            double maxAllowY = sheet.MaxY - orient.HeightMm;

            List<PlacementCandidate> candidates = BuildSheetCandidates(sheet, orient, spacing, config, maxAllowX, maxAllowY);
            foreach (PlacementCandidate cand in candidates)
            {
                if (cand.ScorePrimary >= bestScore)
                {
                    break;
                }

                if (!CanFitOnSheet(sheet, orient, cand.X, cand.Y, spacing, config.AllowNestingInHoles, out (double X, double Y)[]? candidatePoly))
                {
                    continue;
                }

                (double compX, double compY, (double X, double Y)[] compPoly) = CompactSheetPlacement(
                    sheet,
                    orient,
                    cand.X,
                    cand.Y,
                    spacing,
                    config.AllowNestingInHoles,
                    candidatePoly!);

                double newMaxX = Math.Max(sheet.UsedBoundsMaxX, compX + orient.WidthMm);
                double score = ComputeEnvelopeScore(newMaxX, compX, compY);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestOrient = orient;
                    bestX = compX;
                    bestY = compY;
                    bestPoly = compPoly;
                }

                // Se já encaixou sem expandir a extensão horizontal atual no canto esquerdo-superior, encerramos busca desta orientação
                if (compX + orient.WidthMm <= sheet.UsedBoundsMaxX + 1e-6)
                {
                    break;
                }
            }
        }

        if (bestOrient is not null && bestPoly is not null)
        {
            placement = BuildPlacedEntry(inst, sheet.SheetIndex, bestOrient, bestX, bestY, bestPoly, placedInsideHole: false);
            return true;
        }

        return false;
    }

    private static List<PlacementCandidate> BuildSheetCandidates(
        SheetWorkspace sheet,
        PartOrientation orient,
        double spacing,
        NestingSheetConfig config,
        double maxAllowX,
        double maxAllowY)
    {
        var xs = new SortedSet<double>();
        var ys = new SortedSet<double>();

        void AddX(double raw)
        {
            if (raw >= sheet.MinX - 1e-6 && raw <= maxAllowX + 1e-6)
            {
                double clamped = Math.Clamp(raw, sheet.MinX, maxAllowX);
                // Evita acúmulo denso (< 1,5 mm) mantendo 100% dos cantos exatos de encaixe
                foreach (double existing in xs)
                {
                    if (Math.Abs(existing - clamped) < 1.5 && Math.Abs(clamped - sheet.MinX) > 1e-3 && Math.Abs(clamped - maxAllowX) > 1e-3)
                    {
                        return;
                    }
                }

                xs.Add(Math.Round(clamped, 3));
            }
        }

        void AddY(double raw)
        {
            if (raw >= sheet.MinY - 1e-6 && raw <= maxAllowY + 1e-6)
            {
                double clamped = Math.Clamp(raw, sheet.MinY, maxAllowY);
                foreach (double existing in ys)
                {
                    if (Math.Abs(existing - clamped) < 1.5 && Math.Abs(clamped - sheet.MinY) > 1e-3 && Math.Abs(clamped - maxAllowY) > 1e-3)
                    {
                        return;
                    }
                }

                ys.Add(Math.Round(clamped, 3));
            }
        }

        AddX(sheet.MinX);
        AddX(maxAllowX);
        AddY(sheet.MinY);
        AddY(maxAllowY);

        foreach (PlacedEntry entry in sheet.Entries)
        {
            PlacedNestingPart p = entry.Placement;
            AddX(p.MinXMm);
            AddX(p.MaxXMm + spacing);
            AddX(p.MaxXMm - orient.WidthMm);
            AddX(p.MinXMm - orient.WidthMm - spacing);

            AddY(p.MinYMm);
            AddY(p.MaxYMm + spacing);
            AddY(p.MaxYMm - orient.HeightMm);
            AddY(p.MinYMm - orient.HeightMm - spacing);

            // Projeções de vértices internos reais (concavidades em L/T/U), ignorando dentes finos na borda
            if (p.TransformedOuterContour.Count <= 24)
            {
                for (int v = 0; v < p.TransformedOuterContour.Count; v++)
                {
                    (double vx, double vy) = p.TransformedOuterContour[v];
                    AddX(vx + spacing);
                    AddX(vx - orient.WidthMm - spacing);
                    AddY(vy + spacing);
                    AddY(vy - orient.HeightMm - spacing);
                }
            }
            else
            {
                for (int v = 0; v < p.TransformedOuterContour.Count; v++)
                {
                    (double vx, double vy) = p.TransformedOuterContour[v];
                    if (vx > p.MinXMm + 8.0 && vx < p.MaxXMm - 8.0)
                    {
                        AddX(vx + spacing);
                        AddX(vx - orient.WidthMm - spacing);
                    }

                    if (vy > p.MinYMm + 8.0 && vy < p.MaxYMm - 8.0)
                    {
                        AddY(vy + spacing);
                        AddY(vy - orient.HeightMm - spacing);
                    }
                }
            }
        }

        // Complementa com grade adaptativa leve caso haja bolsões internos não alinhados a quinas
        if (config.PlacementStepMm > 0.0)
        {
            double s = Math.Max(2.0, config.PlacementStepMm);
            for (double x = sheet.MinX; x <= maxAllowX; x += s) AddX(x);
            for (double y = sheet.MinY; y <= maxAllowY; y += s) AddY(y);
        }
        else if (sheet.Entries.Count <= 8)
        {
            double stepX = Math.Max(16.0, (maxAllowX - sheet.MinX) / 6.0);
            double stepY = Math.Max(16.0, (maxAllowY - sheet.MinY) / 6.0);
            for (double x = sheet.MinX; x <= maxAllowX; x += stepX) AddX(x);
            for (double y = sheet.MinY; y <= maxAllowY; y += stepY) AddY(y);
        }

        var list = new List<PlacementCandidate>(xs.Count * ys.Count);
        foreach (double x in xs)
        {
            double newMaxX = Math.Max(sheet.UsedBoundsMaxX, x + orient.WidthMm);
            foreach (double y in ys)
            {
                list.Add(new PlacementCandidate
                {
                    ScorePrimary = ComputeEnvelopeScore(newMaxX, x, y),
                    X = x,
                    Y = y,
                });
            }
        }

        list.Sort(static (a, b) =>
        {
            int c = a.ScorePrimary.CompareTo(b.ScorePrimary);
            if (c != 0) return c;
            c = a.X.CompareTo(b.X);
            return c != 0 ? c : a.Y.CompareTo(b.Y);
        });

        return list;
    }

    private static double ComputeEnvelopeScore(double newMaxX, double x, double y)
        => (newMaxX * 1000.0) + (x * 1.0) + (y * 0.35);

    private static (double X, double Y, (double X, double Y)[] Poly) CompactSheetPlacement(
        SheetWorkspace sheet,
        PartOrientation orient,
        double startX,
        double startY,
        double spacing,
        bool allowNestingInHoles,
        (double X, double Y)[] startPoly)
    {
        double curX = startX;
        double curY = startY;
        (double X, double Y)[] curPoly = startPoly;

        // Compactação esquerda por redução iterativa de passo mantendo ausência de obstáculos intermediários
        if (curX > sheet.MinX + MinCompactionEpsMm)
        {
            double step = Math.Max(2.0, (curX - sheet.MinX) * 0.25);
            while (step >= MinCompactionEpsMm)
            {
                while (curX - step >= sheet.MinX - 1e-6 &&
                       CanFitOnSheet(sheet, orient, Math.Max(sheet.MinX, curX - step), curY, spacing, allowNestingInHoles, out (double X, double Y)[]? moved))
                {
                    curX = Math.Max(sheet.MinX, curX - step);
                    curPoly = moved!;
                    if (curX <= sheet.MinX + 1e-6) break;
                }

                step *= 0.4;
            }
        }

        // Compactação vertical para o topo útil
        if (curY > sheet.MinY + MinCompactionEpsMm)
        {
            double step = Math.Max(2.0, (curY - sheet.MinY) * 0.25);
            while (step >= MinCompactionEpsMm)
            {
                while (curY - step >= sheet.MinY - 1e-6 &&
                       CanFitOnSheet(sheet, orient, curX, Math.Max(sheet.MinY, curY - step), spacing, allowNestingInHoles, out (double X, double Y)[]? moved))
                {
                    curY = Math.Max(sheet.MinY, curY - step);
                    curPoly = moved!;
                    if (curY <= sheet.MinY + 1e-6) break;
                }

                step *= 0.4;
            }
        }

        return (curX, curY, curPoly);
    }

    private static (double X, double Y, (double X, double Y)[] Poly) CompactHolePlacement(
        SheetWorkspace sheet,
        PlacedEntry host,
        HolePolygonInfo hole,
        PartOrientation orient,
        double startX,
        double startY,
        double minX,
        double minY,
        double spacing,
        bool allowNestingInHoles,
        (double X, double Y)[] startPoly)
    {
        double curX = startX;
        double curY = startY;
        (double X, double Y)[] curPoly = startPoly;

        for (int axis = 0; axis < 2; axis++)
        {
            double step = 2.0;
            while (step >= MinCompactionEpsMm)
            {
                while (true)
                {
                    double nextX = axis == 0 ? Math.Max(minX, curX - step) : curX;
                    double nextY = axis == 1 ? Math.Max(minY, curY - step) : curY;
                    if ((axis == 0 && Math.Abs(nextX - curX) < 1e-6) || (axis == 1 && Math.Abs(nextY - curY) < 1e-6))
                    {
                        break;
                    }

                    if (!CanFitInsideSpecificHole(sheet, host, hole, orient, nextX, nextY, spacing, allowNestingInHoles, out (double X, double Y)[]? moved))
                    {
                        break;
                    }

                    curX = nextX;
                    curY = nextY;
                    curPoly = moved!;
                }

                step *= 0.4;
            }
        }

        return (curX, curY, curPoly);
    }

    private static bool CanFitInsideSpecificHole(
        SheetWorkspace sheet,
        PlacedEntry host,
        HolePolygonInfo hole,
        PartOrientation orient,
        double x,
        double y,
        double spacing,
        bool allowNestingInHoles,
        out (double X, double Y)[]? candidatePoly)
    {
        candidatePoly = null;
        double maxX = x + orient.WidthMm;
        double maxY = y + orient.HeightMm;
        if (x < hole.MinX + spacing - 1e-4 || y < hole.MinY + spacing - 1e-4 ||
            maxX > hole.MaxX - spacing + 1e-4 || maxY > hole.MaxY - spacing + 1e-4)
        {
            return false;
        }

        (double X, double Y)[] poly = PolygonNestingGeometry.TranslatePoints(orient.OuterContour, x, y);
        if (!PolygonNestingGeometry.IsInsidePolygonWithClearance(poly, hole.Points, spacing))
        {
            return false;
        }

        foreach (PlacedEntry other in sheet.Entries)
        {
            if (ReferenceEquals(other, host))
            {
                continue;
            }

            if (ConflictsWithPlaced(other, poly, x, y, maxX, maxY, spacing, allowNestingInHoles))
            {
                return false;
            }
        }

        candidatePoly = poly;
        return true;
    }

    private static bool CanFitOnSheet(
        SheetWorkspace sheet,
        PartOrientation orient,
        double x,
        double y,
        double spacing,
        bool allowNestingInHoles,
        out (double X, double Y)[]? candidatePoly)
    {
        candidatePoly = null;
        double maxX = x + orient.WidthMm;
        double maxY = y + orient.HeightMm;

        if (x < sheet.MinX - 1e-6 || y < sheet.MinY - 1e-6 ||
            maxX > sheet.MaxX + 1e-6 || maxY > sheet.MaxY + 1e-6)
        {
            return false;
        }

        (double X, double Y)[]? poly = null;
        foreach (PlacedEntry placed in sheet.Entries)
        {
            PlacedNestingPart p = placed.Placement;
            if (maxX + spacing - 1e-4 <= p.MinXMm ||
                x - spacing + 1e-4 >= p.MaxXMm ||
                maxY + spacing - 1e-4 <= p.MinYMm ||
                y - spacing + 1e-4 >= p.MaxYMm)
            {
                continue;
            }

            poly ??= PolygonNestingGeometry.TranslatePoints(orient.OuterContour, x, y);
            if (ConflictsWithPlaced(placed, poly, x, y, maxX, maxY, spacing, allowNestingInHoles))
            {
                return false;
            }
        }

        candidatePoly = poly ?? PolygonNestingGeometry.TranslatePoints(orient.OuterContour, x, y);
        return true;
    }

    private static bool ConflictsWithPlaced(
        PlacedEntry placed,
        (double X, double Y)[] candidatePoly,
        double candMinX,
        double candMinY,
        double candMaxX,
        double candMaxY,
        double spacing,
        bool allowNestingInHoles)
    {
        PlacedNestingPart p = placed.Placement;
        if (candMaxX + spacing - 1e-4 <= p.MinXMm ||
            candMinX - spacing + 1e-4 >= p.MaxXMm ||
            candMaxY + spacing - 1e-4 <= p.MinYMm ||
            candMinY - spacing + 1e-4 >= p.MaxYMm)
        {
            return false;
        }

        if (PolygonNestingGeometry.IsMinimumSpacingSatisfied(candidatePoly, p.TransformedOuterContour, spacing))
        {
            return false;
        }

        if (allowNestingInHoles && placed.Holes.Count > 0)
        {
            foreach (HolePolygonInfo hole in placed.Holes)
            {
                if (candMinX >= hole.MinX + spacing - 1e-4 &&
                    candMinY >= hole.MinY + spacing - 1e-4 &&
                    candMaxX <= hole.MaxX - spacing + 1e-4 &&
                    candMaxY <= hole.MaxY - spacing + 1e-4 &&
                    PolygonNestingGeometry.IsInsidePolygonWithClearance(candidatePoly, hole.Points, spacing))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static PlacedEntry BuildPlacedEntry(
        ExpandedInstance inst,
        int sheetIndex,
        PartOrientation orient,
        double x,
        double y,
        (double X, double Y)[] placedOuter,
        bool placedInsideHole)
    {
        var transformedFeatures = new List<NestingPolyline2D>(orient.InternalFeatures.Count);
        var holes = new List<HolePolygonInfo>();

        for (int i = 0; i < orient.InternalFeatures.Count; i++)
        {
            NestingPolyline2D src = orient.InternalFeatures[i];
            (double X, double Y)[] pts = PolygonNestingGeometry.TranslatePoints(src.Points, x, y);
            transformedFeatures.Add(src with { Points = pts });

            if (src.IsClosed && src.Layer == NestingSemanticLayer.CutInternal && pts.Length >= 3)
            {
                (double hMinX, double hMinY, double hMaxX, double hMaxY) = PolygonNestingGeometry.ComputeBounds(pts);
                holes.Add(new HolePolygonInfo(pts, hMinX, hMinY, hMaxX, hMaxY));
            }
        }

        var transformedRasters = new NestingRasterOverlay[orient.RasterOverlays.Count];
        for (int i = 0; i < orient.RasterOverlays.Count; i++)
        {
            NestingRasterOverlay src = orient.RasterOverlays[i];
            transformedRasters[i] = src with
            {
                X = src.X + x,
                Y = src.Y + y,
            };
        }

        var placement = new PlacedNestingPart
        {
            Part = inst.Part,
            InstanceIndex = inst.InstanceIndex,
            SheetIndex = sheetIndex,
            OffsetXMm = Math.Round(x, 4),
            OffsetYMm = Math.Round(y, 4),
            RotationDeg = orient.AngleDeg,
            TransformedOuterContour = placedOuter,
            TransformedInternalFeatures = transformedFeatures,
            TransformedRasterOverlays = transformedRasters,
            MinXMm = x,
            MinYMm = y,
            MaxXMm = x + orient.WidthMm,
            MaxYMm = y + orient.HeightMm,
            PlacedInsideHole = placedInsideHole,
        };

        return new PlacedEntry
        {
            Placement = placement,
            Holes = holes,
        };
    }

    private static IReadOnlyList<PartOrientation> BuildUniqueOrientations(
        NestingPart part,
        NestingRotationPolicy policy)
    {
        double[] angles = policy switch
        {
            NestingRotationPolicy.Locked0 => [0.0],
            NestingRotationPolicy.Step180 => [0.0, 180.0],
            NestingRotationPolicy.Step90 => [0.0, 90.0, 180.0, 270.0],
            NestingRotationPolicy.Step45 => [0.0, 45.0, 90.0, 135.0, 180.0, 225.0, 270.0, 315.0],
            _ => [0.0, 90.0, 180.0, 270.0],
        };

        var result = new List<PartOrientation>(angles.Length);
        foreach (double angle in angles)
        {
            (IReadOnlyList<(double X, double Y)> outer,
             IReadOnlyList<NestingPolyline2D> feats,
             IReadOnlyList<NestingRasterOverlay> rasters,
             double w,
             double h) = PolygonNestingGeometry.RotateAndNormalizeToZero(part, angle);

            if (result.Any(existing => HasEquivalentGeometry(existing, w, h, outer, feats.Count, rasters.Count)))
            {
                continue;
            }

            double[] vx = outer.Select(p => Math.Round(p.X, 2)).Distinct().OrderBy(v => v).ToArray();
            double[] vy = outer.Select(p => Math.Round(p.Y, 2)).Distinct().OrderBy(v => v).ToArray();

            result.Add(new PartOrientation(angle, w, h, outer, feats, rasters, vx, vy));
        }

        return result;
    }

    private static bool HasEquivalentGeometry(
        PartOrientation a,
        double w,
        double h,
        IReadOnlyList<(double X, double Y)> outerB,
        int featCountB,
        int rasterCountB)
    {
        // Somente deduplica rotações para formas puras ou simétricas simples sem feições internas assimétricas
        if (featCountB > 0 || rasterCountB > 0 || a.InternalFeatures.Count > 0 || a.RasterOverlays.Count > 0)
        {
            return false;
        }

        if (Math.Abs(a.WidthMm - w) > 1e-3 || Math.Abs(a.HeightMm - h) > 1e-3 || a.OuterContour.Count != outerB.Count)
        {
            return false;
        }

        foreach ((double x, double y) in outerB)
        {
            bool matched = false;
            for (int i = 0; i < a.OuterContour.Count; i++)
            {
                if (Math.Abs(a.OuterContour[i].X - x) <= 1e-3 && Math.Abs(a.OuterContour[i].Y - y) <= 1e-3)
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static double ResolveStep(NestingSheetConfig config, PartOrientation orient, double spanW, double spanH)
    {
        if (config.PlacementStepMm > 0.0)
        {
            return Math.Max(0.5, config.PlacementStepMm);
        }

        double maxDim = Math.Max(spanW, spanH);
        return Math.Clamp(Math.Min(orient.WidthMm, orient.HeightMm) * 0.35, 2.0, Math.Max(4.0, maxDim * 0.1));
    }

    private static double AdvanceGridCoord(double current, double end, double step)
    {
        if (current < end - 1e-6 && current + step > end)
        {
            return end;
        }

        return current + step;
    }

    private static string FormatWinningSummary(StrategyEvaluation eval)
        => $"{eval.StrategyName}: {eval.TotalPlaced} peças em {eval.Sheets.Count} chapa(s), {eval.OverallUtilizationPercent:0.0}%";
}
