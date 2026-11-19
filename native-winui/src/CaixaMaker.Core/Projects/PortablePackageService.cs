using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;

namespace CaixaMaker.Core.Projects;

/// <summary>
/// Exporta e importa pacotes portáteis <c>.caixamaker.json</c> (v1.2.0 schema_version = 1).
/// Porta integralmente as rotas <c>/export-bundle</c> e <c>/import-bundle</c> de <c>backend/personalization_api.py</c>.
/// </summary>
public sealed partial class PortablePackageService
{
    private readonly BoxModelRegistry _registry;

    [GeneratedRegex(@"[^A-Za-z0-9_-]+", RegexOptions.Compiled)]
    private static partial Regex BuildSlugSanitizerRegex();

    public PortablePackageService(BoxModelRegistry? registry = null)
    {
        _registry = registry ?? BoxModelRegistry.Default;
    }

    public static string BuildSlug(string? projectName, string fallbackDisplayName)
    {
        string cleanName = string.IsNullOrWhiteSpace(projectName)
            ? fallbackDisplayName
            : projectName.Trim();

        string slug = BuildSlugSanitizerRegex()
            .Replace(cleanName, "_")
            .Trim('_');

        return string.IsNullOrEmpty(slug) ? "projeto_caixa_maker" : slug;
    }

    public static string BuildBundleFilename(string? projectName, string fallbackDisplayName)
        => $"{BuildSlug(projectName, fallbackDisplayName)}.caixamaker.json";

    public PortablePackageExportResult ExportBundle(ProjectSaveRequest request, string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        IBoxModelDefinition boxModel = _registry.Get(request.ModelId);
        PersonalizationSnapshot snapshot = ProjectRepository.BuildSnapshotForSave(request, boxModel, _registry);

        string cleanName = string.IsNullOrWhiteSpace(request.Name)
            ? boxModel.DisplayName
            : request.Name.Trim();

        string filename = BuildBundleFilename(cleanName, boxModel.DisplayName);
        var bundle = new PortableProjectBundle
        {
            Format = PortableProjectBundle.CanonicalFormat,
            SchemaVersion = PortableProjectBundle.CurrentSchemaVersion,
            AppVersion = string.IsNullOrWhiteSpace(appVersion) ? Storage.AppVersion.Version : appVersion,
            ExportedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            Name = cleanName,
            ModelId = boxModel.ModelId,
            ModelName = boxModel.DisplayName,
            BoxParameters = ProjectBoxParameters.FromBoxParameters(request.BoxParameters),
            ViewerState = request.ViewerState ?? new ProjectViewerState(),
            SafetyMarginMm = request.SafetyMarginMm,
            Personalization = snapshot,
        };

        string json = JsonSerializer.Serialize(bundle, PersonalizationJsonOptions.Indented);
        return new PortablePackageExportResult
        {
            Filename = filename,
            Bundle = bundle,
            Json = json,
        };
    }

