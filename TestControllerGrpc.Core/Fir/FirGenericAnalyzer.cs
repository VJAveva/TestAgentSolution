using System.Text.RegularExpressions;

namespace TestControllerGrpc.Core.Fir;

/// <summary>
/// Layer 1: everything a TRX can say on its own. It knows MSTest/vstest structure and nothing about any
/// product or keyword framework - rule packs add that on top. With no pack this still names the failure
/// type, the frame that raised it, the first error-level output line and any leaked secret.
/// </summary>
public static partial class FirGenericAnalyzer
{
    public const string PackName = "generic";

    public static FirReport Analyze(FirTrxInput input, FirRuleSet? rules = null)
    {
        rules ??= FirRuleSet.Empty;
        var frames = FirStackTrace.Parse(input.StackTrace);
        var failing = FirStackTrace.FailingFrame(frames);
        var raisedInCleanup = FirStackTrace.RaisedInCleanup(frames);

        var failureType = DetectFailureType(input.Outcome, input.ErrorMessage, raisedInCleanup);
        var frame = failing is null
            ? null
            : new FirFailingFrame(
                FirStackTrace.ClassifyKind(failing, input.ClassName),
                failing.Method,
                failing.DeclaringType,
                failing.SourceFile,
                failing.SourceLine);

        var (firstError, noiseLines, secretLines) = ScanOutput(input, rules);

        return new FirReport
        {
            Test = input.TestName,
            Outcome = input.Outcome,
            Duration = input.Duration,
            Computer = input.Computer,
            RulePacks = [PackName],
            FailureType = failureType,
            FailingFrame = frame,
            Verdict = FirRedactor.Mask(BuildVerdict(input, failureType, frame, firstError)),
            FirstErrorLine = firstError is null ? null : FirRedactor.Mask(firstError),
            NoiseLines = noiseLines,
            SecretsFound = secretLines,
        };
    }

    internal static FirFailureType DetectFailureType(string outcome, string? errorMessage, bool raisedInCleanup)
    {
        if (IsTimeout(outcome, errorMessage)) return FirFailureType.Timeout;

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            // Assertion first: an AssertFailedException also matches the exception shape.
            if (AssertionRegex().IsMatch(errorMessage))
                return raisedInCleanup ? FirFailureType.SummaryCheck : FirFailureType.Assertion;

            if (ExceptionRegex().IsMatch(errorMessage))
                return FirFailureType.Exception;
        }

