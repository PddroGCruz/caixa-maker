using System.Globalization;
using System.Text;

namespace CaixaMaker.Core.Logging;

/// <summary>
/// Logger rotativo em arquivo UTF-8 equivalente ao RotatingFileHandler de backend/sidecar_entry.py
/// (limite padrão de 2 MB por arquivo e até 3 backups rotacionados).
/// </summary>
public sealed class RotatingFileLogger : IDisposable
{
    public const long DefaultMaxFileBytes = 2 * 1024 * 1024;
    public const int DefaultBackupCount = 3;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _sync = new();
    private readonly long _maxFileBytes;
    private readonly int _backupCount;
    private StreamWriter? _writer;
    private long _currentSize;
    private bool _disposed;

    public string LogFilePath { get; }

    public RotatingFileLogger(
        string logFilePath,
        long maxFileBytes = DefaultMaxFileBytes,
        int backupCount = DefaultBackupCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        if (maxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "Limite de tamanho deve ser positivo.");
        }

        if (backupCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(backupCount), "Quantidade de backups não pode ser negativa.");
        }

        LogFilePath = Path.GetFullPath(logFilePath);
        _maxFileBytes = maxFileBytes;
        _backupCount = backupCount;

        string? directory = Path.GetDirectoryName(LogFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        OpenWriter();
    }

    public void Info(string category, string message) => WriteEntry("INFO", category, message);

    public void Warn(string category, string message) => WriteEntry("WARN", category, message);

    public void Error(string category, string message, Exception? exception = null)
    {
        string detail = exception is null
            ? message
            : $"{message} | {exception.GetType().Name}: {exception.Message}";
        WriteEntry("ERROR", category, detail);
    }

    public void WriteEntry(string level, string category, string message)
    {
        string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string line = $"{timestamp} [{level}] {category}: {message}";
        byte[] encoded = Utf8NoBom.GetBytes(line + Environment.NewLine);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_writer is null)
            {
                OpenWriter();
            }

            if (_currentSize + encoded.Length > _maxFileBytes && _currentSize > 0)
            {
                RotateLocked();
            }

            _writer!.WriteLine(line);
            _writer.Flush();
            _currentSize += encoded.Length;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void OpenWriter()
    {
        FileStream stream = new(
            LogFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        _currentSize = stream.Length;
        _writer = new StreamWriter(stream, Utf8NoBom)
        {
            AutoFlush = false,
        };
    }

    private void RotateLocked()
    {
        _writer?.Dispose();
        _writer = null;

        if (_backupCount > 0)
        {
            for (int index = _backupCount - 1; index >= 1; index--)
            {
                string source = $"{LogFilePath}.{index}";
                string target = $"{LogFilePath}.{index + 1}";
                if (File.Exists(source))
                {
                    File.Move(source, target, overwrite: true);
                }
            }

            string firstBackup = $"{LogFilePath}.1";
            if (File.Exists(LogFilePath))
            {
                File.Move(LogFilePath, firstBackup, overwrite: true);
            }
        }
        else if (File.Exists(LogFilePath))
        {
            File.Delete(LogFilePath);
        }

        OpenWriter();
    }
}
