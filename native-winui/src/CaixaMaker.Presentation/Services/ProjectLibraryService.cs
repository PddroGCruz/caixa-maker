using System.Globalization;
using System.Text;
using CaixaMaker.App.Models;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;

namespace CaixaMaker.App.Services;

/// <summary>
/// Serviço oficial da Biblioteca Local de Projetos baseada no formato nativo <c>.cmaker</c>,
/// com índice em cache de miniaturas e metadados, busca/filtragem, duplicação e migração de legados.
/// </summary>
public sealed class ProjectLibraryService
{
    private sealed record CachedSummaryEntry(
        long LastWriteUtcTicks,
        long Length,
        ProjectSummary Summary);

    private readonly string _projectsDir;
    private readonly BoxModelRegistry _registry;
    private readonly CmakerPackageService _packageService;
    private readonly CmakerMigrationService _migrationService;
    private readonly ProjectRepository _legacyRepository;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedSummaryEntry> _summaryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public string ProjectsDir => _projectsDir;
    public CmakerPackageService PackageService => _packageService;
    public CmakerMigrationService MigrationService => _migrationService;

    public ProjectLibraryService(
        AppPaths paths,
        BoxModelRegistry? registry = null,
        CmakerPackageService? packageService = null,
        CmakerMigrationService? migrationService = null)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).ProjectsDir, registry, packageService, migrationService)
    {
    }

    public ProjectLibraryService(
        string projectsDir,
        BoxModelRegistry? registry = null,
        CmakerPackageService? packageService = null,
        CmakerMigrationService? migrationService = null)
    {
        if (string.IsNullOrWhiteSpace(projectsDir))
        {
            throw new ArgumentException("O diretório da biblioteca de projetos não pode ficar vazio.", nameof(projectsDir));
        }

        _projectsDir = Path.GetFullPath(projectsDir);
        Directory.CreateDirectory(_projectsDir);
        _registry = registry ?? BoxModelRegistry.Default;
        _packageService = packageService ?? new CmakerPackageService(_registry);
        _migrationService = migrationService ?? new CmakerMigrationService(_registry, _packageService);
        _legacyRepository = new ProjectRepository(_projectsDir);
    }

    /// <summary>Conta quantos arquivos <c>.json</c> legados ainda aguardam conversão para <c>.cmaker</c>.</summary>
    public int CountPendingLegacyJsonProjects()
        => CmakerMigrationService.CountPendingLegacyJsonProjects(_projectsDir);

    /// <summary>Executa a migração em lote dos projetos <c>.json</c> legados locais para <c>.cmaker</c>.</summary>
    public LegacyCmakerMigrationReport MigrateLegacyProjects(bool archiveConvertedJson = true)
    {
        LegacyCmakerMigrationReport report = _migrationService.MigrateDirectoryLegacyProjects(_projectsDir, archiveConvertedJson);
        InvalidateCache();
        return report;
    }

    /// <summary>Lista e filtra os projetos da biblioteca local ordenados pelo <c>UpdatedAt</c> mais recente.</summary>
    public IReadOnlyList<ProjectSummary> ListProjects(string? searchQuery = null)
    {
        Directory.CreateDirectory(_projectsDir);
        var byId = new Dictionary<string, ProjectSummary>(StringComparer.OrdinalIgnoreCase);
        var activeCmakerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string cmakerPath in Directory.EnumerateFiles(
            _projectsDir,
            $"*{CmakerPackageService.FileExtension}",
            SearchOption.TopDirectoryOnly))
        {
            string fullPath = Path.GetFullPath(cmakerPath);
            activeCmakerPaths.Add(fullPath);

            try
            {
                ProjectSummary summary = ReadCachedSummary(fullPath);
                byId[summary.Id] = summary;
            }
            catch
            {
                // Ignora arquivo temporário ou inválido sem derrubar a listagem.
            }
        }

        PruneStaleCacheEntries(activeCmakerPaths);

        // Fallback não-destrutivo: exibe projetos .json legados que ainda não foram convertidos para .cmaker
        foreach (ProjectSummary legacySummary in _legacyRepository.ListProjects())
        {
            if (byId.ContainsKey(legacySummary.Id))
            {
                continue;
            }

            string legacyPath = _legacyRepository.ResolveProjectPath(legacySummary.Id);
            long sizeBytes = File.Exists(legacyPath) ? new FileInfo(legacyPath).Length : 0L;
            byId[legacySummary.Id] = new ProjectSummary
            {
                Id = legacySummary.Id,
                Name = legacySummary.Name,
                Description = legacySummary.Description,
                Author = legacySummary.Author,
                Tags = legacySummary.Tags,
                Notes = legacySummary.Notes,
                CreatedAt = legacySummary.CreatedAt,
                UpdatedAt = legacySummary.UpdatedAt,
                ModelId = legacySummary.ModelId,
                ModelName = legacySummary.ModelName,
                BoxParameters = legacySummary.BoxParameters,
                ViewerState = legacySummary.ViewerState,
                ElementCount = legacySummary.ElementCount,
                AssetCount = 0,
                TotalCutPerimeterMeters = legacySummary.TotalCutPerimeterMeters,
                ValidationValid = legacySummary.ValidationValid,
                ErrorCount = legacySummary.ErrorCount,
                WarningCount = legacySummary.WarningCount,
                PackageFormat = "legacy_json",
                PackageSizeBytes = sizeBytes,
                PackageFilePath = legacyPath,
            };
        }

        List<ProjectSummary> results = string.IsNullOrWhiteSpace(searchQuery)
            ? byId.Values.ToList()
            : byId.Values.Where(p => MatchesSearchQuery(p, searchQuery!)).ToList();

        results.Sort((a, b) => StringComparer.Ordinal.Compare(b.UpdatedAt, a.UpdatedAt));
        return results;
    }

    /// <summary>Cria ou salva um projeto na biblioteca local no formato nativo <c>.cmaker</c>.</summary>
    public CmakerPackageBuildResult SaveFromRequest(
        ProjectSaveRequest request,
        CmakerManufacturingState? manufacturingState = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(_projectsDir);

        string targetId = !string.IsNullOrWhiteSpace(request.SavedId)
            ? CmakerPackageService.ValidateProjectId(request.SavedId)
            : GenerateNewProjectId();

        string? existingPath = ResolveCmakerPathById(targetId);
        string destinationPath = existingPath ?? Path.Combine(_projectsDir, $"{targetId}{CmakerPackageService.FileExtension}");

        ProjectSaveRequest effectiveRequest = MergeExistingMetadata(request, existingPath);
        CmakerPackageBuildResult saved = _packageService.SaveToFile(
            effectiveRequest,
            destinationPath,
            AppVersion.Version,
            manufacturingState);

        InvalidateCache(destinationPath);
        return saved;
    }

    /// <summary>Atualiza um projeto existente na biblioteca local preservando <c>created_at</c> e metadados não sobrescritos.</summary>
    public CmakerPackageBuildResult UpdateFromRequest(
        string projectId,
        ProjectSaveRequest request,
        CmakerManufacturingState? manufacturingState = null)
    {
        string safeId = ValidateOrResolveId(projectId);
        var updatedRequest = new ProjectSaveRequest
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
            SavedId = safeId,
            CreatedAtOverride = request.CreatedAtOverride,
        };

        return SaveFromRequest(updatedRequest, manufacturingState);
    }

    /// <summary>Abre um projeto da biblioteca local (<c>.cmaker</c> preferencial ou fallback <c>.json</c>).</summary>
    public LoadedProjectSession OpenProject(string projectId)
    {
        string? cmakerPath = ResolveCmakerPathById(projectId);
        if (cmakerPath is not null && File.Exists(cmakerPath))
        {
            return _packageService.LoadPackage(cmakerPath);
        }

        string legacyPath = _legacyRepository.ResolveProjectPath(projectId);
        if (File.Exists(legacyPath))
        {
            return _migrationService.LoadAnyProjectFile(legacyPath);
        }

        throw new FileNotFoundException($"Projeto salvo '{projectId}' não encontrado na biblioteca local.", cmakerPath ?? legacyPath);
    }

    /// <summary>Duplica um projeto da biblioteca gerando novo ID único e preservando todos os ativos incorporados.</summary>
    public LoadedProjectSession DuplicateProject(string projectId, string? newName = null)
    {
        LoadedProjectSession source = OpenProject(projectId);
        string copyName = !string.IsNullOrWhiteSpace(newName)
            ? newName.Trim()
            : $"{source.Record.Name} (Cópia)";

        string newId = GenerateNewProjectId();
        var request = new ProjectSaveRequest
        {
            Name = copyName,
            Description = source.Record.Description,
            Author = string.IsNullOrWhiteSpace(source.Record.Author) ? AppVersion.DeveloperName : source.Record.Author,
            Tags = source.Record.Tags,
            Notes = source.Record.Notes,
            ModelId = source.Record.ModelId,
            ModelName = source.Record.ModelName,
            BoxParameters = source.BoxParameters,
            ViewerState = source.ViewerState,
            Manager = source.Manager,
            SafetyMarginMm = source.Manager.SafetyMarginMm,
            SavedId = newId,
        };

        CmakerPackageBuildResult saved = SaveFromRequest(request);
        return _packageService.LoadPackage(saved.Summary.PackageFilePath!);
    }

    /// <summary>
    /// Importa um arquivo externo (<c>.cmaker</c>, <c>.caixamaker.json</c> ou <c>.json</c>),
    /// registrando uma cópia editável na biblioteca local se <paramref name="saveToLibrary"/> for <see langword="true"/>.
    /// </summary>
    public LoadedProjectSession ImportExternalPackage(string externalFilePath, bool saveToLibrary = true)
    {
        LoadedProjectSession loaded = _migrationService.LoadAnyProjectFile(externalFilePath);
        if (!saveToLibrary)
        {
            return loaded;
        }

        string? requestedId = AppPaths.IsSafeProjectId(loaded.Record.Id) ? loaded.Record.Id : null;
        bool idAlreadyInLibrary = requestedId is not null && ResolveCmakerPathById(requestedId) is not null;
        string targetId = idAlreadyInLibrary || requestedId is null
            ? GenerateNewProjectId()
            : requestedId;

        var request = new ProjectSaveRequest
        {
            Name = loaded.Record.Name,
            Description = loaded.Record.Description,
            Author = string.IsNullOrWhiteSpace(loaded.Record.Author) ? AppVersion.DeveloperName : loaded.Record.Author,
            Tags = loaded.Record.Tags,
            Notes = loaded.Record.Notes,
            ModelId = loaded.Record.ModelId,
            ModelName = loaded.Record.ModelName,
            BoxParameters = loaded.BoxParameters,
            ViewerState = loaded.ViewerState,
            Manager = loaded.Manager,
            SafetyMarginMm = loaded.Manager.SafetyMarginMm,
            SavedId = targetId,
            CreatedAtOverride = loaded.Record.CreatedAt,
        };

        CmakerPackageBuildResult stored = SaveFromRequest(request);
        LoadedProjectSession reopened = _packageService.LoadPackage(stored.Summary.PackageFilePath!);
        if (!string.IsNullOrWhiteSpace(loaded.MigrationNotice))
        {
            return new LoadedProjectSession
            {
                Record = reopened.Record,
                Summary = reopened.Summary,
                BoxParameters = reopened.BoxParameters,
                ViewerState = reopened.ViewerState,
                Manager = reopened.Manager,
                SourceFormat = loaded.SourceFormat,
                PackageSchemaVersion = reopened.PackageSchemaVersion,
                PackageFilePath = reopened.PackageFilePath,
                MigrationNotice = loaded.MigrationNotice,
                AssetCount = reopened.AssetCount,
                PackageSizeBytes = reopened.PackageSizeBytes,
            };
        }

        return reopened;
    }

    /// <summary>Exporta qualquer projeto da biblioteca local para um caminho <c>.cmaker</c> portátil.</summary>
    public void ExportProjectPackage(string projectId, string destinationCmakerPath)
    {
        if (string.IsNullOrWhiteSpace(destinationCmakerPath))
        {
            throw new ArgumentException("O caminho de destino não pode ficar vazio.", nameof(destinationCmakerPath));
        }

        string? cmakerPath = ResolveCmakerPathById(projectId);
        if (cmakerPath is not null && File.Exists(cmakerPath))
        {
            byte[] bytes = File.ReadAllBytes(cmakerPath);
            CmakerPackageService.WriteBytesAtomic(destinationCmakerPath, bytes);
            return;
        }

        string legacyPath = _legacyRepository.ResolveProjectPath(projectId);
        if (File.Exists(legacyPath))
        {
            _ = _migrationService.MigrateLegacyFileToCmaker(legacyPath, destinationCmakerPath);
            return;
        }

        throw new FileNotFoundException($"Projeto salvo '{projectId}' não encontrado para exportação.", cmakerPath ?? legacyPath);
    }

    /// <summary>Exclui permanentemente um projeto (<c>.cmaker</c> e eventual arquivo legado correspondente).</summary>
    public void DeleteProject(string projectId)
    {
        bool deletedAny = false;

        string? cmakerPath = ResolveCmakerPathById(projectId);
        if (cmakerPath is not null && File.Exists(cmakerPath))
        {
            File.Delete(cmakerPath);
            InvalidateCache(cmakerPath);
            deletedAny = true;
        }

        if (AppPaths.IsSafeProjectId(projectId))
        {
            string legacyPath = _legacyRepository.ResolveProjectPath(projectId);
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
                deletedAny = true;
            }
        }

        if (!deletedAny)
        {
            throw new FileNotFoundException($"Projeto salvo '{projectId}' não encontrado para exclusão.", cmakerPath);
        }
    }

    public static string GenerateNewProjectId()
        => $"proj_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}";

    private ProjectSaveRequest MergeExistingMetadata(ProjectSaveRequest request, string? existingCmakerPath)
    {
        if (existingCmakerPath is null || !File.Exists(existingCmakerPath))
        {
            return request;
        }

        try
        {
            ProjectSummary existing = _packageService.ReadSummary(existingCmakerPath);
            return new ProjectSaveRequest
            {
                Name = request.Name,
                Description = request.Description ?? existing.Description,
                Author = request.Author ?? existing.Author,
                Tags = request.Tags ?? existing.Tags,
                Notes = request.Notes ?? existing.Notes,
                ModelId = request.ModelId,
                ModelName = request.ModelName,
                BoxParameters = request.BoxParameters,
                ViewerState = request.ViewerState,
                Manager = request.Manager,
                PersonalizationSnapshot = request.PersonalizationSnapshot,
                SafetyMarginMm = request.SafetyMarginMm,
                SavedId = existing.Id,
                CreatedAtOverride = !string.IsNullOrWhiteSpace(request.CreatedAtOverride)
                    ? request.CreatedAtOverride
                    : existing.CreatedAt,
            };
        }
        catch
        {
            return request;
        }
    }

    private ProjectSummary ReadCachedSummary(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        long ticks = fi.LastWriteTimeUtc.Ticks;
        long len = fi.Length;

        lock (_cacheLock)
        {
            if (_summaryCache.TryGetValue(fullPath, out CachedSummaryEntry? cached) &&
                cached.LastWriteUtcTicks == ticks &&
                cached.Length == len)
            {
                return cached.Summary;
            }
        }

        ProjectSummary parsed = _packageService.ReadSummary(fullPath);
        lock (_cacheLock)
        {
            _summaryCache[fullPath] = new CachedSummaryEntry(ticks, len, parsed);
        }

        return parsed;
    }

    private void PruneStaleCacheEntries(HashSet<string> activePaths)
    {
        lock (_cacheLock)
        {
            List<string> stale = _summaryCache.Keys
                .Where(k => !activePaths.Contains(k))
                .ToList();
            foreach (string key in stale)
            {
                _summaryCache.Remove(key);
            }
        }
    }

    private void InvalidateCache(string? specificPath = null)
    {
        lock (_cacheLock)
        {
            if (string.IsNullOrWhiteSpace(specificPath))
            {
                _summaryCache.Clear();
            }
            else
            {
                _summaryCache.Remove(Path.GetFullPath(specificPath));
            }
        }
    }

    private string? ResolveCmakerPathById(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        if (AppPaths.IsSafeProjectId(projectId))
        {
            string direct = Path.Combine(_projectsDir, $"{projectId}{CmakerPackageService.FileExtension}");
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        if (!Directory.Exists(_projectsDir))
        {
            return null;
        }

        foreach (string path in Directory.EnumerateFiles(
            _projectsDir,
            $"*{CmakerPackageService.FileExtension}",
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                ProjectSummary summary = ReadCachedSummary(Path.GetFullPath(path));
                if (string.Equals(summary.Id, projectId, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(path);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private string ValidateOrResolveId(string projectId)
    {
        if (AppPaths.IsSafeProjectId(projectId))
        {
            return projectId;
        }

        if (ResolveCmakerPathById(projectId) is not null)
        {
            return projectId;
        }

        return CmakerPackageService.ValidateProjectId(projectId);
    }

    private static bool MatchesSearchQuery(ProjectSummary summary, string query)
    {
        string needle = query.Trim();
        if (needle.Length == 0)
        {
            return true;
        }

        return summary.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || summary.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || summary.Author.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || summary.ModelName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || summary.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
