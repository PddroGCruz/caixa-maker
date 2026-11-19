using CaixaMaker.Cam;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Storage;

public sealed class AppVersionTests
{
    [Fact]
    public void AppVersion_MatchesRootVersionFileAndMetadata()
    {
        string? versionFile = AppVersion.ResolveVersionFile(AppContext.BaseDirectory);
        versionFile.Should().NotBeNull("o arquivo VERSION na raiz do repositório deve estar acessível nos testes");

        string expectedRootVersion = AppVersion.ReadVersionFromFile(versionFile!);

        AppVersion.Version.Should().Be(expectedRootVersion);
        AppVersion.Version.Should().Be("2.4.0");
        AppVersion.IsValidSemVer(AppVersion.Version).Should().BeTrue();
        AppVersion.ProductName.Should().Be("Caixa Maker");
        AppVersion.Company.Should().Be("Laboratório IFMaker do Juruá — IFAC Campus Cruzeiro do Sul");
        AppVersion.DeveloperName.Should().Be("Pedro G. Cruz (PddroGCruz)");
        AppVersion.DeveloperEmail.Should().Be("ggcostcr@gmail.com");
        AppVersion.DeveloperSignature.Should().Contain("Pedro G. Cruz").And.Contain("PddroGCruz").And.Contain("ggcostcr@gmail.com");
        AppVersion.DeveloperName.Should().NotContain("pddrogcruz");
        AppVersion.DeveloperSignature.Should().NotContain("pddrogcruz");
    }

    [Theory]
    [InlineData("0.0.1", true)]
    [InlineData("2.0.0", true)]
    [InlineData("10.20.30", true)]
    [InlineData("1.0", false)]
    [InlineData("v1.0.1", false)]
    [InlineData("01.0.0", false)]
    [InlineData("1.0.0-alpha", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SemVerValidation_MatchesRepositoryRules(string? candidate, bool expectedValid)
    {
        AppVersion.IsValidSemVer(candidate).Should().Be(expectedValid);
        if (!expectedValid)
        {
            Action act = () => AppVersion.ValidateSemVer(candidate);
            act.Should().Throw<FormatException>();
        }
    }

    [Fact]
    public void CamModule_ExposesCoreVersionAndValidatesSkiaBackend()
    {
        CamModuleInfo.CoreVersion.Should().Be(AppVersion.Version);
        CamModuleInfo.VerifyGraphicsBackend().Should().BeTrue();
    }
}
