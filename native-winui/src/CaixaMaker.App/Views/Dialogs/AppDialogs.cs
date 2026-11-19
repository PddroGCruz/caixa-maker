using System.Globalization;
using System.Runtime.InteropServices;
using CaixaMaker.App.Helpers;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.UI;

namespace CaixaMaker.App.Views.Dialogs;

/// <summary>Fábrica dos modais nativos da aplicação (Sobre/Atalhos, Preferências, Projetos e Propriedades).</summary>
public static class AppDialogs
{
    public static async Task ShowAboutDialogAsync(
        XamlRoot xamlRoot,
        MainWindowViewModel vm,
        AppPaths paths,
        int initialTabIndex = 0,
        Action<ContentDialog>? onOpened = null)
    {
        (ElementTheme theme, DialogThemePalette palette) = ResolveThemeAndPalette(xamlRoot, vm);

        var tabView = new TabView
        {
            RequestedTheme = theme,
            IsAddTabButtonVisible = false,
            SelectedIndex = Math.Clamp(initialTabIndex, 0, 3),
            Width = 680,
            MaxHeight = 460,
            Background = BrushOf(palette.DialogSurfaceArgb),
        };
        ConfigureTabViewResources(tabView, palette);

        // Aba 0: Sobre
        var aboutStack = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(12, 10, 12, 10),
            Background = BrushOf(palette.DialogSurfaceArgb),
        };
        var heroCard = CreateTechnicalCard(new Thickness(12, 10, 12, 10), palette);
        var headerRow = new Grid { ColumnSpacing = 12 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var symbolPlaque = new Border
        {
            Width = 54,
            Height = 54,
            Padding = new Thickness(6),
            Background = BrushOf(palette.BrandPlaqueBgArgb),
            BorderBrush = BrushOf(palette.BorderArgb),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = new Image
            {
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri("ms-appx:///Assets/caixa-maker-symbol.png")),
            },
        };
        Grid.SetColumn(symbolPlaque, 0);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        titleStack.Children.Add(new TextBlock
        {
            Text = $"{AppVersion.ProductName} v{AppVersion.Version}",
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = BrushOf(palette.TextPrimaryArgb),
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "CAD/CAM PARAMÉTRICO NATIVO WINDOWS (.NET 8 • WINUI 3 • SKIASHARP)",
            FontFamily = GetFont("CmMonoFontFamily"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(palette.AccentHighlightArgb),
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "Sistema integrado para projeto estrutural em MDF/madeira, gravação CNC em 6 layers e exportação vetorial 1:1 mm.",
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 12,
            Foreground = BrushOf(palette.TextSecondaryArgb),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(titleStack, 1);

        headerRow.Children.Add(symbolPlaque);
        headerRow.Children.Add(titleStack);
        heroCard.Child = headerRow;
        aboutStack.Children.Add(heroCard);

        aboutStack.Children.Add(CreateSectionHeader("Especificações da Arquitetura CAD/CAM", palette));
        var specsTable = CreateTableContainer(palette);
        var specsStack = new StackPanel();
        specsStack.Children.Add(CreateSpecRow(0, "NÚCLEO PARAMÉTRICO", "CaixaMaker.Core — Finger joints, kerf, cavidade interna e cotas 3D em escala 1:1 mm", palette));
        specsStack.Children.Add(CreateSpecRow(1, "SOLVER GEOMÉTRICO", "Validação SAT (Separating Axis Theorem) por painel com margens e zonas restritas", palette));
        specsStack.Children.Add(CreateSpecRow(2, "MOTOR CAM & LAYERS", "CaixaMaker.Cam — 6 operações CNC (corte externo/interno, gravação vetorial, fill, marcação, raster)", palette));
        specsStack.Children.Add(CreateSpecRow(3, "EXPORTAÇÃO 1:1", "SVG, DXF R2000, PDF e formato nativo autocontido .cmaker (retrocompatível .caixamaker.json) / ZIP CNC", palette));
        specsTable.Child = specsStack;
        aboutStack.Children.Add(specsTable);

        aboutStack.Children.Add(CreateSectionHeader("Autoria e Desenvolvimento de Software", palette));
        aboutStack.Children.Add(BuildDeveloperHighlightCard(palette));

        tabView.TabItems.Add(CreateThemedTabItem("Sobre", aboutStack, palette));

        // Aba 1: Atalhos CAD/CAM (Tabela densa em 2 colunas)
        var shortcutsStack = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(12),
            Background = BrushOf(palette.DialogSurfaceArgb),
        };
        foreach (IGrouping<string, ShortcutReferenceItem> group in vm.ShortcutsReference.GroupBy(s => s.Category))
        {
            shortcutsStack.Children.Add(CreateSectionHeader(group.Key, palette));
            ShortcutReferenceItem[] items = group.ToArray();
            int rowCount = (items.Length + 1) / 2;

            var tableBorder = CreateTableContainer(palette);
            var rowsPanel = new StackPanel();
            for (int r = 0; r < rowCount; r++)
            {
                var rowGrid = new Grid
                {
                    Background = BrushOf(r % 2 == 0 ? palette.RowEvenArgb : palette.RowAltArgb),
                    Padding = new Thickness(8, 4, 8, 4),
                    ColumnSpacing = 8,
                };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Grid leftCell = BuildShortcutCell(items[r * 2], palette);
                Grid.SetColumn(leftCell, 0);
                rowGrid.Children.Add(leftCell);

                var centerDivider = new Border
                {
                    Width = 1,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = BrushOf(palette.BorderArgb),
                };
                Grid.SetColumn(centerDivider, 1);
                rowGrid.Children.Add(centerDivider);

                int rightIdx = r * 2 + 1;
                if (rightIdx < items.Length)
                {
                    Grid rightCell = BuildShortcutCell(items[rightIdx], palette);
                    Grid.SetColumn(rightCell, 2);
                    rowGrid.Children.Add(rightCell);
                }

                rowsPanel.Children.Add(rowGrid);
            }

            tableBorder.Child = rowsPanel;
            shortcutsStack.Children.Add(tableBorder);
        }
        tabView.TabItems.Add(CreateThemedTabItem("Atalhos", shortcutsStack, palette));

        // Aba 2: Instituição (4 cartões dedicados de alto contraste: Plataforma, Laboratório, Instituição e Desenvolvedor)
        var instStack = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(12, 8, 12, 8),
            Background = BrushOf(palette.DialogSurfaceArgb),
        };
        instStack.Children.Add(CreateSectionHeader("Identidade Institucional e Autoria Técnica", palette));

        instStack.Children.Add(BuildIdentityCard(
            imageUri: "ms-appx:///Assets/caixa-maker-symbol.png",
            title: $"{AppVersion.ProductName} v{AppVersion.Version}",
            tag: "PLATAFORMA CAD/CAM PARAMÉTRICA",
            description: "Software nativo para modelagem estrutural em MDF/compensado com juntas dentadas finger joint, personalização vetorial/raster CNC e exportação 1:1 mm.",
            palette: palette));

        instStack.Children.Add(BuildIdentityCard(
            imageUri: "ms-appx:///Assets/ifmaker-jurua.png",
            title: "Laboratório IFMaker do Juruá",
            tag: "FABRICAÇÃO DIGITAL & PROTOTIPAGEM",
            description: "Ambiente de cultura maker, prototipagem rápida, corte a laser e usinagem CNC voltado ao ensino, pesquisa e extensão tecnológica no Vale do Juruá.",
            palette: palette));

        instStack.Children.Add(BuildIdentityCard(
            imageUri: "ms-appx:///Assets/ifac-horizontal.png",
            title: "IFAC — Campus Cruzeiro do Sul",
            tag: "PESQUISA & INOVAÇÃO • PIBIT",
            description: "Desenvolvido no âmbito do programa PIBIT/IFAC para fortalecer oficinas educativas, laboratórios de inovação e produção digital no Acre.",
            palette: palette));

        instStack.Children.Add(BuildDeveloperHighlightCard(palette));

        tabView.TabItems.Add(CreateThemedTabItem("Instituição", instStack, palette));

        // Aba 3: Ambiente
        var envStack = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(12),
            Background = BrushOf(palette.DialogSurfaceArgb),
        };
        envStack.Children.Add(CreateSectionHeader("Ambiente Nativo e Diretórios de Dados", palette));
        var envTable = CreateTableContainer(palette);
        var envRows = new StackPanel();
        envRows.Children.Add(CreateSpecRow(0, "VERSÃO / ARQUITETURA", $"{AppVersion.Version} ({RuntimeInformation.ProcessArchitecture}) — .NET {Environment.Version}", palette, selectable: true));
        envRows.Children.Add(CreateSpecRow(1, "AUTORIA / CONTATO", $"{AppVersion.DeveloperName} • {AppVersion.DeveloperEmail}", palette, selectable: true));
        envRows.Children.Add(CreateSpecRow(2, "SISTEMA OPERACIONAL", RuntimeInformation.OSDescription, palette, selectable: true));
        envRows.Children.Add(CreateSpecRow(3, "PROJETOS LOCAIS", paths.ProjectsDir, palette, selectable: true));
        envRows.Children.Add(CreateSpecRow(4, "CONFIGURAÇÕES UI", paths.ConfigDir, palette, selectable: true));
        envRows.Children.Add(CreateSpecRow(5, "SAÍDAS CNC / CAM", paths.OutputsDir, palette, selectable: true));
        envRows.Children.Add(CreateSpecRow(6, "LOGS NATIVOS", paths.NativeLogFilePath, palette, selectable: true));
        envRows.Children.Add(CreateSpecRow(7, "TEMPORÁRIOS / AUTOSAVE", paths.TempDir, palette, selectable: true));
        envTable.Child = envRows;
        envStack.Children.Add(envTable);
        tabView.TabItems.Add(CreateThemedTabItem("Ambiente", envStack, palette));

