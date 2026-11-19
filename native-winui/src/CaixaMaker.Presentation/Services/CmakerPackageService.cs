using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.Cam.Raster;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;

namespace CaixaMaker.App.Services;

/// <summary>
/// Serviço nativo de empacotamento, validação criptográfica SHA-256 e hidratação completa
/// do formato autocontido <c>.cmaker</c> (v2.0.0) baseado em contêiner ZIP padrão.
/// </summary>
public sealed partial class CmakerPackageService
{
    public const string FileExtension = ".cmaker";
    public const string ManifestEntryPath = "manifest.json";
    public const string MetadataEntryPath = "metadata.json";
    public const string ModelEntryPath = "model.json";
    public const string SurfacesIndexEntryPath = "personalization/surfaces.json";
    public const string PreviewThumbnail3DEntryPath = "preview/thumbnail_3d.png";
    public const string PreviewSheet2DEntryPath = "preview/sheet_2d.png";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string ValidateProjectId(string? projectId)
    {
        if (!AppPaths.IsSafeProjectId(projectId))
        {
            throw new ArgumentException($"ID de projeto inválido: '{projectId}'.", nameof(projectId));
        }

        return projectId!.Trim();
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]+", RegexOptions.Compiled)]
    private static partial Regex BuildAssetStemSanitizerRegex();

    private readonly BoxModelRegistry _registry;

    public CmakerPackageService(BoxModelRegistry? registry = null)
    {
        _registry = registry ?? BoxModelRegistry.Default;
    }

    /// <summary>Gera o nome de arquivo padrão <c>&lt;slug&gt;.cmaker</c> a partir do nome do projeto.</summary>
    public static string BuildCmakerFilename(string? projectName, string fallbackDisplayName)
        => $"{PortablePackageService.BuildSlug(projectName, fallbackDisplayName)}.cmaker";

    /// <summary>Indica se os primeiros bytes de um arquivo correspondem a um contêiner ZIP (<c>.cmaker</c>).</summary>
    public static bool LooksLikeZipArchive(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        using FileStream fs = File.OpenRead(filePath);
        if (fs.Length < 4)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[4];
        int read = fs.Read(header);
        return read == 4 && header[0] == (byte)'P' && header[1] == (byte)'K';
    }

