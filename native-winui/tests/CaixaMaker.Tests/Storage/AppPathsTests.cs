using CaixaMaker.Core.Storage;
using CaixaMaker.Tests.Support;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Storage;

public sealed class AppPathsTests
{
    [Fact]
    public void Resolve_InDesktopModeViaEnvironment_SeparatesRoamingAndLocalSubfolders()
    {
        using TempDirectoryScope temp = new();
        string roaming = temp.GetPath("AppDataRoaming");
        string local = temp.GetPath("AppDataLocal");
        string legacyDir = temp.GetPath("LegacyProjects");

        Dictionary<string, string?> env = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CAIXA_MAKER_DESKTOP_MODE"] = "1",
            ["APPDATA"] = roaming,
            ["LOCALAPPDATA"] = local,
            ["CAIXA_MAKER_LEGACY_PROJECTS_DIR"] = legacyDir,
        };

        AppPaths paths = AppPaths.Resolve(new AppPathsOptions
        {
            RepoRoot = temp.RootPath,
            EnvironmentReader = key => env.TryGetValue(key, out string? val) ? val : null,
        });

        paths.Mode.Should().Be(AppRuntimeMode.Desktop);
        paths.ModeName.Should().Be("desktop");
        paths.ProjectsDir.Should().Be(Path.Combine(roaming, "CaixaMaker", "projects"));
        paths.ConfigDir.Should().Be(Path.Combine(roaming, "CaixaMaker", "config"));
        paths.LegacyMigrationMarkerPath.Should().Be(
            Path.Combine(roaming, "CaixaMaker", "config", "legacy_migration.json"));
        paths.OutputsDir.Should().Be(Path.Combine(local, "CaixaMaker", "outputs"));
        paths.LogsDir.Should().Be(Path.Combine(local, "CaixaMaker", "logs"));
        paths.TempDir.Should().Be(Path.Combine(local, "CaixaMaker", "temp"));
        paths.NativeLogFilePath.Should().Be(
            Path.Combine(local, "CaixaMaker", "logs", "desktop-native.log"));
        paths.LegacyProjectsDirs.Should().Contain(Path.GetFullPath(legacyDir));

        paths.EnsureDirs();
        Directory.Exists(paths.ProjectsDir).Should().BeTrue();
        Directory.Exists(paths.ConfigDir).Should().BeTrue();
        Directory.Exists(paths.OutputsDir).Should().BeTrue();
        Directory.Exists(paths.LogsDir).Should().BeTrue();
        Directory.Exists(paths.TempDir).Should().BeTrue();
    }

    [Fact]
    public void Resolve_WithExplicitOverrides_UsesIsolatedTargetDirectories()
    {
        using TempDirectoryScope temp = new();
        string customProjects = temp.GetPath("CustomProjects");
        string customOutputs = temp.GetPath("CustomOutputs");
        string customConfig = temp.GetPath("CustomConfig");
        string customLogs = temp.GetPath("CustomLogs");

        AppPaths paths = new(new AppPathsOptions
        {
            DesktopMode = true,
            ProjectsDir = customProjects,
            OutputsDir = customOutputs,
            ConfigDir = customConfig,
            LogsDir = customLogs,
            RepoRoot = temp.RootPath,
            LegacyProjectsDirs = [],
        });

        paths.ProjectsDir.Should().Be(Path.GetFullPath(customProjects));
        paths.OutputsDir.Should().Be(Path.GetFullPath(customOutputs));
        paths.ConfigDir.Should().Be(Path.GetFullPath(customConfig));
        paths.LogsDir.Should().Be(Path.GetFullPath(customLogs));
        paths.LegacyProjectsDirs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Maker_Box_200x150x100_t3_0_fp0_4_20261114_053000.svg")]
    [InlineData("caixa_aberta-v2.dxf")]
    [InlineData("plano-corte.1.pdf")]
    [InlineData("pacote_cnc.zip")]
    public void SafeOutputFilename_AcceptsValidGeneratedFilenames(string filename)
    {
        using TempDirectoryScope temp = new();
        AppPaths paths = AppPaths.Resolve(new AppPathsOptions
        {
            RoamingRoot = temp.GetPath("Roaming"),
            LocalRoot = temp.GetPath("Local"),
            RepoRoot = temp.RootPath,
        });

        AppPaths.SAFE_FILENAME_RE.IsMatch(filename).Should().BeTrue();
        AppPaths.IsSafeOutputFilename(filename).Should().BeTrue();

        string resolved = paths.ResolveSafeOutputPath(filename);
        Path.GetDirectoryName(resolved).Should().Be(Path.GetFullPath(paths.OutputsDir));
        Path.GetFileName(resolved).Should().Be(filename);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../models.py")]
    [InlineData("..\\secret.svg")]
    [InlineData("sub/caixa.svg")]
    [InlineData("sub\\caixa.svg")]
    [InlineData("foo..bar.svg")]
    [InlineData("..svg")]
    [InlineData(".hidden.svg")]
    [InlineData("caixa\0.svg")]
    [InlineData("C:\\temp\\caixa.svg")]
    [InlineData("C:caixa.svg")]
    [InlineData("projeto.json")]
    [InlineData("payload.exe")]
    public void SafeOutputFilename_RejectsPathTraversalAndUnsafeNames(string? unsafeFilename)
    {
        using TempDirectoryScope temp = new();
        AppPaths paths = AppPaths.Resolve(new AppPathsOptions
        {
            RoamingRoot = temp.GetPath("Roaming"),
            LocalRoot = temp.GetPath("Local"),
            RepoRoot = temp.RootPath,
        });

        AppPaths.IsSafeOutputFilename(unsafeFilename).Should().BeFalse();

        Action act = () => paths.ResolveSafeOutputPath(unsafeFilename!);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("proj_legacy01", true)]
    [InlineData("caixa-ifmaker-2026", true)]
    [InlineData("..secret", false)]
    [InlineData("../secret", false)]
    [InlineData("proj/01", false)]
    [InlineData("proj\\01", false)]
    [InlineData("abc", false)]
    [InlineData("proj\0test", false)]
    public void SafeProjectId_BlocksPathTraversalAndEnforcesFormat(string projectId, bool expectedValid)
    {
        AppPaths.IsSafeProjectId(projectId).Should().Be(expectedValid);
    }
}
