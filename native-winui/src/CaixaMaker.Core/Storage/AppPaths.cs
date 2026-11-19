using System.Text.RegularExpressions;

namespace CaixaMaker.Core.Storage;

public enum AppRuntimeMode
{
    Desktop,
    Development,
}

public sealed class AppPathsOptions
{
    public bool? DesktopMode { get; init; }
    public string? ProjectsDir { get; init; }
    public string? OutputsDir { get; init; }
    public string? ConfigDir { get; init; }
    public string? LogsDir { get; init; }
    public string? TempDir { get; init; }
    public string? RoamingRoot { get; init; }
    public string? LocalRoot { get; init; }
    public string? RepoRoot { get; init; }
    public IEnumerable<string>? LegacyProjectsDirs { get; init; }
    public Func<string, string?>? EnvironmentReader { get; init; }
}

/// <summary>
/// Resolve e valida os diretórios oficiais de armazenamento e nomes de arquivo seguros
/// com paridade integral em relação a backend/app_paths.py e backend/server.py.
/// </summary>
public sealed partial class AppPaths
{
    public const string AppFolderName = "CaixaMaker";
    public const int DefaultMaxStaleOutputAgeSeconds = 24 * 60 * 60;
    public static readonly TimeSpan DefaultMaxStaleOutputAge =
        TimeSpan.FromSeconds(DefaultMaxStaleOutputAgeSeconds);

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\.(svg|dxf|pdf|zip)$", RegexOptions.Compiled)]
    private static partial Regex BuildSafeFilenameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{4,64}$", RegexOptions.Compiled)]
    private static partial Regex BuildSafeProjectIdRegex();

    /// <summary>
    /// Expressão regular equivalente a SAFE_FILENAME_RE em backend/server.py.
    /// </summary>
    public static Regex SAFE_FILENAME_RE => BuildSafeFilenameRegex();

    /// <summary>
    /// Expressão regular equivalente a SAFE_PROJECT_ID_RE em backend/project_store.py.
    /// </summary>
    public static Regex SAFE_PROJECT_ID_RE => BuildSafeProjectIdRegex();

    public AppRuntimeMode Mode { get; }
    public string ModeName => Mode == AppRuntimeMode.Desktop ? "desktop" : "development";
    public string ProjectsDir { get; }
    public string OutputsDir { get; }
    public string ConfigDir { get; }
    public string LogsDir { get; }
    public string TempDir { get; }
    public string LegacyMigrationMarkerPath => Path.Combine(ConfigDir, "legacy_migration.json");
    public string NativeLogFilePath => Path.Combine(LogsDir, "desktop-native.log");
    public IReadOnlyList<string> LegacyProjectsDirs { get; }

