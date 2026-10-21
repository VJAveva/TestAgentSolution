using System.Text.RegularExpressions;

namespace TestControllerGrpc.Core.Fir;

/// <summary>One parsed "at Type.Method(...) in File.cs:line N" entry.</summary>
public sealed record FirStackFrame(string DeclaringType, string Method, string? SourceFile = null, int? SourceLine = null);

/// <summary>
/// Reads a .NET stack trace. Everything here keys off framework-level names (Assert, TestCleanup, the
/// runtime's own plumbing), never off a product, so it holds for any TRX.
/// </summary>
public static partial class FirStackTrace
{
    /// <summary>Frames that only ever carry the harness getting to the failure, never the failure itself.</summary>
    private static readonly string[] HarnessPrefixes =
    [
        "Microsoft.VisualStudio.TestTools.",
        "Microsoft.VisualStudio.TestPlatform.",
        "NUnit.Framework.Assert",
        "NUnit.Framework.Legacy",
        "Xunit.Assert",
        "Xunit.Sdk.",
        "FluentAssertions.",
        "Shouldly.",
        "System.Runtime.CompilerServices.",
        "System.Runtime.ExceptionServices.",
        "System.Threading.Tasks.Task",
        "System.Threading.ExecutionContext",
    ];

    public static IReadOnlyList<FirStackFrame> Parse(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return [];

        var frames = new List<FirStackFrame>();
        foreach (var line in stackTrace.AsSpan().EnumerateLines())
        {
            if (line.IsWhiteSpace()) continue;

            var match = FrameRegex().Match(line.ToString());
            if (!match.Success) continue;

            var (type, method) = SplitMember(match.Groups["decl"].Value.Trim());
            var file = match.Groups["file"].Success ? match.Groups["file"].Value.Trim() : null;
            int? sourceLine = match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out var n) ? n : null;

            frames.Add(new FirStackFrame(type, method, file, sourceLine));
        }

        return frames;
    }

    public static bool IsHarness(FirStackFrame frame)
        => HarnessPrefixes.Any(p => frame.DeclaringType.StartsWith(p, StringComparison.Ordinal));

    /// <summary>The deepest frame that is not harness plumbing - the first thing a reader wants to see.</summary>
    public static FirStackFrame? FailingFrame(IReadOnlyList<FirStackFrame> frames)
        => frames.FirstOrDefault(f => !IsHarness(f));

    /// <summary>
    /// True when any frame is a cleanup method. Cleanup runs after the test body, so an assertion raised
    /// anywhere under it is a verdict on accumulated results, not on the thing that broke.
    /// </summary>
    public static bool RaisedInCleanup(IReadOnlyList<FirStackFrame> frames)
        => frames.Any(f => CleanupRegex().IsMatch(f.Method));

    public static FirFrameKind ClassifyKind(FirStackFrame frame, string testClassName)
    {
        if (CleanupRegex().IsMatch(frame.Method)) return FirFrameKind.Cleanup;
        if (InitRegex().IsMatch(frame.Method)) return FirFrameKind.Init;
        return IsTestClass(frame.DeclaringType, testClassName) ? FirFrameKind.TestCode : FirFrameKind.Library;
    }

    private static bool IsTestClass(string declaringType, string testClassName)
    {
        if (string.IsNullOrEmpty(declaringType) || string.IsNullOrEmpty(testClassName)) return false;
        if (declaringType.Equals(testClassName, StringComparison.Ordinal)) return true;

        // Some runners write className assembly-qualified.
        var comma = testClassName.IndexOf(',');
        return comma > 0 && declaringType.Equals(testClassName[..comma].Trim(), StringComparison.Ordinal);
    }

    private static (string Type, string Method) SplitMember(string declaration)
    {
        var asyncFrame = AsyncStateMachineRegex().Match(declaration);
        if (asyncFrame.Success)
            return (asyncFrame.Groups["type"].Value.TrimEnd('+', '.'), asyncFrame.Groups["method"].Value);

        var dot = declaration.LastIndexOf('.');
        var type = dot <= 0 ? string.Empty : declaration[..dot];
        var method = dot <= 0 ? declaration : declaration[(dot + 1)..];

        var generic = method.IndexOf('[');
        if (generic > 0) method = method[..generic];

        return (type, method);
    }

    [GeneratedRegex(@"^\s*at\s+(?<decl>[^(]+?)\s*\([^)]*\)(?:\s+in\s+(?<file>.+?):line\s+(?<line>\d+))?\s*$")]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"^(?<type>.+?)[+.]<(?<method>[^>]+)>d__\d+\.MoveNext$")]
    private static partial Regex AsyncStateMachineRegex();

    [GeneratedRegex(@"(?i)^(?:test|class|assembly|my)?(?:cleanup|teardown)$|^onetimeteardown$|^dispose(?:async)?$")]
    private static partial Regex CleanupRegex();

    [GeneratedRegex(@"(?i)^(?:test|class|assembly|my)?(?:initialize|init|setup)$|^onetimesetup$|^\.?ctor$")]
    private static partial Regex InitRegex();
}
