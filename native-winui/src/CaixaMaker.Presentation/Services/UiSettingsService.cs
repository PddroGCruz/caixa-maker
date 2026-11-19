using System.Text.Json;
using CaixaMaker.App.Models;
using CaixaMaker.Core.Storage;

namespace CaixaMaker.App.Services;

/// <summary>
/// Persiste preferências de tema e layout da interface em <c>%APPDATA%\CaixaMaker\config\ui_settings.json</c>.
/// </summary>
public sealed class UiSettingsService
{
    public const string SettingsFileName = "ui_settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string SettingsFilePath { get; }

    public UiSettingsService(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirs();
        SettingsFilePath = Path.Combine(paths.ConfigDir, SettingsFileName);
    }

    public UiSettingsService(string settingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath))
        {
            throw new ArgumentException("O caminho do arquivo de configurações não pode ser vazio.", nameof(settingsFilePath));
        }

        SettingsFilePath = Path.GetFullPath(settingsFilePath);
    }

    public UiSettingsData Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return Normalize(new UiSettingsData());
            }

            string json = File.ReadAllText(SettingsFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Normalize(new UiSettingsData());
            }

            UiSettingsData? loaded = JsonSerializer.Deserialize<UiSettingsData>(json, JsonOptions);
            return Normalize(loaded ?? new UiSettingsData());
        }
        catch
        {
            return Normalize(new UiSettingsData());
        }
    }

    public void Save(UiSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UiSettingsData normalized = Normalize(settings);

        string? directory = Path.GetDirectoryName(SettingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = SettingsFilePath + ".tmp";
        string json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, SettingsFilePath, overwrite: true);
    }

    public static UiSettingsData Normalize(UiSettingsData source)
    {
        string theme = (source.ThemePreference ?? "system").Trim().ToLowerInvariant();
        if (theme is not ("system" or "light" or "dark"))
        {
            theme = "system";
        }

        string splitOrientation = Enum.TryParse<SplitViewportOrientation>(
            source.SplitOrientation,
            ignoreCase: true,
            out SplitViewportOrientation parsedOrientation)
            ? parsedOrientation.ToString()
            : nameof(SplitViewportOrientation.VerticalStacked);

        string laserBedPreset = Enum.TryParse<LaserBedPreset>(
            source.SelectedLaserBedPreset,
            ignoreCase: true,
            out LaserBedPreset parsedBed)
            ? parsedBed.ToString()
            : nameof(LaserBedPreset.Laser600x400);

        return new UiSettingsData
        {
            SchemaVersion = UiSettingsData.SchemaVersionCurrent,
            ThemePreference = theme,
            LeftSidebarWidth = Math.Clamp(
                double.IsFinite(source.LeftSidebarWidth) ? source.LeftSidebarWidth : UiSettingsData.DefaultLeftSidebarWidth,
                UiSettingsData.MinLeftSidebarWidth,
                UiSettingsData.MaxLeftSidebarWidth),
            RightSidebarWidth = Math.Clamp(
                double.IsFinite(source.RightSidebarWidth) ? source.RightSidebarWidth : UiSettingsData.DefaultRightSidebarWidth,
                UiSettingsData.MinRightSidebarWidth,
                UiSettingsData.MaxRightSidebarWidth),
            LeftSidebarOpen = source.LeftSidebarOpen,
            RightSidebarOpen = source.RightSidebarOpen,
            ToolbarVisible = source.ToolbarVisible,
            SplitOrientation = splitOrientation,
            SplitViewportRatio = Math.Clamp(
                double.IsFinite(source.SplitViewportRatio) ? source.SplitViewportRatio : UiSettingsData.DefaultSplitViewportRatio,
                UiSettingsData.MinSplitViewportRatio,
                UiSettingsData.MaxSplitViewportRatio),
            SelectedLaserBedPreset = laserBedPreset,
            CustomBedWidthMm = Math.Clamp(
                double.IsFinite(source.CustomBedWidthMm) ? source.CustomBedWidthMm : UiSettingsData.DefaultCustomBedWidthMm,
                UiSettingsData.MinLaserBedDimensionMm,
                UiSettingsData.MaxLaserBedDimensionMm),
            CustomBedHeightMm = Math.Clamp(
                double.IsFinite(source.CustomBedHeightMm) ? source.CustomBedHeightMm : UiSettingsData.DefaultCustomBedHeightMm,
                UiSettingsData.MinLaserBedDimensionMm,
                UiSettingsData.MaxLaserBedDimensionMm),
            ShowDimensions = source.ShowDimensions,
            DefaultSafetyMarginMm = Math.Clamp(
                double.IsFinite(source.DefaultSafetyMarginMm) ? source.DefaultSafetyMarginMm : 2.0,
                0.0,
                20.0),
            DefaultHatchSpacingMm = Math.Clamp(
                double.IsFinite(source.DefaultHatchSpacingMm) ? source.DefaultHatchSpacingMm : 0.25,
                0.05,
                5.0),
            AutosaveEnabled = source.AutosaveEnabled,
            RecentProjectIds = (source.RecentProjectIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList(),
        };
    }
}
