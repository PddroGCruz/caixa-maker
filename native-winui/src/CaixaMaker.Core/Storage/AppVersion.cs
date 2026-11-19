using System.Reflection;
using System.Text.RegularExpressions;

namespace CaixaMaker.Core.Storage;

/// <summary>
/// Centraliza a versão SemVer e os metadados institucionais do Caixa Maker,
/// mantendo paridade com backend/app_version.py e o arquivo VERSION na raiz.
/// </summary>
public static partial class AppVersion
{
    public const string ProductName = "Caixa Maker";
    public const string Company = "Laboratório IFMaker do Juruá — IFAC Campus Cruzeiro do Sul";
    public const string DeveloperName = "Pedro G. Cruz (PddroGCruz)";
    public const string DeveloperEmail = "ggcostcr@gmail.com";
    public const string DeveloperSignature = "Pedro G. Cruz (PddroGCruz • ggcostcr@gmail.com)";

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.Compiled)]
    private static partial Regex BuildSemVerRegex();

    public static Regex SemVerRegex => BuildSemVerRegex();

    public static string Version => GetAppVersion();

    public static string AssemblyVersion =>
        typeof(AppVersion).Assembly.GetName().Version?.ToString() ?? "2.4.0.0";

    public static string FileVersion =>
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
        ?? AssemblyVersion;

    public static bool IsValidSemVer(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        return SemVerRegex.IsMatch(version.Trim());
    }

    public static string ValidateSemVer(string? version)
    {
        string normalized = (version ?? string.Empty).Trim();
        if (!SemVerRegex.IsMatch(normalized))
        {
            throw new FormatException(
                $"Versão SemVer inválida: '{version}'. Esperado padrão estrito MAJOR.MINOR.PATCH.");
        }

        return normalized;
    }

    public static string GetAppVersion()
    {
        Assembly assembly = typeof(AppVersion).Assembly;
        string? rawInfo = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(rawInfo))
        {
            string baseVersion = rawInfo.Split('+', 2)[0].Trim();
            if (SemVerRegex.IsMatch(baseVersion))
            {
                return baseVersion;
            }
        }

        Version? asmVersion = assembly.GetName().Version;
        if (asmVersion is not null)
        {
            string threePart = $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";
            return ValidateSemVer(threePart);
        }

        throw new InvalidOperationException("Não foi possível determinar a versão compilada do Caixa Maker.");
    }

    /// <summary>
    /// Localiza o arquivo central VERSION subindo a árvore de diretórios a partir de startDirectory.
    /// Equivalente a resolve_version_file() em backend/app_version.py.
    /// </summary>
    public static string? ResolveVersionFile(string? startDirectory = null)
    {
        string current = Path.GetFullPath(
            string.IsNullOrWhiteSpace(startDirectory) ? AppContext.BaseDirectory : startDirectory);

        DirectoryInfo? dir = new(current);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "VERSION");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public static string ReadVersionFromFile(string versionFilePath)
    {
        if (!File.Exists(versionFilePath))
        {
            throw new FileNotFoundException(
                $"Arquivo central de versão não encontrado: '{versionFilePath}'.",
                versionFilePath);
        }

        string raw = File.ReadAllText(versionFilePath).Trim();
        return ValidateSemVer(raw);
    }
}
