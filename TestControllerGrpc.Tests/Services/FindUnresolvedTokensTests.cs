using System.Reflection;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// <see cref="ParameterResolver.FindUnresolvedTokens"/> is the last gate before an action is
/// dispatched. It used to inspect four fields, so an unresolved token in <c>To</c>, <c>Title</c> or
/// <c>Body</c> passed the gate and a mail went out addressed to the literal text "[_EmailCheck]".
/// </summary>
public class FindUnresolvedTokensTests
{
    private static PipelineExecutionContext Empty() => new();

    private static PipelineExecutionContext With(params (string Key, string Value)[] entries)
    {
        var ctx = new PipelineExecutionContext();
        foreach (var (k, v) in entries) ParameterResolver.SetParameter(ctx, k, v, ParameterRank.Global);
        return ctx;
    }

    public static TheoryData<string> StrictFieldNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[]
                 {
                     nameof(ActionConfig.AgentName), nameof(ActionConfig.Command),
                     nameof(ActionConfig.Parameters), nameof(ActionConfig.CompletionCheckCommand),
                     nameof(ActionConfig.UserName), nameof(ActionConfig.Password),
                     nameof(ActionConfig.From), nameof(ActionConfig.To),
                     nameof(ActionConfig.Attachment), nameof(ActionConfig.Embed),
                     nameof(ActionConfig.LargeFilesShare),
                 })
        {
            data.Add(name);
        }
        return data;
    }

    public static TheoryData<string> ProseTolerantFieldNames() =>
        new() { nameof(ActionConfig.Title), nameof(ActionConfig.Body) };

    [Theory]
    [MemberData(nameof(StrictFieldNames))]
    [MemberData(nameof(ProseTolerantFieldNames))]
    public void FindUnresolvedTokens_Should_ReportTheToken_When_AnySubstitutedFieldCarriesIt(string fieldName)
    {
        var action = new ActionConfig { Type = ActionType.SendMail };
        typeof(ActionConfig).GetProperty(fieldName)!.SetValue(action, "[_MissingToken]");

        var missing = ParameterResolver.FindUnresolvedTokens(action, Empty());

        Assert.Equal(["_MissingToken"], missing);
    }

    /// <summary>A stray bracket in a command line or a recipient address is never intentional.</summary>
    [Theory]
    [MemberData(nameof(StrictFieldNames))]
    public void FindUnresolvedTokens_Should_ReportPlainToken_When_FieldIsStrict(string fieldName)
    {
        var action = new ActionConfig { Type = ActionType.SendMail };
        typeof(ActionConfig).GetProperty(fieldName)!.SetValue(action, "[EmailAddress]");

        Assert.Equal(["EmailAddress"], ParameterResolver.FindUnresolvedTokens(action, Empty()));
    }

    /// <summary>
    /// "[INFO] build finished" in a subject or body is a label, not a parameter. Failing a run over
    /// it would be worse than the bug the gate guards against.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProseTolerantFieldNames))]
    public void FindUnresolvedTokens_Should_AllowProse_When_FieldIsTitleOrBody(string fieldName)
    {
        var action = new ActionConfig { Type = ActionType.SendMail };
        typeof(ActionConfig).GetProperty(fieldName)!.SetValue(action, "[INFO] nightly run [WARN] slow");

        Assert.Empty(ParameterResolver.FindUnresolvedTokens(action, Empty()));
    }

    /// <summary>
    /// The prose tolerance must not swallow a real parameter reference: an unresolved
    /// [_BuildNumber] in the body means the parameter source failed to load.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProseTolerantFieldNames))]
    public void FindUnresolvedTokens_Should_ReportUnderscoreToken_When_MixedWithProse(string fieldName)
    {
        var action = new ActionConfig { Type = ActionType.SendMail };
        typeof(ActionConfig).GetProperty(fieldName)!
            .SetValue(action, "[INFO] build [_BuildNumber] finished");

        Assert.Equal(["_BuildNumber"], ParameterResolver.FindUnresolvedTokens(action, Empty()));
    }

    [Fact]
    public void FindUnresolvedTokens_Should_ReturnEmpty_When_UnderscoreTokenInBodyResolves()
    {
        var action = new ActionConfig
        {
            Type = ActionType.SendMail,
            Body = "[INFO] build [_BuildNumber] finished",
        };

        Assert.Empty(ParameterResolver.FindUnresolvedTokens(action, With(("_BuildNumber", "1.2.3"))));
    }

    /// <summary>
    /// Every string field that <see cref="ParameterResolver.ResolveAction"/> substitutes must also be
    /// inspected by the gate. Asserting the two lists agree stops them drifting apart again — the
    /// drift is what left To/Title/Body unchecked for so long.
    /// </summary>
    [Fact]
    public void FindUnresolvedTokens_Should_CoverEveryFieldResolveActionSubstitutes()
    {
        var probe = new ActionConfig { Type = ActionType.SendMail };
        var stringProps = typeof(ActionConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p is { CanRead: true, CanWrite: true })
            .ToList();

        foreach (var p in stringProps) p.SetValue(probe, "[_Probe]");

        var resolved = ParameterResolver.ResolveAction(probe, With(("_Probe", "VALUE")));

        var substituted = stringProps
            .Where(p => (string?)p.GetValue(resolved) == "VALUE")
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // The gate must flag a token in each of those fields; check them one at a time so the
        // failure message names the field that is not covered.
        foreach (var name in substituted)
        {
            var single = new ActionConfig();
            typeof(ActionConfig).GetProperty(name)!.SetValue(single, "[_MissingToken]");

            Assert.True(
                ParameterResolver.FindUnresolvedTokens(single, Empty()).Count == 1,
                $"ResolveAction substitutes ActionConfig.{name}, but FindUnresolvedTokens does not inspect it. "
                + "An unresolved token there would be dispatched as literal text.");
        }
    }
    [Fact]
    public void FindUnresolvedTokens_Should_ReturnEmpty_When_EveryTokenResolves()
    {
        var action = new ActionConfig
        {
            Type = ActionType.SendMail,
            AgentName = "[_Agent1]",
            To = "[_EmailCheck]",
            Title = "Build [_BuildNumber]",
            Body = "Drop = [_DropLocation]",
        };

        var ctx = With(
            ("_Agent1", "jvgr1"),
            ("_EmailCheck", "qa@corp.com"),
            ("_BuildNumber", "1.2.3"),
            ("_DropLocation", @"\\share\drop"));

        Assert.Empty(ParameterResolver.FindUnresolvedTokens(action, ctx));
    }

    [Fact]
    public void FindUnresolvedTokens_Should_ListEachTokenOnce_When_RepeatedAcrossFields()
    {
        var action = new ActionConfig
        {
            Type = ActionType.SendMail,
            Title = "Build [_BuildNumber]",
            Body = "Build [_BuildNumber] finished",
            Attachment = @"C:\logs\[_BuildNumber]\summary.html",
        };

        Assert.Equal(["_BuildNumber"], ParameterResolver.FindUnresolvedTokens(action, Empty()));
    }

    /// <summary>
    /// Tag/Comment are never substituted, so bracketed text there is a label, not a broken token.
    /// Flagging them would block runs over a comment.
    /// </summary>
    [Fact]
    public void FindUnresolvedTokens_Should_IgnoreMetadata_When_BracketsAppearInTagOrComment()
    {
        var action = new ActionConfig
        {
            Type = ActionType.RunCommand,
            Command = @"C:\tools\run.bat",
            Tag = "[Stage 1] Install",
            Comment = "see [TICKET-42]",
            Order = "[first]",
        };

        Assert.Empty(ParameterResolver.FindUnresolvedTokens(action, Empty()));
    }
}
