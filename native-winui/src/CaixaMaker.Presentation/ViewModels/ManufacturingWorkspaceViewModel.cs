using System.Collections.ObjectModel;
using System.Globalization;
using CaixaMaker.App.Models;
using CaixaMaker.Cam.Calibration;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaixaMaker.App.ViewModels;

/// <summary>
/// ViewModel da área de Fabricação e Exportação CNC/CAM (SVG multicamadas, DXF R2000, PDF 1:1 e ZIP).
/// </summary>
public sealed partial class ManufacturingWorkspaceViewModel : ObservableObject
{
    private readonly AppPaths _paths;

    public ObservableCollection<CncLayerItemViewModel> Layers { get; } = new();
    public ObservableCollection<GeneratedOutputRecord> RecentOutputs { get; } = new();

    [ObservableProperty]
    private PersonalizationExportMode _selectedExportScope = PersonalizationExportMode.FullBox;

    [ObservableProperty]
    private SurfaceId _selectedSinglePanelId = SurfaceId.Front;

    [ObservableProperty]
    private LaserBedPreset _selectedLaserBedPreset = LaserBedPreset.Laser600x400;

    [ObservableProperty]
    private double _customBedWidthMm = UiSettingsData.DefaultCustomBedWidthMm;

    [ObservableProperty]
    private double _customBedHeightMm = UiSettingsData.DefaultCustomBedHeightMm;

    [ObservableProperty]
    private int _kerfSlotCount = 11;

    [ObservableProperty]
    private double _kerfStepMm = 0.05;

    [ObservableProperty]
    private bool _showFullSheetPreviewInFabrication = true;

    [ObservableProperty]
    private BoxSheetGeometry2D? _currentSheetGeometry;

    [ObservableProperty]
    private bool _includeEngraveOnlyInZip = true;

    [ObservableProperty]
    private bool _includeFullSheetInZip = true;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private double _currentThicknessMm = BoxParameters.DefaultThickness;

    [ObservableProperty]
    private double _currentFolgaMm = BoxParameters.DefaultFolga;

    [ObservableProperty]
    private string _lastExportStatus = "Pronto para gerar arquivos CNC/CAM.";

    private bool _suppressCustomBedAutoSwitch;
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
            LaserBedSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<LaserBedSpec> AvailableLaserBeds { get; } = BuildAvailableLaserBeds();

    public LaserBedSpec SelectedLaserBed => ResolveLaserBedSpec(SelectedLaserBedPreset);

    public bool IsCustomLaserBedSelected => SelectedLaserBedPreset == LaserBedPreset.Custom;

    public LaserBedFitResult CurrentLaserBedFit => EvaluateLaserBedFit(CurrentSheetGeometry);

    public double KerfMinFolgaMm => Math.Round(-((KerfSlotCount - 1) * 0.5 * KerfStepMm), 4);

    public double KerfMaxFolgaMm => Math.Round((KerfSlotCount - 1) * 0.5 * KerfStepMm, 4);

    public string KerfCombRangeSummary => string.Format(
        CultureInfo.InvariantCulture,
        "{0} rasgos: de {1:+0.00;-0.00;0.00} a {2:+0.00;-0.00;0.00} mm (slots {3:0.00}..{4:0.00} mm) • espiga {5:0.#} mm",
        KerfSlotCount,
        KerfMinFolgaMm,
        KerfMaxFolgaMm,
        Math.Max(0.10, CurrentThicknessMm + KerfMinFolgaMm),
        Math.Max(0.10, CurrentThicknessMm + KerfMaxFolgaMm),
        CurrentThicknessMm);

    public event EventHandler? LayerVisibilityChanged;
    public event EventHandler? LaserBedSettingsChanged;

