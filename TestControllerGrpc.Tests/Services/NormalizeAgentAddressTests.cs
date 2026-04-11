using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Services;

public class NormalizeAgentAddressTests
{
    [Theory]
    [InlineData("jvkbak:5200", "http://jvkbak:5200")]
    [InlineData("jvkbak", "http://jvkbak:5200")]
    [InlineData("http://jvkbak:5200", "http://jvkbak:5200")]
    [InlineData("https://jvkbak:5200", "https://jvkbak:5200")]
    [InlineData("HTTP://HOST:5200", "HTTP://HOST:5200")]
    [InlineData("192.168.1.100:5200", "http://192.168.1.100:5200")]
    [InlineData("192.168.1.100", "http://192.168.1.100:5200")]
    [InlineData("http://192.168.1.100:8080", "http://192.168.1.100:8080")]
    [InlineData("  jvkbak:5200  ", "http://jvkbak:5200")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeAgentAddress_HandlesAllFormats(string? input, string expected)
    {
        var result = MainViewModel.NormalizeAgentAddress(input!, defaultPort: 5200);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeAgentAddress_BareHostname_DefaultsTo5200()
    {
        var result = MainViewModel.NormalizeAgentAddress("myhost");
        Assert.Equal("http://myhost:5200", result);
    }

    [Fact]
    public void NormalizeAgentAddress_HttpsWithPort_Unchanged()
    {
        var result = MainViewModel.NormalizeAgentAddress("https://secure-agent:5201");
        Assert.Equal("https://secure-agent:5201", result);
    }

    [Fact]
    public void NormalizeAgentAddress_ExplicitPort_Preserved()
    {
        var result = MainViewModel.NormalizeAgentAddress("myhost:9090");
        Assert.Equal("http://myhost:9090", result);
    }
}