    public AppPaths(AppPathsOptions? options = null)
    {
        Func<string, string?> readEnv = options?.EnvironmentReader ?? Environment.GetEnvironmentVariable;
        bool desktop = options?.DesktopMode ?? IsDesktopRuntime(readEnv);
        Mode = desktop ? AppRuntimeMode.Desktop : AppRuntimeMode.Development;

        string repoRoot = ResolveRepoRoot(options?.RepoRoot);
        string roamingRoot = ResolveRoamingRoot(options?.RoamingRoot, readEnv);
        string localRoot = ResolveLocalRoot(options?.LocalRoot, readEnv);

        string? envProjects = NormalizeEnv(readEnv("CAIXA_MAKER_PROJECTS_DIR"));
        string? envOutputs = NormalizeEnv(readEnv("CAIXA_MAKER_OUTPUTS_DIR"));
        string? envConfig = NormalizeEnv(readEnv("CAIXA_MAKER_CONFIG_DIR"));
        string? envLogs = NormalizeEnv(readEnv("CAIXA_MAKER_LOGS_DIR"));
        string? envTemp = NormalizeEnv(readEnv("CAIXA_MAKER_TEMP_DIR"));
        string? envLegacy = NormalizeEnv(readEnv("CAIXA_MAKER_LEGACY_PROJECTS_DIR"));

        if (!string.IsNullOrWhiteSpace(options?.ProjectsDir))
        {
            ProjectsDir = Path.GetFullPath(options.ProjectsDir);
        }
        else if (envProjects is not null)
        {
            ProjectsDir = Path.GetFullPath(envProjects);
        }
        else if (desktop)
        {
            // %APPDATA%\CaixaMaker\projects preserva dados autorais entre atualizações e desinstalações
            ProjectsDir = Path.Combine(roamingRoot, AppFolderName, "projects");
        }
        else
        {
            ProjectsDir = Path.Combine(repoRoot, "projects");
        }

        if (!string.IsNullOrWhiteSpace(options?.OutputsDir))
        {
            OutputsDir = Path.GetFullPath(options.OutputsDir);
        }
        else if (envOutputs is not null)
        {
            OutputsDir = Path.GetFullPath(envOutputs);
        }
        else if (desktop)
        {
            // %LOCALAPPDATA%\CaixaMaker\outputs armazena previews e exportações temporárias locais
            OutputsDir = Path.Combine(localRoot, AppFolderName, "outputs");
        }
        else
        {
            OutputsDir = Path.Combine(repoRoot, "outputs");
        }

        if (!string.IsNullOrWhiteSpace(options?.ConfigDir))
        {
            ConfigDir = Path.GetFullPath(options.ConfigDir);
        }
        else if (envConfig is not null)
        {
            ConfigDir = Path.GetFullPath(envConfig);
        }
        else if (desktop)
        {
            ConfigDir = Path.Combine(roamingRoot, AppFolderName, "config");
        }
        else
        {
            string projectsParent = Directory.GetParent(ProjectsDir)?.FullName ?? repoRoot;
            ConfigDir = Path.Combine(projectsParent, ".config");
        }

        if (!string.IsNullOrWhiteSpace(options?.LogsDir))
        {
            LogsDir = Path.GetFullPath(options.LogsDir);
        }
        else if (envLogs is not null)
        {
            LogsDir = Path.GetFullPath(envLogs);
        }
        else if (desktop)
        {
            LogsDir = Path.Combine(localRoot, AppFolderName, "logs");
        }
        else
        {
            string outputsParent = Directory.GetParent(OutputsDir)?.FullName ?? repoRoot;
            LogsDir = Path.Combine(outputsParent, ".logs");
        }

        if (!string.IsNullOrWhiteSpace(options?.TempDir))
        {
            TempDir = Path.GetFullPath(options.TempDir);
        }
        else if (envTemp is not null)
        {
            TempDir = Path.GetFullPath(envTemp);
        }
        else if (desktop)
        {
            TempDir = Path.Combine(localRoot, AppFolderName, "temp");
        }
        else
        {
            TempDir = Path.Combine(OutputsDir, "temp");
        }

        List<string> legacyCandidates = [];
        if (desktop)
        {
            if (envLegacy is not null)
            {
                legacyCandidates.Add(envLegacy);
            }

            if (options?.LegacyProjectsDirs is not null)
            {
                foreach (string candidate in options.LegacyProjectsDirs)
                {
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        legacyCandidates.Add(candidate);
                    }
                }
            }
            else
            {
                string exeProjects = Path.Combine(AppContext.BaseDirectory, "projects");
                string cwdProjects = Path.Combine(Environment.CurrentDirectory, "projects");
                string repoProjects = Path.Combine(repoRoot, "projects");

                legacyCandidates.Add(exeProjects);
                legacyCandidates.Add(cwdProjects);
                if (!string.IsNullOrWhiteSpace(repoRoot))
                {
                    legacyCandidates.Add(repoProjects);
                }
            }
        }