        var modalFooter = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            Background = BrushOf(palette.HeaderStripArgb),
            BorderBrush = BrushOf(palette.BorderArgb),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = new TextBlock
            {
                Text = $"Desenvolvido por {AppVersion.DeveloperSignature} • {AppVersion.Company}",
                FontFamily = GetFont("CmMonoFontFamily"),
                FontSize = 10,
                Foreground = BrushOf(palette.TextSecondaryArgb),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var dialogBody = new StackPanel { Spacing = 0 };
        dialogBody.Children.Add(tabView);
        dialogBody.Children.Add(modalFooter);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = "Sobre o Caixa Maker & Atalhos",
            Content = dialogBody,
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.None,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ConfigureDialogChrome(dialog, palette);
        if (onOpened is not null)
        {
            dialog.Opened += (_, _) => onOpened(dialog);
        }
        await dialog.ShowAsync();
    }

    public static async Task ShowPreferencesDialogAsync(XamlRoot xamlRoot, MainWindowViewModel vm)
    {
        (ElementTheme theme, DialogThemePalette palette) = ResolveThemeAndPalette(xamlRoot, vm);

        var themeBox = new ComboBox
        {
            Style = GetStyle("CadComboBoxStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[]
            {
                new KeyValuePair<AppThemePreference, string>(AppThemePreference.System, "Seguir Sistema (Windows)"),
                new KeyValuePair<AppThemePreference, string>(AppThemePreference.Light, "Tema Claro"),
                new KeyValuePair<AppThemePreference, string>(AppThemePreference.Dark, "Tema Escuro"),
            },
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
            SelectedValue = vm.Theme.Preference,
        };

        var marginBox = new NumberBox
        {
            Minimum = 0.0,
            Maximum = 20.0,
            SmallChange = 0.5,
            FontFamily = GetFont("CmMonoFontFamily"),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = vm.Personalization.SafetyMarginMm,
        };

        var hatchBox = new NumberBox
        {
            Minimum = 0.05,
            Maximum = 5.0,
            SmallChange = 0.05,
            FontFamily = GetFont("CmMonoFontFamily"),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = vm.Personalization.DefaultHatchSpacingMm,
        };

        var autosaveToggle = new ToggleSwitch
        {
            IsOn = vm.AutosaveEnabled,
            OnContent = "Ativado",
            OffContent = "Desativado",
        };

        var stack = new StackPanel { Spacing = 10, MinWidth = 440 };

        stack.Children.Add(CreateSectionHeader("Aparência e Tema CAD", palette));
        var themeCard = CreateTechnicalCard(new Thickness(10, 8, 10, 8), palette);
        var themeRow = CreateLabeledFieldRow("Tema da interface", themeBox, palette);
        themeCard.Child = themeRow;
        stack.Children.Add(themeCard);

        stack.Children.Add(CreateSectionHeader("Parâmetros Padrão CAM & Validação SAT", palette));
        var camCard = CreateTechnicalCard(new Thickness(10, 8, 10, 8), palette);
        var camFields = new StackPanel { Spacing = 8 };
        camFields.Children.Add(CreateLabeledFieldRow("Margem de segurança SAT (mm)", marginBox, palette));
        camFields.Children.Add(CreateLabeledFieldRow("Espaçamento de hachura CNC (mm)", hatchBox, palette));
        camFields.Children.Add(CreateLabeledFieldRow("Rascunho automático", autosaveToggle, palette));
        camCard.Child = camFields;
        stack.Children.Add(camCard);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = "Preferências e Configurações",
            Content = stack,
            PrimaryButtonText = "Aplicar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        ConfigureDialogChrome(dialog, palette);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AppThemePreference themePref = themeBox.SelectedValue is AppThemePreference p ? p : vm.Theme.Preference;
            double margin = double.IsNaN(marginBox.Value) ? vm.Personalization.SafetyMarginMm : marginBox.Value;
            double hatch = double.IsNaN(hatchBox.Value) ? vm.Personalization.DefaultHatchSpacingMm : hatchBox.Value;
            vm.UpdatePreferences(margin, hatch, autosaveToggle.IsOn, themePref);
        }
    }

