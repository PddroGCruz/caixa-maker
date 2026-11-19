using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaixaMaker.App.ViewModels;

/// <summary>Gerencia o tema visual (Sistema, Claro ou Escuro) e sua persistência.</summary>
public sealed partial class ThemeViewModel : ObservableObject
{
    private readonly UiSettingsService? _settingsService;

    [ObservableProperty]
    private AppThemePreference _preference = AppThemePreference.System;

    [ObservableProperty]
    private ResolvedAppTheme _systemTheme = ResolvedAppTheme.Light;

    public ResolvedAppTheme ResolvedTheme => Preference switch
    {
        AppThemePreference.Light => ResolvedAppTheme.Light,
        AppThemePreference.Dark => ResolvedAppTheme.Dark,
        _ => SystemTheme,
    };

    public bool IsDarkTheme => ResolvedTheme == ResolvedAppTheme.Dark;
    public bool IsLightTheme => ResolvedTheme == ResolvedAppTheme.Light;
    public bool IsSystemPreference => Preference == AppThemePreference.System;

    public string PreferenceLabel => Preference switch
    {
        AppThemePreference.System => "Sistema",
        AppThemePreference.Light => "Claro",
        AppThemePreference.Dark => "Escuro",
        _ => "Sistema",
    };

    public string ResolvedThemeLabel => ResolvedTheme == ResolvedAppTheme.Dark ? "Escuro" : "Claro";

    public event EventHandler<ResolvedAppTheme>? ThemeChanged;

    public ThemeViewModel(UiSettingsService? settingsService = null, ResolvedAppTheme initialSystemTheme = ResolvedAppTheme.Light)
    {
        _settingsService = settingsService;
        _systemTheme = initialSystemTheme;

        if (_settingsService is not null)
        {
            UiSettingsData loaded = _settingsService.Load();
            _preference = ParsePreference(loaded.ThemePreference);
        }
    }

    public static AppThemePreference ParsePreference(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "light" => AppThemePreference.Light,
            "dark" => AppThemePreference.Dark,
            _ => AppThemePreference.System,
        };

    public static string ToPreferenceString(AppThemePreference preference)
        => preference switch
        {
            AppThemePreference.Light => "light",
            AppThemePreference.Dark => "dark",
            _ => "system",
        };

    public void UpdateSystemTheme(ResolvedAppTheme systemTheme)
    {
        if (SystemTheme == systemTheme)
        {
            return;
        }

        SystemTheme = systemTheme;
        NotifyResolvedProperties();
    }

    [RelayCommand]
    public void SetPreference(AppThemePreference preference)
    {
        if (Preference != preference)
        {
            Preference = preference;
        }

        if (_settingsService is not null)
        {
            UiSettingsData settings = _settingsService.Load();
            settings.ThemePreference = ToPreferenceString(preference);
            _settingsService.Save(settings);
        }

        NotifyResolvedProperties();
    }

    [RelayCommand]
    public void CycleTheme()
    {
        AppThemePreference next = Preference switch
        {
            AppThemePreference.System => AppThemePreference.Light,
            AppThemePreference.Light => AppThemePreference.Dark,
            _ => AppThemePreference.System,
        };
        SetPreference(next);
    }

    [RelayCommand]
    public void ToggleLightDark()
    {
        SetPreference(ResolvedTheme == ResolvedAppTheme.Dark ? AppThemePreference.Light : AppThemePreference.Dark);
    }

    partial void OnPreferenceChanged(AppThemePreference value) => NotifyResolvedProperties();

    partial void OnSystemThemeChanged(ResolvedAppTheme value) => NotifyResolvedProperties();

    private void NotifyResolvedProperties()
    {
        OnPropertyChanged(nameof(ResolvedTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsSystemPreference));
        OnPropertyChanged(nameof(PreferenceLabel));
        OnPropertyChanged(nameof(ResolvedThemeLabel));
        ThemeChanged?.Invoke(this, ResolvedTheme);
    }
}
