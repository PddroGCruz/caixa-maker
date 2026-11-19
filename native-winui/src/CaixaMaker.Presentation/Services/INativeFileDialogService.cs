using CaixaMaker.App.Models;

namespace CaixaMaker.App.Services;

/// <summary>Dados retornados pelo diálogo de nome e metadados ao salvar um projeto <c>.cmaker</c>.</summary>
public sealed record ProjectMetadataPromptResult(
    string Name,
    string Description = "",
    string Author = "",
    IReadOnlyList<string>? Tags = null,
    string Notes = "");

/// <summary>
/// Contrato desacoplado de UI para diálogos nativos de abertura/gravação de arquivos e confirmações.
/// </summary>
public interface INativeFileDialogService
{
    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<NativeFileFilter> filters);

    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, IReadOnlyList<NativeFileFilter> filters);

    Task<UnsavedChangesDecision> PromptUnsavedChangesAsync(string projectName);

    Task<string?> PromptProjectNameAsync(string title, string defaultName);

    Task<ProjectMetadataPromptResult?> PromptProjectMetadataAsync(string title, ProjectMetadataPromptResult defaults);

    Task<bool> ConfirmActionAsync(string title, string message, string confirmButtonText = "Confirmar");

    Task ShowMessageAsync(string title, string message);
}

/// <summary>Implementação neutra para execução headless e testes automatizados sem janela WinUI.</summary>
public sealed class NullNativeFileDialogService : INativeFileDialogService
{
    public Func<string, IReadOnlyList<NativeFileFilter>, string?>? OnPickOpenFile { get; set; }
    public Func<string, string, IReadOnlyList<NativeFileFilter>, string?>? OnPickSaveFile { get; set; }
    public Func<string, UnsavedChangesDecision>? OnPromptUnsavedChanges { get; set; }
    public Func<string, string, string?>? OnPromptProjectName { get; set; }
    public Func<string, ProjectMetadataPromptResult, ProjectMetadataPromptResult?>? OnPromptProjectMetadata { get; set; }
    public Func<string, string, bool>? OnConfirmAction { get; set; }
    public Action<string, string>? OnShowMessage { get; set; }

    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<NativeFileFilter> filters)
        => Task.FromResult(OnPickOpenFile?.Invoke(title, filters));

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName, IReadOnlyList<NativeFileFilter> filters)
        => Task.FromResult(OnPickSaveFile?.Invoke(title, suggestedFileName, filters));

    public Task<UnsavedChangesDecision> PromptUnsavedChangesAsync(string projectName)
        => Task.FromResult(OnPromptUnsavedChanges?.Invoke(projectName) ?? UnsavedChangesDecision.DiscardAndContinue);

    public Task<string?> PromptProjectNameAsync(string title, string defaultName)
        => Task.FromResult(OnPromptProjectName is not null ? OnPromptProjectName(title, defaultName) : defaultName);

    public Task<ProjectMetadataPromptResult?> PromptProjectMetadataAsync(string title, ProjectMetadataPromptResult defaults)
    {
        if (OnPromptProjectMetadata is not null)
        {
            return Task.FromResult(OnPromptProjectMetadata(title, defaults));
        }

        if (OnPromptProjectName is not null)
        {
            string? chosenName = OnPromptProjectName(title, defaults.Name);
            return Task.FromResult(string.IsNullOrWhiteSpace(chosenName) ? null : defaults with { Name = chosenName });
        }

        return Task.FromResult<ProjectMetadataPromptResult?>(defaults);
    }

    public Task<bool> ConfirmActionAsync(string title, string message, string confirmButtonText = "Confirmar")
        => Task.FromResult(OnConfirmAction?.Invoke(title, message) ?? true);

    public Task ShowMessageAsync(string title, string message)
    {
        OnShowMessage?.Invoke(title, message);
        return Task.CompletedTask;
    }
}