    public static async Task ShowProjectsManagerDialogAsync(XamlRoot xamlRoot, MainWindowViewModel vm, bool startInSaveAs)
    {
        (ElementTheme theme, DialogThemePalette palette) = ResolveThemeAndPalette(xamlRoot, vm);
        bool darkTheme = theme != ElementTheme.Light;
        vm.RefreshSavedProjects();

        var root = new StackPanel { Spacing = 10, MinWidth = 660 };
        ContentDialog? dialog = null;
        Func<Task>? deferredAction = null;
        string? pendingDeleteId = null;

        if (vm.InDesigner)
        {
            root.Children.Add(CreateSectionHeader(startInSaveAs ? "Salvar Projeto Como (.cmaker)" : "Salvar Cópia na Biblioteca (.cmaker)", palette));
            var saveCard = CreateTechnicalCard(new Thickness(10, 8, 10, 8), palette);
            var saveFields = new StackPanel { Spacing = 6 };
            var nameBox = new TextBox
            {
                Style = GetStyle("CadTextBoxStyle"),
                FontFamily = GetFont("CmUiFontFamily"),
                Text = string.IsNullOrWhiteSpace(vm.SavedProjectName)
                    ? $"{vm.Box.SelectedModel.DisplayName} ({vm.Box.DimensionsBadgeText})"
                    : (startInSaveAs ? $"{vm.SavedProjectName} (cópia)" : vm.SavedProjectName),
                PlaceholderText = "Nome do projeto",
            };
            var descBox = new TextBox
            {
                Style = GetStyle("CadTextBoxStyle"),
                FontFamily = GetFont("CmUiFontFamily"),
                Text = vm.SavedProjectDescription,
                PlaceholderText = "Descrição curta (opcional)",
            };
            var saveBtn = new Button
            {
                Content = "Salvar .cmaker",
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(12, 0, 12, 0),
                Style = GetStyle("PrimaryAccentButtonStyle"),
            };
            saveBtn.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    vm.SavedProjectDescription = descBox.Text?.Trim() ?? string.Empty;
                    vm.SaveProjectAs(
                        nameBox.Text.Trim(),
                        description: vm.SavedProjectDescription,
                        author: vm.SavedProjectAuthor,
                        notes: vm.SavedProjectNotes);
                    dialog?.Hide();
                }
            };

