using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaixaMaker.Core.Storage;

public sealed class MigrationReport
{
    [JsonPropertyName("performed")]
    public bool Performed { get; set; }

    [JsonPropertyName("migrated_count")]
    public int MigratedCount { get; set; }

    [JsonPropertyName("skipped_existing_count")]
    public int SkippedExistingCount { get; set; }

    [JsonPropertyName("migrated_ids")]
    public List<string> MigratedIds { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = [];

    [JsonPropertyName("sources_checked")]
    public List<string> SourcesChecked { get; set; } = [];
}

/// <summary>
/// Migra projetos JSON legados para %APPDATA%\CaixaMaker\projects de maneira não-destrutiva,
/// com cópia atômica via arquivo .json.migrating, preservação integral dos originais
/// e registro de estado em config/legacy_migration.json.
/// </summary>
public static class LegacyProjectMigrator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] RequiredFields =
    [
        "id",
        "name",
        "model_id",
        "box_parameters",
    ];

    public static MigrationReport MigrateLegacyProjects(
        string targetProjectsDir,
        IEnumerable<string> legacyDirs,
        string? markerFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectsDir);
        ArgumentNullException.ThrowIfNull(legacyDirs);

        MigrationReport report = new();
        string resolvedTargetDir = Path.GetFullPath(targetProjectsDir);
        Directory.CreateDirectory(resolvedTargetDir);

        foreach (string rawLegacyDir in legacyDirs)
        {
            if (string.IsNullOrWhiteSpace(rawLegacyDir))
            {
                continue;
            }

            string legacyPath = Path.GetFullPath(rawLegacyDir);
            if (!Directory.Exists(legacyPath))
            {
                continue;
            }

            report.SourcesChecked.Add(legacyPath);
            report.Performed = true;

            IEnumerable<string> jsonFiles;
            try
            {
                jsonFiles = Directory
                    .EnumerateFiles(legacyPath, "*.json")
                    .Where(path => string.Equals(
                        Path.GetExtension(path),
                        ".json",
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Errors.Add($"Não foi possível listar '{legacyPath}': {ex.Message}");
                continue;
            }

            foreach (string jsonFile in jsonFiles)
            {
                string fileName = Path.GetFileName(jsonFile);
                string dest = Path.Combine(resolvedTargetDir, fileName);

                if (File.Exists(dest))
                {
                    report.SkippedExistingCount++;
                    continue;
                }

                string tmpDest = $"{dest}.migrating";
                try
                {
                    string rawText = File.ReadAllText(jsonFile, Utf8NoBom);
                    if (!TryValidateLegacyProjectPayload(rawText, out string? parsedId))
                    {
                        report.Errors.Add(
                            $"Arquivo legado '{fileName}' nao possui esquema de projeto valido.");
                        continue;
                    }

                    File.Copy(jsonFile, tmpDest, overwrite: true);

                    string copiedText = File.ReadAllText(tmpDest, Utf8NoBom);
                    if (!TryValidateLegacyProjectPayload(copiedText, out _))
                    {
                        TryDeleteFile(tmpDest);
                        report.Errors.Add(
                            $"Falha na verificacao pos-copia de '{fileName}'.");
                        continue;
                    }

                    File.Move(tmpDest, dest, overwrite: true);
                    report.MigratedCount++;
                    report.MigratedIds.Add(
                        string.IsNullOrWhiteSpace(parsedId)
                            ? Path.GetFileNameWithoutExtension(jsonFile)
                            : parsedId);
                }
                catch (Exception ex)
                {
                    TryDeleteFile(tmpDest);
                    report.Errors.Add($"Nao foi possivel migrar '{fileName}': {ex.Message}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(markerFile) && report.Performed)
        {
            TryWriteMarker(markerFile, report);
        }

        return report;
    }

    public static bool TryValidateLegacyProjectPayload(string jsonText, out string? projectId)
    {
        projectId = null;
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (string field in RequiredFields)
            {
                if (!doc.RootElement.TryGetProperty(field, out JsonElement prop))
                {
                    return false;
                }

                if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return false;
                }

                if (prop.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(prop.GetString()))
                {
                    return false;
                }
            }

            if (doc.RootElement.TryGetProperty("id", out JsonElement idProp))
            {
                projectId = idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : idProp.ToString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void TryWriteMarker(string markerFile, MigrationReport report)
    {
        try
        {
            string fullMarkerPath = Path.GetFullPath(markerFile);
            string? markerDir = Path.GetDirectoryName(fullMarkerPath);
            if (!string.IsNullOrWhiteSpace(markerDir))
            {
                Directory.CreateDirectory(markerDir);
            }

            string json = JsonSerializer.Serialize(report, SerializerOptions);
            string tempMarkerPath = $"{fullMarkerPath}.tmp";
            File.WriteAllText(tempMarkerPath, json, Utf8NoBom);
            File.Move(tempMarkerPath, fullMarkerPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Falhas ao gravar o marcador não devem impedir a inicialização do aplicativo.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignora falha na limpeza de arquivo temporário de migração.
        }
    }
}
