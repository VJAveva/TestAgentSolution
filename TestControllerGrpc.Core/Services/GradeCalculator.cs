using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Computes the build's letter grade from pass rate and high-severity
/// penalties. Deterministic and explainable — every penalty appears in the
/// breakdown lines so a user can see exactly why a build got its grade.
/// Penalty weights and A–F cut points are configurable via
/// <see cref="GradeWeights"/> (appsettings → BuildReportCard:Grade).
/// </summary>
public sealed class GradeCalculator
{
    private readonly BuildReportCardConfig _config;

    public GradeCalculator(BuildReportCardConfig config) => _config = config;

    public BuildGradeResult Compute(
        double passRate,
        int regressionCount,
        int psrFailureCount,
        int psrWarningCount,
        int cisBelow90)
    {
        var w = _config.Grade;
        var lines = new List<string>
        {
            $"Base pass rate: {passRate:0.0}%",
        };

        double penalty = 0;

        if (regressionCount > 0)
        {
            var p = regressionCount * w.RegressionPenalty;
            penalty += p;
            lines.Add($"Regressions ({regressionCount}): -{p:0.0}");
        }
        if (psrFailureCount > 0)
        {
            var p = psrFailureCount * w.PsrFailurePenalty;
            penalty += p;
            lines.Add($"PSR failures ({psrFailureCount}): -{p:0.0}");
        }
        if (psrWarningCount > 0)
        {
            var p = psrWarningCount * w.PsrWarningPenalty;
            penalty += p;
            lines.Add($"PSR warnings ({psrWarningCount}): -{p:0.0}");
        }
        if (cisBelow90 > 0)
        {
            var p = cisBelow90 * w.CiBelow90Penalty;
            penalty += p;
            lines.Add($"CIs below 90% ({cisBelow90}): -{p:0.0}");
        }

        var score = Math.Max(0, passRate - penalty);

        var letter =
            score >= w.AThreshold ? "A" :
            score >= w.BThreshold ? "B" :
            score >= w.CThreshold ? "C" :
            score >= w.DThreshold ? "D" : "F";

        var verdict = letter switch
        {
            "A" => "Ship it",
            "B" => "Ship with caveats",
            "C" => "Review required",
            _ => "Do not ship",
        };

        var severity = letter switch
        {
            "A" => ReportSeverity.Pass,
            "B" or "C" => ReportSeverity.Warn,
            _ => ReportSeverity.Fail,
        };

        lines.Add($"Final score: {score:0.0}% → Grade {letter}");

        return new BuildGradeResult
        {
            Letter = letter,
            Score = score,
            BasePassRate = passRate,
            Verdict = verdict,
            BreakdownLines = lines,
            Severity = severity,
        };
    }
}
