using Microsoft.Extensions.Configuration;
using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Models;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Workstream A / P19. The policy excludes on the RAW ADO type, never on <see cref="RegressionWorkItemKind"/>,
/// because Kind folds Task, Feature and every unmapped type into <c>Other</c>.
/// </summary>
public class CodeChurnWorkItemPolicyTests
{
    private static RegressionWorkItemRef Wi(string? type, RegressionWorkItemKind kind = RegressionWorkItemKind.Other) =>
        new(1, kind, "title", null, null, type);

    [Fact]
    public void ExcludedTypes_Should_DefaultToTask()
    {
        Assert.Equal(["Task"], new CodeChurnWorkItemPolicy().ExcludedTypes);
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("task")]
    [InlineData("TASK")]
    public void IsExcluded_Should_BeCaseInsensitive(string type)
    {
        Assert.True(new CodeChurnWorkItemPolicy().IsExcluded(Wi(type)));
    }

    [Theory]
    [InlineData("Bug")]
    [InlineData("User Story")]
    [InlineData("Feature")]
    public void IsExcluded_Should_BeFalse_When_TypeNotExcluded(string type)
    {
        Assert.False(new CodeChurnWorkItemPolicy().IsExcluded(Wi(type)));
    }

    [Fact]
    public void IsExcluded_Should_BeFalse_When_RawTypeUnknown()
    {
        // Kind.Other covers Task AND Feature AND anything unmapped. Guessing from it would suppress
        // far more than "Task" — inclusion wins when we cannot tell.
        Assert.False(new CodeChurnWorkItemPolicy().IsExcluded(Wi(null, RegressionWorkItemKind.Other)));
    }

    [Fact]
    public void Validate_Should_Throw_When_DepthBelowOne()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new CodeChurnWorkItemPolicy { MaxHierarchyDepth = 0 }.Validate());

        Assert.Contains("MaxHierarchyDepth", ex.Message);
    }

    [Fact]
    public void Validate_Should_Throw_When_OrphanBucketNameBlank()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new CodeChurnWorkItemPolicy { OrphanBucketName = "  " }.Validate());

        Assert.Contains("OrphanBucketName", ex.Message);
    }

    [Fact]
    public void Validate_Should_Throw_When_ExcludedTypeBlank()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new CodeChurnWorkItemPolicy { ExcludedTypes = ["Task", " "] }.Validate());

        Assert.Contains("ExcludedTypes", ex.Message);
    }

    [Fact]
    public void Load_Should_BindFromConfiguration()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodeChurn:WorkItemPolicy:ExcludedTypes:0"] = "Task",
            ["CodeChurn:WorkItemPolicy:ExcludedTypes:1"] = "Test Case",
            ["CodeChurn:WorkItemPolicy:MaxHierarchyDepth"] = "4",
            ["CodeChurn:WorkItemPolicy:OrphanBucketName"] = "No Feature",
        }).Build();

        CodeChurnWorkItemPolicy policy = CodeChurnWorkItemPolicy.Load(config);

        Assert.True(policy.IsExcluded(Wi("Test Case")));
        Assert.Equal(4, policy.MaxHierarchyDepth);
        Assert.Equal("No Feature", policy.OrphanBucketName);
    }

    [Fact]
    public void Load_Should_Throw_When_ConfiguredValueInvalid()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodeChurn:WorkItemPolicy:MaxHierarchyDepth"] = "0",
        }).Build();

        // Fails loudly at startup rather than clamping to a default nobody asked for.
        Assert.Throws<InvalidOperationException>(() => CodeChurnWorkItemPolicy.Load(config));
    }
}
