using System.Reflection;
using System.Xml.Linq;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage200VersionAndDeveloperCreditsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPaths _paths;

    public Stage200VersionAndDeveloperCreditsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"cm_stage200_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _paths = new AppPaths(new AppPathsOptions
        {
            DesktopMode = false,
            ProjectsDir = Path.Combine(_tempRoot, "projects"),
            OutputsDir = Path.Combine(_tempRoot, "outputs"),
            ConfigDir = Path.Combine(_tempRoot, "config"),
            LogsDir = Path.Combine(_tempRoot, "logs"),
            TempDir = Path.Combine(_tempRoot, "temp"),
        });
        _paths.EnsureDirs();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignora falha transitória na limpeza do diretório temporário.
        }
    }

    private static string ResolveRepoRoot()
    {
        string? versionFile = AppVersion.ResolveVersionFile(AppContext.BaseDirectory);
        versionFile.Should().NotBeNull();
        return Path.GetDirectoryName(versionFile!)!;
    }

    [Fact]
    public void AssemblyMetadata_AndRootVersion_ArePromotedToV200WithDeveloperCredits()
    {
        string repoRoot = ResolveRepoRoot();
        string rootVersion = AppVersion.ReadVersionFromFile(Path.Combine(repoRoot, "VERSION"));
        rootVersion.Should().Be("2.3.0");

        AppVersion.Version.Should().Be("2.3.0");
        AppVersion.AssemblyVersion.Should().Be("2.3.0.0");
        AppVersion.FileVersion.Should().Be("2.3.0.0");

        Assembly coreAsm = typeof(AppVersion).Assembly;
        string? company = coreAsm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        string? copyright = coreAsm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        string? description = coreAsm.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
        string? infoVer = coreAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        infoVer.Should().Be("2.3.0");
        company.Should().Be("Laboratório IFMaker do Juruá — IFAC Campus Cruzeiro do Sul");
        copyright.Should().Contain("Laboratório IFMaker do Juruá").And.Contain("Pedro G. Cruz").And.Contain("PddroGCruz").And.NotContain("pddrogcruz");
        description.Should().Contain("IFMaker do Juruá").And.Contain("PddroGCruz");

        string propsPath = Path.Combine(repoRoot, "native-winui", "Directory.Build.props");
        XDocument props = XDocument.Load(propsPath);
        XElement? pg = props.Root?.Element("PropertyGroup");
        pg.Should().NotBeNull();
        pg!.Element("Version")?.Value.Should().Be("2.3.0");
        pg.Element("AssemblyVersion")?.Value.Should().Be("2.3.0.0");
        pg.Element("FileVersion")?.Value.Should().Be("2.3.0.0");
        pg.Element("InformationalVersion")?.Value.Should().Be("2.3.0");
        pg.Element("Company")?.Value.Should().Be("Laboratório IFMaker do Juruá — IFAC Campus Cruzeiro do Sul");
        pg.Element("Authors")?.Value.Should().Contain("Pedro G. Cruz").And.Contain("PddroGCruz").And.Contain("ggcostcr@gmail.com").And.NotContain("pddrogcruz");
        pg.Element("Copyright")?.Value.Should().Contain("Laboratório IFMaker do Juruá").And.Contain("Pedro G. Cruz").And.Contain("PddroGCruz").And.NotContain("pddrogcruz");

        var vm = new MainWindowViewModel(
            _paths,
            new NullNativeFileDialogService(),
            BoxModelRegistry.Default,
            ResolvedAppTheme.Dark);

        vm.AppVersionText.Should().Be("2.3.0");
        vm.DeveloperNameText.Should().Be("Pedro G. Cruz (PddroGCruz)");
        vm.DeveloperEmailText.Should().Be("ggcostcr@gmail.com");
    }

    [Fact]
    public void UiXamlAndManifests_ExposeV200AndHaveZeroAccidentalV120References()
    {
        string repoRoot = ResolveRepoRoot();
        string appDir = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App");

        string[] uiFiles = Directory.GetFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(appDir, "*.xaml.cs", SearchOption.AllDirectories))
            .Concat([
                Path.Combine(appDir, "Views", "Dialogs", "AppDialogs.cs"),
                Path.Combine(appDir, "app.manifest"),
            ])
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        uiFiles.Should().NotBeEmpty();
        foreach (string file in uiFiles)
        {
            string content = File.ReadAllText(file);
            content.Should().NotContain("v1.2.0", $"nenhum arquivo de UI pode manter o badge legado v1.2.0 ({Path.GetFileName(file)})");
            content.Should().NotContain("1.2.0", $"nenhum arquivo de UI/manifesto pode manter literal 1.2.0 ({Path.GetFileName(file)})");
        }

        string catalogXaml = File.ReadAllText(Path.Combine(appDir, "Views", "CatalogHomeView.xaml"));
        catalogXaml.Should().Contain("Text=\"v2.3.0\"");
        catalogXaml.Should().NotContain("v2.2.0");

        string shellXaml = File.ReadAllText(Path.Combine(appDir, "Views", "WorkspaceSplitShell.xaml"));
        shellXaml.Should().Contain("Text=\"v2.3.0\"");
        shellXaml.Should().NotContain("v2.2.0");

        string manifest = File.ReadAllText(Path.Combine(appDir, "app.manifest"));
        manifest.Should().Contain("version=\"2.3.0.0\"");
    }

    [Fact]
    public void DeveloperCredits_ArePresentInAboutDialog_CatalogAndInstallers()
    {
        string repoRoot = ResolveRepoRoot();
        string appDir = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App");

        string dialogsCs = File.ReadAllText(Path.Combine(appDir, "Views", "Dialogs", "AppDialogs.cs"));
        dialogsCs.Should().Contain("BuildDeveloperHighlightCard");
        dialogsCs.Should().Contain("DESENVOLVEDOR & ARQUITETO DE SOFTWARE");
        dialogsCs.Should().Contain("@PddroGCruz").And.NotContain("@pddrogcruz");
        dialogsCs.Should().Contain("AppVersion.DeveloperName");
        dialogsCs.Should().Contain("AppVersion.DeveloperEmail");
        dialogsCs.Should().Contain("AppVersion.DeveloperSignature");
        dialogsCs.Should().Contain("Desenvolvido por {AppVersion.DeveloperSignature} • {AppVersion.Company}");

        // Sidebar do Catálogo deve manter estritamente a identidade institucional, sem créditos pessoais
        string catalogXaml = File.ReadAllText(Path.Combine(appDir, "Views", "CatalogHomeView.xaml"));
        catalogXaml.Should().Contain("Laboratório IFMaker do Juruá");
        catalogXaml.Should().Contain("IFAC — Campus Cruzeiro do Sul");
        catalogXaml.Should().NotContain("Pedro G. Cruz");
        catalogXaml.Should().NotContain("PddroGCruz");
        catalogXaml.Should().NotContain("pddrogcruz");
        catalogXaml.Should().NotContain("ggcostcr@gmail.com");

        string nsisScript = File.ReadAllText(Path.Combine(repoRoot, "native-winui", "installer", "nsis", "CaixaMaker.nsi"));
        nsisScript.Should().Contain("!define VERSION \"2.3.0\"");
        nsisScript.Should().Contain("!define BINARY_VERSION \"2.3.0.0\"");
        nsisScript.Should().Contain("!define PRODUCT_PUBLISHER \"Laboratório IFMaker do Juruá — IFAC\"");
        nsisScript.Should().Contain("Pedro G. Cruz (PddroGCruz • ggcostcr@gmail.com)");
        nsisScript.Should().NotContain("pddrogcruz");

        string wxsProduct = File.ReadAllText(Path.Combine(repoRoot, "native-winui", "installer", "wix", "Product.wxs"));
        wxsProduct.Should().Contain("<?define Version = \"2.3.0\" ?>");
        wxsProduct.Should().Contain("Manufacturer=\"Laboratório IFMaker do Juruá — IFAC\"");
        wxsProduct.Should().Contain("Pedro G. Cruz (PddroGCruz • ggcostcr@gmail.com)");
        wxsProduct.Should().NotContain("pddrogcruz");
    }
}
