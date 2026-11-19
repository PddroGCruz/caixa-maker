using CaixaMaker.App.Helpers;
using CaixaMaker.App.Interop;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Views.Dialogs;
using CaixaMaker.Core.Logging;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace CaixaMaker.App;

/// <summary>Janela principal nativa WinUI 3 hospedando MenuBar Win32, Central de Trabalho e Workspace Split.</summary>
public sealed partial class MainWindow : Window
{
    public const int InitialWidthDip = 1360;
    public const int InitialHeightDip = 860;
    public const int MinWidthDip = 1024;
    public const int MinHeightDip = 680;

    private readonly AppPaths _paths;
    private readonly RotatingFileLogger _logger;
    private IDisposable? _minSizeSubclass;
    private bool _allowDirectClose;
    private bool _hasOpenModal;

    public MainWindowViewModel ViewModel { get; }

    public MainWindow(MainWindowViewModel viewModel, AppPaths paths, RotatingFileLogger logger)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();

        ConfigureWindow();
        BindViewModel();
    }

    private void ConfigureWindow()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        double scale = NativeWindowHelper.GetDpiScaleFactor(hwnd);
        int widthPx = (int)Math.Round(InitialWidthDip * scale);
        int heightPx = (int)Math.Round(InitialHeightDip * scale);
        AppWindow.Resize(new SizeInt32(widthPx, heightPx));
        CenterOnDisplayArea(widthPx, heightPx);

        _minSizeSubclass = NativeWindowHelper.AttachMinSizeSubclass(hwnd, MinWidthDip, MinHeightDip);
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => _minSizeSubclass?.Dispose();
    }

    private void CenterOnDisplayArea(int widthPx, int heightPx)
    {
        DisplayArea? display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is null) return;

        RectInt32 work = display.WorkArea;
        int x = work.X + Math.Max(0, (work.Width - widthPx) / 2);
        int y = work.Y + Math.Max(0, (work.Height - heightPx) / 2);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void BindViewModel()
    {
        CatalogView.BindViewModel(ViewModel);
        WorkspaceShell.BindViewModel(ViewModel);

        ViewModel.MenuStateChanged += (_, _) => SyncWindowChromeAndMenus();
        ViewModel.Theme.ThemeChanged += (_, resolved) => ApplyTheme(resolved);
        ViewModel.OpenPreferencesRequested += async (_, _) => await RunModalAsync(() => AppDialogs.ShowPreferencesDialogAsync(RootLayout.XamlRoot, ViewModel));
        ViewModel.OpenAboutRequested += async (_, tabIndex) => await RunModalAsync(() => AppDialogs.ShowAboutDialogAsync(RootLayout.XamlRoot, ViewModel, _paths, tabIndex));
        ViewModel.OpenProjectsManagerRequested += async (_, saveAs) => await RunModalAsync(() => AppDialogs.ShowProjectsManagerDialogAsync(RootLayout.XamlRoot, ViewModel, saveAs));
        ViewModel.OpenProjectPropertiesRequested += async (_, _) => await RunModalAsync(() => AppDialogs.ShowProjectPropertiesDialogAsync(RootLayout.XamlRoot, ViewModel));
        ViewModel.WindowActionRequested += async (_, action) => await HandleWindowActionAsync(action);

        ApplyTheme(ViewModel.Theme.ResolvedTheme);
        SyncWindowChromeAndMenus();
    }

    private async Task RunModalAsync(Func<Task> openAction)
    {
        if (RootLayout.XamlRoot is null || _hasOpenModal) return;
        _hasOpenModal = true;
        try
        {
            await openAction();
        }
        catch (Exception ex)
        {
            _logger.Error("UI", $"Falha em modal nativo: {ex}");
        }
        finally
        {
            _hasOpenModal = false;
        }
    }

    private void ApplyTheme(ResolvedAppTheme resolved)
    {
        RootLayout.RequestedTheme = resolved == ResolvedAppTheme.Dark
            ? ElementTheme.Dark
            : ElementTheme.Light;
    }

    private void SyncWindowChromeAndMenus()
    {
        Title = ViewModel.WindowTitle;
        CatalogView.Visibility = ViewModel.InDesigner ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceShell.Visibility = ViewModel.InDesigner ? Visibility.Visible : Visibility.Collapsed;

        // 1. Arquivo
        MenuFileSave.IsEnabled = ViewModel.CanSaveProject;
        MenuFileSaveAs.IsEnabled = ViewModel.CanSaveProject;
        MenuFileExportPackage.IsEnabled = ViewModel.InDesigner;
        MenuFileProperties.IsEnabled = ViewModel.InDesigner;
        MenuFileBackToCatalog.IsEnabled = ViewModel.CanReturnToCatalog;
        RebuildRecentProjectsMenu();

        // 2. Editar
        MenuEditUndo.IsEnabled = ViewModel.CanUndo;
        MenuEditRedo.IsEnabled = ViewModel.CanRedo;
        MenuEditCopy.IsEnabled = ViewModel.CanCopy;
        MenuEditPaste.IsEnabled = ViewModel.CanPaste;
        MenuEditDuplicate.IsEnabled = ViewModel.CanDuplicate;
        MenuEditDelete.IsEnabled = ViewModel.CanDelete;
        MenuEditClearAll.IsEnabled = ViewModel.CanClearElements;
        MenuEditResetParams.IsEnabled = ViewModel.InDesigner;

        // 3. Exibir
        MenuViewMode3D.IsEnabled = ViewModel.InDesigner;
        MenuViewMode2D.IsEnabled = ViewModel.InDesigner;
        MenuViewModeSplit.IsEnabled = ViewModel.InDesigner;
        MenuViewMode3D.IsChecked = ViewModel.IsViewMode3D;
        MenuViewMode2D.IsChecked = ViewModel.IsViewMode2D;
        MenuViewModeSplit.IsChecked = ViewModel.IsViewModeSplit;
        MenuViewLeftSidebar.IsEnabled = ViewModel.InDesigner;
        MenuViewRightSidebar.IsEnabled = ViewModel.InDesigner;
        MenuViewToolbar.IsEnabled = ViewModel.InDesigner;
        MenuViewDimensions.IsEnabled = ViewModel.InDesigner;
        MenuViewResetCamera.IsEnabled = ViewModel.InDesigner;
        MenuViewLeftSidebar.IsChecked = ViewModel.LeftSidebarOpen;
        MenuViewRightSidebar.IsChecked = ViewModel.RightSidebarOpen;
        MenuViewToolbar.IsChecked = ViewModel.ToolbarVisible;
        MenuViewDimensions.IsChecked = ViewModel.Box.ShowDimensions;

        MenuThemeSystem.IsChecked = ViewModel.Theme.IsSystemPreference;
        MenuThemeLight.IsChecked = ViewModel.Theme.Preference == AppThemePreference.Light;
        MenuThemeDark.IsChecked = ViewModel.Theme.Preference == AppThemePreference.Dark;

        // 4. Projeto
        MenuAreaModeling.IsEnabled = ViewModel.InDesigner;
        MenuAreaPersonalization.IsEnabled = ViewModel.InDesigner;
        MenuAreaFabrication.IsEnabled = ViewModel.InDesigner;
        MenuAreaModeling.IsChecked = ViewModel.IsModelingArea;
        MenuAreaPersonalization.IsChecked = ViewModel.IsPersonalizationArea;
        MenuAreaFabrication.IsChecked = ViewModel.IsFabricationArea;

        MenuProjExploded.IsEnabled = ViewModel.CanToggleExploded;
        MenuProjExploded.IsChecked = ViewModel.Box.ExplodedView;
        MenuProjLid.IsEnabled = ViewModel.CanToggleLidAngle;
        MenuProjToggleModel.IsEnabled = ViewModel.InDesigner;
        MenuProjAddText.IsEnabled = ViewModel.InDesigner;
        MenuProjImportSvg.IsEnabled = ViewModel.InDesigner;
        MenuProjImportRaster.IsEnabled = ViewModel.InDesigner;
        MenuProjValidate.IsEnabled = ViewModel.InDesigner;

        // 5. Ferramentas
        MenuToolsExportSvg.IsEnabled = ViewModel.CanExportCam;
        MenuToolsExportDxf.IsEnabled = ViewModel.CanExportCam;
        MenuToolsExportPdf.IsEnabled = ViewModel.CanExportCam;
        MenuToolsExportZip.IsEnabled = ViewModel.CanExportCam;

        // 6. Janela
        MenuWindowFullscreen.IsChecked = ViewModel.IsFullscreen;
    }

    private void RebuildRecentProjectsMenu()
    {
        MenuRecentProjectsSub.Items.Clear();
        if (ViewModel.RecentProjects.Count == 0)
        {
            MenuRecentProjectsSub.Items.Add(new MenuFlyoutItem
            {
                Text = "Nenhum projeto recente",
                IsEnabled = false,
            });
            return;
        }

        foreach (ProjectSummary proj in ViewModel.RecentProjects)
        {
            var item = new MenuFlyoutItem
            {
                Text = $"{proj.Name} — {proj.ModelName}",
                Tag = DesktopCommandIds.FileOpenRecentPrefix + proj.Id,
            };
            item.Click += OnMenuCommandClick;
            MenuRecentProjectsSub.Items.Add(item);
        }
    }

    private async void OnMenuCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cmdId })
        {
            await ViewModel.DispatchCommandAsync(cmdId);
        }
    }

    private async void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        bool alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0;

        string? cmdId = MapShortcutToCommandId(e.Key, ctrl, shift, alt);
        if (cmdId is null &&
            ViewModel.InDesigner &&
            ViewModel.IsPersonalizationArea &&
            ViewModel.CenterViewMode != CenterViewMode.Mode3D &&
            !ctrl && !alt && !shift)
        {
            cmdId = e.Key switch
            {
                VirtualKey.V => DesktopCommandIds.ToolSelect,
                VirtualKey.R => DesktopCommandIds.ToolDrawRectangle,
                VirtualKey.C => DesktopCommandIds.ToolDrawCircle,
                VirtualKey.S => DesktopCommandIds.ToolDrawSlot,
                VirtualKey.P => DesktopCommandIds.ToolDrawPolygon,
                VirtualKey.X => DesktopCommandIds.ToolDrawCrosshair,
                VirtualKey.Escape => DesktopCommandIds.ToolCancel,
                _ => null,
            };
        }

        if (cmdId is null) return;

        object? focused = RootLayout.XamlRoot is not null
            ? FocusManager.GetFocusedElement(RootLayout.XamlRoot)
            : null;
        FocusControlKind focusKind = UiConvert.ClassifyFocusedControl(focused, out bool isReadOnly);
        var context = new KeyboardFocusContext(focusKind, isReadOnly, _hasOpenModal);

        if (!KeyboardShortcutGuard.ShouldDispatchCommand(context, cmdId))
        {
            return;
        }

        e.Handled = true;
        await ViewModel.HandleAcceleratorAsync(cmdId, context);
    }

    private static string? MapShortcutToCommandId(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (key == VirtualKey.F1 && !ctrl && !alt) return DesktopCommandIds.HelpShortcuts;
        if (key == VirtualKey.F5 && !ctrl && !alt) return DesktopCommandIds.ProjectValidate;
        if (key == VirtualKey.F11 && !ctrl && !alt) return DesktopCommandIds.WindowToggleFullscreen;
        if (key == VirtualKey.Delete && !ctrl && !alt && !shift) return DesktopCommandIds.EditDelete;

        if (ctrl && !alt)
        {
            if (shift)
            {
                return key switch
                {
                    VirtualKey.O => DesktopCommandIds.FileImportPackage,
                    VirtualKey.S => DesktopCommandIds.FileSaveAs,
                    VirtualKey.B => DesktopCommandIds.ViewToggleRightSidebar,
                    VirtualKey.V => DesktopCommandIds.ViewToggleDimensions,
                    VirtualKey.Back => DesktopCommandIds.EditClearAll,
                    _ => null,
                };
            }

            return key switch
            {
                VirtualKey.N => DesktopCommandIds.FileNew,
                VirtualKey.O => DesktopCommandIds.FileOpenProject,
                VirtualKey.S => DesktopCommandIds.FileSave,
                VirtualKey.E => DesktopCommandIds.FileExportPackage,
                VirtualKey.Z => DesktopCommandIds.EditUndo,
                VirtualKey.Y => DesktopCommandIds.EditRedo,
                VirtualKey.C => DesktopCommandIds.EditCopy,
                VirtualKey.V => DesktopCommandIds.EditPaste,
                VirtualKey.D => DesktopCommandIds.EditDuplicate,
                VirtualKey.A => DesktopCommandIds.EditSelectAll,
                VirtualKey.B => DesktopCommandIds.ViewToggleLeftSidebar,
                VirtualKey.R => DesktopCommandIds.ViewResetCamera,
                VirtualKey.M => DesktopCommandIds.ProjectToggleModel,
                VirtualKey.Number1 or VirtualKey.NumberPad1 => DesktopCommandIds.ViewMode3D,
                VirtualKey.Number2 or VirtualKey.NumberPad2 => DesktopCommandIds.ViewMode2D,
                VirtualKey.Number3 or VirtualKey.NumberPad3 => DesktopCommandIds.ViewModeSplit,
                (VirtualKey)188 => DesktopCommandIds.ToolsPreferences, // VK_OEM_COMMA
                _ => null,
            };
        }

        if (alt && !ctrl)
        {
            return key switch
            {
                VirtualKey.Number1 => DesktopCommandIds.ProjectAreaModeling,
                VirtualKey.Number2 => DesktopCommandIds.ProjectAreaPersonalization,
                VirtualKey.Number3 => DesktopCommandIds.ProjectAreaFabrication,
                VirtualKey.E => DesktopCommandIds.ProjectToggleExploded,
                VirtualKey.D => DesktopCommandIds.ProjectToggleLid,
                VirtualKey.T => DesktopCommandIds.ProjectFocusText,
                _ => null,
            };
        }

        return null;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowDirectClose) return;

        if (ViewModel.InDesigner && ViewModel.IsDirty)
        {
            args.Cancel = true;
            if (await ViewModel.ConfirmUnsavedIfNeededAsync())
            {
                _allowDirectClose = true;
                Close();
            }
        }
    }

    internal Grid RootElement => RootLayout;

    internal void ForceCloseForSmokeTest()
    {
        _allowDirectClose = true;
        Close();
    }

    private async Task HandleWindowActionAsync(string action)
    {
        switch (action)
        {
            case DesktopCommandIds.FileExit:
                if (await ViewModel.ConfirmUnsavedIfNeededAsync())
                {
                    _allowDirectClose = true;
                    Close();
                }
                break;

            case DesktopCommandIds.WindowToggleFullscreen:
                if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                {
                    AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    ViewModel.IsFullscreen = false;
                }
                else
                {
                    AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    ViewModel.IsFullscreen = true;
                }
                SyncWindowChromeAndMenus();
                break;

            case DesktopCommandIds.WindowMinimize:
                if (AppWindow.Presenter is OverlappedPresenter pMin)
                {
                    pMin.Minimize();
                }
                break;

            case DesktopCommandIds.WindowToggleMaximize:
                if (AppWindow.Presenter is OverlappedPresenter pMax)
                {
                    if (pMax.State == OverlappedPresenterState.Maximized)
                        pMax.Restore();
                    else
                        pMax.Maximize();
                }
                break;
        }
    }
}
