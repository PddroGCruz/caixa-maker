using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaixaMaker.Presentation.Diagnostics;

public sealed record UiScreenshotSceneSpec(
    string SceneId,
    string FileName,
    string Description);

public sealed record UiScreenshotFrameStats(
    bool IsNonBlank,
    int MinLuminance,
    int MaxLuminance,
    double DistinctBucketRatio);

public sealed class UiScreenshotManifestItem
{
    [JsonPropertyName("scene_id")]
    public string SceneId { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("byte_size")]
    public long ByteSize { get; set; }

    [JsonPropertyName("is_non_blank")]
    public bool IsNonBlank { get; set; }

    [JsonPropertyName("capture_method")]
    public string CaptureMethod { get; set; } = string.Empty;

    [JsonPropertyName("luminance_min")]
    public int LuminanceMin { get; set; }

    [JsonPropertyName("luminance_max")]
    public int LuminanceMax { get; set; }
}

public sealed class UiScreenshotManifest
{
    public const int TargetWindowWidth = 1360;
    public const int TargetWindowHeight = 860;
    public const string ManifestFileName = "screenshots_manifest.json";

    public static IReadOnlyList<UiScreenshotSceneSpec> DefaultScenes { get; } =
    [
        new("catalogo-dark", "01-catalogo-dark.png", "Central de Trabalho / Catálogo no tema Escuro com projetos de exemplo e banner de rascunho."),
        new("modelagem-dark", "02-modelagem-dark.png", "Área de Modelagem (200x150x100 mm, MDF 3.0 mm, cotas 3D ativas) no tema Escuro."),
        new("personalizacao-split-dark", "03-personalizacao-split-dark.png", "Área de Personalização dividida (3D + 2D) com texto 'CAIXA MAKER' selecionado na tampa e gizmo ativo."),
        new("fabricacao-dark", "04-fabricacao-dark.png", "Área de Fabricação com chapas calculadas e diagnóstico pré-corte no tema Escuro."),
        new("modelagem-light", "05-modelagem-light.png", "Área de Modelagem no tema Claro técnico."),
        new("sobre-institucional-dark", "06-sobre-institucional-dark.png", "Modal institucional exibindo Caixa Maker, Laboratório IFMaker do Juruá e IFAC Campus Cruzeiro do Sul."),
    ];

    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; set; } = string.Empty;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("target_width")]
    public int TargetWidth { get; set; } = TargetWindowWidth;

    [JsonPropertyName("target_height")]
    public int TargetHeight { get; set; } = TargetWindowHeight;

    [JsonPropertyName("all_valid")]
    public bool AllValid { get; set; }

    [JsonPropertyName("screenshots")]
    public List<UiScreenshotManifestItem> Screenshots { get; set; } = [];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Avalia amostragem do buffer BGRA32 para detectar frames vazios, totalmente brancos ou totalmente pretos.
    /// </summary>
    public static UiScreenshotFrameStats EvaluateBgraFrameContent(ReadOnlySpan<byte> bgraPixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgraPixels.Length < width * height * 4)
        {
            return new UiScreenshotFrameStats(false, 0, 0, 0.0);
        }

        int stepX = Math.Max(1, width / 64);
        int stepY = Math.Max(1, height / 64);
        int minLum = 255;
        int maxLum = 0;
        var buckets = new HashSet<int>();
        int sampleCount = 0;

        for (int y = 0; y < height; y += stepY)
        {
            int rowOffset = y * width * 4;
            for (int x = 0; x < width; x += stepX)
            {
                int idx = rowOffset + x * 4;
                byte b = bgraPixels[idx];
                byte g = bgraPixels[idx + 1];
                byte r = bgraPixels[idx + 2];

                int lum = (r * 299 + g * 587 + b * 114) / 1000;
                if (lum < minLum) minLum = lum;
                if (lum > maxLum) maxLum = lum;

                int bucket = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                buckets.Add(bucket);
                sampleCount++;
            }
        }

        double bucketRatio = sampleCount > 0 ? (double)buckets.Count / sampleCount : 0.0;
        bool hasContrast = (maxLum - minLum) >= 18;
        bool hasVariety = buckets.Count >= 6;
        return new UiScreenshotFrameStats(hasContrast && hasVariety, minLum, maxLum, bucketRatio);
    }
}
