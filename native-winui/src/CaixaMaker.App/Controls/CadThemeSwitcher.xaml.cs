using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaixaMaker.App.Controls;

/// <summary>Seletor segmentado compacto de tema (Claro | Escuro | Auto) para barras superiores CAD/CAM.</summary>
public sealed partial class CadThemeSwitcher : UserControl
{
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly SolidColorBrush ActiveForegroundBrush = new(Colors.White);

    private ThemeViewModel? _themeVm;

    public CadThemeSwitcher()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => SyncSelection();
    }

    public void Bind(ThemeViewModel themeViewModel)
    {
        if (_themeVm is not null)
        {
            _themeVm.PropertyChanged -= OnThemePropertyChanged;
            _themeVm.ThemeChanged -= OnThemeChanged;
        }

        _themeVm = themeViewModel;
        _themeVm.PropertyChanged += OnThemePropertyChanged;
        _themeVm.ThemeChanged += OnThemeChanged;
        SyncSelection();
    }

    private void OnThemePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => SyncSelection();

    private void OnThemeChanged(object? sender, ResolvedAppTheme e)
        => SyncSelection();

    private void SyncSelection()
    {
        AppThemePreference pref = _themeVm?.Preference ?? AppThemePreference.System;
        Brush? activeBg = ThemeBrushHelper.ResolveBrush(this, "CmAccentPrimaryBrush");
        Brush? idleFg = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush");

        ApplySegmentVisual(LightButton, LightText, pref == AppThemePreference.Light, activeBg, idleFg);
        ApplySegmentVisual(DarkButton, DarkText, pref == AppThemePreference.Dark, activeBg, idleFg);
        ApplySegmentVisual(AutoButton, AutoText, pref == AppThemePreference.System, activeBg, idleFg);
    }

    private static void ApplySegmentVisual(
        Button button,
        TextBlock label,
        bool isActive,
        Brush? activeBg,
        Brush? idleFg)
    {
        button.Background = isActive ? (activeBg ?? TransparentBrush) : TransparentBrush;
        label.Foreground = isActive ? ActiveForegroundBrush : (idleFg ?? ActiveForegroundBrush);
        label.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void OnSegmentClick(object sender, RoutedEventArgs e)
    {
        if (_themeVm is null || sender is not Button { Tag: string raw })
        {
            return;
        }

        AppThemePreference next = raw switch
        {
            "Light" => AppThemePreference.Light,
            "Dark" => AppThemePreference.Dark,
            _ => AppThemePreference.System,
        };
        _themeVm.SetPreference(next);
    }
}
