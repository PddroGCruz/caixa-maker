using CaixaMaker.App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CaixaMaker.App.Services;

/// <summary>
/// Implementação WinUI 3 / Win32 de <see cref="INativeFileDialogService"/> utilizando
/// <see cref="FileOpenPicker"/>, <see cref="FileSavePicker"/> e <see cref="ContentDialog"/>.
/// </summary>
public sealed class WinUiNativeFileDialogService : INativeFileDialogService
{
    private Window? _ownerWindow;

    public WinUiNativeFileDialogService(Window? ownerWindow = null)
    {
        _ownerWindow = ownerWindow;
    }

    public void AttachWindow(Window ownerWindow)
    {
        _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
    }

    private nint WindowHandle => _ownerWindow is null
        ? 0
        : WindowNative.GetWindowHandle(_ownerWindow);

    private XamlRoot? Root => _ownerWindow?.Content?.XamlRoot;

    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<NativeFileFilter> filters)
    {
        if (_ownerWindow is null) return null;
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };

        InitializeWithWindow.Initialize(picker, WindowHandle);

        bool anyFilter = false;
        foreach (NativeFileFilter filter in filters)
        {
            foreach (string ext in filter.Extensions)
            {
                string dotExt = ext.StartsWith('.') ? ext : "." + ext;
                picker.FileTypeFilter.Add(dotExt);
                anyFilter = true;
            }
        }

        if (!anyFilter)
        {
            picker.FileTypeFilter.Add("*");
        }

        StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(
        string title,
        string suggestedFileName,
        IReadOnlyList<NativeFileFilter> filters)
    {
        if (_ownerWindow is null) return null;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
        };

        InitializeWithWindow.Initialize(picker, WindowHandle);

        foreach (NativeFileFilter filter in filters)
        {
            IList<string> exts = filter.Extensions
                .Select(ext => ext.StartsWith('.') ? ext : "." + ext)
                .ToList();
            if (exts.Count > 0)
            {
                picker.FileTypeChoices.Add(filter.Description, exts);
                picker.DefaultFileExtension ??= exts[0];
            }
        }

        if (picker.FileTypeChoices.Count == 0)
        {
            picker.FileTypeChoices.Add("Arquivo", new List<string> { ".svg" });
        }

        StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<UnsavedChangesDecision> PromptUnsavedChangesAsync(string projectName)
    {
        if (Root is null)
        {
            return UnsavedChangesDecision.DiscardAndContinue;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = "Alterações não salvas",
            Content = $"O projeto \"{projectName}\" possui modificações não salvas. Deseja salvar antes de continuar?",
            PrimaryButtonText = "Salvar",
            SecondaryButtonText = "Descartar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => UnsavedChangesDecision.SaveAndContinue,
            ContentDialogResult.Secondary => UnsavedChangesDecision.DiscardAndContinue,
            _ => UnsavedChangesDecision.Cancel,
        };
    }

    public async Task<string?> PromptProjectNameAsync(string title, string defaultName)
    {
        if (Root is null)
        {
            return defaultName;
        }

        var input = new TextBox
        {
            Text = defaultName,
            SelectionStart = 0,
            SelectionLength = defaultName.Length,
            PlaceholderText = "Nome do projeto",
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Informe o nome para salvar na biblioteca local de projetos:",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(input);

        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = title,
            Content = panel,
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
        {
            return null;
        }

        return input.Text.Trim();
    }

    public async Task<ProjectMetadataPromptResult?> PromptProjectMetadataAsync(
        string title,
        ProjectMetadataPromptResult defaults)
    {
        if (Root is null)
        {
            return defaults;
        }

        var nameBox = new TextBox
        {
            Text = defaults.Name,
            SelectionStart = 0,
            SelectionLength = defaults.Name.Length,
            PlaceholderText = "Ex.: Estojo Organizador IFAC",
        };
        var descBox = new TextBox
        {
            Text = defaults.Description,
            PlaceholderText = "Descrição curta (opcional)",
        };
        var authorBox = new TextBox
        {
            Text = defaults.Author,
            PlaceholderText = "Autor do projeto",
        };
        var tagsBox = new TextBox
        {
            Text = defaults.Tags is { Count: > 0 } ? string.Join(", ", defaults.Tags) : string.Empty,
            PlaceholderText = "Tags separadas por vírgula (ex.: organizador, mdf3mm, lab)",
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "Nome do projeto (.cmaker):",
            FontSize = 12,
        });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Descrição:",
            FontSize = 12,
        });
        panel.Children.Add(descBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Autor:",
            FontSize = 12,
        });
        panel.Children.Add(authorBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Tags:",
            FontSize = 12,
        });
        panel.Children.Add(tagsBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = title,
            Content = panel,
            PrimaryButtonText = "Salvar (.cmaker)",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return null;
        }

        string[] parsedTags = (tagsBox.Text ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProjectMetadataPromptResult(
            Name: nameBox.Text.Trim(),
            Description: (descBox.Text ?? string.Empty).Trim(),
            Author: string.IsNullOrWhiteSpace(authorBox.Text) ? defaults.Author : authorBox.Text.Trim(),
            Tags: parsedTags,
            Notes: defaults.Notes);
    }

    public async Task<bool> ConfirmActionAsync(string title, string message, string confirmButtonText = "Confirmar")
    {
        if (Root is null)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = confirmButtonText,
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (Root is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }
}
