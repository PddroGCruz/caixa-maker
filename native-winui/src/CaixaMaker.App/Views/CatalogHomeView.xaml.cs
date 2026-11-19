using System.Globalization;
using CaixaMaker.App.Controls.CadPrimitives;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Projects;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace CaixaMaker.App.Views;

/// <summary>
/// Start Page CAD/CAM de alta densidade exibindo navegação técnica, comandos rápidos,
/// fichas simétricas de modelos paramétricos com pré-visualização 3D real e tabela compacta de projetos salvos.
/// </summary>
public sealed partial class CatalogHomeView : UserControl
{
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private readonly Dictionary<string, bool> _cardExplodedStates = new(StringComparer.Ordinal);
    private MainWindowViewModel? _vm;
    private string _activeNavSection = "Home";
    private bool _syncingSearchBox;

    public CatalogHomeView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RefreshDynamicState();
    }

    public void BindViewModel(MainWindowViewModel viewModel)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.SavedProjects.CollectionChanged -= OnSavedProjectsChanged;
            _vm.Theme.ThemeChanged -= OnThemeChanged;
        }

        _vm = viewModel;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.SavedProjects.CollectionChanged += OnSavedProjectsChanged;
        _vm.Theme.ThemeChanged += OnThemeChanged;

        CatalogThemeSwitcher.Bind(_vm.Theme);

        string versionTag = $"v{_vm.AppVersionText}";
        VersionBadgeText.Text = versionTag;
        CatalogStatusVersionText.Text = versionTag;

        RefreshDynamicState();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.HasPendingDraft)
            or nameof(MainWindowViewModel.DraftSummaryText)
            or nameof(MainWindowViewModel.InDesigner))
        {
            RefreshDynamicState();
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.PendingLegacyMigrationCount)
            or nameof(MainWindowViewModel.HasPendingLegacyMigrations))
        {
            RefreshSavedProjectsSection();
        }
    }

    private void OnSavedProjectsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RefreshSavedProjectsSection();

    private void OnThemeChanged(object? sender, ResolvedAppTheme e)
        => RefreshDynamicState();

    private void RefreshDynamicState()
    {
        if (_vm is null) return;

        DraftRecoveryBanner.Visibility = _vm.HasPendingDraft ? Visibility.Visible : Visibility.Collapsed;
        DraftSummaryTextBlock.Text = _vm.HasPendingDraft
            ? $"Sessão não salva detectada — {_vm.DraftSummaryText}"
            : string.Empty;

        int modelCount = _vm.Box.CatalogModels.Count;
        CatalogModelsCountText.Text = $"{modelCount} modelo(s) disponíveis";
        CatalogStatusModelsText.Text = $"Catálogo: {modelCount} modelos paramétricos";

        UpdateNavigationRailVisuals();
        RebuildCatalogModelCards();
        RefreshSavedProjectsSection();
    }

    private void RefreshSavedProjectsSection()
    {
        if (_vm is null) return;

        if (!string.Equals(ProjectsSearchBox.Text, _vm.ProjectSearchQuery, StringComparison.Ordinal))
        {
            _syncingSearchBox = true;
            try
            {
                ProjectsSearchBox.Text = _vm.ProjectSearchQuery;
            }
            finally
            {
                _syncingSearchBox = false;
            }
        }

        int projectCount = _vm.SavedProjects.Count;
        SavedProjectsCountLabel.Text = $"({projectCount})";
        CatalogStatusProjectsText.Text = $"Projetos locais: {projectCount}";

        bool hasLegacy = _vm.HasPendingLegacyMigrations;
        MigrateLegacyProjectsBtn.Visibility = hasLegacy ? Visibility.Visible : Visibility.Collapsed;
        if (hasLegacy)
        {
            MigrateLegacyProjectsBtn.Content = $"Migrar Antigos ({_vm.PendingLegacyMigrationCount})";
        }

        RebuildSavedProjectsTable();
    }

    private void UpdateNavigationRailVisuals()
    {
        Brush? accentBar = ThemeBrushHelper.ResolveBrush(this, "CmAccentHighlightBrush");
        Brush? primaryText = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush");
        Brush? secondaryText = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush");
        Brush? selectionBg = ThemeBrushHelper.ResolveBrush(this, "CmAccentSelectionBrush");

        void ApplyNavItem(Button btn, Border indicator, TextBlock text, bool isSelected)
        {
            btn.Background = isSelected ? (selectionBg ?? TransparentBrush) : TransparentBrush;
            indicator.Background = isSelected ? (accentBar ?? TransparentBrush) : TransparentBrush;
            text.Foreground = isSelected ? (primaryText ?? text.Foreground) : (secondaryText ?? text.Foreground);
            text.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        ApplyNavItem(NavHomeBtn, NavHomeIndicator, NavHomeText, _activeNavSection == "Home");
        ApplyNavItem(NavProjectsBtn, NavProjectsIndicator, NavProjectsText, _activeNavSection == "Projects");
        ApplyNavItem(NavModelsBtn, NavModelsIndicator, NavModelsText, _activeNavSection == "Models");

        ModelsSectionPanel.Visibility = _activeNavSection == "Projects" ? Visibility.Collapsed : Visibility.Visible;
        SavedProjectsSectionPanel.Visibility = _activeNavSection == "Models" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildCatalogModelCards()
    {
        if (_vm is null) return;

        CatalogModelsGrid.Children.Clear();
        CatalogModelsGrid.RowDefinitions.Clear();

        IReadOnlyList<BoxModelCatalogItem> models = _vm.Box.CatalogModels;
        int rows = (models.Count + 1) / 2;
        for (int r = 0; r < rows; r++)
        {
            CatalogModelsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < models.Count; i++)
        {
            FrameworkElement card = BuildModelSpecCard(models[i], isPrimaryModel: i == 0);
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            CatalogModelsGrid.Children.Add(card);
        }
    }

    private bool IsDarkThemeActive()
        => _vm?.Theme.ResolvedTheme != ResolvedAppTheme.Light;

    private FrameworkElement BuildModelSpecCard(BoxModelCatalogItem model, bool isPrimaryModel)
    {
        FontFamily? uiFont = ThemeBrushHelper.ResolveResource<FontFamily>("CmUiFontFamily");
        FontFamily? monoFont = ThemeBrushHelper.ResolveResource<FontFamily>("CmMonoFontFamily");
        Style? primaryBtnStyle = ThemeBrushHelper.ResolveResource<Style>("PrimaryAccentButtonStyle");
        Style? secondaryBtnStyle = ThemeBrushHelper.ResolveResource<Style>("CadSecondaryButtonStyle");
        Style? compactToolbarStyle = ThemeBrushHelper.ResolveResource<Style>("CompactToolbarButtonStyle");

        double w = model.DefaultParameters.GetValueOrDefault("box_width", 200);
        double d = model.DefaultParameters.GetValueOrDefault("box_depth", 150);
        double h = model.DefaultParameters.GetValueOrDefault("box_height", 100);
        double t = model.DefaultParameters.GetValueOrDefault("box_thickness", 3.0);

        string resourcesSummary = model.HasLid && model.HasHandles
            ? "Tampa articulada + Alças"
            : "Topo livre sem orelhas";

        string actionLabel = model.Id == OpenBoxModel.Id
            ? "Criar Caixa Aberta"
            : model.Id == BoxWithLidAndHandlesModel.Id
                ? "Criar Projeto com Tampa"
                : $"Criar {model.Name}";

        var rootBorder = new Border
        {
            Background = ThemeBrushHelper.ResolveBrush(this, "CmPanelBackgroundBrush"),
            BorderBrush = ThemeBrushHelper.ResolveBrush(this, "CmSubtleBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(10, 10, 12, 10),
        };

        var cardGrid = new Grid { ColumnSpacing = 12 };
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Coluna Esquerda (200 px): Pré-visualização 3D isométrica real + toggle [Montada | Explodida]
        bool initialExploded = _cardExplodedStates.GetValueOrDefault(model.Id, false);

        var previewBorder = new Border
        {
            Background = ThemeBrushHelper.ResolveBrush(this, "CmViewportCanvasBrush"),
            BorderBrush = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            MinHeight = 172,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var previewGrid = new Grid();
        previewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        previewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(4, 16, 4, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(previewImage, 0);

        WriteableBitmap? previewBitmap = null;
        void RenderModelThumb(bool explodedState)
        {
            using SKBitmap skBitmap = CatalogPreview3DService.RenderModelPreviewBitmap(
                model.Id,
                exploded: explodedState,
                darkTheme: IsDarkThemeActive(),
                widthPx: 380,
                heightPx: 270);
            previewBitmap = SkiaBitmapBridge.BlitBitmap(skBitmap, previewImage, previewBitmap);
        }
        RenderModelThumb(initialExploded);

        var badge3D = new Border
        {
            Margin = new Thickness(5, 5, 0, 0),
            Padding = new Thickness(5, 1, 5, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = ThemeBrushHelper.ResolveBrush(this, "CmShellBackgroundBrush"),
            BorderBrush = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = new TextBlock
            {
                Text = "3D 1:1",
                FontFamily = monoFont ?? FontFamily.XamlAutoFontFamily,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrushHelper.ResolveBrush(this, "CmAccentHighlightBrush"),
            },
        };
        Grid.SetRow(badge3D, 0);

        var toggleStripBorder = new Border
        {
            Margin = new Thickness(6, 2, 6, 6),
            Padding = new Thickness(2),
            Background = ThemeBrushHelper.ResolveBrush(this, "CmShellBackgroundBrush"),
            BorderBrush = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
        };
        var toggleGrid = new Grid { ColumnSpacing = 2 };
        toggleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Button CreateModeToggleButton(string text) => new()
        {
            Content = text,
            Height = 20,
            MinHeight = 20,
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontFamily = uiFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 10,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Style = compactToolbarStyle,
        };

        Button mountedBtn = CreateModeToggleButton("Montada");
        Button explodedBtn = CreateModeToggleButton("Explodida");
        Grid.SetColumn(mountedBtn, 0);
        Grid.SetColumn(explodedBtn, 1);

        void SyncModeButtons(bool isExploded)
        {
            Brush? activeBg = ThemeBrushHelper.ResolveBrush(this, "CmAccentSelectionBrush");
            Brush? activeBorder = ThemeBrushHelper.ResolveBrush(this, "CmAccentSelectionBorderBrush");
            Brush? activeFg = ThemeBrushHelper.ResolveBrush(this, "CmAccentHighlightBrush");
            Brush? inactiveFg = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush");

            mountedBtn.Background = !isExploded ? (activeBg ?? TransparentBrush) : TransparentBrush;
            mountedBtn.BorderBrush = !isExploded ? (activeBorder ?? TransparentBrush) : TransparentBrush;
            mountedBtn.Foreground = !isExploded ? (activeFg ?? mountedBtn.Foreground) : (inactiveFg ?? mountedBtn.Foreground);
            mountedBtn.FontWeight = !isExploded ? FontWeights.SemiBold : FontWeights.Normal;

            explodedBtn.Background = isExploded ? (activeBg ?? TransparentBrush) : TransparentBrush;
            explodedBtn.BorderBrush = isExploded ? (activeBorder ?? TransparentBrush) : TransparentBrush;
            explodedBtn.Foreground = isExploded ? (activeFg ?? explodedBtn.Foreground) : (inactiveFg ?? explodedBtn.Foreground);
            explodedBtn.FontWeight = isExploded ? FontWeights.SemiBold : FontWeights.Normal;
        }

        SyncModeButtons(initialExploded);

        mountedBtn.Click += (_, _) =>
        {
            if (!_cardExplodedStates.GetValueOrDefault(model.Id, false))
            {
                return;
            }
            _cardExplodedStates[model.Id] = false;
            SyncModeButtons(false);
            RenderModelThumb(false);
        };
        explodedBtn.Click += (_, _) =>
        {
            if (_cardExplodedStates.GetValueOrDefault(model.Id, false))
            {
                return;
            }
            _cardExplodedStates[model.Id] = true;
            SyncModeButtons(true);
            RenderModelThumb(true);
        };

        toggleGrid.Children.Add(mountedBtn);
        toggleGrid.Children.Add(explodedBtn);
        toggleStripBorder.Child = toggleGrid;
        Grid.SetRow(toggleStripBorder, 1);

        previewGrid.Children.Add(previewImage);
        previewGrid.Children.Add(badge3D);
        previewGrid.Children.Add(toggleStripBorder);
        previewBorder.Child = previewGrid;
        Grid.SetColumn(previewBorder, 0);

        // Coluna Direita (*): Ficha técnica paramétrica e ação de criação
        var layoutGrid = new Grid { RowSpacing = 7 };
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(layoutGrid, 1);

        // Topo da ficha
        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Spacing = 1 };
        titleStack.Children.Add(new TextBlock
        {
            Text = model.Name,
            FontFamily = uiFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush"),
        });
        Grid.SetColumn(titleStack, 0);

        var panelsBadge = new CadStatusBadge
        {
            Kind = isPrimaryModel ? CadBadgeKind.Valid : CadBadgeKind.Neutral,
            Text = $"{model.TotalPanels} CHAPAS",
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(panelsBadge, 1);

        headerGrid.Children.Add(titleStack);
        headerGrid.Children.Add(panelsBadge);
        Grid.SetRow(headerGrid, 0);

        // Descrição técnica enxuta
        var descText = new TextBlock
        {
            Text = model.Description,
            FontFamily = uiFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 11.5,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(descText, 1);

        // Grade técnica monoespaçada (CadMetricTable)
        var metrics = new CadMetricTable
        {
            Items = new[]
            {
                new CadMetricItem("Dimensões base", string.Format(CultureInfo.InvariantCulture, "{0:0} × {1:0} × {2:0} mm", w, d, h), IsHighlighted: true),
                new CadMetricItem("MDF padrão", string.Format(CultureInfo.InvariantCulture, "{0:0.0} mm", t)),
                new CadMetricItem("Recursos", resourcesSummary),
                new CadMetricItem("Encaixes", "Finger Joint 1:1"),
            },
        };
        Grid.SetRow(metrics, 2);

        // Rodapé da ficha
        var footerGrid = new Grid
        {
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var modelSurfaceHint = new TextBlock
        {
            Text = $"{model.SurfaceIds.Count} faces personalizáveis",
            FontFamily = monoFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 10.5,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(modelSurfaceHint, 0);

        var createBtn = new Button
        {
            Content = actionLabel,
            Tag = model.Id,
            Height = 28,
            Padding = new Thickness(11, 0, 11, 0),
            Style = primaryBtnStyle ?? secondaryBtnStyle,
        };
        createBtn.Click += OnOpenCatalogModelClick;
        Grid.SetColumn(createBtn, 1);

        footerGrid.Children.Add(modelSurfaceHint);
        footerGrid.Children.Add(createBtn);
        Grid.SetRow(footerGrid, 3);

        layoutGrid.Children.Add(headerGrid);
        layoutGrid.Children.Add(descText);
        layoutGrid.Children.Add(metrics);
        layoutGrid.Children.Add(footerGrid);

        cardGrid.Children.Add(previewBorder);
        cardGrid.Children.Add(layoutGrid);

        rootBorder.Child = cardGrid;
        return rootBorder;
    }

    private void RebuildSavedProjectsTable()
    {
        if (_vm is null) return;

        SavedProjectsRowsStack.Children.Clear();
        bool empty = _vm.SavedProjects.Count == 0;
        NoProjectsEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            return;
        }

        bool showAll = _activeNavSection == "Projects" || !string.IsNullOrWhiteSpace(_vm.ProjectSearchQuery);
        int maxRows = showAll ? _vm.SavedProjects.Count : Math.Min(8, _vm.SavedProjects.Count);
        for (int i = 0; i < maxRows; i++)
        {
            SavedProjectsRowsStack.Children.Add(BuildSavedProjectRow(_vm.SavedProjects[i], i, i == maxRows - 1));
        }
    }

    private FrameworkElement BuildSavedProjectRow(ProjectSummary project, int index, bool isLast)
    {
        FontFamily? uiFont = ThemeBrushHelper.ResolveResource<FontFamily>("CmUiFontFamily");
        FontFamily? monoFont = ThemeBrushHelper.ResolveResource<FontFamily>("CmMonoFontFamily");
        Style? primaryBtnStyle = ThemeBrushHelper.ResolveResource<Style>("PrimaryAccentButtonStyle");
        Style? secondaryBtnStyle = ThemeBrushHelper.ResolveResource<Style>("CadSecondaryButtonStyle");
        Style? dangerBtnStyle = ThemeBrushHelper.ResolveResource<Style>("DangerOutlineButtonStyle");

        string rowBgKey = index % 2 == 0 ? "CmTableRowEvenBrush" : "CmTableRowAltBrush";
        var rowBorder = new Border
        {
            Height = 48,
            Padding = new Thickness(10, 0, 10, 0),
            Background = ThemeBrushHelper.ResolveBrush(this, rowBgKey),
            BorderBrush = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush"),
            BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });

        var projectCellGrid = new Grid
        {
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        projectCellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        projectCellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var thumbBorder = new Border
        {
            Width = 48,
            Height = 34,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrushHelper.ResolveBrush(this, "CmViewportCanvasBrush"),
            BorderBrush = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
        };
        var thumbImage = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        using (SKBitmap thumbBitmap = CatalogPreview3DService.RenderProjectThumbnailBitmap(
            project,
            darkTheme: IsDarkThemeActive(),
            widthPx: 96,
            heightPx: 68))
        {
            _ = SkiaBitmapBridge.BlitBitmap(thumbBitmap, thumbImage, currentBitmap: null);
        }
        thumbBorder.Child = thumbImage;
        Grid.SetColumn(thumbBorder, 0);

        var titleBlockStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var titleTopRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleTopRow.Children.Add(new TextBlock
        {
            Text = project.Name,
            FontFamily = uiFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        bool isLegacyFormat = string.Equals(project.PackageFormat, "legacy-json", StringComparison.OrdinalIgnoreCase);
        var formatBadge = new Border
        {
            Padding = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(2),
            BorderBrush = ThemeBrushHelper.ResolveBrush(this, "CmHairlineBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeBrushHelper.ResolveBrush(this, "CmSectionHeaderBackgroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = isLegacyFormat ? "JSON" : ".cmaker",
                FontFamily = monoFont ?? FontFamily.XamlAutoFontFamily,
                FontSize = 9.5,
                Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush"),
            },
        };
        titleTopRow.Children.Add(formatBadge);
        titleBlockStack.Children.Add(titleTopRow);

        titleBlockStack.Children.Add(new TextBlock
        {
            Text = BuildSavedProjectSubtitle(project),
            FontFamily = uiFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 10.5,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(titleBlockStack, 1);

        projectCellGrid.Children.Add(thumbBorder);
        projectCellGrid.Children.Add(titleBlockStack);
        Grid.SetColumn(projectCellGrid, 0);

        var modelText = new TextBlock
        {
            Text = project.ModelName,
            FontFamily = uiFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 11.5,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(modelText, 1);

        double w = project.BoxParameters.BoxWidth ?? 200.0;
        double d = project.BoxParameters.BoxDepth ?? 150.0;
        double h = project.BoxParameters.BoxHeight ?? 100.0;
        double t = project.BoxParameters.BoxThickness ?? 3.0;

        var dimsText = new TextBlock
        {
            Text = string.Format(CultureInfo.InvariantCulture, "{0:0.#} × {1:0.#} × {2:0.#}", w, d, h),
            FontFamily = monoFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 11.5,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(dimsText, 2);

        var materialText = new TextBlock
        {
            Text = string.Format(CultureInfo.InvariantCulture, "MDF {0:0.0} mm", t),
            FontFamily = monoFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 11.5,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(materialText, 3);

        var updatedText = new TextBlock
        {
            Text = FormatCompactTimestamp(project.UpdatedAt),
            FontFamily = monoFont ?? FontFamily.XamlAutoFontFamily,
            FontSize = 11,
            Foreground = ThemeBrushHelper.ResolveBrush(this, "CmTextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(updatedText, 4);

        var actionsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        var openBtn = new Button
        {
            Content = "Abrir",
            Tag = project.Id,
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 11,
            Style = primaryBtnStyle,
        };
        openBtn.Click += OnOpenSavedProjectClick;

        var duplicateBtn = new Button
        {
            Content = "Duplicar",
            Tag = project.Id,
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(7, 0, 7, 0),
            FontSize = 11,
            Style = secondaryBtnStyle,
        };
        ToolTipService.SetToolTip(duplicateBtn, "Criar cópia editável independente na biblioteca local");
        duplicateBtn.Click += OnDuplicateSavedProjectClick;

        var exportBtn = new Button
        {
            Content = "Exportar",
            Tag = project.Id,
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(7, 0, 7, 0),
            FontSize = 11,
            Style = secondaryBtnStyle,
        };
        ToolTipService.SetToolTip(exportBtn, "Exportar pacote editável autocontido .cmaker");
        exportBtn.Click += OnExportSavedProjectClick;

        var deleteBtn = new Button
        {
            Content = "Excluir",
            Tag = project.Id,
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(7, 0, 7, 0),
            FontSize = 11,
            Style = dangerBtnStyle,
        };
        deleteBtn.Click += OnDeleteSavedProjectClick;

        actionsStack.Children.Add(openBtn);
        actionsStack.Children.Add(duplicateBtn);
        actionsStack.Children.Add(exportBtn);
        actionsStack.Children.Add(deleteBtn);
        Grid.SetColumn(actionsStack, 5);

        grid.Children.Add(projectCellGrid);
        grid.Children.Add(modelText);
        grid.Children.Add(dimsText);
        grid.Children.Add(materialText);
        grid.Children.Add(updatedText);
        grid.Children.Add(actionsStack);

        rowBorder.Child = grid;
        return rowBorder;
    }

    private static string BuildSavedProjectSubtitle(ProjectSummary project)
    {
        var parts = new List<string>(4)
        {
            $"{project.ElementCount} elem.",
            $"{project.AssetCount} asset(s)",
        };

        if (project.PackageSizeBytes > 0)
        {
            parts.Add(FormatCompactSize(project.PackageSizeBytes));
        }

        if (!string.IsNullOrWhiteSpace(project.Author))
        {
            string author = project.Author.Split('(')[0].Trim();
            if (!string.IsNullOrWhiteSpace(author))
            {
                parts.Add(author);
            }
        }

        if (project.Tags is { Count: > 0 })
        {
            parts.Add(string.Join(", ", project.Tags.Take(3).Select(t => $"#{t}")));
        }

        return string.Join("  •  ", parts);
    }

    private static string FormatCompactSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", bytes / 1024.0);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} MB", bytes / (1024.0 * 1024.0));
    }

    private static string FormatCompactTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "—";
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
        {
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return raw.Length > 16 ? raw[..16] : raw;
    }

    private void OnNavSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section })
        {
            _activeNavSection = section;
            RefreshDynamicState();
        }
    }

    private void OnQuickStartDefaultModelClick(object sender, RoutedEventArgs e)
        => _vm?.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

    private void OnQuickStartOpenBoxClick(object sender, RoutedEventArgs e)
        => _vm?.StartNewProjectFromCatalog(OpenBoxModel.Id);

    private void OnOpenCatalogModelClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: string modelId }) return;
        _vm.StartNewProjectFromCatalog(modelId);
    }

    private void OnOpenSavedProjectClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: string id }) return;
        _vm.OpenSavedProject(id);
    }

    private void OnDuplicateSavedProjectClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: string id }) return;
        _vm.DuplicateSavedProject(id);
    }

    private async void OnExportSavedProjectClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: string id }) return;
        await _vm.ExportSavedProjectPackageAsync(id);
    }

    private async void OnDeleteSavedProjectClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { Tag: string id }) return;
        string? name = _vm.SavedProjects.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))?.Name;
        await _vm.DeleteSavedProjectWithConfirmationAsync(id, name);
    }

    private void OnProjectsSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is null || _syncingSearchBox) return;
        _vm.ProjectSearchQuery = ProjectsSearchBox.Text ?? string.Empty;
    }

    private void OnMigrateLegacyProjectsClick(object sender, RoutedEventArgs e)
        => _vm?.MigrateLegacyProjects();

    private void OnRefreshProjectsClick(object sender, RoutedEventArgs e)
        => _vm?.RefreshSavedProjects();

    private void OnRestoreDraftClick(object sender, RoutedEventArgs e)
        => _vm?.RestoreDraft();

    private void OnDiscardDraftClick(object sender, RoutedEventArgs e)
        => _vm?.DiscardDraft();

    private async void OnImportPackageClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.DispatchCommandAsync(DesktopCommandIds.FileImportPackage);
    }

    private async void OnOpenProjectsManagerClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.DispatchCommandAsync(DesktopCommandIds.ToolsSavedProjects);
    }

    private async void OnOpenPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.DispatchCommandAsync(DesktopCommandIds.ToolsPreferences);
    }

    private async void OnOpenAboutClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.DispatchCommandAsync(DesktopCommandIds.HelpAbout);
    }

    private async void OnOpenShortcutsClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.DispatchCommandAsync(DesktopCommandIds.HelpShortcuts);
    }

    private async void OnOpenInstitutionAboutClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.DispatchCommandAsync(DesktopCommandIds.HelpInstitution);
    }
}
