using System.Xml.Linq;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class ReleasePackagingIntegrityTests
{
    private static string ResolveRepoRoot()
    {
        string? versionFile = AppVersion.ResolveVersionFile(AppContext.BaseDirectory);
        versionFile.Should().NotBeNull("o arquivo VERSION na raiz do repositório deve estar acessível");
        return Path.GetDirectoryName(versionFile!)!;
    }

    [Fact]
    public void VersioningFiles_AreSynchronizedWithRootVersion()
    {
        string repoRoot = ResolveRepoRoot();
        string rootVersion = AppVersion.ReadVersionFromFile(Path.Combine(repoRoot, "VERSION"));

        AppVersion.IsValidSemVer(rootVersion).Should().BeTrue();
        AppVersion.Version.Should().Be(rootVersion);
        AppVersion.AssemblyVersion.Should().Be($"{rootVersion}.0");
        AppVersion.FileVersion.Should().Be($"{rootVersion}.0");

        string propsPath = Path.Combine(repoRoot, "native-winui", "Directory.Build.props");
        File.Exists(propsPath).Should().BeTrue();
        XDocument propsDoc = XDocument.Load(propsPath);
        XElement? propertyGroup = propsDoc.Root?.Element("PropertyGroup");
        propertyGroup.Should().NotBeNull();

        propertyGroup!.Element("Version")?.Value.Should().Be(rootVersion);
        propertyGroup.Element("AssemblyVersion")?.Value.Should().Be($"{rootVersion}.0");
        propertyGroup.Element("FileVersion")?.Value.Should().Be($"{rootVersion}.0");
        propertyGroup.Element("InformationalVersion")?.Value.Should().Be(rootVersion);
    }

    [Fact]
    public void AppProjectAndPublishProfile_AreConfiguredForWinUi3SelfContainedX64()
    {
        string repoRoot = ResolveRepoRoot();
        string appDir = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App");
        string csprojPath = Path.Combine(appDir, "CaixaMaker.App.csproj");
        string pubxmlPath = Path.Combine(appDir, "Properties", "PublishProfiles", "win-x64.pubxml");
        string manifestPath = Path.Combine(appDir, "app.manifest");
        string iconPath = Path.Combine(appDir, "Assets", "icon.ico");

        File.Exists(csprojPath).Should().BeTrue();
        File.Exists(pubxmlPath).Should().BeTrue();
        File.Exists(manifestPath).Should().BeTrue();
        File.Exists(iconPath).Should().BeTrue();

        XDocument csproj = XDocument.Load(csprojPath);
        XElement? mainProps = csproj.Root?.Elements("PropertyGroup").FirstOrDefault();
        mainProps.Should().NotBeNull();

        mainProps!.Element("OutputType")?.Value.Should().Be("WinExe");
        mainProps.Element("AssemblyName")?.Value.Should().Be("CaixaMaker");
        mainProps.Element("RuntimeIdentifier")?.Value.Should().Be("win-x64");
        mainProps.Element("WindowsPackageType")?.Value.Should().Be("None");
        mainProps.Element("WindowsAppSDKSelfContained")?.Value.Should().Be("true");
        mainProps.Element("SelfContained")?.Value.Should().Be("true");
        mainProps.Element("ApplicationIcon")?.Value.Should().Be(@"Assets\icon.ico");
        mainProps.Element("ApplicationManifest")?.Value.Should().Be("app.manifest");

        XDocument pubxml = XDocument.Load(pubxmlPath);
        XElement? pubProps = pubxml.Root?.Element("PropertyGroup");
        pubProps.Should().NotBeNull();
        pubProps!.Element("RuntimeIdentifier")?.Value.Should().Be("win-x64");
        pubProps.Element("SelfContained")?.Value.Should().Be("true");
        pubProps.Element("WindowsPackageType")?.Value.Should().Be("None");
        pubProps.Element("WindowsAppSDKSelfContained")?.Value.Should().Be("true");
        pubProps.Element("PublishSingleFile")?.Value.Should().Be("false");
        pubProps.Element("DebugType")?.Value.Should().Be("none");
        pubProps.Element("DebugSymbols")?.Value.Should().Be("false");
    }

    [Fact]
    public void NsisInstallerScript_EnforcesNativePtBrAndZeroLegacyDependencies()
    {
        string repoRoot = ResolveRepoRoot();
        string nsisDir = Path.Combine(repoRoot, "native-winui", "installer", "nsis");
        string scriptPath = Path.Combine(nsisDir, "CaixaMaker.nsi");
        string localePath = Path.Combine(nsisDir, "PortugueseBR.nsh");

        File.Exists(scriptPath).Should().BeTrue();
        File.Exists(localePath).Should().BeTrue();

        string script = File.ReadAllText(scriptPath);
        string locale = File.ReadAllText(localePath);

        script.Should().Contain("Unicode true");
        script.Should().Contain("ManifestDPIAwareness PerMonitorV2");
        script.Should().Contain("SetCompressor /SOLID lzma");
        script.Should().Contain("RequestExecutionLevel user");
        script.Should().Contain("SetRegView 64");
        script.Should().Contain("!insertmacro MUI_LANGUAGE \"PortugueseBR\"");
        script.Should().Contain("CaixaMaker.exe");
        script.Should().Contain("$SMPROGRAMS\\");
        script.Should().Contain("$DESKTOP\\");
        script.Should().Contain("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\");

        // Deve preservar dados de usuário em %APPDATA%\CaixaMaker na desinstalação
        script.Should().NotContain("RMDir /r \"$APPDATA\\CaixaMaker\"");

        // Zero dependências de WebView2 ou Python
        script.Should().NotContainEquivalentOf("webview2");
        script.Should().NotContainEquivalentOf("python");
        locale.Should().NotContainEquivalentOf("webview2");
        locale.Should().NotContainEquivalentOf("python");
        locale.Should().Contain("${LANG_PORTUGUESEBR}");
    }

    [Fact]
    public void WixInstallerDescriptor_PreservesUpgradeCodePtBrAndZeroLegacyDependencies()
    {
        string repoRoot = ResolveRepoRoot();
        string wixDir = Path.Combine(repoRoot, "native-winui", "installer", "wix");
        string productPath = Path.Combine(wixDir, "Product.wxs");
        string localePath = Path.Combine(wixDir, "Locale-ptBR.wxl");

        File.Exists(productPath).Should().BeTrue();
        File.Exists(localePath).Should().BeTrue();

        string productText = File.ReadAllText(productPath);
        string localeText = File.ReadAllText(localePath);

        productText.Should().NotContainEquivalentOf("webview2");
        productText.Should().NotContainEquivalentOf("python");
        localeText.Should().NotContainEquivalentOf("webview2");
        localeText.Should().NotContainEquivalentOf("python");

        XDocument productDoc = XDocument.Load(productPath);
        XNamespace wix = "http://schemas.microsoft.com/wix/2006/wi";

        XElement? productEl = productDoc.Root?.Element(wix + "Product");
        productEl.Should().NotBeNull();
        productEl!.Attribute("Name")?.Value.Should().Be("Caixa Maker");
        productEl.Attribute("Language")?.Value.Should().Be("1046");
        productEl.Attribute("Codepage")?.Value.Should().Be("1252");
        productEl.Attribute("UpgradeCode")?.Value.Should().Be("18839699-a044-5ba7-9322-34800fbe7ab1");

        XElement? pkgEl = productEl.Element(wix + "Package");
        pkgEl.Should().NotBeNull();
        pkgEl!.Attribute("Platform")?.Value.Should().Be("x64");
        pkgEl.Attribute("InstallScope")?.Value.Should().Be("perMachine");
        pkgEl.Attribute("Languages")?.Value.Should().Be("1046");

        productEl.Element(wix + "MajorUpgrade").Should().NotBeNull();
        productEl.Element(wix + "Icon")?.Attribute("Id")?.Value.Should().Be("ProductIcon");

        productDoc.Descendants(wix + "Shortcut")
            .Select(s => s.Attribute("Id")?.Value)
            .Should().Contain(new[] { "ApplicationStartMenuShortcut", "ApplicationDesktopShortcut" });

        productDoc.Descendants(wix + "ComponentGroupRef")
            .Select(c => c.Attribute("Id")?.Value)
            .Should().Contain("AppPayloadGroup");

        XDocument localeDoc = XDocument.Load(localePath);
        localeDoc.Root?.Attribute("Culture")?.Value.Should().Be("pt-br");
        localeDoc.Root?.Attribute("Codepage")?.Value.Should().Be("1252");
    }

    [Fact]
    public void NativeReleasePipelineScript_CoversAllSixReleaseStages()
    {
        string repoRoot = ResolveRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "scripts", "build_native_release.ps1");
        File.Exists(scriptPath).Should().BeTrue();

        string script = File.ReadAllText(scriptPath);
        script.Should().Contain("version_manager.py");
        script.Should().Contain("--check");
        script.Should().Contain("dotnet");
        script.Should().Contain("test");
        script.Should().Contain("publish");
        script.Should().Contain("--smoke-test");
        script.Should().Contain("makensis.exe");
        script.Should().Contain("heat.exe");
        script.Should().Contain("candle.exe");
        script.Should().Contain("light.exe");
        script.Should().Contain("CaixaMaker-Setup.exe");
        script.Should().Contain("CaixaMaker-Setup-$AppVersion-x64.exe");
        script.Should().Contain("CaixaMaker-$AppVersion-x64.msi");
        script.Should().Contain("Get-FileHash -Algorithm SHA256");
    }
}
