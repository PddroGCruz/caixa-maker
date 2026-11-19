namespace CaixaMaker.Core.Storage;

/// <summary>
/// Serviço de limpeza automática de artefatos temporários em outputs/,
/// preservando integralmente arquivos de projetos (.json) e arquivos recentes dentro da janela de retenção.
/// Equivalente a cleanup_stale_outputs em backend/app_paths.py.
/// </summary>
public static class OutputCleanupService
{
    private static readonly HashSet<string> CleanableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg",
        ".dxf",
        ".pdf",
        ".zip",
        ".tmp",
    };

    public static int CleanupStaleOutputs(
        string outputsDir,
        IEnumerable<string>? sessionFiles = null,
        TimeSpan? maxAge = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(outputsDir) || !Directory.Exists(outputsDir))
        {
            return 0;
        }

        TimeSpan effectiveMaxAge = maxAge ?? AppPaths.DefaultMaxStaleOutputAge;
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        HashSet<string> explicitSessionPaths = new(StringComparer.OrdinalIgnoreCase);
        if (sessionFiles is not null)
        {
            foreach (string filePath in sessionFiles)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    continue;
                }

                try
                {
                    explicitSessionPaths.Add(Path.GetFullPath(filePath));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignora caminhos inválidos na lista explícita de sessão.
                }
            }
        }

        int removed = 0;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(outputsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (string entryPath in entries)
        {
            string ext = Path.GetExtension(entryPath);
            if (!CleanableExtensions.Contains(ext))
            {
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(entryPath);
                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
                TimeSpan age = now.UtcDateTime - lastWriteUtc;

                bool isExplicitSessionFile = explicitSessionPaths.Contains(fullPath);
                bool isExpired = effectiveMaxAge <= TimeSpan.Zero
                    ? age >= effectiveMaxAge
                    : age > effectiveMaxAge;

                if (isExplicitSessionFile || isExpired)
                {
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        removed++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mantém mesma tolerância do backend Python: ignora arquivos bloqueados em uso.
            }
        }

        return removed;
    }
}
