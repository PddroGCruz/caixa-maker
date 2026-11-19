namespace CaixaMaker.App.Models;

/// <summary>Presets padrão de área útil de corte de máquinas Laser/CNC.</summary>
public enum LaserBedPreset
{
    None = 0,
    Laser400x400 = 1,
    Laser600x400 = 2,
    Laser900x600 = 3,
    Laser1300x900 = 4,
    Custom = 5,
}

/// <summary>Severidade diagnóstica (não bloqueante) do enquadramento da folha de corte na mesa selecionada.</summary>
public enum LaserBedFitSeverity
{
    NoLimit = 0,
    Fits = 1,
    FitsRotatedOnly = 2,
    ExceedsBed = 3,
}

/// <summary>Especificação geométrica de uma mesa/gabarito de máquina laser/CNC.</summary>
public sealed record LaserBedSpec(
    LaserBedPreset Preset,
    string Label,
    string ShortLabel,
    double WidthMm,
    double HeightMm)
{
    public bool HasLimit => Preset != LaserBedPreset.None && WidthMm > 0.0 && HeightMm > 0.0;
}

/// <summary>Resultado do diagnóstico de compatibilidade entre a folha 1:1 gerada e a mesa laser configurada.</summary>
public sealed record LaserBedFitResult(
    LaserBedSpec Bed,
    double SheetWidthMm,
    double SheetHeightMm,
    bool FitsWithoutRotation,
    bool FitsIfRotated90,
    double ExcessXMm,
    double ExcessYMm,
    double UtilizationPercent,
    double MaxPanelWidthMm,
    double MaxPanelHeightMm,
    bool AllSinglePanelsFit,
    LaserBedFitSeverity Severity,
    string SuggestedAlternative,
    string SummaryText)
{
    public bool FitsAnyOrientation => FitsWithoutRotation || FitsIfRotated90;
}
