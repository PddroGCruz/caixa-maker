using System.Text.Json;
using CaixaMaker.App.Models;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;

namespace CaixaMaker.App.Services;

/// <summary>
/// Gerencia o rascunho automático de sessão em <c>%LOCALAPPDATA%\CaixaMaker\temp\autosave_draft.json</c>.
/// </summary>
public sealed class AutosaveDraftService
{
    public const string DraftFileName = "autosave_draft.json";

    public string DraftFilePath { get; }

    public AutosaveDraftService(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirs();
        DraftFilePath = Path.Combine(paths.TempDir, DraftFileName);
    }

    public AutosaveDraftService(string draftFilePath)
    {
        if (string.IsNullOrWhiteSpace(draftFilePath))
        {
            throw new ArgumentException("O caminho do arquivo de rascunho não pode ser vazio.", nameof(draftFilePath));
        }

        DraftFilePath = Path.GetFullPath(draftFilePath);
    }

    public bool HasDraft()
    {
        try
        {
            return File.Exists(DraftFilePath) && new FileInfo(DraftFilePath).Length > 2;
        }
        catch
        {
            return false;
        }
    }

    public AutosaveDraftRecord? TryLoadDraft()
    {
        try
        {
            if (!File.Exists(DraftFilePath))
            {
                return null;
            }

            string json = File.ReadAllText(DraftFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            AutosaveDraftRecord? draft = JsonSerializer.Deserialize<AutosaveDraftRecord>(
                json,
                PersonalizationJsonOptions.Default);

            if (draft is null || string.IsNullOrWhiteSpace(draft.ModelId))
            {
                return null;
            }

            return draft;
        }
        catch
        {
            return null;
        }
    }

    public void SaveDraft(AutosaveDraftRecord draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        string? directory = Path.GetDirectoryName(DraftFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = DraftFilePath + ".tmp";
        string json = JsonSerializer.Serialize(draft, PersonalizationJsonOptions.Indented);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, DraftFilePath, overwrite: true);
    }

    public void ClearDraft()
    {
        try
        {
            if (File.Exists(DraftFilePath))
            {
                File.Delete(DraftFilePath);
            }
        }
        catch
        {
            // Ignora falha transitória ao remover arquivo temporário
        }
    }
}
