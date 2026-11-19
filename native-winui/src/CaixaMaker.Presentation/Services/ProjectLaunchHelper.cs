namespace CaixaMaker.App.Services;

/// <summary>
/// Resolve caminhos de projetos (.cmaker, .caixamaker.json, .json) recebidos via linha de comando
/// ou ativação externa do Windows (clique duplo / redirecionamento de instância única).
/// </summary>
public static class ProjectLaunchHelper
{
    public static bool IsSupportedProjectExtension(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string trimmed = filePath.Trim().Trim('"');
        return trimmed.EndsWith(".cmaker", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".caixamaker.json", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractProjectFilePath(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return null;
        }

        for (int i = 0; i < args.Count; i++)
        {
            string? raw = args[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string trimmed = raw.Trim().Trim('"');
            if (trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                if (string.Equals(trimmed, "--capture-ui-screenshots", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                }

                continue;
            }

            if (IsSupportedProjectExtension(trimmed))
            {
                return TryNormalizeFullPath(trimmed);
            }
        }

        return null;
    }

    public static string? ExtractProjectFilePathFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        List<string> tokens = TokenizeCommandLine(commandLine);
        if (tokens.Count == 0)
        {
            return null;
        }

        int startIndex = tokens[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || tokens[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

        if (startIndex >= tokens.Count)
        {
            return null;
        }

        return ExtractProjectFilePath(tokens.Skip(startIndex).ToArray());
    }

    private static List<string> TokenizeCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string TryNormalizeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
