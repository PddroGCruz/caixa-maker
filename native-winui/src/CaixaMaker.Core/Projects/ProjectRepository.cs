using System.Globalization;
using System.Text;
using System.Text.Json;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;

namespace CaixaMaker.Core.Projects;

/// <summary>
/// Repositório local em disco para projetos Caixa Maker (<c>{id}.json</c>)
/// com escrita atômica e validação estrita de identificador.
/// Porta integralmente <c>backend/project_store.py</c>.
/// </summary>
public sealed class ProjectRepository
{
    public string ProjectsDir { get; }

    public ProjectRepository(string projectsDir)
    {
        if (string.IsNullOrWhiteSpace(projectsDir))
        {
            throw new ArgumentException("O diretório de projetos não pode ser vazio.", nameof(projectsDir));
        }

        ProjectsDir = Path.GetFullPath(projectsDir);
        Directory.CreateDirectory(ProjectsDir);
    }

    public ProjectRepository(AppPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).ProjectsDir)
    {
    }

    public string ValidateProjectId(string? projectId)
    {
        if (!AppPaths.IsSafeProjectId(projectId))
        {
            throw new ArgumentException($"Identificador de projeto inválido: '{projectId}'.", nameof(projectId));
        }

        return projectId!;
    }

    public string ResolveProjectPath(string projectId)
    {
        string safeId = ValidateProjectId(projectId);
        return AppPaths.ResolveSafeChildPath(ProjectsDir, $"{safeId}.json");
    }

    public static ProjectSummary ToSummary(ProjectRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        PersonalizationSnapshot personalization = record.Personalization ?? new PersonalizationSnapshot();
        ValidationReport? validation = personalization.Validation;

        return new ProjectSummary
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description ?? string.Empty,
            Author = record.Author ?? string.Empty,
            Tags = record.Tags is { Count: > 0 } ? record.Tags.ToArray() : Array.Empty<string>(),
            Notes = record.Notes ?? string.Empty,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            ModelId = record.ModelId,
            ModelName = record.ModelName,
            BoxParameters = record.BoxParameters ?? new ProjectBoxParameters(),
            ViewerState = record.ViewerState ?? new ProjectViewerState(),
            ElementCount = personalization.Elements.Count,
            ValidationValid = validation?.IsValid ?? true,
            ErrorCount = validation?.ErrorCount ?? 0,
            WarningCount = validation?.WarningCount ?? 0,
        };
    }

    public IReadOnlyList<ProjectSummary> ListProjects()
    {
        Directory.CreateDirectory(ProjectsDir);
        var items = new List<ProjectSummary>();

        foreach (string path in Directory.EnumerateFiles(ProjectsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!AppPaths.IsSafeProjectId(stem))
            {
                continue;
            }

            try
            {
                string rawJson = File.ReadAllText(path, Encoding.UTF8);
                ProjectRecord? record = JsonSerializer.Deserialize<ProjectRecord>(
                    rawJson,
                    PersonalizationJsonOptions.Default);

                if (record is null || string.IsNullOrWhiteSpace(record.Id))
                {
                    continue;
                }

                items.Add(ToSummary(record));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
        }

        items.Sort((a, b) => StringComparer.Ordinal.Compare(b.UpdatedAt, a.UpdatedAt));
        return items;
    }

    public ProjectRecord SaveProject(
        string name,
        string modelId,
        string modelName,
        BoxParameters boxParameters,
        ProjectViewerState? viewerState,
        PersonalizationSnapshot personalizationSnapshot,
        string? savedId = null,
        string? description = null,
        string? author = null,
        IReadOnlyList<string>? tags = null,
        string? notes = null,
        string? createdAtOverride = null)
    {
        ArgumentNullException.ThrowIfNull(boxParameters);
        ArgumentNullException.ThrowIfNull(personalizationSnapshot);

        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            throw new ArgumentException("O nome do projeto não pode ficar em branco.", nameof(name));
        }

        string projectId = savedId is not null
            ? ValidateProjectId(savedId)
            : $"proj_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}";

        string targetPath = ResolveProjectPath(projectId);
        string now = UtcNowIso();
        string createdAt = !string.IsNullOrWhiteSpace(createdAtOverride) ? createdAtOverride : now;
        string existingDesc = description ?? string.Empty;
        string existingAuthor = author ?? string.Empty;
        List<string> existingTags = tags is not null ? tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
        string existingNotes = notes ?? string.Empty;

        if (File.Exists(targetPath))
        {
            try
            {
                string existingRaw = File.ReadAllText(targetPath, Encoding.UTF8);
                ProjectRecord? existing = JsonSerializer.Deserialize<ProjectRecord>(
                    existingRaw,
                    PersonalizationJsonOptions.Default);
                if (string.IsNullOrWhiteSpace(createdAtOverride) && !string.IsNullOrWhiteSpace(existing?.CreatedAt))
                {
                    createdAt = existing.CreatedAt;
                }

                if (description is null && existing is not null) existingDesc = existing.Description;
                if (author is null && existing is not null) existingAuthor = existing.Author;
                if (tags is null && existing is not null) existingTags = existing.Tags;
                if (notes is null && existing is not null) existingNotes = existing.Notes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                createdAt = !string.IsNullOrWhiteSpace(createdAtOverride) ? createdAtOverride : now;
            }
        }

        var record = new ProjectRecord
        {
            SchemaVersion = 1,
            Id = projectId,
            Name = cleanName,
            Description = existingDesc,
            Author = existingAuthor,
            Tags = existingTags,
            Notes = existingNotes,
            CreatedAt = createdAt,
            UpdatedAt = now,
            ModelId = modelId,
            ModelName = modelName,
            BoxParameters = ProjectBoxParameters.FromBoxParameters(boxParameters),
            ViewerState = viewerState ?? new ProjectViewerState(),
            Personalization = personalizationSnapshot,
        };

        WriteAtomicJson(targetPath, record);
        return record;
    }

    public ProjectRecord SaveFromRequest(ProjectSaveRequest request, BoxModelRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        BoxModelRegistry activeRegistry = registry ?? BoxModelRegistry.Default;
        IBoxModelDefinition boxModel = activeRegistry.Get(request.ModelId);
        PersonalizationSnapshot snapshot = BuildSnapshotForSave(request, boxModel, activeRegistry);

        return SaveProject(
            name: request.Name,
            modelId: boxModel.ModelId,
            modelName: string.IsNullOrWhiteSpace(request.ModelName) ? boxModel.DisplayName : request.ModelName,
            boxParameters: request.BoxParameters,
            viewerState: request.ViewerState,
            personalizationSnapshot: snapshot,
            savedId: request.SavedId,
            description: request.Description,
            author: request.Author,
            tags: request.Tags,
            notes: request.Notes,
            createdAtOverride: request.CreatedAtOverride);
    }

    public ProjectRecord UpdateFromRequest(string savedId, ProjectSaveRequest request, BoxModelRegistry? registry = null)
    {
        // Garante existência prévia antes de atualizar, igual ao endpoint PUT /{saved_id} da v1.2.0.
        _ = LoadProject(savedId);

        var updateRequest = new ProjectSaveRequest
        {
            Name = request.Name,
            Description = request.Description,
            Author = request.Author,
            Tags = request.Tags,
            Notes = request.Notes,
            ModelId = request.ModelId,
            ModelName = request.ModelName,
            BoxParameters = request.BoxParameters,
            ViewerState = request.ViewerState,
            Manager = request.Manager,
            PersonalizationSnapshot = request.PersonalizationSnapshot,
            SafetyMarginMm = request.SafetyMarginMm,
            SavedId = savedId,
            CreatedAtOverride = request.CreatedAtOverride,
        };

        return SaveFromRequest(updateRequest, registry);
    }

    public ProjectRecord LoadProject(string savedId)
    {
        string targetPath = ResolveProjectPath(savedId);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException($"Projeto salvo '{savedId}' não encontrado.", targetPath);
        }

        string raw = File.ReadAllText(targetPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<ProjectRecord>(raw, PersonalizationJsonOptions.Default)
            ?? throw new JsonException($"Arquivo de projeto corrompido ou inválido: '{savedId}'.");
    }

    public LoadedProjectSession OpenProject(string savedId, BoxModelRegistry? registry = null)
    {
        ProjectRecord record = LoadProject(savedId);
        BoxModelRegistry activeRegistry = registry ?? BoxModelRegistry.Default;
        IBoxModelDefinition boxModel = activeRegistry.Get(record.ModelId);

        PersonalizationSnapshot snapshot = record.Personalization ?? new PersonalizationSnapshot();
        SurfaceManager manager = SurfaceManager.FromSnapshot(snapshot, activeRegistry, boxModel);
        BoxParameters boxParams = record.BoxParameters?.ToBoxParameters()
            ?? snapshot.BoxParameters.ToBoxParameters();

        return new LoadedProjectSession
        {
            Record = record,
            Summary = ToSummary(record),
            BoxParameters = boxParams,
            ViewerState = record.ViewerState ?? new ProjectViewerState(),
            Manager = manager,
        };
    }

    public void DeleteProject(string savedId)
    {
        string targetPath = ResolveProjectPath(savedId);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException($"Projeto salvo '{savedId}' não encontrado.", targetPath);
        }

        File.Delete(targetPath);
    }

    public static PersonalizationSnapshot BuildSnapshotForSave(
        ProjectSaveRequest request,
        IBoxModelDefinition boxModel,
        BoxModelRegistry registry)
    {
        BoxCalculator calc = boxModel.CreateCalculator(request.BoxParameters);

        if (request.Manager is not null)
        {
            SurfaceManager mgr = request.Manager;
            if (!string.Equals(mgr.BoxModel.ModelId, boxModel.ModelId, StringComparison.Ordinal)
                || Math.Abs(mgr.Calc.Width - calc.Width) > 1e-6
                || Math.Abs(mgr.Calc.Depth - calc.Depth) > 1e-6
                || Math.Abs(mgr.Calc.Height - calc.Height) > 1e-6
                || Math.Abs(mgr.Calc.Thickness - calc.Thickness) > 1e-6
                || Math.Abs(mgr.Calc.Folga - calc.Folga) > 1e-6
                || Math.Abs(mgr.SafetyMarginMm - request.SafetyMarginMm) > 1e-6)
            {
                mgr.UpdateBoxCalculator(
                    calc,
                    boxModel: boxModel,
                    safetyMarginMm: request.SafetyMarginMm,
                    dropUnsupportedSurfaceElements: !string.Equals(mgr.BoxModel.ModelId, boxModel.ModelId, StringComparison.Ordinal));
            }

            return mgr.CreateSnapshot(includeRawBase64: true);
        }

        if (request.PersonalizationSnapshot is not null)
        {
            SurfaceManager mgr = SurfaceManager.FromSnapshot(request.PersonalizationSnapshot, registry, boxModel);
            mgr.UpdateBoxCalculator(calc, boxModel: boxModel, safetyMarginMm: request.SafetyMarginMm);
            return mgr.CreateSnapshot(includeRawBase64: true);
        }

        var emptyMgr = new SurfaceManager(calc, boxModel, request.SafetyMarginMm);
        return emptyMgr.CreateSnapshot(includeRawBase64: true);
    }

    private static string UtcNowIso()
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static void WriteAtomicJson(string targetPath, ProjectRecord record)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{targetPath}.tmp";
        string json = JsonSerializer.Serialize(record, PersonalizationJsonOptions.Indented);
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, targetPath, overwrite: true);
    }
}