        LegacyProjectsDirs = DeduplicatePathsExcludingTarget(legacyCandidates, ProjectsDir);
    }

    public static AppPaths Resolve(AppPathsOptions? options = null) => new(options);

    public static bool IsDesktopRuntime(Func<string, string?>? environmentReader = null)
    {
        Func<string, string?> readEnv = environmentReader ?? Environment.GetEnvironmentVariable;
        string? raw = NormalizeEnv(readEnv("CAIXA_MAKER_DESKTOP_MODE"));
        if (string.Equals(raw, "0", StringComparison.Ordinal))
        {
            return false;
        }

        // Em execução nativa Windows WinUI 3, o modo padrão é Desktop salvo indicação explícita "0".
        return true;
    }

    public void EnsureDirs()
    {
        Directory.CreateDirectory(ProjectsDir);
        Directory.CreateDirectory(OutputsDir);
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(TempDir);
    }

    /// <summary>
    /// Valida nome de arquivo exportado (.svg, .dxf, .pdf, .zip) contra path traversal e caracteres inválidos.
    /// </summary>
    public static bool IsSafeOutputFilename(string? filename)
    {
        if (!HasSafeSingleSegmentChars(filename))
        {
            return false;
        }

        return SAFE_FILENAME_RE.IsMatch(filename!);
    }

    /// <summary>
    /// Valida identificador de projeto contra path traversal e formato seguro.
    /// </summary>
    public static bool IsSafeProjectId(string? projectId)
    {
        if (!HasSafeSingleSegmentChars(projectId))
        {
            return false;
        }

        return SAFE_PROJECT_ID_RE.IsMatch(projectId!);
    }

    /// <summary>
    /// Valida qualquer nome simples de arquivo sem diretórios, segmentos relativos ("..") ou caracteres nulos/inválidos.
    /// </summary>
    public static bool IsSafeRelativeFileName(string? filename)
    {
        if (!HasSafeSingleSegmentChars(filename))
        {
            return false;
        }

        string candidate = filename!;
        if (candidate is "." or "..")
        {
            return false;
        }

        return Path.GetFileName(candidate) == candidate && !Path.IsPathRooted(candidate);
    }

    public string ResolveSafeOutputPath(string filename)
    {
        if (!IsSafeOutputFilename(filename))
        {
            throw new ArgumentException(
                $"Nome de arquivo de saída inválido ou inseguro: '{filename}'.",
                nameof(filename));
        }

        return EnsureContainment(OutputsDir, filename, nameof(filename));
    }

    public string ResolveSafeProjectPath(string projectId)
    {
        if (!IsSafeProjectId(projectId))
        {
            throw new ArgumentException(
                $"Identificador de projeto inválido ou inseguro: '{projectId}'.",
                nameof(projectId));
        }

        return EnsureContainment(ProjectsDir, $"{projectId}.json", nameof(projectId));
    }

    public static string ResolveSafeChildPath(string baseDirectory, string filename)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Diretório base inválido.", nameof(baseDirectory));
        }

        if (!IsSafeRelativeFileName(filename))
        {
            throw new ArgumentException(
                $"Nome de arquivo inválido ou tentativa de path traversal detectada: '{filename}'.",
                nameof(filename));
        }

        return EnsureContainment(baseDirectory, filename, nameof(filename));
    }

    private static string EnsureContainment(string baseDirectory, string filename, string paramName)
    {
        string normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        string candidate = Path.GetFullPath(Path.Combine(normalizedBase, filename));
        string? parent = Path.GetDirectoryName(candidate);

        if (parent is null ||
            !string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                normalizedBase,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Caminho fora do diretório autorizado: '{filename}'.",
                paramName);
        }

        return candidate;
    }

    private static bool HasSafeSingleSegmentChars(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains('\0', StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.StartsWith('.'))
        {
            return false;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in value)
        {
            if (char.IsControl(c) || Array.IndexOf(invalidChars, c) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeEnv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string ResolveRoamingRoot(string? overrideRoot, Func<string, string?> readEnv)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

        string? appData = NormalizeEnv(readEnv("APPDATA"));
        if (appData is not null)
        {
            return Path.GetFullPath(appData);
        }

        string folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            return Path.GetFullPath(folder);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "Roaming");
    }

    private static string ResolveLocalRoot(string? overrideRoot, Func<string, string?> readEnv)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

        string? localAppData = NormalizeEnv(readEnv("LOCALAPPDATA"));
        if (localAppData is not null)
        {
            return Path.GetFullPath(localAppData);
        }

        string folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            return Path.GetFullPath(folder);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "Local");
    }

    private static string ResolveRepoRoot(string? overrideRepoRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideRepoRoot))
        {
            return Path.GetFullPath(overrideRepoRoot);
        }

        string? versionFile = AppVersion.ResolveVersionFile();
        if (versionFile is not null)
        {
            string? root = Path.GetDirectoryName(versionFile);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.GetFullPath(root);
            }
        }

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static IReadOnlyList<string> DeduplicatePathsExcludingTarget(
        IEnumerable<string> candidates,
        string targetProjectsDir)
    {
        string normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetProjectsDir));
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in candidates)
        {
            string normalized;
            try
            {
                normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (string.Equals(normalized, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
