using TestControllerGrpc.PipelineEditor;

namespace TestControllerGrpc.Tests.PipelineEditor;

public class PipelineXmlMapTests
{
    private const string Xml =
        "<WatchItem Tag=\"P\" Path=\"C:\\\" Filter=\"t.txt\">\n" +
        "  <Event Type=\"Renamed\" ExecutionType=\"Sequential\">\n" +
        "    <Comment>note</Comment>\n" +
        "    <!-- keep me -->\n" +
        "    <Action Tag=\"First\" Command=\"a.bat\" />\n" +
        "    <ActionGroup Tag=\"G\" ExecutionType=\"Parallel\">\n" +
        "      <Action Tag=\"Inner\" AgentName=\"node7\" Command=\"b.bat\" Skip=\"true\" />\n" +
        "    </ActionGroup>\n" +
        "  </Event>\n" +
        "</WatchItem>";

    [Fact]
    public void TryBuild_Should_GiveStructuralPaths_When_CommentsArePresent()
    {
        var map = PipelineXmlMap.TryBuild(Xml)!;

        Assert.Equal(["", "e0", "e0/c0", "e0/c1", "e0/c1/c0"], map.All.Select(n => n.Path));
        var inner = map.Find("e0/c1/c0")!;
        Assert.Equal("Inner", inner.Name);
        Assert.True(inner.IsSkipped);
        Assert.Contains("node7", inner.SearchText);
    }

    [Fact]
    public void NodeAt_Should_ReturnDeepestNode_When_CaretIsInsideAnAction()
    {
        var map = PipelineXmlMap.TryBuild(Xml)!;

        Assert.Equal("e0/c1/c0", map.NodeAt(Xml.IndexOf("node7", StringComparison.Ordinal))!.Path);
        Assert.Equal("e0/c0", map.NodeAt(line: 5, column: 10)!.Path);
    }

    [Fact]
    public void TryBuild_Should_ReturnNull_When_XmlIsMalformed()
    {
        Assert.Null(PipelineXmlMap.TryBuild("<WatchItem><Event></WatchItem>"));
    }
}

public class PipelineXmlEditsTests
{
    private const string Xml =
        "<WatchItem Tag=\"P\">\n" +
        "  <Event Type=\"Renamed\">\n" +
        "    <!-- keep me -->\n" +
        "    <Action Tag=\"A\" />\n" +
        "    <Action Tag=\"B\" />\n" +
        "  </Event>\n" +
        "</WatchItem>";

    [Fact]
    public void Move_Should_SwapStepsAndKeepXmlComments_When_MovedUp()
    {
        var result = PipelineXmlEdits.Move(Xml, "e0/c1", up: true)!;

        Assert.Contains("<!-- keep me -->", result.Xml);
        Assert.True(result.Xml.IndexOf("Tag=\"B\"", StringComparison.Ordinal) < result.Xml.IndexOf("Tag=\"A\"", StringComparison.Ordinal));
        Assert.Equal("e0/c0", result.SelectPath);
    }

    [Fact]
    public void Delete_Should_RemoveStepAndSelectParent_When_PathIsAStep()
    {
        var result = PipelineXmlEdits.Delete(Xml, "e0/c0")!;

        Assert.DoesNotContain("Tag=\"A\"", result.Xml);
        Assert.Equal("e0", result.SelectPath);
    }

    [Fact]
    public void SetSkip_Should_WriteAndRemoveSkipAttributes_When_SkippedThenIncluded()
    {
        var now = new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
        var skipped = PipelineXmlEdits.SetSkip(Xml, "e0/c0", true, "flaky", "jv", now)!;
        Assert.Contains("Skip=\"true\" SkipReason=\"flaky\" SkippedBy=\"jv\"", skipped.Xml);

        var included = PipelineXmlEdits.SetSkip(skipped.Xml, "e0/c0", false, null, null, now)!;
        Assert.Equal(Xml, included.Xml);
    }

    [Fact]
    public void Edit_Should_ReturnNull_When_PathDoesNotExist()
    {
        Assert.Null(PipelineXmlEdits.Delete(Xml, "e0/c9"));
        Assert.Null(PipelineXmlEdits.Move(Xml, "e0/c0", up: true));
    }
}