            var saveRow = new Grid { ColumnSpacing = 8 };
            saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(nameBox, 0);
            Grid.SetColumn(descBox, 1);
            Grid.SetColumn(saveBtn, 2);
            saveRow.Children.Add(nameBox);
            saveRow.Children.Add(descBox);
            saveRow.Children.Add(saveBtn);
            saveFields.Children.Add(saveRow);
            saveCard.Child = saveFields;
            root.Children.Add(saveCard);
        }

        var libraryHeaderGrid = new Grid { ColumnSpacing = 8 };
        libraryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        libraryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        libraryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var listHeaderText = CreateSectionHeader($"Biblioteca Local de Projetos ({vm.SavedProjects.Count})", palette);
        Grid.SetColumn(listHeaderText, 0);

        var searchBox = new TextBox
        {
            Width = 220,
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(8, 2, 8, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 11.5,
            Text = vm.ProjectSearchQuery,
            PlaceholderText = "Buscar nome, modelo, tag...",
            Style = GetStyle("CadTextBoxStyle"),
        };
        Grid.SetColumn(searchBox, 1);

        var migrateBtn = new Button
        {
            Content = $"Migrar Antigos ({vm.PendingLegacyMigrationCount})",
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(9, 0, 9, 0),
            Style = GetStyle("CadSecondaryButtonStyle"),
            Visibility = vm.HasPendingLegacyMigrations ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(migrateBtn, 2);

        libraryHeaderGrid.Children.Add(listHeaderText);
        libraryHeaderGrid.Children.Add(searchBox);
        libraryHeaderGrid.Children.Add(migrateBtn);
        root.Children.Add(libraryHeaderGrid);

        var migrationNote = new TextBlock
        {
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 11,
            Foreground = BrushOf(palette.TextSecondaryArgb),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(migrationNote);

        var listStack = new StackPanel { Spacing = 5 };
        void RebuildList()
        {
            listStack.Children.Clear();
            listHeaderText.Text = $"BIBLIOTECA LOCAL DE PROJETOS ({vm.SavedProjects.Count})";
            migrateBtn.Visibility = vm.HasPendingLegacyMigrations ? Visibility.Visible : Visibility.Collapsed;
            if (vm.HasPendingLegacyMigrations)
            {
                migrateBtn.Content = $"Migrar Antigos ({vm.PendingLegacyMigrationCount})";
            }

            if (vm.SavedProjects.Count == 0)
            {
                var emptyCard = CreateTechnicalCard(new Thickness(12), palette);
                emptyCard.Child = new TextBlock
                {
                    Text = "Nenhum projeto encontrado na biblioteca local.",
                    FontSize = 12,
                    Foreground = BrushOf(palette.TextSecondaryArgb),
                };
                listStack.Children.Add(emptyCard);
                return;
            }

            foreach (ProjectSummary item in vm.SavedProjects)
            {
                var card = CreateTechnicalCard(new Thickness(9, 7, 9, 7), palette);
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var thumbBorder = new Border
                {
                    Width = 52,
                    Height = 36,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = BrushOf(palette.DialogSurfaceArgb),
                    BorderBrush = BrushOf(palette.BorderArgb),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                };
                var thumbImg = new Image
                {
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                using (SKBitmap bmp = CatalogPreview3DService.RenderProjectThumbnailBitmap(
                    item,
                    darkTheme: darkTheme,
                    widthPx: 104,
                    heightPx: 72))
                {
                    _ = SkiaBitmapBridge.BlitBitmap(bmp, thumbImg, currentBitmap: null);
                }
                thumbBorder.Child = thumbImg;
                Grid.SetColumn(thumbBorder, 0);

                bool isLegacy = string.Equals(item.PackageFormat, "legacy-json", StringComparison.OrdinalIgnoreCase);
                string formatTag = isLegacy ? "JSON legado" : ".cmaker";
                string tagsSuffix = item.Tags is { Count: > 0 }
                    ? $"  •  {string.Join(", ", item.Tags.Take(3).Select(t => $"#{t}"))}"
                    : string.Empty;

                var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = $"{item.Name}  [{formatTag}]",
                    FontFamily = GetFont("CmUiFontFamily"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    Foreground = BrushOf(palette.TextPrimaryArgb),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"{item.ModelName}  •  {item.ElementCount} elem.  •  {item.AssetCount} assets  •  {FormatDialogSize(item.PackageSizeBytes)}  •  {item.UpdatedAt}{tagsSuffix}",
                    FontFamily = GetFont("CmMonoFontFamily"),
                    FontSize = 10,
                    Foreground = BrushOf(palette.TextSecondaryArgb),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                Grid.SetColumn(info, 1);

                string captureId = item.Id;

                var openBtn = new Button
                {
                    Content = "Abrir",
                    Height = 25,
                    MinHeight = 25,
                    Padding = new Thickness(9, 0, 9, 0),
                    Style = GetStyle("PrimaryAccentButtonStyle"),
                };
                openBtn.Click += (_, _) =>
                {
                    deferredAction = async () =>
                    {
                        if (await vm.ConfirmUnsavedIfNeededAsync())
                        {
                            vm.OpenSavedProject(captureId);
                        }
                    };
                    dialog?.Hide();
                };
                Grid.SetColumn(openBtn, 2);

                var dupBtn = new Button
                {
                    Content = "Duplicar",
                    Height = 25,
                    MinHeight = 25,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = GetStyle("CadSecondaryButtonStyle"),
                };
                dupBtn.Click += (_, _) =>
                {
                    pendingDeleteId = null;
                    vm.DuplicateSavedProject(captureId);
                    RebuildList();
                };
                Grid.SetColumn(dupBtn, 3);

                var exportBtn = new Button
                {
                    Content = "Exportar",
                    Height = 25,
                    MinHeight = 25,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = GetStyle("CadSecondaryButtonStyle"),
                };
                exportBtn.Click += (_, _) =>
                {
                    deferredAction = () => vm.ExportSavedProjectPackageAsync(captureId);
                    dialog?.Hide();
                };
                Grid.SetColumn(exportBtn, 4);

                bool awaitingDeleteConfirm = string.Equals(pendingDeleteId, captureId, StringComparison.Ordinal);
                var delBtn = new Button
                {
                    Content = awaitingDeleteConfirm ? "Confirmar?" : "Excluir",
                    Height = 25,
                    MinHeight = 25,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = GetStyle("DangerOutlineButtonStyle"),
                };
                delBtn.Click += (_, _) =>
                {
                    if (!string.Equals(pendingDeleteId, captureId, StringComparison.Ordinal))
                    {
                        pendingDeleteId = captureId;
                        RebuildList();
                        return;
                    }

                    pendingDeleteId = null;
                    vm.DeleteSavedProject(captureId);
                    RebuildList();
                };
                Grid.SetColumn(delBtn, 5);

                row.Children.Add(thumbBorder);
                row.Children.Add(info);
                row.Children.Add(openBtn);
                row.Children.Add(dupBtn);
                row.Children.Add(exportBtn);
                row.Children.Add(delBtn);
                card.Child = row;
                listStack.Children.Add(card);
            }
        }

        searchBox.TextChanged += (_, _) =>
        {
            pendingDeleteId = null;
            vm.ProjectSearchQuery = searchBox.Text ?? string.Empty;
            RebuildList();
        };

        migrateBtn.Click += (_, _) =>
        {
            pendingDeleteId = null;
            LegacyCmakerMigrationReport report = vm.MigrateLegacyProjects();
            migrationNote.Text = $"Migração concluída: {report.MigratedCount} convertido(s), {report.SkippedExistingCount} já existente(s), {report.ErrorCount} erro(s).";
            migrationNote.Visibility = Visibility.Visible;
            RebuildList();
        };

        RebuildList();
        root.Children.Add(new ScrollViewer { MaxHeight = 310, Content = listStack });

        dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = "Gerenciador de Biblioteca (.cmaker)",
            Content = root,
            SecondaryButtonText = "Importar .cmaker / legado...",
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.None,
        };
        ConfigureDialogChrome(dialog, palette);
        dialog.Resources["ContentDialogMaxWidth"] = 840.0;

        ContentDialogResult res = await dialog.ShowAsync();
        if (deferredAction is not null)
        {
            await deferredAction();
        }
        else if (res == ContentDialogResult.Secondary)
        {
            await vm.DispatchCommandAsync(DesktopCommandIds.FileImportPackage);
        }
    }

    public static async Task ShowProjectPropertiesDialogAsync(XamlRoot xamlRoot, MainWindowViewModel vm)
    {
        (ElementTheme theme, DialogThemePalette palette) = ResolveThemeAndPalette(xamlRoot, vm);

        var stack = new StackPanel { Spacing = 8, MinWidth = 540 };
        stack.Children.Add(CreateSectionHeader("Metadados Editáveis do Projeto (.cmaker)", palette));

        var nameBox = new TextBox
        {
            Style = GetStyle("CadTextBoxStyle"),
            FontFamily = GetFont("CmUiFontFamily"),
            Text = vm.SavedProjectName ?? vm.ActiveModelOrProjectName,
            PlaceholderText = "Nome do projeto",
        };
        var descBox = new TextBox
        {
            Style = GetStyle("CadTextBoxStyle"),
            FontFamily = GetFont("CmUiFontFamily"),
            Text = vm.SavedProjectDescription,
            PlaceholderText = "Descrição breve da peça ou cliente",
        };
        var authorBox = new TextBox
        {
            Style = GetStyle("CadTextBoxStyle"),
            FontFamily = GetFont("CmUiFontFamily"),
            Text = string.IsNullOrWhiteSpace(vm.SavedProjectAuthor) ? AppVersion.DeveloperName : vm.SavedProjectAuthor,
            PlaceholderText = AppVersion.DeveloperName,
        };
        var tagsBox = new TextBox
        {
            Style = GetStyle("CadTextBoxStyle"),
            FontFamily = GetFont("CmUiFontFamily"),
            Text = vm.SavedProjectTagsText,
            PlaceholderText = "ex.: ifac, organizador, corte-laser",
        };
        var notesBox = new TextBox
        {
            Style = GetStyle("CadTextBoxStyle"),
            FontFamily = GetFont("CmUiFontFamily"),
            Text = vm.SavedProjectNotes,
            PlaceholderText = "Notas de fabricação (potência, velocidade, lote de MDF...)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            MaxHeight = 88,
        };

        var metaCard = CreateTechnicalCard(new Thickness(10, 8, 10, 8), palette);
        var metaForm = new StackPanel { Spacing = 6 };
        metaForm.Children.Add(CreateLabeledFieldRow("Nome do Projeto", nameBox, palette));
        metaForm.Children.Add(CreateLabeledFieldRow("Descrição", descBox, palette));
        metaForm.Children.Add(CreateLabeledFieldRow("Autor / Origem", authorBox, palette));
        metaForm.Children.Add(CreateLabeledFieldRow("Tags (vírgulas)", tagsBox, palette));
        metaForm.Children.Add(CreateLabeledFieldRow("Notas Técnicas", notesBox, palette));
        metaCard.Child = metaForm;
        stack.Children.Add(metaCard);

        stack.Children.Add(CreateSectionHeader("Pacote Autocontido .cmaker e Especificações CNC", palette));

        var table = CreateTableContainer(palette);
        var rows = new StackPanel();
        rows.Children.Add(CreateSpecRow(0, "IDENTIFICADOR", vm.SavedProjectId ?? "Projeto ainda não salvo localmente", palette));
        rows.Children.Add(CreateSpecRow(1, "FORMATO / SCHEMA", vm.SavedProjectFormatBadge, palette));
        rows.Children.Add(CreateSpecRow(2, "CRIADO / ATUALIZADO", $"{FormatDialogTimestamp(vm.SavedProjectCreatedAt)}  |  {FormatDialogTimestamp(vm.SavedProjectUpdatedAt)}", palette));
        rows.Children.Add(CreateSpecRow(3, "ASSETS EMBUTIDOS", $"{vm.SavedProjectAssetCount} arquivo(s)  •  Pacote: {FormatDialogSize(vm.SavedProjectPackageSizeBytes)}", palette));
        rows.Children.Add(CreateSpecRow(4, "ARQUIVO .CMAKER", string.IsNullOrWhiteSpace(vm.SavedProjectPackagePath) ? "Será criado ao salvar na biblioteca" : vm.SavedProjectPackagePath, palette, selectable: true));
        rows.Children.Add(CreateSpecRow(5, "MODELO PARAMÉTRICO", $"{vm.Box.SelectedModel.DisplayName} ({vm.Box.SelectedModel.TotalPanels} chapas)", palette));
        rows.Children.Add(CreateSpecRow(6, "DIMENSÕES EXTERNAS", $"{vm.Box.DimensionsBadgeText}  •  Interna: {vm.Box.InnerDimensionsDisplay}", palette));
        rows.Children.Add(CreateSpecRow(7, "ÁREA / PERÍMETRO CNC", $"{vm.Box.EstimatedAreaDisplay}  •  Perímetro: {vm.Box.TotalCutPerimeterMeters:0.00} m", palette));
        rows.Children.Add(CreateSpecRow(8, "ELEMENTOS & SAT", $"{vm.Personalization.TotalElementCount} elemento(s)  •  {vm.Personalization.ValidationSummaryBadge}", palette, monoValue: false));
        table.Child = rows;
        stack.Children.Add(table);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = "Propriedades e Metadados do Projeto",
            Content = new ScrollViewer { MaxHeight = 520, Content = stack },
            PrimaryButtonText = vm.InDesigner ? "Salvar Metadados" : string.Empty,
            CloseButtonText = "Fechar",
            DefaultButton = vm.InDesigner ? ContentDialogButton.Primary : ContentDialogButton.None,
        };
        ConfigureDialogChrome(dialog, palette);
        dialog.Resources["ContentDialogMaxWidth"] = 780.0;

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && vm.InDesigner)
        {
            string resolvedName = string.IsNullOrWhiteSpace(nameBox.Text)
                ? (vm.SavedProjectName ?? vm.ActiveModelOrProjectName)
                : nameBox.Text.Trim();
            IReadOnlyList<string> parsedTags = (tagsBox.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await vm.UpdateActiveProjectMetadataAsync(
                name: resolvedName,
                description: descBox.Text?.Trim() ?? string.Empty,
                author: string.IsNullOrWhiteSpace(authorBox.Text) ? AppVersion.DeveloperName : authorBox.Text.Trim(),
                tags: parsedTags,
                notes: notesBox.Text?.Trim() ?? string.Empty);
        }
    }

    private static string FormatDialogSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", bytes / 1024.0);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} MB", bytes / (1024.0 * 1024.0));
    }

    private static string FormatDialogTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
        {
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return raw.Length > 16 ? raw[..16] : raw;
    }

    private static (ElementTheme Theme, DialogThemePalette Palette) ResolveThemeAndPalette(
        XamlRoot xamlRoot,
        MainWindowViewModel? vm)
    {
        ElementTheme theme = xamlRoot.Content is FrameworkElement fe ? fe.RequestedTheme : ElementTheme.Default;
        bool isLight = theme == ElementTheme.Light ||
                       (theme == ElementTheme.Default &&
                        ((xamlRoot.Content is FrameworkElement rootEl && rootEl.ActualTheme == ElementTheme.Light) ||
                         vm?.Theme.ResolvedTheme == ResolvedAppTheme.Light));
        ElementTheme resolvedElementTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
        DialogThemePalette palette = isLight ? DialogThemePalette.Light : DialogThemePalette.Dark;
        return (resolvedElementTheme, palette);
    }

    private static SolidColorBrush BrushOf(uint argb) => new(ColorOf(argb));

    private static Color ColorOf(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];
    private static FontFamily GetFont(string key) => (FontFamily)Application.Current.Resources[key];

    private static TabViewItem CreateThemedTabItem(string header, StackPanel content, DialogThemePalette palette) => new()
    {
        Header = header,
        IsClosable = false,
        Background = BrushOf(palette.DialogSurfaceArgb),
        Foreground = BrushOf(palette.TextPrimaryArgb),
        Content = new ScrollViewer
        {
            Background = BrushOf(palette.DialogSurfaceArgb),
            Content = content,
        },
    };

    private static void ConfigureTabViewResources(TabView tabView, DialogThemePalette palette)
    {
        tabView.Resources["TabViewBackground"] = BrushOf(palette.DialogSurfaceArgb);
        tabView.Resources["TabViewBorderBrush"] = BrushOf(palette.BorderArgb);
        tabView.Resources["TabViewItemHeaderBackground"] = BrushOf(palette.HeaderStripArgb);
        tabView.Resources["TabViewItemHeaderBackgroundPointerOver"] = BrushOf(palette.ButtonHoverSurfaceArgb);
        tabView.Resources["TabViewItemHeaderBackgroundPressed"] = BrushOf(palette.CardSurfaceArgb);
        tabView.Resources["TabViewItemHeaderBackgroundSelected"] = BrushOf(palette.CardSurfaceArgb);
        tabView.Resources["TabViewItemHeaderForeground"] = BrushOf(palette.TextSecondaryArgb);
        tabView.Resources["TabViewItemHeaderForegroundPointerOver"] = BrushOf(palette.TextPrimaryArgb);
        tabView.Resources["TabViewItemHeaderForegroundSelected"] = BrushOf(palette.TextPrimaryArgb);
        tabView.Resources["TabViewSelectedItemHeaderBackground"] = BrushOf(palette.CardSurfaceArgb);
    }

    private static void ConfigureDialogChrome(ContentDialog dialog, DialogThemePalette palette)
    {
        dialog.HorizontalAlignment = HorizontalAlignment.Center;
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        dialog.Resources["ContentDialogBackground"] = BrushOf(palette.DialogSurfaceArgb);
        dialog.Resources["ContentDialogTopOverlay"] = BrushOf(palette.DialogSurfaceArgb);
        dialog.Resources["ContentDialogBorderBrush"] = BrushOf(palette.BorderArgb);
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = BrushOf(palette.BorderArgb);
        dialog.Resources["AccentButtonBackground"] = BrushOf(palette.AccentPrimaryArgb);
        dialog.Resources["AccentButtonBackgroundPointerOver"] = BrushOf(palette.AccentPrimaryHoverArgb);
        dialog.Resources["AccentButtonBorderBrush"] = BrushOf(palette.AccentPrimaryArgb);
        dialog.Resources["AccentButtonForeground"] = new SolidColorBrush(Colors.White);
        dialog.Background = BrushOf(palette.DialogSurfaceArgb);
        dialog.Foreground = BrushOf(palette.TextPrimaryArgb);
        dialog.BorderBrush = BrushOf(palette.BorderArgb);
        dialog.BorderThickness = new Thickness(1);
        dialog.CornerRadius = new CornerRadius(4);
        dialog.PrimaryButtonStyle = GetStyle("PrimaryAccentButtonStyle");
        dialog.SecondaryButtonStyle = GetStyle("CadSecondaryButtonStyle");
        dialog.CloseButtonStyle = GetStyle("CadSecondaryButtonStyle");
    }

    private static TextBlock CreateSectionHeader(string title, DialogThemePalette palette) => new()
    {
        Text = title.ToUpperInvariant(),
        FontFamily = GetFont("CmUiFontFamily"),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        CharacterSpacing = 40,
        Foreground = BrushOf(palette.TextSecondaryArgb),
        Margin = new Thickness(0, 2, 0, 2),
    };

    private static Border CreateTechnicalCard(Thickness padding, DialogThemePalette palette) => new()
    {
        Padding = padding,
        Background = BrushOf(palette.CardSurfaceArgb),
        BorderBrush = BrushOf(palette.BorderArgb),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
    };

    private static Border CreateTableContainer(DialogThemePalette palette) => new()
    {
        Background = BrushOf(palette.CardSurfaceArgb),
        BorderBrush = BrushOf(palette.BorderArgb),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
    };

    private static Grid CreateSpecRow(
        int rowIndex,
        string label,
        string value,
        DialogThemePalette palette,
        bool monoValue = true,
        bool selectable = false)
    {
        var row = new Grid
        {
            Background = BrushOf(rowIndex % 2 == 0 ? palette.RowEvenArgb : palette.RowAltArgb),
            Padding = new Thickness(10, 6, 10, 6),
            ColumnSpacing = 12,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(palette.TextSecondaryArgb),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock
        {
            Text = value,
            FontFamily = GetFont(monoValue ? "CmMonoFontFamily" : "CmUiFontFamily"),
            FontSize = monoValue ? 11 : 12,
            Foreground = BrushOf(palette.TextPrimaryArgb),
            IsTextSelectionEnabled = selectable,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueBlock, 1);

        row.Children.Add(labelBlock);
        row.Children.Add(valueBlock);
        return row;
    }

    private static Grid CreateLabeledFieldRow(string label, FrameworkElement control, DialogThemePalette palette)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 12,
            Foreground = BrushOf(palette.TextSecondaryArgb),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(control, 1);

        row.Children.Add(labelText);
        row.Children.Add(control);
        return row;
    }

    private static Grid BuildShortcutCell(ShortcutReferenceItem item, DialogThemePalette palette)
    {
        var cell = new Grid { ColumnSpacing = 8 };
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var kbd = new Border
        {
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = BrushOf(palette.KbdBgArgb),
            BorderBrush = BrushOf(palette.KbdBorderArgb),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = new TextBlock
            {
                Text = item.Shortcut,
                FontFamily = GetFont("CmMonoFontFamily"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf(palette.KbdTextArgb),
            },
        };
        Grid.SetColumn(kbd, 0);

        var actText = new TextBlock
        {
            Text = item.Action,
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 11.5,
            Foreground = BrushOf(palette.TextPrimaryArgb),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(actText, 1);

        cell.Children.Add(kbd);
        cell.Children.Add(actText);
        return cell;
    }

    private static Border BuildIdentityCard(
        string imageUri,
        string title,
        string tag,
        string description,
        DialogThemePalette palette)
    {
        var plaqueContent = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Source = new BitmapImage(new Uri(imageUri)),
        };

        return BuildHighlightCard(
            plaqueContent,
            palette.BrandPlaqueBgArgb,
            palette.SubtleBorderArgb,
            title,
            tag,
            description,
            palette);
    }

    private static Border BuildDeveloperHighlightCard(DialogThemePalette palette)
    {
        var devStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 2,
        };
        devStack.Children.Add(new TextBlock
        {
            Text = "</> PGC",
            FontFamily = GetFont("CmMonoFontFamily"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = BrushOf(palette.AccentHighlightArgb),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        devStack.Children.Add(new TextBlock
        {
            Text = "@PddroGCruz",
            FontFamily = GetFont("CmMonoFontFamily"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(palette.TextPrimaryArgb),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        return BuildHighlightCard(
            devStack,
            palette.HeaderStripArgb,
            palette.AccentBadgeBorderArgb,
            title: AppVersion.DeveloperName,
            tag: "DESENVOLVEDOR & ARQUITETO DE SOFTWARE",
            description: $"Desenvolvimento integral da plataforma nativa CAD/CAM (.NET 8 + WinUI 3 + SkiaSharp) • Contato: {AppVersion.DeveloperEmail}",
            palette: palette);
    }

    private static Border BuildHighlightCard(
        UIElement plaqueChild,
        uint plaqueBgArgb,
        uint plaqueBorderArgb,
        string title,
        string tag,
        string description,
        DialogThemePalette palette)
    {
        var card = CreateTechnicalCard(new Thickness(9, 6, 9, 6), palette);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var plaque = new Border
        {
            Width = 148,
            Height = 46,
            Padding = new Thickness(8, 4, 8, 4),
            Background = BrushOf(plaqueBgArgb),
            BorderBrush = BrushOf(plaqueBorderArgb),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = plaqueChild,
        };
        Grid.SetColumn(plaque, 0);

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(palette.TextPrimaryArgb),
        });
        info.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 1, 6, 1),
            Background = BrushOf(palette.AccentBadgeBgArgb),
            BorderBrush = BrushOf(palette.AccentBadgeBorderArgb),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = new TextBlock
            {
                Text = tag,
                FontFamily = GetFont("CmMonoFontFamily"),
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf(palette.AccentHighlightArgb),
            },
        });
        info.Children.Add(new TextBlock
        {
            Text = description,
            FontFamily = GetFont("CmUiFontFamily"),
            FontSize = 11,
            Foreground = BrushOf(palette.TextSecondaryArgb),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(info, 1);

        grid.Children.Add(plaque);
        grid.Children.Add(info);
        card.Child = grid;
        return card;
    }
}
