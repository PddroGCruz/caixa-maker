using CaixaMaker.Core.Logging;
using CaixaMaker.Tests.Support;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Logging;

public sealed class RotatingFileLoggerTests
{
    [Fact]
    public void RotatingFileLogger_RotatesLogsWhenMaxBytesExceeded()
    {
        using TempDirectoryScope temp = new();
        string logPath = temp.GetPath("logs", "desktop-native.log");

        using (RotatingFileLogger logger = new(logPath, maxFileBytes: 200, backupCount: 2))
        {
            for (int i = 0; i < 12; i++)
            {
                logger.Info("Test", $"Entrada de log rotativo numero {i:D2} com conteudo suficiente para rotação.");
            }
        }

        File.Exists(logPath).Should().BeTrue();
        File.Exists($"{logPath}.1").Should().BeTrue();
        File.Exists($"{logPath}.2").Should().BeTrue();
        File.Exists($"{logPath}.3").Should().BeFalse();
    }
}
