namespace CaixaMaker.Tests.Support;

internal sealed class TempDirectoryScope : IDisposable
{
    public string RootPath { get; }

    public TempDirectoryScope()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "CaixaMakerTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string GetPath(params string[] segments)
    {
        string[] all = new string[segments.Length + 1];
        all[0] = RootPath;
        Array.Copy(segments, 0, all, 1, segments.Length);
        return Path.Combine(all);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
            // Melhor esforço para limpeza de diretório temporário de teste.
        }
    }
}
