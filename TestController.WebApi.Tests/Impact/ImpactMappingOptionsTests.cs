using TestControllerGrpc.Core.Impact;
using Xunit;

namespace TestController.WebApi.Tests.Impact;

/// <summary>Unit tests for <see cref="ImpactMappingOptions.Validate"/> (P02).</summary>
public class ImpactMappingOptionsTests
{
    [Fact]
    public void Validate_Should_ReturnEmpty_ForDefaults()
    {
        Assert.Empty(new ImpactMappingOptions().Validate());
    }

    [Fact]
    public void Validate_Should_Flag_When_RrfKZero()
    {
        var opts = new ImpactMappingOptions();
        opts.Retrieval.RrfK = 0;

        Assert.NotEmpty(opts.Validate());
    }

    [Fact]
    public void Validate_Should_Flag_When_FanOutFloorAboveCeiling()
    {
        var opts = new ImpactMappingOptions();
        opts.Retrieval.FanOutPenaltyFloor = 2.0;
        opts.Retrieval.FanOutPenaltyCeiling = 1.0;

        Assert.NotEmpty(opts.Validate());
    }
}