    /// <summary>Constrói em memória o pacote <c>.cmaker</c> completo com manifesto SHA-256, ativos e miniaturas.</summary>
    public CmakerPackageBuildResult BuildPackage(
        ProjectSaveRequest request,
        string? appVersion = null,
        CmakerManufacturingState? manufacturingState = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        IBoxModelDefinition boxModel = _registry.Contains(request.ModelId)
            ? _registry.Get(request.ModelId)
            : _registry.Get(BoxWithLidAndHandlesModel.Id);

        BoxCalculator calc = boxModel.CreateCalculator(request.BoxParameters);
        BoxDrawer drawer = boxModel.CreateDrawer(calc);
        BoxCalculationSummary boxSummary = boxModel.BuildSummary(calc, drawer);

        // Reconstrói SurfaceManager independente para não mutar o estado vivo de AssetEntryPath do workspace sem controle
        PersonalizationSnapshot baseSnapshot = ProjectRepository.BuildSnapshotForSave(request, boxModel, _registry);
        SurfaceManager workingManager = SurfaceManager.FromSnapshot(baseSnapshot, _registry, boxModel);
        RestoreRuntimeBinaryAssets(request.Manager, workingManager);

        string resolvedAppVersion = string.IsNullOrWhiteSpace(appVersion)
            ? AppVersion.Version
            : appVersion.Trim();

        string projectId = !string.IsNullOrWhiteSpace(request.SavedId)
            ? ValidateProjectId(request.SavedId)
            : $"proj_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}";

        string cleanName = string.IsNullOrWhiteSpace(request.Name)
            ? boxModel.DisplayName
            : request.Name.Trim();

        string nowIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        string createdAt = !string.IsNullOrWhiteSpace(request.CreatedAtOverride)
            ? request.CreatedAtOverride!
            : nowIso;

        ProjectViewerState viewerState = CloneViewerState(request.ViewerState);
        string dimMode = NormalizeDimensionMode(viewerState.DimensionMode);
        viewerState.DimensionMode = dimMode;

        List<string> normalizedTags = NormalizeTags(request.Tags);
        string author = string.IsNullOrWhiteSpace(request.Author)
            ? AppVersion.DeveloperName
            : request.Author.Trim();

        // 1. Extrai e deduplica ativos binários originais (SVG + Raster) -> assets/<sha12>_<nome>.<ext>
        var entriesMap = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var assetPathByContentKey = new Dictionary<string, string>(StringComparer.Ordinal);

        IReadOnlyList<PersonalizationElement> allElements = workingManager.ListElements();
        foreach (PersonalizationElement element in allElements)
        {
            if (element is VectorSvgPersonalizationElement svg)
            {
                byte[]? svgBytes = ResolveSvgAssetBytes(svg);
                if (svgBytes is { Length: > 0 })
                {
                    string assetPath = StoreDeduplicatedAsset(
                        entriesMap,
                        assetPathByContentKey,
                        svgBytes,
                        svg.OriginalFileName ?? svg.SourceFilename ?? svg.Name,
                        ".svg");
                    svg.AssetEntryPath = assetPath;
                    svg.OriginalFileName ??= svg.SourceFilename ?? Path.GetFileName(assetPath);
                }
            }
            else if (element is RasterImagePersonalizationElement raster)
            {
                byte[]? rasterBytes = ResolveRasterAssetBytes(raster);
                if (rasterBytes is { Length: > 0 })
                {
                    string ext = ResolveRasterExtension(rasterBytes, raster.SourceFormat, raster.OriginalFileName ?? raster.SourceFilename);
                    string assetPath = StoreDeduplicatedAsset(
                        entriesMap,
                        assetPathByContentKey,
                        rasterBytes,
                        raster.OriginalFileName ?? raster.SourceFilename ?? raster.Name,
                        ext);
                    raster.AssetEntryPath = assetPath;
                    raster.OriginalFileName ??= raster.SourceFilename ?? Path.GetFileName(assetPath);
                }
            }
        }

        int assetCount = assetPathByContentKey.Count;

        // 2. Serializa model.json
        CmakerManufacturingState mfgState = manufacturingState ?? new CmakerManufacturingState
        {
            LaserBedPreset = viewerState.LaserBedPreset,
            LaserBedWidthMm = viewerState.LaserBedWidthMm,
            LaserBedHeightMm = viewerState.LaserBedHeightMm,
            KerfSlotCount = viewerState.KerfSlotCount,
            KerfStepMm = viewerState.KerfStepMm,
        };

        var modelState = new CmakerModelState
        {
            ModelId = boxModel.ModelId,
            BoxParameters = ProjectBoxParameters.FromBoxParameters(request.BoxParameters),
            DimensionReferenceMode = dimMode,
            SafetyMarginMm = workingManager.SafetyMarginMm,
            ViewerState = viewerState,
            ManufacturingState = mfgState,
        };
        entriesMap[ModelEntryPath] = SerializeJsonUtf8(modelState, PersonalizationJsonOptions.Indented);

        // 3. Serializa personalization/surfaces.json e personalization/<surface_id>.json por face
        IReadOnlyList<SurfaceDefinition> surfaces = workingManager.Surfaces.ListSurfaces();
        var surfaceIds = new List<string>(surfaces.Count);
        var surfaceHeaders = new List<CmakerSurfaceHeader>(surfaces.Count);

        foreach (SurfaceDefinition surf in surfaces)
        {
            string surfaceKey = SnakeCaseEnumJsonConverter<SurfaceId>.ToApiString(surf.PanelId);
            List<PersonalizationElement> faceElements = allElements
                .Where(e => e.PanelId == surf.PanelId)
                .ToList();

            surfaceIds.Add(surfaceKey);
            surfaceHeaders.Add(new CmakerSurfaceHeader
            {
                PanelId = surfaceKey,
                DisplayName = surf.DisplayName,
                WidthMm = PythonMath.Round(surf.WidthMm, 4),
                HeightMm = PythonMath.Round(surf.HeightMm, 4),
                ElementCount = faceElements.Count,
            });

            var faceDoc = new CmakerSurfaceDocument
            {
                PanelId = surfaceKey,
                DisplayName = surf.DisplayName,
                Elements = faceElements,
            };
            entriesMap[$"personalization/{surfaceKey}.json"] = SerializeJsonUtf8(
                faceDoc,
                PersonalizationJsonOptions.CmakerWithoutRawB64);
        }

        var surfacesIndex = new CmakerSurfacesIndex
        {
            SafetyMarginMm = workingManager.SafetyMarginMm,
            SurfaceIds = surfaceIds,
            Surfaces = surfaceHeaders,
        };
        entriesMap[SurfacesIndexEntryPath] = SerializeJsonUtf8(surfacesIndex, PersonalizationJsonOptions.Indented);

        // 4. Gera previews 3D (320x220 SSAA 2x) e 2D (360x240) reais com os elementos aplicados
        byte[] thumb3dPng = CatalogPreview3DService.RenderProjectSceneWithElementsPng(
            boxModel,
            request.BoxParameters,
            workingManager,
            viewerState,
            darkTheme: true,
            widthPx: 320,
            heightPx: 220);
        entriesMap[PreviewThumbnail3DEntryPath] = thumb3dPng;

        byte[] sheet2dPng = CatalogPreview3DService.RenderManufacturingSheetThumbnailPng(
            boxModel,
            request.BoxParameters,
            workingManager,
            darkTheme: true,
            widthPx: 360,
            heightPx: 240);
        entriesMap[PreviewSheet2DEntryPath] = sheet2dPng;

        // 5. Validação CNC + metadata.json
        ValidationReport validation = workingManager.Validate();
        (double innerW, double innerD, double innerH) = DimensionReferenceConverter.ToInternalUsable(
            request.BoxParameters.BoxWidth,
            request.BoxParameters.BoxDepth,
            request.BoxParameters.BoxHeight,
            request.BoxParameters.BoxThickness);

        var metadata = new CmakerMetadata
        {
            ProjectId = projectId,
            Name = cleanName,
            Description = (request.Description ?? string.Empty).Trim(),
            Author = author,
            Tags = normalizedTags,
            Notes = (request.Notes ?? string.Empty).Trim(),
            CreatedAt = createdAt,
            UpdatedAt = nowIso,
            AppVersion = resolvedAppVersion,
            ModelId = boxModel.ModelId,
            ModelDisplayName = boxModel.DisplayName,
            ExternalDimensionsMm = new CmakerDimensionsDto
            {
                Width = PythonMath.Round(request.BoxParameters.BoxWidth, 4),
                Depth = PythonMath.Round(request.BoxParameters.BoxDepth, 4),
                Height = PythonMath.Round(request.BoxParameters.BoxHeight, 4),
            },
            InternalUsableDimensionsMm = new CmakerDimensionsDto
            {
                Width = PythonMath.Round(innerW, 4),
                Depth = PythonMath.Round(innerD, 4),
                Height = PythonMath.Round(innerH, 4),
            },
            DimensionReferenceMode = dimMode,
            ThicknessMm = PythonMath.Round(request.BoxParameters.BoxThickness, 4),
            FolgaMm = PythonMath.Round(request.BoxParameters.Folga, 4),
            SafetyMarginMm = PythonMath.Round(workingManager.SafetyMarginMm, 4),
            PanelCount = boxModel.TotalPanels,
            TotalCutPerimeterMeters = PythonMath.Round(boxSummary.TotalCutPerimeterMeters, 4),
            ElementCount = allElements.Count,
            AssetCount = assetCount,
            ValidationValid = validation.IsValid,
            ErrorCount = validation.ErrorCount,
            WarningCount = validation.WarningCount,
        };
        entriesMap[MetadataEntryPath] = SerializeJsonUtf8(metadata, PersonalizationJsonOptions.Indented);

        // 6. Manifesto raiz com SHA-256 e tamanho por entrada
        var manifest = new CmakerManifest
        {
            Format = CmakerManifest.CanonicalFormat,
            SchemaVersion = CmakerManifest.CurrentSchemaVersion,
            AppVersion = resolvedAppVersion,
            CreatedUtc = nowIso,
            Entries = entriesMap
                .Select(kv => new CmakerManifestEntry
                {
                    Path = kv.Key,
                    SizeBytes = kv.Value.LongLength,
                    Sha256 = ComputeSha256Hex(kv.Value),
                })
                .ToList(),
        };
        byte[] manifestBytes = SerializeJsonUtf8(manifest, PersonalizationJsonOptions.Indented);

        // 7. Empacotamento ZIP ordenado e determinístico
        byte[] packageBytes = WriteZipBytes(manifestBytes, entriesMap);

        var summary = new ProjectSummary
        {
            Id = metadata.ProjectId,
            Name = metadata.Name,
            Description = metadata.Description,
            Author = metadata.Author,
            Tags = metadata.Tags.ToArray(),
            Notes = metadata.Notes,
            CreatedAt = metadata.CreatedAt,
            UpdatedAt = metadata.UpdatedAt,
            ModelId = metadata.ModelId,
            ModelName = metadata.ModelDisplayName,
            BoxParameters = modelState.BoxParameters,
            ViewerState = viewerState,
            ElementCount = metadata.ElementCount,
            AssetCount = metadata.AssetCount,
            TotalCutPerimeterMeters = metadata.TotalCutPerimeterMeters,
            ValidationValid = metadata.ValidationValid,
            ErrorCount = metadata.ErrorCount,
            WarningCount = metadata.WarningCount,
            PackageFormat = "cmaker",
            PackageSchemaVersion = manifest.SchemaVersion,
            PackageSizeBytes = packageBytes.LongLength,
            ThumbnailPngBytes = thumb3dPng,
        };

        return new CmakerPackageBuildResult
        {
            FileName = BuildCmakerFilename(cleanName, boxModel.DisplayName),
            PackageBytes = packageBytes,
            Manifest = manifest,
            Metadata = metadata,
            Summary = summary,
        };
    }