        return FirFailureType.Unknown;
    }

    /// <summary>
    /// One pass over the output. Secrets are counted as distinct LINES, so a pack rule and a generic
    /// pattern firing on the same line report one leak rather than two.
    /// </summary>
    internal static (string? FirstErrorLine, int NoiseLines, int SecretLines) ScanOutput(FirTrxInput input, FirRuleSet rules)
    {
        string? firstError = null;
        var noise = 0;
        var secrets = 0;

        foreach (var section in input.OutputSections())
        {
            foreach (var line in section.AsSpan().EnumerateLines())
            {
                if (line.IsWhiteSpace()) continue;

                if (HarnessNoiseRegex().IsMatch(line) || MatchesPackNoise(rules, line))
                {
                    noise++;
                    continue;
                }

                var text = line.ToString();
                if (MatchesSecretLine(rules, text) || FirRedactor.CountSecrets(text) > 0) secrets++;

                if (firstError is null && ErrorLineRegex().IsMatch(line))
                    firstError = text.Trim();
            }
        }

        return (firstError, noise, secrets);
    }

    private static bool MatchesPackNoise(FirRuleSet rules, ReadOnlySpan<char> line)
    {
        foreach (var pattern in rules.Noise)
        {
            try
            {
                if (pattern.IsMatch(line)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // A pack's runaway pattern must not decide the whole scan.
            }
        }

        return false;
    }

    private static bool MatchesSecretLine(FirRuleSet rules, string line)
    {
        foreach (var pattern in rules.SecretLines)
        {
            try
            {
                if (pattern.IsMatch(line)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return false;
    }

    internal static string BuildVerdict(FirTrxInput input, FirFailureType type, FirFailingFrame? frame, string? firstError)
    {
        var at = frame is null ? string.Empty : $" in {Describe(frame)}";

        return type switch
        {
            FirFailureType.SummaryCheck =>
                $"The test failed on a summary check{at} raised after the run, so that frame is not the cause - "
                + (firstError is null
                    ? "the step that broke is earlier in the output."
                    : $"the first error-level line is \"{Clip(firstError)}\"."),

            FirFailureType.Assertion => $"An assertion failed{at}.{Suffix(firstError)}",
            FirFailureType.Exception => $"{ExceptionTypeOf(input.ErrorMessage) ?? "An exception"} was thrown{at}.{Suffix(firstError)}",
            FirFailureType.Timeout => $"The test timed out after {Humanize(input.Duration)}.{Suffix(firstError)}",
            _ => $"The test reported {Describe(input.Outcome)} and the TRX does not say how.{Suffix(firstError)}",
        };
    }

    private static string Suffix(string? firstError)
        => firstError is null ? string.Empty : $" First error-level line: \"{Clip(firstError)}\".";

    private static string Describe(FirFailingFrame frame)
        => frame.SourceFileLine is null ? frame.Method : $"{frame.Method} ({frame.SourceFileLine})";

    private static string Describe(string outcome)
        => string.IsNullOrWhiteSpace(outcome) ? "no outcome" : outcome;

    internal static string? ExceptionTypeOf(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage)) return null;
        var match = ExceptionRegex().Match(errorMessage);
        return match.Success ? match.Groups["type"].Value : null;
    }

    internal static string Humanize(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "0 s";
        if (duration.TotalMinutes < 1) return $"{duration.Seconds} s";
        if (duration.TotalHours < 1) return $"{duration.Minutes} m {duration.Seconds} s";
        return $"{(int)duration.TotalHours} h {duration.Minutes} m";
    }

    private static string Clip(string line, int max = 160)
        => line.Length <= max ? line : string.Concat(line.AsSpan(0, max - 1).TrimEnd(), "\u2026");

    private static bool IsTimeout(string outcome, string? errorMessage)
        => string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)
           || (!string.IsNullOrWhiteSpace(errorMessage) && TimeoutRegex().IsMatch(errorMessage));

    [GeneratedRegex(@"(?i)(?:\b(?:Assert|CollectionAssert|StringAssert)\.\w+(?:\(\))?\s+(?:failed|failure)\b)|(?:\bAssert(?:Failed|ion)Exception\b)|(?:\bXunit\.Sdk\.\w+Exception\b)|(?:\bAssert\.That\b)|(?:^[ \t]*Expected:[ \t].*[\r\n]+[ \t]*But was:)", RegexOptions.Multiline)]
    private static partial Regex AssertionRegex();

    [GeneratedRegex(@"(?m)^\s*(?<type>[A-Za-z_][\w.`+]*(?:Exception|Error))\s*:")]
    private static partial Regex ExceptionRegex();

    [GeneratedRegex(@"(?i)\b(?:test(?:\s+\w+){0,3}\s+timed\s+out|exceeded\s+(?:the\s+)?execution\s+timeout|timeout\s+expired)\b")]
    private static partial Regex TimeoutRegex();

    // vstest writes these into every TRX regardless of product, so they belong to the generic layer.
    [GeneratedRegex(@"^\s*(?:Warning:\s*)?Test Run deployment issue\b|^\s*Parameter name:\s*startIndex\s*$")]
    private static partial Regex HarnessNoiseRegex();

    [GeneratedRegex(@"(?i)\b(?:error|failed|failure|exception|fatal)\b\s*[:\]]|\ban?\s+error\s+occurred\b|\bfailed\s+to\b|^\s*\[?(?:ERROR|FATAL|SEVERE)\b|^\s*Unhandled exception")]
    private static partial Regex ErrorLineRegex();
}
