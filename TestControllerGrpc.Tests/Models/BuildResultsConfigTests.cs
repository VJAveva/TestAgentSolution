using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.Models;

public class BuildResultsConfigTests
{
    [Fact]
    public void BuildResultsConfig_Should_HaveDefaults_When_Created()
    {
        var config = new BuildResultsConfig();

        Assert.Equal(@"C:\TestResults", config.ResultsRootPath);
        Assert.Equal(95.0, config.GoodThreshold);
        Assert.Equal(85.0, config.WarningThreshold);
        Assert.Equal("", config.ReportRecipients);
        Assert.Equal("smtp", config.SmtpServer);
        Assert.Equal(25, config.SmtpPort);
        Assert.Equal("", config.FromAddress);
        Assert.Equal("", config.QaAlertRecipients);
        Assert.Equal(2, config.ConsecutiveFailThreshold);
        Assert.True(config.AlertOnLoad);
    }

    [Fact]
    public void BuildResultsConfig_Should_AllowOverride_When_PropertiesSet()
    {
        var config = new BuildResultsConfig
        {
            ResultsRootPath = @"D:\Results",
            GoodThreshold = 90.0,
            WarningThreshold = 75.0,
            ReportRecipients = "team@company.com",
            SmtpServer = "mail.company.com",
            SmtpPort = 587,
            ConsecutiveFailThreshold = 5,
            AlertOnLoad = false,
        };

        Assert.Equal(@"D:\Results", config.ResultsRootPath);
        Assert.Equal(90.0, config.GoodThreshold);
        Assert.Equal(75.0, config.WarningThreshold);
        Assert.Equal("team@company.com", config.ReportRecipients);
        Assert.Equal(587, config.SmtpPort);
        Assert.Equal(5, config.ConsecutiveFailThreshold);
        Assert.False(config.AlertOnLoad);
    }

    [Fact]
    public void BuildResultsConfig_Should_HaveValidThresholdRelationship_When_DefaultsUsed()
    {
        var config = new BuildResultsConfig();
        Assert.True(config.GoodThreshold > config.WarningThreshold,
            "GoodThreshold should be higher than WarningThreshold");
    }
}