    /// <summary>Constrói e grava atomicamente um arquivo <c>.cmaker</c> no disco.</summary>
    public CmakerPackageBuildResult SaveToFile(
        ProjectSaveRequest request,
        string destinationPath,
        string? appVersion = null,
        CmakerManufacturingState? manufacturingState = null)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("O caminho de destino .cmaker não pode estar vazio.", nameof(destinationPath));
        }

        CmakerPackageBuildResult built = BuildPackage(request, appVersion, manufacturingState);
        WriteBytesAtomic(destinationPath, built.PackageBytes);

        return new CmakerPackageBuildResult
        {
            FileName = built.FileName,
            PackageBytes = built.PackageBytes,
            Manifest = built.Manifest,
            Metadata = built.Metadata,
            Summary = new ProjectSummary
            {
                Id = built.Summary.Id,
                Name = built.Summary.Name,
                Description = built.Summary.Description,
                Author = built.Summary.Author,
                Tags = built.Summary.Tags,
                Notes = built.Summary.Notes,
                CreatedAt = built.Summary.CreatedAt,
                UpdatedAt = built.Summary.UpdatedAt,
                ModelId = built.Summary.ModelId,
                ModelName = built.Summary.ModelName,
                BoxParameters = built.Summary.BoxParameters,
                ViewerState = built.Summary.ViewerState,
                ElementCount = built.Summary.ElementCount,
                AssetCount = built.Summary.AssetCount,
                TotalCutPerimeterMeters = built.Summary.TotalCutPerimeterMeters,
                ValidationValid = built.Summary.ValidationValid,
                ErrorCount = built.Summary.ErrorCount,
                WarningCount = built.Summary.WarningCount,
                PackageFormat = "cmaker",
                PackageSchemaVersion = built.Summary.PackageSchemaVersion,
                PackageSizeBytes = built.PackageBytes.LongLength,
                PackageFilePath = Path.GetFullPath(destinationPath),
                ThumbnailPngBytes = built.Summary.ThumbnailPngBytes,
            },
        };
    }

    /// <summary>Lê rapidamente apenas metadados e miniatura 3D de um arquivo <c>.cmaker</c> sem deserializar toda a cena.</summary>
    public ProjectSummary ReadSummary(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo .cmaker não encontrado.", filePath);
        }

        string fullPath = Path.GetFullPath(filePath);
        var fileInfo = new FileInfo(fullPath);

        using FileStream fs = File.OpenRead(fullPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        byte[]? manifestBytes = ReadZipEntryBytesOptional(zip, ManifestEntryPath);
        CmakerManifest? manifest = manifestBytes is { Length: > 0 }
            ? DeserializeJsonUtf8<CmakerManifest>(manifestBytes, PersonalizationJsonOptions.Default)
            : null;

        byte[] metadataBytes = ReadZipEntryBytesRequired(zip, MetadataEntryPath);
        CmakerMetadata metadata = DeserializeJsonUtf8<CmakerMetadata>(metadataBytes, PersonalizationJsonOptions.Default)
            ?? throw new InvalidDataException("Arquivo metadata.json inválido dentro do .cmaker.");

        byte[]? modelBytes = ReadZipEntryBytesOptional(zip, ModelEntryPath);
        CmakerModelState? modelState = modelBytes is { Length: > 0 }
            ? DeserializeJsonUtf8<CmakerModelState>(modelBytes, PersonalizationJsonOptions.Default)
            : null;

        byte[]? thumbBytes = ReadZipEntryBytesOptional(zip, PreviewThumbnail3DEntryPath);

        ProjectBoxParameters boxParams = modelState?.BoxParameters ?? new ProjectBoxParameters
        {
            BoxWidth = metadata.ExternalDimensionsMm.Width > 0 ? metadata.ExternalDimensionsMm.Width : BoxParameters.DefaultWidth,
            BoxDepth = metadata.ExternalDimensionsMm.Depth > 0 ? metadata.ExternalDimensionsMm.Depth : BoxParameters.DefaultDepth,
            BoxHeight = metadata.ExternalDimensionsMm.Height > 0 ? metadata.ExternalDimensionsMm.Height : BoxParameters.DefaultHeight,
            BoxThickness = metadata.ThicknessMm > 0 ? metadata.ThicknessMm : BoxParameters.DefaultThickness,
            Folga = metadata.FolgaMm,
        };

        ProjectViewerState viewerState = modelState?.ViewerState ?? new ProjectViewerState();
        if (string.IsNullOrWhiteSpace(viewerState.DimensionMode))
        {
            viewerState.DimensionMode = metadata.DimensionReferenceMode;
        }

        string resolvedId = !string.IsNullOrWhiteSpace(metadata.ProjectId)
            ? metadata.ProjectId
            : Path.GetFileNameWithoutExtension(fullPath);

        return new ProjectSummary
        {
            Id = resolvedId,
            Name = string.IsNullOrWhiteSpace(metadata.Name) ? Path.GetFileNameWithoutExtension(fullPath) : metadata.Name,
            Description = metadata.Description ?? string.Empty,
            Author = metadata.Author ?? string.Empty,
            Tags = metadata.Tags is { Count: > 0 } ? metadata.Tags.ToArray() : Array.Empty<string>(),
            Notes = metadata.Notes ?? string.Empty,
            CreatedAt = metadata.CreatedAt ?? string.Empty,
            UpdatedAt = metadata.UpdatedAt ?? string.Empty,
            ModelId = string.IsNullOrWhiteSpace(metadata.ModelId) ? BoxWithLidAndHandlesModel.Id : metadata.ModelId,
            ModelName = metadata.ModelDisplayName ?? string.Empty,
            BoxParameters = boxParams,
            ViewerState = viewerState,
            ElementCount = metadata.ElementCount,
            AssetCount = metadata.AssetCount,
            TotalCutPerimeterMeters = metadata.TotalCutPerimeterMeters,
            ValidationValid = metadata.ValidationValid,
            ErrorCount = metadata.ErrorCount,
            WarningCount = metadata.WarningCount,
            PackageFormat = "cmaker",
            PackageSchemaVersion = manifest?.SchemaVersion ?? CmakerManifest.CurrentSchemaVersion,
            PackageSizeBytes = fileInfo.Length,
            PackageFilePath = fullPath,
            ThumbnailPngBytes = thumbBytes,
        };
    }

    /// <summary>Lê, valida integridade SHA-256 e hidrata completamente uma sessão editável a partir de um <c>.cmaker</c>.</summary>
    public LoadedProjectSession LoadPackage(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo .cmaker não encontrado.", filePath);
        }

        string fullPath = Path.GetFullPath(filePath);
        byte[] packageBytes = File.ReadAllBytes(fullPath);
        LoadedProjectSession loaded = LoadPackageBytes(packageBytes, Path.GetFileName(fullPath));

        return new LoadedProjectSession
        {
            Record = loaded.Record,
            Summary = new ProjectSummary
            {
                Id = loaded.Summary.Id,
                Name = loaded.Summary.Name,
                Description = loaded.Summary.Description,
                Author = loaded.Summary.Author,
                Tags = loaded.Summary.Tags,
                Notes = loaded.Summary.Notes,
                CreatedAt = loaded.Summary.CreatedAt,
                UpdatedAt = loaded.Summary.UpdatedAt,
                ModelId = loaded.Summary.ModelId,
                ModelName = loaded.Summary.ModelName,
                BoxParameters = loaded.Summary.BoxParameters,
                ViewerState = loaded.Summary.ViewerState,
                ElementCount = loaded.Summary.ElementCount,
                AssetCount = loaded.Summary.AssetCount,
                TotalCutPerimeterMeters = loaded.Summary.TotalCutPerimeterMeters,
                ValidationValid = loaded.Summary.ValidationValid,
                ErrorCount = loaded.Summary.ErrorCount,
                WarningCount = loaded.Summary.WarningCount,
                PackageFormat = loaded.Summary.PackageFormat,
                PackageSchemaVersion = loaded.Summary.PackageSchemaVersion,
                PackageSizeBytes = packageBytes.LongLength,
                PackageFilePath = fullPath,
                ThumbnailPngBytes = loaded.Summary.ThumbnailPngBytes,
            },
            BoxParameters = loaded.BoxParameters,
            ViewerState = loaded.ViewerState,
            Manager = loaded.Manager,
            SourceFormat = loaded.SourceFormat,
            PackageSchemaVersion = loaded.PackageSchemaVersion,
            PackageFilePath = fullPath,
            MigrationNotice = loaded.MigrationNotice,
            AssetCount = loaded.AssetCount,
            PackageSizeBytes = packageBytes.LongLength,
        };
    }

    /// <summary>Lê, verifica SHA-256 e reconstrói uma sessão a partir dos bytes de um arquivo <c>.cmaker</c>.</summary>
    public LoadedProjectSession LoadPackageBytes(byte[] packageBytes, string? sourceFilename = null)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        if (packageBytes.Length < 4 || packageBytes[0] != (byte)'P' || packageBytes[1] != (byte)'K')
        {
            throw new InvalidDataException("O arquivo informado não possui assinatura válida de pacote .cmaker (ZIP).");
        }

        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var ms = new MemoryStream(packageBytes, writable: false))
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                string normalizedPath = NormalizeAndValidateZipEntryPath(entry.FullName);
                using Stream es = entry.Open();
                using var copyMs = new MemoryStream();
                es.CopyTo(copyMs);
                entries[normalizedPath] = copyMs.ToArray();
            }
        }

        if (!entries.TryGetValue(ManifestEntryPath, out byte[]? manifestBytes))
        {
            throw new InvalidDataException("O pacote .cmaker não contém o manifesto obrigatório 'manifest.json'.");
        }

        CmakerManifest manifest = DeserializeJsonUtf8<CmakerManifest>(manifestBytes, PersonalizationJsonOptions.Default)
            ?? throw new InvalidDataException("O arquivo 'manifest.json' do pacote .cmaker é inválido.");

        if (!string.Equals(manifest.Format, CmakerManifest.CanonicalFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Formato de manifesto inesperado: '{manifest.Format}'. Esperado '{CmakerManifest.CanonicalFormat}'.");
        }

        VerifyManifestEntriesIntegrity(manifest, entries);

        (int major, int minor, int patch) schema = ParseSemVerTuple(manifest.SchemaVersion);
        if (schema.major <= 1)
        {
            return CmakerMigrationService.MigrateV1ArchiveToSession(manifest, entries, _registry, packageBytes.LongLength);
        }

        string? migrationNotice = schema.major > 2
            ? $"Este projeto foi salvo em uma versão mais nova do formato .cmaker (v{manifest.SchemaVersion}). Campos extras desconhecidos foram preservados."
            : null;

        if (!entries.TryGetValue(MetadataEntryPath, out byte[]? metadataBytes))
        {
            throw new InvalidDataException("O pacote .cmaker não contém 'metadata.json'.");
        }

        CmakerMetadata metadata = DeserializeJsonUtf8<CmakerMetadata>(metadataBytes, PersonalizationJsonOptions.Default)
            ?? throw new InvalidDataException("Arquivo 'metadata.json' inválido dentro do .cmaker.");

        CmakerModelState modelState = entries.TryGetValue(ModelEntryPath, out byte[]? modelBytes)
            ? (DeserializeJsonUtf8<CmakerModelState>(modelBytes, PersonalizationJsonOptions.Default) ?? new CmakerModelState())
            : new CmakerModelState();

        string requestedModelId = !string.IsNullOrWhiteSpace(modelState.ModelId)
            ? modelState.ModelId
            : metadata.ModelId;
        IBoxModelDefinition boxModel = _registry.Contains(requestedModelId)
            ? _registry.Get(requestedModelId)
            : _registry.Get(BoxWithLidAndHandlesModel.Id);

        BoxParameters boxParams = ResolveBoxParameters(modelState, metadata);
        ProjectViewerState viewerState = modelState.ViewerState ?? new ProjectViewerState();
        ApplyManufacturingStateToViewerState(viewerState, modelState.ManufacturingState);
        if (string.IsNullOrWhiteSpace(viewerState.DimensionMode))
        {
            viewerState.DimensionMode = !string.IsNullOrWhiteSpace(modelState.DimensionReferenceMode)
                ? modelState.DimensionReferenceMode
                : metadata.DimensionReferenceMode;
        }

        CmakerSurfacesIndex? surfacesIndex = entries.TryGetValue(SurfacesIndexEntryPath, out byte[]? surfacesIdxBytes)
            ? DeserializeJsonUtf8<CmakerSurfacesIndex>(surfacesIdxBytes, PersonalizationJsonOptions.Default)
            : null;

        double safetyMargin = modelState.SafetyMarginMm > 0 || surfacesIndex is null
            ? modelState.SafetyMarginMm
            : surfacesIndex.SafetyMarginMm;

        List<PersonalizationElement> loadedElements = LoadAndHydrateSurfaceElements(
            entries,
            surfacesIndex,
            out int missingAssetWarnings);

        if (missingAssetWarnings > 0 && string.IsNullOrEmpty(migrationNotice))
        {
            migrationNotice = $"{missingAssetWarnings} ativo(s) referenciado(s) não foram encontrados dentro do contêiner; geometria em cache mantida.";
        }

        var snapshot = new PersonalizationSnapshot
        {
            ModelId = boxModel.ModelId,
            BoxParameters = PersonalizationBoxParametersSnapshot.FromCalculator(boxModel.CreateCalculator(boxParams)),
            SafetyMarginMm = safetyMargin,
            Elements = loadedElements,
        };

        SurfaceManager manager = SurfaceManager.FromSnapshot(snapshot, _registry, boxModel);
        RestoreRuntimeBinaryAssetsFromList(loadedElements, manager);

        BoxCalculator calc = boxModel.CreateCalculator(boxParams);
        BoxDrawer drawer = boxModel.CreateDrawer(calc);
        BoxCalculationSummary boxSummary = boxModel.BuildSummary(calc, drawer);

        string fallbackStem = string.IsNullOrWhiteSpace(sourceFilename)
            ? boxModel.DisplayName
            : Path.GetFileNameWithoutExtension(sourceFilename);
        string resolvedId = !string.IsNullOrWhiteSpace(metadata.ProjectId)
            ? metadata.ProjectId
            : $"proj_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}";
        string resolvedName = !string.IsNullOrWhiteSpace(metadata.Name)
            ? metadata.Name
            : fallbackStem;

        var record = new ProjectRecord
        {
            SchemaVersion = 2,
            Id = resolvedId,
            Name = resolvedName,
            Description = metadata.Description ?? string.Empty,
            Author = metadata.Author ?? string.Empty,
            Tags = metadata.Tags is not null ? new List<string>(metadata.Tags) : [],
            Notes = metadata.Notes ?? string.Empty,
            CreatedAt = metadata.CreatedAt ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            UpdatedAt = metadata.UpdatedAt ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ModelId = boxModel.ModelId,
            ModelName = string.IsNullOrWhiteSpace(metadata.ModelDisplayName) ? boxModel.DisplayName : metadata.ModelDisplayName,
            BoxParameters = ProjectBoxParameters.FromBoxParameters(boxParams),
            ViewerState = viewerState,
            Personalization = manager.CreateSnapshot(includeRawBase64: true),
        };

        ValidationReport validation = manager.Validate();
        byte[]? thumbBytes = entries.TryGetValue(PreviewThumbnail3DEntryPath, out byte[]? tBytes) ? tBytes : null;
        int assetCount = entries.Keys.Count(k => k.StartsWith("assets/", StringComparison.OrdinalIgnoreCase));

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
            AssetCount = assetCount,
            TotalCutPerimeterMeters = PythonMath.Round(boxSummary.TotalCutPerimeterMeters, 4),
            ValidationValid = validation.IsValid,
            ErrorCount = validation.ErrorCount,
            WarningCount = validation.WarningCount,
            PackageFormat = "cmaker",
            PackageSchemaVersion = manifest.SchemaVersion,
            PackageSizeBytes = packageBytes.LongLength,
            ThumbnailPngBytes = thumbBytes,
        };

        return new LoadedProjectSession
        {
            Record = record,
            Summary = summary,
            BoxParameters = boxParams,
            ViewerState = viewerState,
            Manager = manager,
            SourceFormat = "cmaker",
            PackageSchemaVersion = manifest.SchemaVersion,
            MigrationNotice = migrationNotice,
            AssetCount = assetCount,
            PackageSizeBytes = packageBytes.LongLength,
        };
    }

    internal static void VerifyManifestEntriesIntegrity(
        CmakerManifest manifest,
        IReadOnlyDictionary<string, byte[]> entries)
    {
        if (manifest.Entries is null || manifest.Entries.Count == 0)
        {
            return;
        }

        foreach (CmakerManifestEntry listed in manifest.Entries)
        {
            string entryPath = NormalizeAndValidateZipEntryPath(listed.Path);
            if (!entries.TryGetValue(entryPath, out byte[]? actualBytes))
            {
                throw new InvalidDataException($"Arquivo obrigatório '{entryPath}' ausente no contêiner .cmaker.");
            }

            if (listed.SizeBytes >= 0 && actualBytes.LongLength != listed.SizeBytes)
            {
                throw new InvalidDataException(
                    $"Tamanho divergente para '{entryPath}' no contêiner .cmaker (esperado {listed.SizeBytes} B, encontrado {actualBytes.LongLength} B).");
            }

            if (!string.IsNullOrWhiteSpace(listed.Sha256))
            {
                string actualSha = ComputeSha256Hex(actualBytes);
                if (!string.Equals(actualSha, listed.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Falha na verificação criptográfica SHA-256 da entrada '{entryPath}'. O pacote .cmaker pode estar corrompido.");
                }
            }
        }
    }

    internal static string NormalizeAndValidateZipEntryPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new InvalidDataException("Entrada ZIP vazia ou inválida no pacote .cmaker.");
        }

        string normalized = rawPath.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException($"Caminho absoluto proibido dentro do pacote .cmaker: '{rawPath}'.");
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == ".." || seg == ".")
            {
                throw new InvalidDataException($"Caminho inseguro (Zip Slip) detectado no pacote .cmaker: '{rawPath}'.");
            }
        }

        return string.Join('/', segments);
    }

    internal static string ComputeSha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static void WriteBytesAtomic(string destinationPath, byte[] bytes)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static byte[] WriteZipBytes(byte[] manifestBytes, SortedDictionary<string, byte[]> entriesMap)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Utf8NoBom))
        {
            WriteZipEntry(zip, ManifestEntryPath, manifestBytes);
            foreach ((string path, byte[] data) in entriesMap)
            {
                WriteZipEntry(zip, path, data);
            }
        }

        return ms.ToArray();
    }

    private static void WriteZipEntry(ZipArchive zip, string path, byte[] bytes)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] SerializeJsonUtf8<T>(T value, JsonSerializerOptions options)
        => JsonSerializer.SerializeToUtf8Bytes(value, options);

    internal static T? DeserializeJsonUtf8<T>(byte[] bytes, JsonSerializerOptions options)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return JsonSerializer.Deserialize<T>(span, options);
    }

    private static byte[]? ReadZipEntryBytesOptional(ZipArchive zip, string entryPath)
    {
        ZipArchiveEntry? entry = zip.GetEntry(entryPath);
        if (entry is null)
        {
            return null;
        }

        using Stream stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] ReadZipEntryBytesRequired(ZipArchive zip, string entryPath)
        => ReadZipEntryBytesOptional(zip, entryPath)
           ?? throw new InvalidDataException($"Entrada obrigatória '{entryPath}' não encontrada no pacote .cmaker.");

    private static List<PersonalizationElement> LoadAndHydrateSurfaceElements(
        IReadOnlyDictionary<string, byte[]> entries,
        CmakerSurfacesIndex? surfacesIndex,
        out int missingAssetWarnings)
    {
        missingAssetWarnings = 0;
        var orderedSurfacePaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (surfacesIndex?.SurfaceIds is { Count: > 0 })
        {
            foreach (string id in surfacesIndex.SurfaceIds)
            {
                string path = $"personalization/{id.Trim().ToLowerInvariant()}.json";
                if (entries.ContainsKey(path) && seenPaths.Add(path))
                {
                    orderedSurfacePaths.Add(path);
                }
            }
        }

        foreach (string path in entries.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (path.StartsWith("personalization/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(path, SurfacesIndexEntryPath, StringComparison.OrdinalIgnoreCase) &&
                seenPaths.Add(path))
            {
                orderedSurfacePaths.Add(path);
            }
        }

        var elements = new List<PersonalizationElement>();
        foreach (string surfPath in orderedSurfacePaths)
        {
            CmakerSurfaceDocument? doc = DeserializeJsonUtf8<CmakerSurfaceDocument>(
                entries[surfPath],
                PersonalizationJsonOptions.Default);

            if (doc?.Elements is null)
            {
                continue;
            }

            foreach (PersonalizationElement element in doc.Elements)
            {
                if (element is VectorSvgPersonalizationElement svg)
                {
                    if (!string.IsNullOrWhiteSpace(svg.AssetEntryPath))
                    {
                        string assetKey = NormalizeAndValidateZipEntryPath(svg.AssetEntryPath);
                        if (entries.TryGetValue(assetKey, out byte[]? svgBytes))
                        {
                            svg.RawSvgText = Encoding.UTF8.GetString(svgBytes).TrimStart('\uFEFF');
                            svg.OriginalFileName ??= svg.SourceFilename ?? Path.GetFileName(assetKey);
                        }
                        else
                        {
                            missingAssetWarnings++;
                        }
                    }
                }
                else if (element is RasterImagePersonalizationElement raster)
                {
                    if (!string.IsNullOrWhiteSpace(raster.AssetEntryPath))
                    {
                        string assetKey = NormalizeAndValidateZipEntryPath(raster.AssetEntryPath);
                        if (entries.TryGetValue(assetKey, out byte[]? assetBytes))
                        {
                            raster.RawImageBytes = assetBytes;
                            raster.OriginalFileName ??= raster.SourceFilename ?? Path.GetFileName(assetKey);
                            if (string.IsNullOrWhiteSpace(raster.RawImageB64))
                            {
                                try
                                {
                                    byte[] canonicalPng = RasterProcessor.EncodeCanonicalPngBytes(assetBytes);
                                    raster.RawImageB64 = Convert.ToBase64String(canonicalPng);
                                }
                                catch
                                {
                                    // Caso algum ativo customizado não seja decodificável como raster padrão, preserva os bytes brutos
                                }
                            }
                        }
                        else
                        {
                            missingAssetWarnings++;
                        }
                    }
                    else if (raster.RawImageBytes is not { Length: > 0 } && !string.IsNullOrWhiteSpace(raster.RawImageB64))
                    {
                        raster.RawImageBytes = Convert.FromBase64String(raster.RawImageB64);
                    }
                }

                elements.Add(element);
            }
        }

        return elements;
    }

    private static void RestoreRuntimeBinaryAssets(SurfaceManager? sourceManager, SurfaceManager targetManager)
    {
        if (sourceManager is null)
        {
            return;
        }

        var sourceById = sourceManager.ListElements().ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (PersonalizationElement el in targetManager.ListElements())
        {
            if (!sourceById.TryGetValue(el.Id, out PersonalizationElement? srcEl))
            {
                continue;
            }

            if (el is VectorSvgPersonalizationElement dstSvg && srcEl is VectorSvgPersonalizationElement srcSvg)
            {
                dstSvg.OriginalFileName ??= srcSvg.OriginalFileName;
                dstSvg.RawSvgText ??= srcSvg.RawSvgText;
            }
            else if (el is RasterImagePersonalizationElement dstRaster && srcEl is RasterImagePersonalizationElement srcRaster)
            {
                dstRaster.OriginalFileName ??= srcRaster.OriginalFileName;
                dstRaster.RawImageBytes ??= srcRaster.RawImageBytes is { Length: > 0 }
                    ? (byte[])srcRaster.RawImageBytes.Clone()
                    : null;
            }
        }
    }

    private static void RestoreRuntimeBinaryAssetsFromList(IReadOnlyList<PersonalizationElement> loadedElements, SurfaceManager targetManager)
    {
        var byId = loadedElements.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (PersonalizationElement el in targetManager.ListElements())
        {
            if (!byId.TryGetValue(el.Id, out PersonalizationElement? loadedEl))
            {
                continue;
            }

            if (el is VectorSvgPersonalizationElement dstSvg && loadedEl is VectorSvgPersonalizationElement srcSvg)
            {
                dstSvg.RawSvgText = srcSvg.RawSvgText;
                dstSvg.OriginalFileName = srcSvg.OriginalFileName;
                dstSvg.AssetEntryPath = srcSvg.AssetEntryPath;
            }
            else if (el is RasterImagePersonalizationElement dstRaster && loadedEl is RasterImagePersonalizationElement srcRaster)
            {
                dstRaster.RawImageBytes = srcRaster.RawImageBytes;
                dstRaster.OriginalFileName = srcRaster.OriginalFileName;
                dstRaster.AssetEntryPath = srcRaster.AssetEntryPath;
            }
        }
    }

    private static string StoreDeduplicatedAsset(
        SortedDictionary<string, byte[]> entriesMap,
        Dictionary<string, string> assetPathByContentKey,
        byte[] assetBytes,
        string? rawName,
        string extension)
    {
        string shaFull = ComputeSha256Hex(assetBytes);
        string dedupKey = $"{shaFull}:{extension.ToLowerInvariant()}";
        if (assetPathByContentKey.TryGetValue(dedupKey, out string? existingPath))
        {
            return existingPath;
        }

        string safeName = SanitizeAssetFilename(rawName, extension);
        string assetPath = $"assets/{shaFull[..12]}_{safeName}";
        entriesMap[assetPath] = assetBytes;
        assetPathByContentKey[dedupKey] = assetPath;
        return assetPath;
    }

    private static byte[]? ResolveSvgAssetBytes(VectorSvgPersonalizationElement svg)
    {
        if (!string.IsNullOrWhiteSpace(svg.RawSvgText))
        {
            return Utf8NoBom.GetBytes(svg.RawSvgText);
        }

        if (svg.Paths.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        double w = Math.Max(0.1, svg.BaseWidthMm);
        double h = Math.Max(0.1, svg.BaseHeightMm);
        sb.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w:0.####}mm\" height=\"{h:0.####}mm\" viewBox=\"0 0 {w:0.####} {h:0.####}\">"));
        foreach (VectorPathItem path in svg.Paths)
        {
            string stroke = path.HasStroke ? "#0D47A1" : "none";
            string fill = path.HasFill ? "#0D47A1" : "none";
            sb.Append($"<path d=\"{System.Security.SecurityElement.Escape(path.D)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"0.2\" />");
        }
        sb.Append("</svg>");
        return Utf8NoBom.GetBytes(sb.ToString());
    }

    private static byte[]? ResolveRasterAssetBytes(RasterImagePersonalizationElement raster)
    {
        if (raster.RawImageBytes is { Length: > 0 })
        {
            return raster.RawImageBytes;
        }

        string b64 = !string.IsNullOrWhiteSpace(raster.RawImageB64)
            ? raster.RawImageB64
            : raster.ProcessedImageB64;
        if (string.IsNullOrWhiteSpace(b64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string ResolveRasterExtension(byte[] bytes, string? sourceFormat, string? rawFilename)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return ".bmp";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
        {
            return ".png";
        }

        string? ext = Path.GetExtension(rawFilename)?.ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
        {
            return ext == ".jpeg" ? ".jpg" : ext;
        }

        return (sourceFormat ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "JPEG" or "JPG" => ".jpg",
            "BMP" => ".bmp",
            _ => ".png",
        };
    }

    private static string SanitizeAssetFilename(string? rawName, string extension)
    {
        string normalizedExt = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        string baseName = Path.GetFileNameWithoutExtension(rawName ?? string.Empty).Trim();
        string cleaned = BuildAssetStemSanitizerRegex().Replace(baseName, "_").Trim('_', '.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "asset";
        }

        if (cleaned.Length > 48)
        {
            cleaned = cleaned[..48].TrimEnd('_', '.');
        }

        return $"{cleaned}{normalizedExt}";
    }

    private static List<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        return tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static string NormalizeDimensionMode(string? mode)
        => string.Equals(mode, "internal_usable", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(mode, "InternalUsable", StringComparison.OrdinalIgnoreCase)
            ? "internal_usable"
            : "external";

    private static ProjectViewerState CloneViewerState(ProjectViewerState? source)
    {
        if (source is null)
        {
            return new ProjectViewerState();
        }

        return new ProjectViewerState
        {
            Exploded = source.Exploded,
            LidAngle = source.LidAngle,
            ShowDimensions = source.ShowDimensions,
            ActiveSurface = source.ActiveSurface,
            DimensionMode = source.DimensionMode,
            CameraYawDeg = source.CameraYawDeg,
            CameraPitchDeg = source.CameraPitchDeg,
            CameraZoom = source.CameraZoom,
            CameraPanX = source.CameraPanX,
            CameraPanY = source.CameraPanY,
            CenterViewMode = source.CenterViewMode,
            SplitOrientation = source.SplitOrientation,
            SplitViewportRatio = source.SplitViewportRatio,
            LaserBedPreset = source.LaserBedPreset,
            LaserBedWidthMm = source.LaserBedWidthMm,
            LaserBedHeightMm = source.LaserBedHeightMm,
            KerfSlotCount = source.KerfSlotCount,
            KerfStepMm = source.KerfStepMm,
        };
    }

    private static BoxParameters ResolveBoxParameters(CmakerModelState modelState, CmakerMetadata metadata)
    {
        ProjectBoxParameters raw = modelState.BoxParameters;
        double w = raw.BoxWidth ?? (metadata.ExternalDimensionsMm.Width > 0 ? metadata.ExternalDimensionsMm.Width : BoxParameters.DefaultWidth);
        double d = raw.BoxDepth ?? (metadata.ExternalDimensionsMm.Depth > 0 ? metadata.ExternalDimensionsMm.Depth : BoxParameters.DefaultDepth);
        double h = raw.BoxHeight ?? (metadata.ExternalDimensionsMm.Height > 0 ? metadata.ExternalDimensionsMm.Height : BoxParameters.DefaultHeight);
        double t = raw.BoxThickness ?? (metadata.ThicknessMm > 0 ? metadata.ThicknessMm : BoxParameters.DefaultThickness);
        double f = raw.Folga ?? metadata.FolgaMm;

        if (raw.ExtensionData is not null)
        {
            BoxParameters fromExt = raw.ToBoxParameters();
            if (!raw.BoxWidth.HasValue) w = fromExt.BoxWidth;
            if (!raw.BoxDepth.HasValue) d = fromExt.BoxDepth;
            if (!raw.BoxHeight.HasValue) h = fromExt.BoxHeight;
            if (!raw.BoxThickness.HasValue) t = fromExt.BoxThickness;
            if (!raw.Folga.HasValue) f = fromExt.Folga;
        }

        return new BoxParameters
        {
            BoxWidth = Math.Clamp(w, BoxParameters.MinWidth, BoxParameters.MaxWidth),
            BoxDepth = Math.Clamp(d, BoxParameters.MinDepth, BoxParameters.MaxDepth),
            BoxHeight = Math.Clamp(h, BoxParameters.MinHeight, BoxParameters.MaxHeight),
            BoxThickness = Math.Clamp(t, BoxParameters.MinThickness, BoxParameters.MaxThickness),
            Folga = Math.Clamp(f, BoxParameters.MinFolga, BoxParameters.MaxFolga),
        };
    }

    private static void ApplyManufacturingStateToViewerState(
        ProjectViewerState viewerState,
        CmakerManufacturingState? mfg)
    {
        if (mfg is null)
        {
            return;
        }

        viewerState.LaserBedPreset ??= mfg.LaserBedPreset;
        viewerState.LaserBedWidthMm ??= mfg.LaserBedWidthMm;
        viewerState.LaserBedHeightMm ??= mfg.LaserBedHeightMm;
        viewerState.KerfSlotCount ??= mfg.KerfSlotCount;
        viewerState.KerfStepMm ??= mfg.KerfStepMm;
    }

    internal static (int Major, int Minor, int Patch) ParseSemVerTuple(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return (2, 0, 0);
        }

        string[] parts = rawVersion.Trim().Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        int major = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : 2;
        int minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 0;
        return (major, minor, patch);
    }
}
