using System.Xml.Linq;
using ICSharpCode.AvalonEdit.Document;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.Views;

namespace TestControllerGrpc.Tests.Services;

public sealed class WatchListXmlEditorAnalyzerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"xml-editor-{Guid.NewGuid():N}");

    public WatchListXmlEditorAnalyzerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Analyze_ShouldTreatPassAndFailAsWarnings_WhenBracketedTextIsNotToken()
    {
        var xml = WatchItemXml(parameters: "echo [PASS] [FAIL]");

        var analysis = WatchListXmlEditorAnalyzer.Analyze(xml, XmlEditorDocumentKind.WatchItem);

        Assert.Contains(analysis.Diagnostics, d =>
            d.Severity == WatchIssueSeverity.Warning && d.Message.Contains("[PASS]", StringComparison.Ordinal));
        Assert.Contains(analysis.Diagnostics, d =>
            d.Severity == WatchIssueSeverity.Warning && d.Message.Contains("[FAIL]", StringComparison.Ordinal));
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Message.Contains("Token [", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ShouldListProfiles_WhenInitializeProfileIsEmpty()
    {
        var parameterFile = WriteParameterFile(
            """{"profiles":{"Sanity":{},"Warm":{}},"global":{},"pipelines":{}}""");
        var xml = WatchItemXml(parameterFile: parameterFile, profile: "");

        var analysis = WatchListXmlEditorAnalyzer.Analyze(xml, XmlEditorDocumentKind.WatchItem);

        var issue = Assert.Single(analysis.Diagnostics, d =>
            d.Message.Contains("Profile is empty", StringComparison.Ordinal));
        Assert.Contains("Sanity", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Warm", issue.Message, StringComparison.Ordinal);
        Assert.NotEmpty(issue.QuickFixes);
    }

    [Fact]
    public void Analyze_ShouldReportOneSourceErrorAndSuppressTokenErrors_WhenJsonIsInvalid()
    {
        var parameterFile = WriteParameterFile("""{"global":{"_BuildNumber":""");
        var xml = WatchItemXml(parameterFile: parameterFile, parameters: "[_Missing]");

        var analysis = WatchListXmlEditorAnalyzer.Analyze(xml, XmlEditorDocumentKind.WatchItem);

        Assert.Single(analysis.Diagnostics, d =>
            d.Severity == WatchIssueSeverity.Error && d.Message.Contains("invalid JSON", StringComparison.Ordinal));
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Message.Contains("Token [_Missing]", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ShouldNameActionUsingUnresolvedToken()
    {
        var parameterFile = WriteParameterFile("""{"global":{},"profiles":{},"pipelines":{}}""");
        var xml = WatchItemXml(parameterFile: parameterFile, parameters: "[_Missing]");

        var analysis = WatchListXmlEditorAnalyzer.Analyze(xml, XmlEditorDocumentKind.WatchItem);

        var issue = Assert.Single(analysis.Diagnostics, d =>
            d.Message.Contains("Token [_Missing]", StringComparison.Ordinal));
        Assert.Contains("Action[Run]", issue.NodePath, StringComparison.Ordinal);
        Assert.NotNull(issue.QuickFixes.FirstOrDefault(f => f.Kind == XmlEditorQuickFixKind.RemoveStepsUsingToken));
    }

    [Fact]
    public void Analyze_ShouldCheckUnresolvedTokens_WhenInitializeIsAbsent()
    {
        var xml = WatchItemXml(parameters: "[_Missing]");

        var analysis = WatchListXmlEditorAnalyzer.Analyze(xml, XmlEditorDocumentKind.WatchItem);

        Assert.Contains(analysis.Diagnostics, d =>
            d.Message.Contains("Token [_Missing]", StringComparison.Ordinal));
    }

    [Fact]
    public void GetTokenCompletions_ShouldShowResolvedLayersAndMaskSecrets()
    {
        var parameterFile = WriteParameterFile(
            """{"global":{"_AdminPassword":"SensitiveValue"},"profiles":{"Sanity":{"_ProfileOnly":"profile-value"}},"pipelines":{"Case":{"_PipelineOnly":"pipeline-value"}}}""");
        var xml = WatchItemXml(parameterFile: parameterFile, profile: "Sanity");

        var completions = WatchListXmlEditorAnalyzer.GetTokenCompletions(
            xml, XmlEditorDocumentKind.WatchItem);

        var secret = Assert.Single(completions, completion => completion.Text == "[_AdminPassword]");
        Assert.Contains(TokenDisplay.Mask, secret.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("SensitiveValue", secret.Description, StringComparison.Ordinal);
        Assert.Contains("Global", secret.Description, StringComparison.Ordinal);
        Assert.Contains("Profile", Assert.Single(completions,
            completion => completion.Text == "[_ProfileOnly]").Description, StringComparison.Ordinal);
        Assert.Contains("Pipeline", Assert.Single(completions,
            completion => completion.Text == "[_PipelineOnly]").Description, StringComparison.Ordinal);
        Assert.Contains("Trigger", Assert.Single(completions,
            completion => completion.Text == "[_BuildNumber]").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ShouldReportTriggerPathAndFilterCollisions()
    {
        var xml = new XElement("WatchList",
            new XElement("WatchItem",
                new XAttribute("Tag", "First"),
                new XAttribute("Path", _directory),
                new XAttribute("Filter", "*.txt")),
            new XElement("WatchItem",
                new XAttribute("Tag", "Second"),
                new XAttribute("Path", _directory),
                new XAttribute("Filter", "*.txt")))
            .ToString(SaveOptions.DisableFormatting);

        var analysis = WatchListXmlEditorAnalyzer.Analyze(xml, XmlEditorDocumentKind.WatchList);

        Assert.Equal(2, analysis.Diagnostics.Count(d =>
            d.Message.Contains("Trigger Path + Filter collides", StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplyQuickFix_ShouldMoveBatchCredentialsToSecretEnv_AndReplaceLiteral()
    {
        var xml = WatchItemXml(command: "C:\\scripts\\run.cmd", parameters: "/password=DoNotExpose");
        var analysis = WatchListXmlEditorAnalyzer.Analyze(xml, XmlEditorDocumentKind.WatchItem);
        var issue = Assert.Single(analysis.Diagnostics, d =>
            d.Message.Contains("Credential-like value", StringComparison.Ordinal));
        var fix = Assert.Single(issue.QuickFixes);
        Assert.Contains("Action[Run]", issue.NodePath, StringComparison.Ordinal);
        Assert.Equal(XmlEditorQuickFixKind.MoveCredentialsToSecretEnv, fix.Kind);

        var updated = WatchListXmlEditorAnalyzer.ApplyQuickFix(
            xml, XmlEditorDocumentKind.WatchItem, fix);
        Assert.NotNull(updated);
        Assert.Contains("%SECRET_PASSWORD%", updated, StringComparison.Ordinal);
        var action = XDocument.Parse(updated!).Descendants("Action").Single();

        Assert.True(action.Attribute("Parameters")!.Value.Contains(
            "password=%SECRET_PASSWORD%", StringComparison.Ordinal));
        Assert.Contains("SECRET_PASSWORD=DoNotExpose", action.Attribute("SecretEnv")!.Value);
        Assert.DoesNotContain("DoNotExpose", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickFix_ShouldUndoInOneStep()
    {
        var document = new TextDocument("<old />");

        XmlEditorQuickFixApplier.ReplaceAsSingleUndo(document, "<new />");
        Assert.Equal("<new />", document.Text);

        document.UndoStack.Undo();

        Assert.Equal("<old />", document.Text);
        Assert.False(document.UndoStack.CanUndo);
    }

    [Fact]
    public void SerializeWatchList_ShouldPreserveRootFieldsAndComments()
    {
        const string xml = """
            <!-- vocabulary comment -->
            <WatchList GlobalVariablesFile="Parameters\GlobalVariables.json">
              <!-- pipeline comment -->
              <WatchItem Tag="Case" Path="C:\triggers" Filter="*.txt" IsEnabled="false" BuildBasePath="C:\builds">
                <Event Type="Renamed">
                  <!-- action comment -->
                  <Action Type="RunCommand" Command="tool.exe" />
                </Event>
              </WatchItem>
            </WatchList>
            """;
        var config = WatchListXmlParser.DeserializeWatchList(xml)!;

        var serialized = XDocument.Parse(WatchListXmlParser.SerializeWatchList(config));

        Assert.Equal("Parameters\\GlobalVariables.json", serialized.Root!.Attribute("GlobalVariablesFile")!.Value);
        Assert.Equal("false", serialized.Descendants("WatchItem").Single().Attribute("IsEnabled")!.Value);
        Assert.Equal("C:\\builds", serialized.Descendants("WatchItem").Single().Attribute("BuildBasePath")!.Value);
        Assert.Contains("vocabulary comment", serialized.ToString(), StringComparison.Ordinal);
        Assert.Contains("pipeline comment", serialized.ToString(), StringComparison.Ordinal);
        Assert.Contains("action comment", serialized.ToString(), StringComparison.Ordinal);
    }

    private string WriteParameterFile(string contents)
    {
        var path = Path.Combine(_directory, $"parameters-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    private string WatchItemXml(
        string? parameterFile = null,
        string? profile = null,
        string? command = "tool.exe",
        string? parameters = "")
    {
        var children = new List<object>();
        if (parameterFile is not null)
        {
            var initAttributes = new List<object>
            {
                new XAttribute("Tag", "Initialize"),
                new XAttribute("ParameterFile", parameterFile),
            };
            if (profile is not null)
                initAttributes.Add(new XAttribute("Profile", profile));
            children.Add(new XElement("Initialize", initAttributes));
        }

        if (command is not null)
        {
            children.Add(new XElement("Action",
                new XAttribute("Tag", "Run"),
                new XAttribute("Type", "RunCommand"),
                new XAttribute("Command", command),
                new XAttribute("Parameters", parameters ?? "")));
        }

        var root = new XElement("WatchItem",
            new XAttribute("Tag", "Case"),
            new XAttribute("Path", _directory),
            new XAttribute("Filter", "*.txt"),
            new XElement("Event",
                new XAttribute("Type", "Renamed"),
                new XElement("ActionGroup", new XAttribute("Tag", "Setup"), children)));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
