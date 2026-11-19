using CaixaMaker.Core.Storage;
using CaixaMaker.Tests.Support;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Storage;

public sealed class OutputCleanupServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public void CleanupStaleOutputs_RemovesExpiredOutputsAndPreservesRecentAndJsonFiles()
    {
        using TempDirectoryScope temp = new();
        string outputsDir = temp.GetPath("outputs");
        Directory.CreateDirectory(outputsDir);

        DateTimeOffset fixedNow = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        FixedTimeProvider timeProvider = new(fixedNow);

        string staleSvg = Path.Combine(outputsDir, "stale.svg");
        string staleDxf = Path.Combine(outputsDir, "stale.dxf");
        string staleTmp = Path.Combine(outputsDir, "stale.tmp");
        string recentPdf = Path.Combine(outputsDir, "recent.pdf");
        string sessionZip = Path.Combine(outputsDir, "session.zip");
        string projectJson = Path.Combine(outputsDir, "proj_keep.json");

        File.WriteAllText(staleSvg, "<svg/>");
        File.WriteAllText(staleDxf, "0\nSECTION");
        File.WriteAllText(staleTmp, "tmp");
        File.WriteAllText(recentPdf, "%PDF-1.4");
        File.WriteAllText(sessionZip, "PK");
        File.WriteAllText(projectJson, "{}");

        DateTime expiredTime = fixedNow.UtcDateTime.AddHours(-25);
        DateTime freshTime = fixedNow.UtcDateTime.AddHours(-2);

        File.SetLastWriteTimeUtc(staleSvg, expiredTime);
        File.SetLastWriteTimeUtc(staleDxf, expiredTime);
        File.SetLastWriteTimeUtc(staleTmp, expiredTime);
        File.SetLastWriteTimeUtc(recentPdf, freshTime);
        File.SetLastWriteTimeUtc(sessionZip, fixedNow.UtcDateTime);
        File.SetLastWriteTimeUtc(projectJson, expiredTime);

        int removed = OutputCleanupService.CleanupStaleOutputs(
            outputsDir,
            sessionFiles: [sessionZip],
            timeProvider: timeProvider);

        removed.Should().Be(4);
        File.Exists(staleSvg).Should().BeFalse();
        File.Exists(staleDxf).Should().BeFalse();
        File.Exists(staleTmp).Should().BeFalse();
        File.Exists(sessionZip).Should().BeFalse();
        File.Exists(recentPdf).Should().BeTrue();
        File.Exists(projectJson).Should().BeTrue();
    }

    [Fact]
    public void CleanupStaleOutputs_WithZeroRetention_NeverDeletesJsonProjects()
    {
        using TempDirectoryScope temp = new();
        string projectsDir = temp.GetPath("projects");
        Directory.CreateDirectory(projectsDir);

        string projectFile = Path.Combine(projectsDir, "proj_desktop01.json");
        File.WriteAllText(projectFile, """{"id":"proj_desktop01"}""");

        int removed = OutputCleanupService.CleanupStaleOutputs(
            projectsDir,
            maxAge: TimeSpan.Zero);

        removed.Should().Be(0);
        File.Exists(projectFile).Should().BeTrue();
    }

    [Fact]
    public void CleanupStaleOutputs_WhenDirectoryDoesNotExist_ReturnsZero()
    {
        using TempDirectoryScope temp = new();
        string missingDir = temp.GetPath("missing_outputs");

        int removed = OutputCleanupService.CleanupStaleOutputs(missingDir);

        removed.Should().Be(0);
    }
}
