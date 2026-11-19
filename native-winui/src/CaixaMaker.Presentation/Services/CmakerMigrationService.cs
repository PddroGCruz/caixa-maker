using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CaixaMaker.App.Models;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;

namespace CaixaMaker.App.Services;

/// <summary>
/// Serviço de migração retrocompatível e leitura tolerante entre projetos legados
/// (<c>.json</c> / <c>.caixamaker.json</c> v1.x) e contêineres <c>.cmaker</c> v2.0+.
/// </summary>
public sealed class CmakerMigrationService
{
    private readonly BoxModelRegistry _registry;
    private readonly CmakerPackageService _packageService;
    private readonly PortablePackageService _legacyPackageService;

    public CmakerMigrationService(
        BoxModelRegistry? registry = null,
        CmakerPackageService? packageService = null)
    {
        _registry = registry ?? BoxModelRegistry.Default;
        _packageService = packageService ?? new CmakerPackageService(_registry);
        _legacyPackageService = new PortablePackageService(_registry);
    }

    /// <summary>Abre qualquer arquivo de projeto suportado (<c>.cmaker</c>, <c>.caixamaker.json</c>, <c>.json</c>).</summary>
    public LoadedProjectSession LoadAnyProjectFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo de projeto não encontrado.", filePath);
        }

        string fullPath = Path.GetFullPath(filePath);
        if (CmakerPackageService.LooksLikeZipArchive(fullPath) ||
            string.Equals(Path.GetExtension(fullPath), CmakerPackageService.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return _packageService.LoadPackage(fullPath);
        }

        string rawJson = File.ReadAllText(fullPath, Encoding.UTF8);
        return LoadLegacyJsonString(rawJson, Path.GetFileName(fullPath), fullPath);
    }

    /// <summary>Lê um JSON legado (<c>ProjectRecord</c> de biblioteca ou <c>PortableProjectBundle</c>).</summary>
    public LoadedProjectSession LoadLegacyJsonString(
        string rawJson,
        string? sourceFilename = null,
        string? sourceFullPath = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            throw new InvalidDataException("O arquivo JSON do projeto está vazio.");
        }

        using var doc = JsonDocument.Parse(rawJson);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("O conteúdo JSON do projeto deve ser um objeto raiz.");
        }

        // Caso 1: Projeto local salvo pelo ProjectRepository v1 (tem "id" e "personalization", sem wrapper de bundle)
        if (!root.TryGetProperty("bundle", out _) &&
            !root.TryGetProperty("format", out _) &&
            root.TryGetProperty("id", out _) &&
            root.TryGetProperty("personalization", out _))
        {
            ProjectRecord record = JsonSerializer.Deserialize<ProjectRecord>(rawJson, PersonalizationJsonOptions.Default)
                ?? throw new InvalidDataException("Não foi possível interpretar o projeto .json legado.");

            string targetModelId = _registry.Contains(record.ModelId) ? record.ModelId : BoxWithLidAndHandlesModel.Id;
            IBoxModelDefinition model = _registry.Get(targetModelId);
            BoxParameters boxParams = record.BoxParameters?.ToBoxParameters()
                ?? record.Personalization?.BoxParameters.ToBoxParameters()
                ?? BoxParameters.Default;

            PersonalizationSnapshot snapshot = record.Personalization ?? new PersonalizationSnapshot();
            SurfaceManager manager = SurfaceManager.FromSnapshot(snapshot, _registry, model);
            HydrateLegacyRawBytes(manager);

            BoxCalculator calc = model.CreateCalculator(boxParams);
            BoxDrawer drawer = model.CreateDrawer(calc);
            BoxCalculationSummary boxSummary = model.BuildSummary(calc, drawer);
            ValidationReport validation = manager.Validate();

            long byteSize = Encoding.UTF8.GetByteCount(rawJson);
            ProjectSummary summary = ProjectRepository.ToSummary(record);
            ProjectSummary enriched = new()
            {
                Id = summary.Id,
                Name = summary.Name,
                Description = summary.Description,
                Author = summary.Author,
                Tags = summary.Tags,
                Notes = summary.Notes,
                CreatedAt = summary.CreatedAt,
                UpdatedAt = summary.UpdatedAt,
                ModelId = summary.ModelId,
                ModelName = string.IsNullOrWhiteSpace(summary.ModelName) ? model.DisplayName : summary.ModelName,
                BoxParameters = summary.BoxParameters,
                ViewerState = summary.ViewerState,
                ElementCount = manager.ElementCount,
                TotalCutPerimeterMeters = PythonMath.Round(boxSummary.TotalCutPerimeterMeters, 4),
                ValidationValid = validation.IsValid,
                ErrorCount = validation.ErrorCount,
                WarningCount = validation.WarningCount,
                PackageFormat = "legacy_json",
                PackageSizeBytes = byteSize,
                PackageFilePath = sourceFullPath,
            };

            return new LoadedProjectSession
            {
                Record = record,
                Summary = enriched,
                BoxParameters = boxParams,
                ViewerState = record.ViewerState ?? new ProjectViewerState(),
                Manager = manager,
                SourceFormat = "legacy_json",
                PackageFilePath = sourceFullPath,
                PackageSizeBytes = byteSize,
                MigrationNotice = "Projeto legado .json carregado. Salve para converter para o formato autocontido .cmaker.",
            };
        }

        // Caso 2: Pacote portátil .caixamaker.json (com ou sem envelope "bundle")
        LoadedProjectSession imported = _legacyPackageService.ImportBundleJson(rawJson, repository: null, sourceFilename: sourceFilename);
        HydrateLegacyRawBytes(imported.Manager);
        return new LoadedProjectSession
        {
            Record = imported.Record,
            Summary = imported.Summary,
            BoxParameters = imported.BoxParameters,
            ViewerState = imported.ViewerState,
            Manager = imported.Manager,
            SourceFormat = "legacy_caixamaker_json",
            PackageFilePath = sourceFullPath,
            PackageSizeBytes = Encoding.UTF8.GetByteCount(rawJson),
            MigrationNotice = "Pacote legado .caixamaker.json importado. Salve como .cmaker para incorporar todos os ativos e prévia 3D.",
        };
    }

    /// <summary>Converte um arquivo legado (<c>.json</c> / <c>.caixamaker.json</c>) diretamente em um contêiner <c>.cmaker</c>.</summary>
    public CmakerPackageBuildResult MigrateLegacyFileToCmaker(
        string legacyFilePath,
        string destinationCmakerPath,
        string? defaultAuthor = null)
    {
        LoadedProjectSession loaded = LoadAnyProjectFile(legacyFilePath);
        string resolvedAuthor = !string.IsNullOrWhiteSpace(loaded.Record.Author)
            ? loaded.Record.Author
            : (defaultAuthor ?? AppVersion.DeveloperName);

        var request = new ProjectSaveRequest
        {
            Name = loaded.Record.Name,
            Description = loaded.Record.Description,
            Author = resolvedAuthor,
            Tags = loaded.Record.Tags,
            Notes = loaded.Record.Notes,
            ModelId = loaded.Record.ModelId,
            ModelName = loaded.Record.ModelName,
            BoxParameters = loaded.BoxParameters,
            ViewerState = loaded.ViewerState,
            Manager = loaded.Manager,
            SafetyMarginMm = loaded.Manager.SafetyMarginMm,
            SavedId = AppPaths.IsSafeProjectId(loaded.Record.Id) ? loaded.Record.Id : null,
            CreatedAtOverride = loaded.Record.CreatedAt,
        };

        return _packageService.SaveToFile(request, destinationCmakerPath, AppVersion.Version);
    }

    /// <summary>
    /// Migra em lote arquivos <c>*.json</c> de projeto encontrados no diretório da biblioteca para <c>.cmaker</c>,
    /// preservando originais em subpasta <c>_legacy_backup/</c>.
    /// </summary>
    public LegacyCmakerMigrationReport MigrateDirectoryLegacyProjects(
        string projectsDir,
        bool archiveConvertedJson = true)
    {
        if (string.IsNullOrWhiteSpace(projectsDir) || !Directory.Exists(projectsDir))
        {
            return new LegacyCmakerMigrationReport();
        }

        int migrated = 0;
        int skipped = 0;
        var migratedFiles = new List<string>();
        var errors = new List<string>();
        string backupDir = Path.Combine(projectsDir, "_legacy_backup");

        foreach (string jsonPath in Directory.EnumerateFiles(projectsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            string stem = Path.GetFileNameWithoutExtension(jsonPath);
            if (!LooksLikeLegacyProjectStem(stem, jsonPath))
            {
                continue;
            }

            try
            {
                LoadedProjectSession session = LoadAnyProjectFile(jsonPath);
                string targetId = AppPaths.IsSafeProjectId(session.Record.Id)
                    ? session.Record.Id
                    : AppPaths.IsSafeProjectId(stem)
                        ? stem
                        : $"proj_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}";

                string targetCmakerPath = Path.Combine(projectsDir, $"{targetId}{CmakerPackageService.FileExtension}");
                if (File.Exists(targetCmakerPath))
                {
                    skipped++;
                    if (archiveConvertedJson)
                    {
                        ArchiveLegacyJsonFile(jsonPath, backupDir);
                    }
                    continue;
                }

                MigrateLegacyFileToCmaker(jsonPath, targetCmakerPath);
                if (archiveConvertedJson)
                {
                    ArchiveLegacyJsonFile(jsonPath, backupDir);
                }

                migrated++;
                migratedFiles.Add(Path.GetFileName(targetCmakerPath));
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(jsonPath)}: {ex.Message}");
            }
        }

        return new LegacyCmakerMigrationReport
        {
            MigratedCount = migrated,
            SkippedExistingCount = skipped,
            MigratedFiles = migratedFiles,
            Errors = errors,
        };
    }

    /// <summary>Retorna a quantidade de projetos <c>.json</c> legados pendentes de migração em <paramref name="projectsDir"/>.</summary>
    public static int CountPendingLegacyJsonProjects(string projectsDir)
    {
        if (string.IsNullOrWhiteSpace(projectsDir) || !Directory.Exists(projectsDir))
        {
            return 0;
        }

        int count = 0;
        foreach (string jsonPath in Directory.EnumerateFiles(projectsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            string stem = Path.GetFileNameWithoutExtension(jsonPath);
            if (!LooksLikeLegacyProjectStem(stem, jsonPath))
            {
                continue;
            }

            string counterpart = Path.Combine(projectsDir, $"{stem}{CmakerPackageService.FileExtension}");
            if (!File.Exists(counterpart))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Atualiza deterministicamente um pacote <c>.cmaker</c> v1.x (ZIP em memória) para o esquema v2.0.0.</summary>
    public byte[] MigrateV1BytesToV2Bytes(byte[] v1PackageBytes)
    {
        ArgumentNullException.ThrowIfNull(v1PackageBytes);
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using (var ms = new MemoryStream(v1PackageBytes, writable: false))
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                string path = CmakerPackageService.NormalizeAndValidateZipEntryPath(entry.FullName);
                using Stream es = entry.Open();
                using var copyMs = new MemoryStream();
                es.CopyTo(copyMs);
                entries[path] = copyMs.ToArray();
            }
        }

        if (!entries.TryGetValue(CmakerPackageService.ManifestEntryPath, out byte[]? mBytes))
        {
            throw new InvalidDataException("Manifesto v1 ausente no arquivo .cmaker.");
        }

        CmakerManifest v1Manifest = CmakerPackageService.DeserializeJsonUtf8<CmakerManifest>(mBytes, PersonalizationJsonOptions.Default)
            ?? throw new InvalidDataException("Manifesto v1 inválido.");

        LoadedProjectSession session = MigrateV1ArchiveToSession(v1Manifest, entries, _registry, v1PackageBytes.LongLength);
        var request = new ProjectSaveRequest
        {
            Name = session.Record.Name,
            Description = session.Record.Description,
            Author = session.Record.Author,
            Tags = session.Record.Tags,
            Notes = session.Record.Notes,
            ModelId = session.Record.ModelId,
            ModelName = session.Record.ModelName,
            BoxParameters = session.BoxParameters,
            ViewerState = session.ViewerState,
            Manager = session.Manager,
            SafetyMarginMm = session.Manager.SafetyMarginMm,
            SavedId = AppPaths.IsSafeProjectId(session.Record.Id) ? session.Record.Id : null,
            CreatedAtOverride = session.Record.CreatedAt,
        };

        return _packageService.BuildPackage(request, AppVersion.Version).PackageBytes;
    }

    internal static LoadedProjectSession MigrateV1ArchiveToSession(
        CmakerManifest v1Manifest,
        IReadOnlyDictionary<string, byte[]> entries,
        BoxModelRegistry registry,
        long packageSizeBytes)
    {
        CmakerMetadata? meta = entries.TryGetValue(CmakerPackageService.MetadataEntryPath, out byte[]? metaBytes)
            ? CmakerPackageService.DeserializeJsonUtf8<CmakerMetadata>(metaBytes, PersonalizationJsonOptions.Default)
            : null;

        CmakerModelState? modelState = entries.TryGetValue(CmakerPackageService.ModelEntryPath, out byte[]? modelBytes)
            ? CmakerPackageService.DeserializeJsonUtf8<CmakerModelState>(modelBytes, PersonalizationJsonOptions.Default)
            : null;

        // No esquema v1 hipotético, personalização podia residir em "personalization.json" único ou por face
        PersonalizationSnapshot? snapshot = null;
        if (entries.TryGetValue("personalization.json", out byte[]? singlePersBytes))
        {
            snapshot = CmakerPackageService.DeserializeJsonUtf8<PersonalizationSnapshot>(singlePersBytes, PersonalizationJsonOptions.Default);
        }

        string targetModelId = !string.IsNullOrWhiteSpace(modelState?.ModelId)
            ? modelState!.ModelId
            : !string.IsNullOrWhiteSpace(meta?.ModelId)
                ? meta!.ModelId
                : !string.IsNullOrWhiteSpace(snapshot?.ModelId)
                    ? snapshot!.ModelId
                    : BoxWithLidAndHandlesModel.Id;

        IBoxModelDefinition boxModel = registry.Contains(targetModelId)
            ? registry.Get(targetModelId)
            : registry.Get(BoxWithLidAndHandlesModel.Id);

        BoxParameters boxParams = modelState?.BoxParameters.ToBoxParameters()
            ?? snapshot?.BoxParameters.ToBoxParameters()
            ?? BoxParameters.Default;

        if (snapshot is null)
        {
            var elements = new List<PersonalizationElement>();
            foreach ((string path, byte[] data) in entries)
            {
                if (!path.StartsWith("personalization/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, CmakerPackageService.SurfacesIndexEntryPath, StringComparison.OrdinalIgnoreCase) ||
                    !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CmakerSurfaceDocument? doc = CmakerPackageService.DeserializeJsonUtf8<CmakerSurfaceDocument>(data, PersonalizationJsonOptions.Default);
                if (doc?.Elements is { Count: > 0 })
                {
                    elements.AddRange(doc.Elements);
                }
            }

            snapshot = new PersonalizationSnapshot
            {
                ModelId = boxModel.ModelId,
                BoxParameters = PersonalizationBoxParametersSnapshot.FromCalculator(boxModel.CreateCalculator(boxParams)),
                SafetyMarginMm = modelState?.SafetyMarginMm ?? GeometryValidator.DefaultSafetyMarginMm,
                Elements = elements,
            };
        }

        SurfaceManager manager = SurfaceManager.FromSnapshot(snapshot, registry, boxModel);
        RestoreV1AssetsFromArchive(entries, manager);

        ProjectViewerState viewerState = modelState?.ViewerState ?? new ProjectViewerState();
        string resolvedId = !string.IsNullOrWhiteSpace(meta?.ProjectId)
            ? meta!.ProjectId
            : $"proj_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}";
        string resolvedName = !string.IsNullOrWhiteSpace(meta?.Name)
            ? meta!.Name
            : boxModel.DisplayName;

        string nowIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var record = new ProjectRecord
        {
            SchemaVersion = 2,
            Id = resolvedId,
            Name = resolvedName,
            Description = meta?.Description ?? string.Empty,
            Author = meta?.Author ?? string.Empty,
            Tags = meta?.Tags is not null ? new List<string>(meta.Tags) : [],
            Notes = meta?.Notes ?? string.Empty,
            CreatedAt = string.IsNullOrWhiteSpace(meta?.CreatedAt) ? nowIso : meta!.CreatedAt,
            UpdatedAt = string.IsNullOrWhiteSpace(meta?.UpdatedAt) ? nowIso : meta!.UpdatedAt,
            ModelId = boxModel.ModelId,
            ModelName = string.IsNullOrWhiteSpace(meta?.ModelDisplayName) ? boxModel.DisplayName : meta!.ModelDisplayName,
            BoxParameters = ProjectBoxParameters.FromBoxParameters(boxParams),
            ViewerState = viewerState,
            Personalization = manager.CreateSnapshot(includeRawBase64: true),
        };

        ValidationReport validation = manager.Validate();
        BoxCalculationSummary boxSummary = boxModel.BuildSummary(boxParams);
        byte[]? thumbBytes = entries.TryGetValue(CmakerPackageService.PreviewThumbnail3DEntryPath, out byte[]? tBytes) ? tBytes : null;

        var summary = new ProjectSummary
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            Author = record.Author,
            Tags = record.Tags.ToArray(),
            Notes = record.Notes,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            ModelId = record.ModelId,
            ModelName = record.ModelName,
            BoxParameters = record.BoxParameters,
            ViewerState = viewerState,
            ElementCount = manager.ElementCount,
            TotalCutPerimeterMeters = PythonMath.Round(boxSummary.TotalCutPerimeterMeters, 4),
            ValidationValid = validation.IsValid,
            ErrorCount = validation.ErrorCount,
            WarningCount = validation.WarningCount,
            PackageFormat = "cmaker",
            PackageSchemaVersion = v1Manifest.SchemaVersion,
            PackageSizeBytes = packageSizeBytes,
            ThumbnailPngBytes = thumbBytes,
        };

        return new LoadedProjectSession
        {
            Record = record,
            Summary = summary,
            BoxParameters = boxParams,
            ViewerState = viewerState,
            Manager = manager,
            SourceFormat = "cmaker_v1_migrated",
            PackageSchemaVersion = v1Manifest.SchemaVersion,
            PackageSizeBytes = packageSizeBytes,
            MigrationNotice = $"Projeto migrado automaticamente do esquema .cmaker v{v1Manifest.SchemaVersion} para v{CmakerManifest.CurrentSchemaVersion}.",
        };
    }

    private static void RestoreV1AssetsFromArchive(IReadOnlyDictionary<string, byte[]> entries, SurfaceManager manager)
    {
        foreach (PersonalizationElement el in manager.ListElements())
        {
            if (el is VectorSvgPersonalizationElement svg && !string.IsNullOrWhiteSpace(svg.AssetEntryPath))
            {
                string key = CmakerPackageService.NormalizeAndValidateZipEntryPath(svg.AssetEntryPath);
                if (entries.TryGetValue(key, out byte[]? bytes))
                {
                    svg.RawSvgText = Encoding.UTF8.GetString(bytes);
                }
            }
            else if (el is RasterImagePersonalizationElement raster && !string.IsNullOrWhiteSpace(raster.AssetEntryPath))
            {
                string key = CmakerPackageService.NormalizeAndValidateZipEntryPath(raster.AssetEntryPath);
                if (entries.TryGetValue(key, out byte[]? bytes))
                {
                    raster.RawImageBytes = bytes;
                }
            }
        }

        HydrateLegacyRawBytes(manager);
    }

    private static void HydrateLegacyRawBytes(SurfaceManager manager)
    {
        foreach (PersonalizationElement el in manager.ListElements())
        {
            if (el is RasterImagePersonalizationElement raster &&
                raster.RawImageBytes is not { Length: > 0 })
            {
                string b64 = !string.IsNullOrWhiteSpace(raster.RawImageB64)
                    ? raster.RawImageB64
                    : raster.ProcessedImageB64;
                if (!string.IsNullOrWhiteSpace(b64))
                {
                    try
                    {
                        raster.RawImageBytes = Convert.FromBase64String(b64);
                    }
                    catch (FormatException)
                    {
                    }
                }
            }
        }
    }

    private static bool LooksLikeLegacyProjectStem(string stem, string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(stem) ||
            stem.Equals("legacy_migration", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (stem.EndsWith(".caixamaker", StringComparison.OrdinalIgnoreCase) ||
            AppPaths.IsSafeProjectId(stem))
        {
            return true;
        }

        try
        {
            using FileStream fs = File.OpenRead(jsonPath);
            using JsonDocument doc = JsonDocument.Parse(fs);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   (doc.RootElement.TryGetProperty("personalization", out _) ||
                    doc.RootElement.TryGetProperty("bundle", out _));
        }
        catch
        {
            return false;
        }
    }

    private static void ArchiveLegacyJsonFile(string jsonPath, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        string dest = Path.Combine(backupDir, Path.GetFileName(jsonPath));
        if (File.Exists(dest))
        {
            string stem = Path.GetFileNameWithoutExtension(jsonPath);
            dest = Path.Combine(backupDir, $"{stem}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
        }

        File.Move(jsonPath, dest, overwrite: true);
    }
}