    public void ExportBundleToFile(ProjectSaveRequest request, string destinationPath, string? appVersion = null)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("O caminho de destino não pode ser vazio.", nameof(destinationPath));
        }

        PortablePackageExportResult exported = ExportBundle(request, appVersion);
        string fullPath = Path.GetFullPath(destinationPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{fullPath}.tmp";
        File.WriteAllText(tempPath, exported.Json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, fullPath, overwrite: true);
    }

    public LoadedProjectSession ImportBundleJson(
        string bundleJson,
        ProjectRepository? repository = null,
        string? sourceFilename = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleJson);

        using JsonDocument doc = JsonDocument.Parse(bundleJson);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Arquivo de projeto inválido: raiz JSON deve ser um objeto.");
        }

        if (root.TryGetProperty("bundle", out JsonElement nestedBundle) &&
            nestedBundle.ValueKind == JsonValueKind.Object)
        {
            root = nestedBundle;
        }

        ValidateFormatAndSchemaIfPresent(root);

        JsonElement persElement = default;
        bool hasPers = TryGetObjectProperty(root, "personalization", out persElement)
            || TryGetObjectProperty(root, "personalization_snapshot", out persElement);

        string modelId = ReadStringProperty(root, "model_id")
            ?? (hasPers ? ReadStringProperty(persElement, "model_id") : null)
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidDataException(
                "Arquivo de projeto não contém identificador de modelo ('model_id').");
        }

        IBoxModelDefinition boxModel = _registry.Get(modelId.Trim());

        BoxParameters boxParams = ParseBoxParameters(root, hasPers ? persElement : null);
        // Valida as dimensões nominais usando BoxCalculator do próprio modelo.
        BoxCalculator calc = boxModel.CreateCalculator(boxParams);

        double safetyMarginMm = ReadDoubleProperty(root, "safety_margin_mm")
            ?? (hasPers ? ReadDoubleProperty(persElement, "safety_margin_mm") : null)
            ?? GeometryValidator.DefaultSafetyMarginMm;

        PersonalizationSnapshot snapshot = hasPers
            ? (JsonSerializer.Deserialize<PersonalizationSnapshot>(
                   persElement.GetRawText(),
                   PersonalizationJsonOptions.Default) ?? new PersonalizationSnapshot())
            : new PersonalizationSnapshot
            {
                ModelId = boxModel.ModelId,
                BoxParameters = PersonalizationBoxParametersSnapshot.FromCalculator(calc),
                SafetyMarginMm = safetyMarginMm,
            };

        if (string.IsNullOrWhiteSpace(snapshot.ModelId))
        {
            snapshot.ModelId = boxModel.ModelId;
        }

        SurfaceManager manager = SurfaceManager.FromSnapshot(snapshot, _registry, boxModel);
        manager.UpdateBoxCalculator(calc, boxModel: boxModel, safetyMarginMm: safetyMarginMm);

        string fallbackStem = string.Empty;
        if (!string.IsNullOrWhiteSpace(sourceFilename))
        {
            fallbackStem = Path.GetFileNameWithoutExtension(sourceFilename)
                .Replace(".caixamaker", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        string projectName = ReadStringProperty(root, "name")?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(projectName))
        {
            projectName = !string.IsNullOrWhiteSpace(fallbackStem) ? fallbackStem : boxModel.DisplayName;
        }

        ProjectViewerState viewerState = TryGetObjectProperty(root, "viewer_state", out JsonElement viewerEl)
            ? (JsonSerializer.Deserialize<ProjectViewerState>(viewerEl.GetRawText(), PersonalizationJsonOptions.Default)
               ?? new ProjectViewerState())
            : new ProjectViewerState();

        PersonalizationSnapshot normalizedSnapshot = manager.CreateSnapshot(includeRawBase64: true);

        if (repository is not null)
        {
            ProjectRecord saved = repository.SaveProject(
                name: projectName,
                modelId: boxModel.ModelId,
                modelName: boxModel.DisplayName,
                boxParameters: boxParams,
                viewerState: viewerState,
                personalizationSnapshot: normalizedSnapshot);

            return new LoadedProjectSession
            {
                Record = saved,
                Summary = ProjectRepository.ToSummary(saved),
                BoxParameters = boxParams,
                ViewerState = viewerState,
                Manager = manager,
            };
        }

        string ephemeralId = $"proj_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}";
        string now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var transientRecord = new ProjectRecord
        {
            SchemaVersion = 1,
            Id = ephemeralId,
            Name = projectName,
            CreatedAt = now,
            UpdatedAt = now,
            ModelId = boxModel.ModelId,
            ModelName = boxModel.DisplayName,
            BoxParameters = ProjectBoxParameters.FromBoxParameters(boxParams),
            ViewerState = viewerState,
            Personalization = normalizedSnapshot,
        };

        return new LoadedProjectSession
        {
            Record = transientRecord,
            Summary = ProjectRepository.ToSummary(transientRecord),
            BoxParameters = boxParams,
            ViewerState = viewerState,
            Manager = manager,
        };
    }

    public LoadedProjectSession ImportBundleFile(
        string sourcePath,
        ProjectRepository? repository = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("O caminho do pacote .caixamaker.json não pode ser vazio.", nameof(sourcePath));
        }

        string fullPath = Path.GetFullPath(sourcePath);
        string rawJson = File.ReadAllText(fullPath, Encoding.UTF8);
        return ImportBundleJson(rawJson, repository, Path.GetFileName(fullPath));
    }

    private static void ValidateFormatAndSchemaIfPresent(JsonElement root)
    {
        if (root.TryGetProperty("format", out JsonElement formatEl) && formatEl.ValueKind == JsonValueKind.String)
        {
            string? format = formatEl.GetString();
            if (!string.IsNullOrWhiteSpace(format) &&
                !string.Equals(format, PortableProjectBundle.CanonicalFormat, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Formato de pacote não suportado: '{format}'.");
            }
        }

        if (root.TryGetProperty("schema_version", out JsonElement schemaEl) &&
            schemaEl.ValueKind == JsonValueKind.Number &&
            schemaEl.TryGetInt32(out int schemaVersion))
        {
            if (schemaVersion != PortableProjectBundle.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Versão de schema não suportada: {schemaVersion}. Esperado: {PortableProjectBundle.CurrentSchemaVersion}.");
            }
        }
    }

    private static BoxParameters ParseBoxParameters(JsonElement root, JsonElement? persElement)
    {
        JsonElement bpEl = default;
        bool hasBp = TryGetObjectProperty(root, "box_parameters", out bpEl)
            || (persElement.HasValue && TryGetObjectProperty(persElement.Value, "box_parameters", out bpEl));

        if (!hasBp)
        {
            return BoxParameters.Default;
        }

        double thickness = ReadDoubleProperty(bpEl, "box_thickness")
            ?? ReadDoubleProperty(bpEl, "thickness_mm")
            ?? BoxParameters.DefaultThickness;
        double width = ReadDoubleProperty(bpEl, "box_width")
            ?? ReadDoubleProperty(bpEl, "width_mm")
            ?? BoxParameters.DefaultWidth;
        double depth = ReadDoubleProperty(bpEl, "box_depth")
            ?? ReadDoubleProperty(bpEl, "depth_mm")
            ?? BoxParameters.DefaultDepth;
        double height = ReadDoubleProperty(bpEl, "box_height")
            ?? ReadDoubleProperty(bpEl, "height_mm")
            ?? BoxParameters.DefaultHeight;
        double folga = ReadDoubleProperty(bpEl, "folga")
            ?? ReadDoubleProperty(bpEl, "folga_mm")
            ?? BoxParameters.DefaultFolga;

        return new BoxParameters
        {
            BoxThickness = thickness,
            BoxWidth = width,
            BoxDepth = depth,
            BoxHeight = height,
            Folga = folga,
        };
    }

    private static bool TryGetObjectProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadStringProperty(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static double? ReadDoubleProperty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d))
        {
            return d;
        }

        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed))
        {
            return parsed;
        }

        return null;
    }
}