    public ManufacturingWorkspaceViewModel(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

        Layers.Add(new CncLayerItemViewModel(CncLayerSpecs.CutExternal, 1));
        Layers.Add(new CncLayerItemViewModel(CncLayerSpecs.CutInternal, 1));
        Layers.Add(new CncLayerItemViewModel(CncLayerSpecs.EngraveVector));
        Layers.Add(new CncLayerItemViewModel(CncLayerSpecs.EngraveFill));
        Layers.Add(new CncLayerItemViewModel(CncLayerSpecs.Mark));
        Layers.Add(new CncLayerItemViewModel(CncLayerSpecs.EngraveRaster));

        foreach (CncLayerItemViewModel layer in Layers)
        {
            layer.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CncLayerItemViewModel.IsVisible))
                {
                    LayerVisibilityChanged?.Invoke(this, EventArgs.Empty);
                }
            };
        }
    }

    public static IReadOnlyList<LaserBedSpec> BuildAvailableLaserBeds() =>
    [
        new(LaserBedPreset.None, "Sem limite de mesa (Livre)", "Livre", 0.0, 0.0),
        new(LaserBedPreset.Laser400x400, "Laser Compacta (400 × 400 mm)", "400×400", 400.0, 400.0),
        new(LaserBedPreset.Laser600x400, "Laser Bancada (600 × 400 mm)", "600×400", 600.0, 400.0),
        new(LaserBedPreset.Laser900x600, "Laser Industrial (900 × 600 mm)", "900×600", 900.0, 600.0),
        new(LaserBedPreset.Laser1300x900, "Laser / Router Grande (1300 × 900 mm)", "1300×900", 1300.0, 900.0),
        new(
            LaserBedPreset.Custom,
            "Personalizada (L × P mm)",
            "Custom",
            UiSettingsData.DefaultCustomBedWidthMm,
            UiSettingsData.DefaultCustomBedHeightMm),
    ];

    public static LaserBedSpec GetLaserBedSpec(LaserBedPreset preset)
        => BuildAvailableLaserBeds().FirstOrDefault(b => b.Preset == preset)
           ?? BuildAvailableLaserBeds()[0];

    public LaserBedSpec ResolveLaserBedSpec(LaserBedPreset? preset = null)
    {
        LaserBedPreset effective = preset ?? SelectedLaserBedPreset;
        if (effective == LaserBedPreset.Custom)
        {
            double w = ClampLaserBedDim(CustomBedWidthMm, UiSettingsData.DefaultCustomBedWidthMm);
            double h = ClampLaserBedDim(CustomBedHeightMm, UiSettingsData.DefaultCustomBedHeightMm);
            return new LaserBedSpec(
                LaserBedPreset.Custom,
                string.Format(CultureInfo.InvariantCulture, "Personalizada ({0:0.#} × {1:0.#} mm)", w, h),
                string.Format(CultureInfo.InvariantCulture, "{0:0.#}×{1:0.#}", w, h),
                w,
                h);
        }

        return GetLaserBedSpec(effective);
    }

    public void LoadLaserBedSettings(LaserBedPreset preset, double customWidthMm, double customHeightMm)
    {
        _suppressCustomBedAutoSwitch = true;
        try
        {
            CustomBedWidthMm = ClampLaserBedDim(customWidthMm, UiSettingsData.DefaultCustomBedWidthMm);
            CustomBedHeightMm = ClampLaserBedDim(customHeightMm, UiSettingsData.DefaultCustomBedHeightMm);
            SelectedLaserBedPreset = preset;
        }
        finally
        {
            _suppressCustomBedAutoSwitch = false;
        }
    }

    public LaserBedFitResult EvaluateLaserBedFit(
        BoxSheetGeometry2D? sheet,
        LaserBedPreset? presetOverride = null)
    {
        LaserBedSpec bed = ResolveLaserBedSpec(presetOverride);
        double sheetW = sheet is not null ? Math.Round(sheet.Layout.SheetWidth, 3) : 0.0;
        double sheetH = sheet is not null ? Math.Round(sheet.Layout.SheetHeight, 3) : 0.0;

        double maxPanelW = 0.0;
        double maxPanelH = 0.0;
        bool allPanelsFit = true;

        if (sheet is not null)
        {
            foreach (PanelGeometry2D panel in sheet.Panels)
            {
                BoundingBox2D b = panel.LocalBounds;
                maxPanelW = Math.Max(maxPanelW, b.Width);
                maxPanelH = Math.Max(maxPanelH, b.Height);

                if (bed.HasLimit)
                {
                    bool directPanel = b.Width <= bed.WidthMm + 1e-6 && b.Height <= bed.HeightMm + 1e-6;
                    bool rotPanel = b.Height <= bed.WidthMm + 1e-6 && b.Width <= bed.HeightMm + 1e-6;
                    if (!directPanel && !rotPanel)
                    {
                        allPanelsFit = false;
                    }
                }
            }
        }

        if (!bed.HasLimit || sheet is null)
        {
            string livreText = sheet is null
                ? "Sem limite de mesa configurado."
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "Sem limite de mesa • Folha completa: {0:0.#} × {1:0.#} mm",
                    sheetW,
                    sheetH);

            return new LaserBedFitResult(
                Bed: bed,
                SheetWidthMm: sheetW,
                SheetHeightMm: sheetH,
                FitsWithoutRotation: true,
                FitsIfRotated90: true,
                ExcessXMm: 0.0,
                ExcessYMm: 0.0,
                UtilizationPercent: 0.0,
                MaxPanelWidthMm: Math.Round(maxPanelW, 3),
                MaxPanelHeightMm: Math.Round(maxPanelH, 3),
                AllSinglePanelsFit: true,
                Severity: LaserBedFitSeverity.NoLimit,
                SuggestedAlternative: string.Empty,
                SummaryText: livreText);
        }

        double excessX = Math.Max(0.0, Math.Round(sheetW - bed.WidthMm, 3));
        double excessY = Math.Max(0.0, Math.Round(sheetH - bed.HeightMm, 3));
        bool fitsDirect = excessX <= 1e-6 && excessY <= 1e-6;
        bool fitsRotated90 = sheetH <= bed.WidthMm + 1e-6 && sheetW <= bed.HeightMm + 1e-6;

        double bedArea = bed.WidthMm * bed.HeightMm;
        double utilization = bedArea > 1e-6
            ? Math.Round((sheetW * sheetH) / bedArea * 100.0, 1)
            : 0.0;

        LaserBedSpec? nextBiggerBed = AvailableLaserBeds
            .FirstOrDefault(candidate =>
                candidate.HasLimit &&
                sheetW <= candidate.WidthMm + 1e-6 &&
                sheetH <= candidate.HeightMm + 1e-6);

        string suggestion = nextBiggerBed is not null
            ? (allPanelsFit ? $"Peça Individual ou mesa {nextBiggerBed.ShortLabel}" : $"Mesa {nextBiggerBed.ShortLabel}")
            : "Peça Individual (dividir chapas)";

        LaserBedFitSeverity severity;
        string summaryText;
        if (fitsDirect)
        {
            severity = LaserBedFitSeverity.Fits;
            summaryText = string.Format(
                CultureInfo.InvariantCulture,
                "✓ Folha {0:0.#}×{1:0.#} mm cabe na mesa {2} (uso {3:0.0}%)",
                sheetW,
                sheetH,
                bed.ShortLabel,
                utilization);
        }
        else if (fitsRotated90)
        {
            severity = LaserBedFitSeverity.FitsRotatedOnly;
            string axisOverflow = FormatExcessAxesText(excessX, excessY);
            summaryText = string.Format(
                CultureInfo.InvariantCulture,
                "⚠ Folha {0:0.#}×{1:0.#} mm excede {2} • Cabe rotacionada 90° em {3}",
                sheetW,
                sheetH,
                axisOverflow,
                bed.ShortLabel);
        }
        else
        {
            severity = LaserBedFitSeverity.ExceedsBed;
            string axisOverflow = FormatExcessAxesText(excessX, excessY);
            summaryText = string.Format(
                CultureInfo.InvariantCulture,
                "⚠ Folha {0:0.#}×{1:0.#} mm excede mesa {2} {3} • Sugestão: {4}",
                sheetW,
                sheetH,
                bed.ShortLabel,
                axisOverflow,
                suggestion);
        }

        return new LaserBedFitResult(
            Bed: bed,
            SheetWidthMm: sheetW,
            SheetHeightMm: sheetH,
            FitsWithoutRotation: fitsDirect,
            FitsIfRotated90: fitsRotated90,
            ExcessXMm: excessX,
            ExcessYMm: excessY,
            UtilizationPercent: utilization,
            MaxPanelWidthMm: Math.Round(maxPanelW, 3),
            MaxPanelHeightMm: Math.Round(maxPanelH, 3),
            AllSinglePanelsFit: allPanelsFit,
            Severity: severity,
            SuggestedAlternative: suggestion,
            SummaryText: summaryText);
    }

    private static string FormatExcessAxesText(double excessX, double excessY)
    {
        if (excessX > 1e-6 && excessY > 1e-6)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "em X (+{0:0.#} mm) e Y (+{1:0.#} mm)",
                excessX,
                excessY);
        }

        if (excessX > 1e-6)
        {
            return string.Format(CultureInfo.InvariantCulture, "em X (+{0:0.#} mm)", excessX);
        }

        if (excessY > 1e-6)
        {
            return string.Format(CultureInfo.InvariantCulture, "em Y (+{0:0.#} mm)", excessY);
        }

        return string.Empty;
    }

    private static double ClampLaserBedDim(double value, double fallback)
        => Math.Clamp(
            double.IsFinite(value) ? Math.Round(value, 2) : fallback,
            UiSettingsData.MinLaserBedDimensionMm,
            UiSettingsData.MaxLaserBedDimensionMm);

    [RelayCommand]
    public void ApplyKerfCombPreset(string? presetKey)
    {
        switch (presetKey?.Trim().ToLowerInvariant())
        {
            case "wide":
            case "amplo":
                KerfSlotCount = 11;
                KerfStepMm = 0.10;
                break;
            case "extended":
            case "estendido":
                KerfSlotCount = 15;
                KerfStepMm = 0.05;
                break;
            default:
                KerfSlotCount = 11;
                KerfStepMm = 0.05;
                break;
        }
    }

    public KerfCalibrationCombResult GenerateCurrentKerfComb()
        => KerfCalibrationCombGenerator.Default.Generate(new KerfCalibrationCombOptions
        {
            ThicknessMm = CurrentThicknessMm,
            CenterFolgaMm = 0.0,
            SlotCount = KerfSlotCount,
            StepMm = KerfStepMm,
        });

    partial void OnSelectedExportScopeChanged(PersonalizationExportMode value)
    {
        ShowFullSheetPreviewInFabrication = value == PersonalizationExportMode.FullBox;
    }

    partial void OnSelectedLaserBedPresetChanged(LaserBedPreset value)
    {
        OnPropertyChanged(nameof(IsCustomLaserBedSelected));
        OnPropertyChanged(nameof(SelectedLaserBed));
        OnPropertyChanged(nameof(CurrentLaserBedFit));
        if (!_suppressCustomBedAutoSwitch)
        {
            LaserBedSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnCustomBedWidthMmChanged(double value)
    {
        double clamped = ClampLaserBedDim(value, UiSettingsData.DefaultCustomBedWidthMm);
        if (Math.Abs(clamped - value) > 1e-6)
        {
            CustomBedWidthMm = clamped;
            return;
        }

        if (!_suppressCustomBedAutoSwitch && SelectedLaserBedPreset != LaserBedPreset.Custom)
        {
            SelectedLaserBedPreset = LaserBedPreset.Custom;
        }

        OnPropertyChanged(nameof(SelectedLaserBed));
        OnPropertyChanged(nameof(CurrentLaserBedFit));
        if (!_suppressCustomBedAutoSwitch)
        {
            if (IsInteractiveUpdate)
            {
                _hasDeferredInteractiveCommit = true;
            }
            LaserBedSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnCustomBedHeightMmChanged(double value)
    {
        double clamped = ClampLaserBedDim(value, UiSettingsData.DefaultCustomBedHeightMm);
        if (Math.Abs(clamped - value) > 1e-6)
        {
            CustomBedHeightMm = clamped;
            return;
        }

        if (!_suppressCustomBedAutoSwitch && SelectedLaserBedPreset != LaserBedPreset.Custom)
        {
            SelectedLaserBedPreset = LaserBedPreset.Custom;
        }

        OnPropertyChanged(nameof(SelectedLaserBed));
        OnPropertyChanged(nameof(CurrentLaserBedFit));
        if (!_suppressCustomBedAutoSwitch)
        {
            if (IsInteractiveUpdate)
            {
                _hasDeferredInteractiveCommit = true;
            }
            LaserBedSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnKerfSlotCountChanged(int value)
    {
        int odd = Math.Clamp(value % 2 == 0 ? value + 1 : value, 5, 21);
        if (odd != value)
        {
            KerfSlotCount = odd;
            return;
        }

        OnPropertyChanged(nameof(KerfMinFolgaMm));
        OnPropertyChanged(nameof(KerfMaxFolgaMm));
        OnPropertyChanged(nameof(KerfCombRangeSummary));
    }

    partial void OnKerfStepMmChanged(double value)
    {
        double clamped = Math.Clamp(
            double.IsFinite(value) ? Math.Round(value, 3) : 0.05,
            0.02,
            0.25);
        if (Math.Abs(clamped - value) > 1e-6)
        {
            KerfStepMm = clamped;
            return;
        }

        OnPropertyChanged(nameof(KerfMinFolgaMm));
        OnPropertyChanged(nameof(KerfMaxFolgaMm));
        OnPropertyChanged(nameof(KerfCombRangeSummary));
    }

    partial void OnCurrentThicknessMmChanged(double value)
    {
        OnPropertyChanged(nameof(KerfCombRangeSummary));
    }

    partial void OnCurrentSheetGeometryChanged(BoxSheetGeometry2D? value)
    {
        OnPropertyChanged(nameof(CurrentLaserBedFit));
    }

    public void RefreshLayerCounts(SurfaceManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        IReadOnlyList<PersonalizationElement> elements = manager.ListElements();
        foreach (CncLayerItemViewModel layer in Layers)
        {
            if (layer.Operation == CncOperationType.CutExternal)
            {
                layer.ElementCount = manager.ListSurfaces().Count;
            }
            else if (layer.Operation == CncOperationType.CutInternal)
            {
                layer.ElementCount = manager.BoxModel.HasLid ? 1 : 0;
                layer.ElementCount += elements.Count(e => e.Operation == CncOperationType.CutInternal && e.Visible);
            }
            else
            {
                layer.ElementCount = elements.Count(e => e.Operation == layer.Operation && e.Visible);
            }
        }
    }

    public bool IsLayerVisible(CncOperationType operation)
        => Layers.FirstOrDefault(l => l.Operation == operation)?.IsVisible ?? true;

    public string BuildSvgForScope(
        SurfaceManager manager,
        PersonalizationExportMode scope,
        SurfaceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var exporter = new PersonalizationSvgExporter(manager);
        SurfaceId effectivePanel = panelId ?? SelectedSinglePanelId;

        return scope switch
        {
            PersonalizationExportMode.PanelCombined => exporter.ExportPanelCombinedSvg(effectivePanel),
            PersonalizationExportMode.PanelEngraveOnly => exporter.ExportPanelEngraveOnlySvg(effectivePanel),
            _ => exporter.ExportFullBoxSvg(),
        };
    }

    public byte[] GenerateSingleFileBytes(
        SurfaceManager manager,
        VectorExportFormat format,
        PersonalizationExportMode scope,
        SurfaceId? panelId = null)
    {
        string svg = BuildSvgForScope(manager, scope, panelId);
        return VectorConverter.SvgToExportBytes(svg, format);
    }

    public ExportBundleResult GenerateBundleZip(
        SurfaceManager manager,
        string projectPrefix)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var exporter = new PersonalizationSvgExporter(manager);
        string safePrefix = SanitizeFileStem(projectPrefix);
        return exporter.ExportAllPanelsBundle(
            includeEngraveOnly: IncludeEngraveOnlyInZip,
            includeFullSheet: IncludeFullSheetInZip,
            projectPrefix: safePrefix);
    }

    /// <summary>
    /// Gera e salva em 1 clique o Pente de Calibração de Kerf para a espessura atual (<see cref="CurrentThicknessMm"/>)
    /// nos formatos <c>svg</c>, <c>dxf</c> ou <c>pdf</c>, registrando na lista de saídas da sessão.
    /// </summary>
    [RelayCommand]
    public Task<string?> ExportKerfCalibrationCombAsync(string? format)
    {
        string normalized = string.IsNullOrWhiteSpace(format)
            ? "svg"
            : format.Trim().ToLowerInvariant();

        VectorExportFormat vectorFormat = normalized switch
        {
            "svg" => VectorExportFormat.Svg,
            "dxf" => VectorExportFormat.Dxf,
            "pdf" => VectorExportFormat.Pdf,
            _ => throw new ArgumentException(
                $"Formato inválido para pente de calibração: '{format}'. Utilize 'svg', 'dxf' ou 'pdf'.",
                nameof(format)),
        };

        string fileName = string.Format(
            CultureInfo.InvariantCulture,
            "pente_kerf_{0:0.0}mm.{1}",
            CurrentThicknessMm,
            normalized);
        string destinationPath = BuildDefaultOutputPath(fileName);
        string saved = ExportKerfCalibrationCombToFile(
            vectorFormat,
            CurrentThicknessMm,
            destinationPath,
            centerFolgaMm: 0.0,
            slotCount: KerfSlotCount,
            stepMm: KerfStepMm);
        return Task.FromResult<string?>(saved);
    }

    /// <summary>
    /// Gera o Pente de Calibração de Kerf para uma espessura específica em caminho explícito
    /// e registra o arquivo gerado em <see cref="RecentOutputs"/>.
    /// </summary>
    public string ExportKerfCalibrationCombToFile(
        VectorExportFormat format,
        double thicknessMm,
        string destinationPath,
        double centerFolgaMm = 0.0,
        int? slotCount = null,
        double? stepMm = null)
    {
        int effectiveSlots = slotCount ?? KerfSlotCount;
        double effectiveStep = stepMm ?? KerfStepMm;
        IsExporting = true;
        try
        {
            byte[] bytes = KerfCalibrationCombGenerator.Default.ExportBytes(
                thicknessMm,
                format,
                centerFolgaMm,
                effectiveSlots,
                effectiveStep);
            string label = $"KERF {format.ToString().ToUpperInvariant()}";
            return SaveBytesToFile(bytes, destinationPath, label);
        }
        finally
        {
            IsExporting = false;
        }
    }

    public string SaveBytesToFile(
        byte[] content,
        string destinationPath,
        string formatLabel)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Caminho de destino inválido.", nameof(destinationPath));
        }

        string fullPath = Path.GetFullPath(destinationPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(fullPath, content);
        var record = new GeneratedOutputRecord(
            Filename: Path.GetFileName(fullPath),
            FormatLabel: formatLabel,
            FullPath: fullPath,
            SizeBytes: content.LongLength,
            TimestampText: DateTimeOffset.Now.ToString("HH:mm:ss"));

        RecentOutputs.Insert(0, record);
        while (RecentOutputs.Count > 12)
        {
            RecentOutputs.RemoveAt(RecentOutputs.Count - 1);
        }

        LastExportStatus = $"{formatLabel} salvo: {record.Filename} ({FormatSize(content.LongLength)})";
        return fullPath;
    }

    public string BuildDefaultOutputPath(string suggestedFileName)
    {
        _paths.EnsureDirs();
        return Path.Combine(_paths.OutputsDir, suggestedFileName);
    }

    public static string BuildSuggestedKerfFileName(
        double thicknessMm,
        VectorExportFormat format = VectorExportFormat.Svg)
    {
        string ext = format switch
        {
            VectorExportFormat.Dxf => "dxf",
            VectorExportFormat.Pdf => "pdf",
            _ => "svg",
        };
        return string.Format(
            CultureInfo.InvariantCulture,
            "pente-kerf-mdf-{0:0.0}mm.{1}",
            thicknessMm,
            ext);
    }

    public static string BuildSuggestedFileName(
        string? projectName,
        string fallbackModelName,
        VectorExportFormat? format,
        bool isZip = false)
    {
        string stem = SanitizeFileStem(string.IsNullOrWhiteSpace(projectName) ? fallbackModelName : projectName);
        if (isZip)
        {
            return $"{stem}_pacote_cnc.zip";
        }

        string ext = format switch
        {
            VectorExportFormat.Dxf => "dxf",
            VectorExportFormat.Pdf => "pdf",
            _ => "svg",
        };
        return $"{stem}_cnc.{ext}";
    }

    private static string SanitizeFileStem(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "caixa-maker";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(input.Trim()
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch)
            .ToArray())
            .Trim('_');
        return string.IsNullOrEmpty(cleaned) ? "caixa-maker" : cleaned;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
