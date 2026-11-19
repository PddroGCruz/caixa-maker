using System.Text;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Cam;

public sealed class SvgImporterTests
{
    private readonly SvgImporter _importer = new();

    [Fact]
    public void SanitizeSvgXml_RejectsDtdEntityDeclaration()
    {
        const string malicious = """
            <?xml version="1.0"?>
            <!DOCTYPE svg [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">
              <rect width="10" height="10" fill="#000"/>
            </svg>
            """;

        Action act = () => _importer.SanitizeSvgXml(malicious);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*ENTITY*");
    }

    [Fact]
    public void ImportFromString_StripsScriptEventHandlersAndExternalUrls_WithWarnings()
    {
        const string xssSvg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="100" height="100" onload="alert(1)">
              <script>alert('xss')</script>
              <foreignObject width="20" height="20"><body>hack</body></foreignObject>
              <a xlink:href="javascript:alert(1)">
                <rect x="0" y="0" width="96" height="96" fill="none" stroke="#0D47A1" onclick="evil()" style="background:url(https://evil.test/track.png)"/>
              </a>
            </svg>
            """;

        SvgImportResult result = _importer.ImportFromString(xssSvg, sourceFilename: "logo.svg");

        result.Warnings.Should().Contain(w => w.Contains("script", StringComparison.OrdinalIgnoreCase));
        result.Warnings.Should().Contain(w => w.Contains("foreignobject", StringComparison.OrdinalIgnoreCase));
        result.Warnings.Should().Contain(w => w.Contains("onload", StringComparison.OrdinalIgnoreCase));
        result.Warnings.Should().Contain(w => w.Contains("onclick", StringComparison.OrdinalIgnoreCase));
        result.Warnings.Should().Contain(w => w.Contains("url(", StringComparison.OrdinalIgnoreCase));

        // 96 px @ 96 PPI == 25.4 mm exatos
        result.Element.BaseWidthMm.Should().BeApproximately(25.4, 1e-3);
        result.Element.BaseHeightMm.Should().BeApproximately(25.4, 1e-3);
        result.Element.Paths.Should().ContainSingle();
        result.Element.Paths[0].Operation.Should().Be(CncOperationType.EngraveVector);
    }

    [Fact]
    public void ImportFromString_RejectsSvgExceeding10MbLimit()
    {
        string huge = "<svg xmlns=\"http://www.w3.org/2000/svg\">"
            + new string(' ', SvgImporter.MaxSvgBytes + 1024)
            + "</svg>";

        Action act = () => _importer.ImportFromString(huge);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*10 MB*");
    }

    [Fact]
    public void ImportFromString_FlattensGroupTransformsResolvesUseAndRespectsTargetSize()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="50mm" viewBox="0 0 100 50">
              <defs>
                <rect id="chip" x="0" y="0" width="10" height="10" fill="#6A1B9A" stroke="none"/>
              </defs>
              <g transform="translate(10, 5) scale(2, 2)">
                <use href="#chip" x="5" y="5"/>
                <circle cx="25" cy="10" r="5" fill="#6A1B9A" stroke="none"/>
              </g>
              <text x="10" y="40">Texto ignorado</text>
            </svg>
            """;

        SvgImportResult res = _importer.ImportFromString(svg, targetWidthMm: 80.0);

        res.Element.SourceUnit.Should().Be("mm");
        res.Element.OriginalWidthMm.Should().BeApproximately(100.0, 1e-3);
        res.Element.OriginalHeightMm.Should().BeApproximately(50.0, 1e-3);
        res.Element.Paths.Should().HaveCount(2);
        res.Element.Operation.Should().Be(CncOperationType.EngraveFill);
        res.Element.WidthMm.Should().BeApproximately(80.0, 1e-3);
        res.Warnings.Should().Contain(w => w.Contains("<text>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportFromString_NormalizesGeometryToOriginZero()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="200mm" height="200mm" viewBox="0 0 200 200">
              <rect x="80" y="90" width="40" height="20" fill="none" stroke="#0D47A1"/>
            </svg>
            """;

        SvgImportResult res = _importer.ImportFromString(svg);
        res.Element.BaseWidthMm.Should().BeApproximately(40.0, 1e-3);
        res.Element.BaseHeightMm.Should().BeApproximately(20.0, 1e-3);
        res.Element.Paths.Should().ContainSingle();
        res.Element.Paths[0].D.Should().StartWith("M 0 0 L 40 0 L 40 20 L 0 20 Z");
    }
}
