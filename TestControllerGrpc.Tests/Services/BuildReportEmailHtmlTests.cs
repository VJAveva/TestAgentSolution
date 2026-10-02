using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Guards the e-mail paths against the three things Outlook's Word renderer silently drops - CSS custom
/// properties, flexbox and grid - and against the dark in-app palette leaking into mail, where it renders as
/// near-black blocks on clients that force a white background.
/// </summary>
public sealed class BuildReportEmailHtmlTests
{
    private static BuildReportHtmlGenerator Generator() => new(new BuildResultsConfig());

    private static BuildNode Node() => new()
    {
        BuildNumber = "OAK_SP-2023-R2-SP2_20260915.5",
        TotalTests = 10,
        PassedTests = 9,
        FailedTests = 1,
        PassRate = 90.0,
        Health = HealthStatus.Warning,
        UseCases =
        [
            new UseCaseNode { UseCaseName = "Pool", Total = 10, Passed = 9, Failed = 1, PassRate = 90.0 },
        ],
        AllFailedTests =
        [
            new TestResult { TestName = "Login_Should_Succeed", TrxFileName = "a.trx", ErrorMessage = "boom" },
        ],
    };

    // Dark-theme surfaces from the in-app palette; none belong in an e-mail body.
    private static readonly string[] DarkSurfaces = ["#0F1629", "#1A2238", "#1E293B", "#151D30", "#263350"];

    public static TheoryData<string, string> EmailHtml() => new()
    {
        { "single", Generator().GenerateSingleBuildHtml(Node()) },
        { "multi", Generator().GenerateMultiBuildHtml([Node()], 10, 9, 1, 0) },
    };

    [Theory]
    [MemberData(nameof(EmailHtml))]
    public void EmailHtml_Should_AvoidUnsupportedLayout_When_Generated(string label, string html)
    {
        Assert.DoesNotContain("display: flex", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("display:flex", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("display: grid", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("var(--", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(label));
    }

    [Theory]
    [MemberData(nameof(EmailHtml))]
    public void EmailHtml_Should_UseFluentLightPalette_When_Generated(string label, string html)
    {
        foreach (var dark in DarkSurfaces)
            Assert.DoesNotContain(dark, html, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(EmailPalette.PageBg, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(EmailPalette.Text, html, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(label));
    }

    [Fact]
    public void SingleBuildHtml_Should_UseAsciiSeparator_InHealthBar()
    {
        var html = Generator().GenerateSingleBuildHtml(Node());

        Assert.Contains($"90.0%{EmailPalette.SubjectSeparator}Warning", html, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', html);
    }

    private static TrendReport Trend() => new()
    {
        Builds =
        [
            new BuildTrendEntry { BuildNumber = "OAK_main_20260615.3", Date = new DateTime(2026, 6, 15), TotalTests = 10, PassedTests = 9, FailedTests = 1, PassRate = 90.0, Health = HealthStatus.Warning },
            new BuildTrendEntry { BuildNumber = "OAK_main_20260616.1", Date = new DateTime(2026, 6, 16), TotalTests = 10, PassedTests = 10, PassRate = 100.0, Health = HealthStatus.Good },
        ],
    };

    [Theory]
    [InlineData("Light", "light")]
    [InlineData("Dark", "dark")]
    [InlineData("High Contrast", "contrast")]
    [InlineData(null, "light")]
    public void TrendHtml_Should_CarryDataTheme_When_ThemeSupplied(string? theme, string expected)
    {
        var html = Generator().GenerateTrendHtml(Trend(), theme);

        Assert.Contains($"data-theme='{expected}'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TrendHtml_Should_DriveColoursFromVariables_When_Generated()
    {
        var html = Generator().GenerateTrendHtml(Trend(), "Dark");

        // Every dark surface must come from a variable definition, never a literal on an element.
        Assert.DoesNotContain("style='color:#", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fill='#", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stroke='#", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("var(--", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TrendHtml_Should_ForceLightPalette_When_Printed()
    {
        var html = Generator().GenerateTrendHtml(Trend(), "Dark");

        var print = html.IndexOf("@media print", StringComparison.Ordinal);
        Assert.True(print > 0, "trend report has no print stylesheet");

        // Must outrank html[data-theme="dark"], which is more specific than :root alone.
        var block = html[print..];
        Assert.Contains("html[data-theme=\"dark\"]", block, StringComparison.Ordinal);
        Assert.Contains(EmailPalette.Text, block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrendChart_Should_ShortenLabelsAndKeepFullNameInTooltip()
    {
        var html = Generator().GenerateTrendHtml(Trend(), "Light");

        Assert.Contains("<title>OAK_main_20260615.3</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TrendEmailHtml_Should_AvoidEverythingOutlookDrops()
    {
        var html = Generator().GenerateTrendEmailHtml(Trend());

        // Word renderer: no custom properties, no flex/grid, and inline SVG draws nothing at all.
        Assert.DoesNotContain("var(--", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("display:flex", html.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("display:grid", html.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\uFFFD', html);

        foreach (var dark in DarkSurfaces)
            Assert.DoesNotContain(dark, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrendEmailHtml_Should_DrawBarsProportionalToPassRate()
    {
        var html = Generator().GenerateTrendEmailHtml(Trend());

        // 90.0% and 100% are the two fixtures; the filled cell carries the rounded percentage as its width.
        Assert.Contains("width=\"90%\"", html, StringComparison.Ordinal);
        Assert.Contains("width=\"100%\"", html, StringComparison.Ordinal);
        Assert.Contains(EmailPalette.Success, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrendEmailHtml_Should_StayCalm_When_NoBuilds()
    {
        var html = Generator().GenerateTrendEmailHtml(new TrendReport());

        Assert.Contains("No build data available", html, StringComparison.Ordinal);
        Assert.Contains("</html>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTrendSubject_Should_BeAscii_When_SeparatingFields()
    {
        var subject = BuildReportHtmlGenerator.BuildTrendSubject(Trend());

        var nonAscii = subject.Where(c => c > 0x7F).Distinct().ToArray();
        Assert.True(nonAscii.Length == 0,
            $"Subject carries non-ASCII: {string.Join(", ", nonAscii.Select(c => $"U+{(int)c:X4}"))} in '{subject}'");
        Assert.Contains("2 builds", subject, StringComparison.Ordinal);
    }
}
