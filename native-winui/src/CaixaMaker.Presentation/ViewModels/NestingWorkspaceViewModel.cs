using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.Cam.Dxf;
using CaixaMaker.Cam.Nesting;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Nesting;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaixaMaker.App.ViewModels;

/// <summary>
/// ViewModel da área dedicada do Otimizador de Corte 2D (Nesting), orquestrando fila de peças
/// (chapas da caixa em memória + SVG/DXF externos), otimização assíncrona via <see cref="Task.Run"/>,
/// navegação entre chapas e exportação 1:1 (SVG, DXF R2000, PDF 1.4 e ZIP).
/// </summary>
public sealed partial class NestingWorkspaceViewModel : ObservableObject
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public const string GlobalRotationPerPart = "Por peça";
    public const string GlobalRotationLocked0 = "Travado 0°";
    public const string GlobalRotationStep180 = "0° / 180°";
    public const string GlobalRotationStep90 = "0° / 90°";
    public const string GlobalRotationStep45 = "45°";

    private readonly AppPaths _paths;
    private readonly INativeFileDialogService _dialogs;
    private readonly Func<(double WidthMm, double HeightMm)?>? _activeLaserBedProvider;
    private readonly TrueShapeNestingEngine _engine = new();
    private readonly BoxPanelNestingAdapter _boxAdapter = new();
    private readonly NestingSheetExporter _exporter = new();

    private CancellationTokenSource? _activeCts;
    private int _importCounter;

    public ObservableCollection<NestingPartItemViewModel> Parts { get; } = new();

    public IReadOnlyList<string> GlobalRotationOptions { get; } =
    [
        GlobalRotationPerPart,
        GlobalRotationLocked0,
        GlobalRotationStep180,
        GlobalRotationStep90,
        GlobalRotationStep45,
    ];

    [ObservableProperty]
    private bool _keepImportedFileAsSinglePart;

    [ObservableProperty]
    private bool _hasActiveBoxProject;

    [ObservableProperty]
    private int _boxCopiesCount = 1;

    [ObservableProperty]
    private double _sheetWidthMm = 600.0;

    [ObservableProperty]
    private double _sheetHeightMm = 400.0;

    [ObservableProperty]
    private double _marginMm = 5.0;

    [ObservableProperty]
    private double _partSpacingMm = 3.0;

    [ObservableProperty]
    private bool _allowNestingInHoles = true;

    [ObservableProperty]
    private string _selectedGlobalRotationLabel = GlobalRotationPerPart;

    [ObservableProperty]
    private bool _isOptimizing;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressStatusText = "Adicione chapas da caixa ou importe arquivos SVG/DXF para iniciar.";

    [ObservableProperty]
    private NestingResult? _currentResult;

    [ObservableProperty]
    private int _selectedSheetIndex;

    [ObservableProperty]
    private bool _showSheetBoundary = true;

    [ObservableProperty]
    private bool _showPartLabels = true;

    [ObservableProperty]
    private bool _showUsableMargin = true;

    public event EventHandler? VisualStateChanged;

    public bool HasParts => Parts.Count > 0;
    public int TotalRequestedInstances => Parts.Where(p => p.IsEnabled).Sum(p => p.Quantity);
    public bool HasResult => CurrentResult is { Sheets.Count: > 0 };
    public bool CanExport => HasResult && !IsOptimizing;

    public NestingSheetLayout? CurrentSheet =>
        CurrentResult is { Sheets.Count: > 0 } && SelectedSheetIndex >= 0 && SelectedSheetIndex < CurrentResult.Sheets.Count
            ? CurrentResult.Sheets[SelectedSheetIndex]
            : null;

    public string SheetCounterLabel => CurrentResult is { Sheets.Count: > 0 }
        ? $"Chapa {SelectedSheetIndex + 1} de {CurrentResult.Sheets.Count}"
        : "Sem chapas geradas";

    public bool CanGoPreviousSheet => HasResult && SelectedSheetIndex > 0;
    public bool CanGoNextSheet => CurrentResult is { Sheets.Count: > 0 } && SelectedSheetIndex < CurrentResult.Sheets.Count - 1;

    public string OverallUtilizationLabel => CurrentResult is not null
        ? string.Format(PtBr, "{0:0.0}%", CurrentResult.OverallUtilizationPercent)
        : "—";

    public string PlacedSummaryLabel => CurrentResult is not null
        ? $"{CurrentResult.TotalPlacedInstances} / {CurrentResult.TotalRequestedInstances} peças"
        : $"0 / {TotalRequestedInstances} peças";

    public string SheetCountLabel => CurrentResult is null
        ? "0 chapas"
        : CurrentResult.Sheets.Count == 1 ? "1 chapa" : $"{CurrentResult.Sheets.Count} chapas";

    public string ElapsedLabel
    {
        get
        {
            if (CurrentResult is null) return "—";
            double ms = CurrentResult.Elapsed.TotalMilliseconds;
            return ms >= 1000.0
                ? string.Format(PtBr, "{0:0.00} s", ms / 1000.0)
                : string.Format(PtBr, "{0:0} ms", ms);
        }
    }

    public bool HasUnplacedParts => CurrentResult is { UnplacedParts.Count: > 0 };

    public string UnplacedPartsSummary
    {
        get
        {
            if (CurrentResult is not { UnplacedParts.Count: > 0 })
            {
                return string.Empty;
            }

            List<string> grouped = CurrentResult.UnplacedParts
                .GroupBy(u => (u.PartName, u.Reason))
                .Select(g => g.Count() > 1 ? $"{g.Key.PartName} (×{g.Count()}): {g.Key.Reason}" : $"{g.Key.PartName}: {g.Key.Reason}")
                .Take(4)
                .ToList();

            string suffix = CurrentResult.UnplacedParts.Count > grouped.Count ? " …" : string.Empty;
            return $"⚠ {CurrentResult.UnplacedParts.Count} peça(s) não couberam no retalho — {string.Join(" | ", grouped)}{suffix}";
        }
    }

    public NestingRotationPolicy? GlobalRotationOverride => SelectedGlobalRotationLabel switch
    {
        GlobalRotationLocked0 => NestingRotationPolicy.Locked0,
        GlobalRotationStep180 => NestingRotationPolicy.Step180,
        GlobalRotationStep90 => NestingRotationPolicy.Step90,
        GlobalRotationStep45 => NestingRotationPolicy.Step45,
        _ => null,
    };

    public NestingWorkspaceViewModel(
        AppPaths paths,
        INativeFileDialogService dialogs,
        Func<(double WidthMm, double HeightMm)?>? activeLaserBedProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _activeLaserBedProvider = activeLaserBedProvider;

        Parts.CollectionChanged += OnPartsCollectionChanged;
    }

    public NestingSheetConfig BuildCurrentConfig() => new(
        SheetWidthMm: Math.Clamp(SheetWidthMm, 50.0, 5000.0),
        SheetHeightMm: Math.Clamp(SheetHeightMm, 50.0, 5000.0),
        MarginMm: Math.Clamp(MarginMm, 0.0, Math.Min(SheetWidthMm, SheetHeightMm) * 0.4),
        PartSpacingMm: Math.Clamp(PartSpacingMm, 0.0, 50.0),
        AllowNestingInHoles: AllowNestingInHoles,
        GlobalRotationOverride: GlobalRotationOverride);

    /// <summary>Carrega diretamente em memória todas as chapas da caixa ativa (sem exportar/reimportar em disco).</summary>
    public int LoadActiveBoxPanels(
        IBoxModelDefinition model,
        BoxParameters parameters,
        SurfaceManager? surfaceManager = null,
        bool replaceExistingBoxParts = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(parameters);

        IReadOnlyList<NestingPart> extracted = _boxAdapter.ExtractParts(
            model,
            parameters,
            surfaceManager,
            boxCopies: BoxCopiesCount,
            rotationPolicy: NestingRotationPolicy.Step90);

        if (replaceExistingBoxParts)
        {
            for (int i = Parts.Count - 1; i >= 0; i--)
            {
                if (Parts[i].IsFromActiveBox)
                {
                    Parts.RemoveAt(i);
                }
            }
        }

        int insertAt = 0;
        foreach (NestingPart part in extracted)
        {
            NestingPart uniquePart = EnsureUniquePartId(part);
            Parts.Insert(insertAt++, new NestingPartItemViewModel(uniquePart, RemovePartItem, NotifyQueueMetricsChanged));
        }

        HasActiveBoxProject = true;
        ProgressStatusText = $"{extracted.Count} chapa(s) da caixa carregadas em memória (×{BoxCopiesCount} caixa(s)).";
        NotifyQueueMetricsChanged();
        return extracted.Count;
    }

    public async Task<int> ImportExternalFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return 0;
        }

        bool singlePartFlag = KeepImportedFileAsSinglePart;
        var importedParts = new List<NestingPart>();
        var errors = new List<string>();

        await Task.Run(() =>
        {
            var dxfImporter = new DxfImporter();
            var svgImporter = new SvgNestingImporter();

            foreach (string rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath) || !File.Exists(rawPath))
                {
                    errors.Add($"Arquivo não encontrado: {Path.GetFileName(rawPath)}");
                    continue;
                }

                string ext = Path.GetExtension(rawPath).ToLowerInvariant();
                try
                {
                    if (ext == ".dxf")
                    {
                        DxfImportResult res = dxfImporter.ImportFromFile(rawPath, keepAsSinglePart: singlePartFlag);
                        importedParts.AddRange(res.Parts);
                    }
                    else if (ext == ".svg")
                    {
                        SvgNestingImportResult res = svgImporter.ImportFromFile(rawPath, keepAsSinglePart: singlePartFlag);
                        importedParts.AddRange(res.Parts);
                    }
                    else
                    {
                        errors.Add($"Formato não suportado: {Path.GetFileName(rawPath)}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(rawPath)}: {ex.Message}");
                }
            }
        });

        foreach (NestingPart part in importedParts)
        {
            NestingPart unique = EnsureUniquePartId(part);
            Parts.Add(new NestingPartItemViewModel(unique, RemovePartItem, NotifyQueueMetricsChanged));
        }

        if (errors.Count > 0 && importedParts.Count == 0)
        {
            ProgressStatusText = $"Falha ao importar: {string.Join(" | ", errors.Take(2))}";
        }
        else if (errors.Count > 0)
        {
            ProgressStatusText = $"{importedParts.Count} peça(s) importada(s), com {errors.Count} aviso(s): {errors[0]}";
        }
        else if (importedParts.Count > 0)
        {
            ProgressStatusText = $"{importedParts.Count} peça(s) importada(s) em escala 1:1 para a fila.";
        }
        else
        {
            ProgressStatusText = "Nenhum contorno geométrico válido encontrado nos arquivos selecionados.";
        }

        NotifyQueueMetricsChanged();
        return importedParts.Count;
    }

    [RelayCommand]
    public async Task PickAndImportExternalFilesAsync()
    {
        IReadOnlyList<string> selected = await _dialogs.PickOpenFilesAsync(
            "Importar Peças SVG / DXF para Otimização 2D",
            [
                new NativeFileFilter("Desenhos CAD/CAM (*.svg;*.dxf)", [".svg", ".dxf"]),
                new NativeFileFilter("SVG Vetorial (*.svg)", [".svg"]),
                new NativeFileFilter("AutoCAD DXF (*.dxf)", [".dxf"]),
            ]);

        if (selected.Count > 0)
        {
            await ImportExternalFilesAsync(selected);
        }
    }

    [RelayCommand]
    public void ClearParts()
    {
        if (IsOptimizing)
        {
            CancelOptimization();
        }

        Parts.Clear();
        CurrentResult = null;
        SelectedSheetIndex = 0;
        ProgressPercent = 0.0;
        ProgressStatusText = "Fila de peças limpa.";
        NotifyAllResultProperties();
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void IncrementBoxCopies() => BoxCopiesCount = Math.Min(50, BoxCopiesCount + 1);

    [RelayCommand(CanExecute = nameof(CanDecrementBoxCopies))]
    public void DecrementBoxCopies() => BoxCopiesCount = Math.Max(1, BoxCopiesCount - 1);

    private bool CanDecrementBoxCopies() => BoxCopiesCount > 1;

    [RelayCommand]
    public void ApplySheetPreset(string? presetKey)
    {
        if (string.IsNullOrWhiteSpace(presetKey))
        {
            return;
        }

        switch (presetKey.Trim())
        {
            case "ActiveLaserBed":
            case "Mesa Laser Ativa":
            {
                (double WidthMm, double HeightMm)? bed = _activeLaserBedProvider?.Invoke();
                if (bed.HasValue && bed.Value.WidthMm >= 50.0 && bed.Value.HeightMm >= 50.0)
                {
                    SheetWidthMm = bed.Value.WidthMm;
                    SheetHeightMm = bed.Value.HeightMm;
                    ProgressStatusText = string.Format(PtBr, "Mesa laser ativa aplicada: {0:0.#} × {1:0.#} mm.", SheetWidthMm, SheetHeightMm);
                }
                else
                {
                    SheetWidthMm = 600.0;
                    SheetHeightMm = 400.0;
                    ProgressStatusText = "Mesa padrão 600 × 400 mm aplicada (nenhuma mesa fixa selecionada na Etapa 3).";
                }
                break;
            }
            case "Scrap300x200":
            case "Retalho 300 × 200 mm":
                SheetWidthMm = 300.0;
                SheetHeightMm = 200.0;
                ProgressStatusText = "Preset aplicado: Retalho 300 × 200 mm.";
                break;
            case "Scrap400x300":
            case "Retalho 400 × 300 mm":
                SheetWidthMm = 400.0;
                SheetHeightMm = 300.0;
                ProgressStatusText = "Preset aplicado: Retalho 400 × 300 mm.";
                break;
            case "Sheet600x400":
            case "Chapa 600 × 400 mm":
                SheetWidthMm = 600.0;
                SheetHeightMm = 400.0;
                ProgressStatusText = "Preset aplicado: Chapa 600 × 400 mm.";
                break;
            case "Sheet900x600":
            case "Chapa 900 × 600 mm":
                SheetWidthMm = 900.0;
                SheetHeightMm = 600.0;
                ProgressStatusText = "Preset aplicado: Chapa 900 × 600 mm.";
                break;
        }

        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanRunOptimization() => !IsOptimizing && TotalRequestedInstances > 0;

    [RelayCommand(CanExecute = nameof(CanRunOptimization))]
    public async Task RunOptimizationAsync()
    {
        if (IsOptimizing)
        {
            return;
        }

        List<NestingPart> snapshotParts = Parts
            .Where(item => item.IsEnabled && item.Quantity > 0)
            .Select(item => item.Part)
            .ToList();

        if (snapshotParts.Count == 0)
        {
            ProgressStatusText = "Nenhuma peça habilitada na fila para otimizar.";
            return;
        }

        NestingSheetConfig config = BuildCurrentConfig();
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        CancellationToken ct = _activeCts.Token;

        IsOptimizing = true;
        ProgressPercent = 5.0;
        ProgressStatusText = $"Otimizando {TotalRequestedInstances} peça(s) em retalho {config.SheetWidthMm:0.#} × {config.SheetHeightMm:0.#} mm...";

        SynchronizationContext? uiContext = SynchronizationContext.Current;
        var progress = new Progress<NestingProgress>(p =>
        {
            void Apply()
            {
                if (!IsOptimizing) return;
                ProgressPercent = p.TotalInstances > 0
                    ? Math.Clamp((double)p.ProcessedInstances / p.TotalInstances * 100.0, 5.0, 99.0)
                    : 50.0;
                ProgressStatusText = p.StatusMessage;
            }

            if (uiContext is not null)
            {
                uiContext.Post(_ => Apply(), null);
            }
            else
            {
                Apply();
            }
        });

        try
        {
            NestingResult result = await Task.Run(() => _engine.Optimize(snapshotParts, config, progress, ct), ct);
            CurrentResult = result;
            SelectedSheetIndex = 0;
            ProgressPercent = 100.0;
            ProgressStatusText = result.UnplacedParts.Count > 0
                ? $"Concluído: {result.TotalPlacedInstances}/{result.TotalRequestedInstances} peças em {result.Sheets.Count} chapa(s) ({result.OverallUtilizationPercent:0.0}%) — {result.UnplacedParts.Count} não couberam."
                : $"Otimização concluída: {result.TotalPlacedInstances} peça(s) em {result.Sheets.Count} chapa(s) • {result.OverallUtilizationPercent:0.0}% ({result.WinningStrategy}).";
        }
        catch (OperationCanceledException)
        {
            ProgressPercent = 0.0;
            ProgressStatusText = "Otimização cancelada pelo usuário.";
        }
        catch (Exception ex)
        {
            ProgressPercent = 0.0;
            ProgressStatusText = $"Erro durante a otimização: {ex.Message}";
        }
        finally
        {
            IsOptimizing = false;
            NotifyAllResultProperties();
            VisualStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(IsOptimizing))]
    public void CancelOptimization()
    {
        try
        {
            _activeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignora caso CTS já tenha sido descartado
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPreviousSheet))]
    public void PreviousSheet()
    {
        if (SelectedSheetIndex > 0)
        {
            SelectedSheetIndex--;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNextSheet))]
    public void NextSheet()
    {
        if (CurrentResult is not null && SelectedSheetIndex < CurrentResult.Sheets.Count - 1)
        {
            SelectedSheetIndex++;
        }
    }

    public async Task ExportCurrentSheetSvgAsync(string path)
    {
        NestingSheetLayout sheet = CurrentSheet ?? throw new InvalidOperationException("Nenhuma chapa otimizada selecionada para exportação.");
        bool includeBoundary = ShowSheetBoundary;
        await Task.Run(() =>
        {
            string svg = _exporter.ExportSheetToSvg(sheet, includeBoundary);
            EnsureParentDirectory(path);
            File.WriteAllText(path, svg, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        });
        ProgressStatusText = $"Chapa {sheet.SheetIndex:D2} exportada em SVG 1:1: {Path.GetFileName(path)}";
    }

    public async Task ExportCurrentSheetDxfAsync(string path)
    {
        NestingSheetLayout sheet = CurrentSheet ?? throw new InvalidOperationException("Nenhuma chapa otimizada selecionada para exportação.");
        bool includeBoundary = ShowSheetBoundary;
        await Task.Run(() =>
        {
            string dxf = _exporter.ExportSheetToDxf(sheet, includeBoundary);
            EnsureParentDirectory(path);
            File.WriteAllText(path, dxf, Encoding.ASCII);
        });
        ProgressStatusText = $"Chapa {sheet.SheetIndex:D2} exportada em DXF R2000: {Path.GetFileName(path)}";
    }

    public async Task ExportCurrentSheetPdfAsync(string path)
    {
        NestingSheetLayout sheet = CurrentSheet ?? throw new InvalidOperationException("Nenhuma chapa otimizada selecionada para exportação.");
        bool includeBoundary = ShowSheetBoundary;
        await Task.Run(() =>
        {
            byte[] pdf = _exporter.ExportSheetToPdf(sheet, includeBoundary);
            EnsureParentDirectory(path);
            File.WriteAllBytes(path, pdf);
        });
        ProgressStatusText = $"Chapa {sheet.SheetIndex:D2} exportada em PDF 1:1: {Path.GetFileName(path)}";
    }

    public async Task ExportFullZipAsync(string path)
    {
        NestingResult result = CurrentResult ?? throw new InvalidOperationException("Execute a otimização antes de exportar o pacote ZIP.");
        bool includeBoundary = ShowSheetBoundary;
        await Task.Run(() => _exporter.ExportResultToZip(result, path, includeBoundary));
        ProgressStatusText = $"Pacote ZIP multi-chapa ({result.Sheets.Count} chapas) salvo em: {Path.GetFileName(path)}";
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task PickAndExportSvgAsync()
    {
        if (CurrentSheet is not { } sheet) return;
        string? dest = await _dialogs.PickSaveFileAsync(
            "Exportar Chapa Otimizada (SVG 1:1)",
            $"nesting_chapa_{sheet.SheetIndex:D2}.svg",
            [new NativeFileFilter("SVG Vetorial 1:1 (*.svg)", [".svg"])]);
        if (!string.IsNullOrWhiteSpace(dest))
        {
            await ExportCurrentSheetSvgAsync(dest);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task PickAndExportDxfAsync()
    {
        if (CurrentSheet is not { } sheet) return;
        string? dest = await _dialogs.PickSaveFileAsync(
            "Exportar Chapa Otimizada (DXF R2000)",
            $"nesting_chapa_{sheet.SheetIndex:D2}.dxf",
            [new NativeFileFilter("AutoCAD DXF R2000 (*.dxf)", [".dxf"])]);
        if (!string.IsNullOrWhiteSpace(dest))
        {
            await ExportCurrentSheetDxfAsync(dest);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task PickAndExportPdfAsync()
    {
        if (CurrentSheet is not { } sheet) return;
        string? dest = await _dialogs.PickSaveFileAsync(
            "Exportar Chapa Otimizada (PDF 1.4 1:1)",
            $"nesting_chapa_{sheet.SheetIndex:D2}.pdf",
            [new NativeFileFilter("PDF Vetorial 1:1 (*.pdf)", [".pdf"])]);
        if (!string.IsNullOrWhiteSpace(dest))
        {
            await ExportCurrentSheetPdfAsync(dest);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task PickAndExportZipAsync()
    {
        if (CurrentResult is null) return;
        string? dest = await _dialogs.PickSaveFileAsync(
            "Exportar Pacote Completo de Nesting (.ZIP)",
            "caixa_maker_nesting_lote.zip",
            [new NativeFileFilter("Pacote Compactado (*.zip)", [".zip"])]);
        if (!string.IsNullOrWhiteSpace(dest))
        {
            await ExportFullZipAsync(dest);
        }
    }

    partial void OnBoxCopiesCountChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 50);
        if (clamped != value)
        {
            BoxCopiesCount = clamped;
            return;
        }

        foreach (NestingPartItemViewModel item in Parts)
        {
            if (item.IsFromActiveBox)
            {
                item.Quantity = clamped;
            }
        }

        DecrementBoxCopiesCommand.NotifyCanExecuteChanged();
        NotifyQueueMetricsChanged();
    }

    partial void OnSheetWidthMmChanged(double value) => VisualStateChanged?.Invoke(this, EventArgs.Empty);
    partial void OnSheetHeightMmChanged(double value) => VisualStateChanged?.Invoke(this, EventArgs.Empty);
    partial void OnMarginMmChanged(double value) => VisualStateChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowSheetBoundaryChanged(bool value) => VisualStateChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowPartLabelsChanged(bool value) => VisualStateChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowUsableMarginChanged(bool value) => VisualStateChanged?.Invoke(this, EventArgs.Empty);
    partial void OnSelectedGlobalRotationLabelChanged(string value) => OnPropertyChanged(nameof(GlobalRotationOverride));

    partial void OnIsOptimizingChanged(bool value)
    {
        RunOptimizationCommand.NotifyCanExecuteChanged();
        CancelOptimizationCommand.NotifyCanExecuteChanged();
        NotifyExportCommandsCanExecute();
    }

    partial void OnSelectedSheetIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentSheet));
        OnPropertyChanged(nameof(SheetCounterLabel));
        OnPropertyChanged(nameof(CanGoPreviousSheet));
        OnPropertyChanged(nameof(CanGoNextSheet));
        PreviousSheetCommand.NotifyCanExecuteChanged();
        NextSheetCommand.NotifyCanExecuteChanged();
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCurrentResultChanged(NestingResult? value) => NotifyAllResultProperties();

    private void OnPartsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NotifyQueueMetricsChanged();

    private void RemovePartItem(NestingPartItemViewModel item)
    {
        if (Parts.Remove(item))
        {
            NotifyQueueMetricsChanged();
        }
    }

    private void NotifyQueueMetricsChanged()
    {
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(TotalRequestedInstances));
        OnPropertyChanged(nameof(PlacedSummaryLabel));
        RunOptimizationCommand.NotifyCanExecuteChanged();
    }

    private void NotifyAllResultProperties()
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CurrentSheet));
        OnPropertyChanged(nameof(SheetCounterLabel));
        OnPropertyChanged(nameof(CanGoPreviousSheet));
        OnPropertyChanged(nameof(CanGoNextSheet));
        OnPropertyChanged(nameof(OverallUtilizationLabel));
        OnPropertyChanged(nameof(PlacedSummaryLabel));
        OnPropertyChanged(nameof(SheetCountLabel));
        OnPropertyChanged(nameof(ElapsedLabel));
        OnPropertyChanged(nameof(HasUnplacedParts));
        OnPropertyChanged(nameof(UnplacedPartsSummary));
        PreviousSheetCommand.NotifyCanExecuteChanged();
        NextSheetCommand.NotifyCanExecuteChanged();
        NotifyExportCommandsCanExecute();
    }

    private void NotifyExportCommandsCanExecute()
    {
        OnPropertyChanged(nameof(CanExport));
        PickAndExportSvgCommand.NotifyCanExecuteChanged();
        PickAndExportDxfCommand.NotifyCanExecuteChanged();
        PickAndExportPdfCommand.NotifyCanExecuteChanged();
        PickAndExportZipCommand.NotifyCanExecuteChanged();
    }

    private NestingPart EnsureUniquePartId(NestingPart part)
    {
        if (!Parts.Any(p => string.Equals(p.Id, part.Id, StringComparison.Ordinal)))
        {
            return part;
        }

        _importCounter++;
        string uniqueId = $"{part.Id}_{_importCounter}";
        return new NestingPart(
            id: uniqueId,
            name: part.Name,
            outerContour: part.OuterContour,
            internalFeatures: part.InternalFeatures,
            rasterOverlays: part.RasterOverlays,
            sourceTag: part.SourceTag,
            sourcePanelKey: part.SourcePanelKey,
            quantity: part.Quantity,
            rotationPolicy: part.RotationPolicy,
            enabled: part.Enabled);
    }

    private static void EnsureParentDirectory(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
