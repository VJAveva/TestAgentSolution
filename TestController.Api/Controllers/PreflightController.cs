using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestController.Api.Security;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

/// <summary>
/// Pre-flight checks, served by the host that actually executes pipelines.
/// </summary>
/// <remarks>
/// This lives on the controller, not the web tier, because the checks probe paths and the answer
/// depends on WHO is asking. The IIS pool runs as ApplicationPoolIdentity (machine account), which
/// cannot read the build-drop shares; the controller runs as the operator and can. Checking from
/// the web tier therefore reported a perfectly good drop folder as missing and blocked the run.
/// The web tier proxies here so the checks run under the identity that will do the work.
/// </remarks>
[ApiController]
[Route("api/preflight")]
[Authorize(Policy = SecurityPolicies.User)]
public class PreflightController : ControllerBase
{
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly PreflightService _preflight;
    private readonly IAppLogger _logger;

    public PreflightController(
        IVocabularyMonitor vocabMonitor,
        PreflightService preflight,
        IAppLogger logger)
    {
        _vocabMonitor = vocabMonitor;
        _preflight = preflight;
        _logger = logger;
    }

    /// <summary>POST /api/preflight/{tag} — run every check for one pipeline without starting it.</summary>
    [HttpPost("{tag}")]
    public IActionResult Check(string tag)
    {
        var config = _vocabMonitor.CurrentConfig;
        if (config is null) return Problem("WatchList is not loaded.", statusCode: 503);

        var pipeline = config.WatchItems
            .FirstOrDefault(w => string.Equals(w.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (pipeline is null) return NotFound($"Pipeline '{tag}' not found.");

        PreflightReport report;
        try
        {
            report = _preflight.Check(config, pipeline, scope: PreflightScope.Pipeline);
        }
        catch (Exception ex)
        {
            _logger.Error("Preflight", $"Pre-flight failed to run for '{tag}'", ex);
            return Problem($"Pre-flight failed to run: {ex.Message}", statusCode: 500);
        }

        _logger.Info("Preflight", $"{report.Target}: {report.Summary}");
        return Ok(PreflightResponse.From(report, Environment.MachineName));
    }
}

/// <summary>Wire shape shared by the controller endpoint and the web tier's proxy.</summary>
public static class PreflightResponse
{
    public static object From(PreflightReport report, string checkedBy) => new
    {
        target = report.Target,
        canRun = report.CanRun,
        hasErrors = report.HasErrors,
        hasWarnings = report.HasWarnings,
        summary = report.Summary,
        elapsedSeconds = report.Elapsed.TotalSeconds,
        checkedBy,
        checks = report.Checks.Select(c => new
        {
            group = c.Group,
            name = c.Name,
            status = c.Status.ToString(),
            detail = c.Detail,
            fixHint = c.FixHint,
        }),
        tokens = report.Tokens.Select(t => new
        {
            token = t.Token,
            value = t.Value,
            sourceLayer = t.SourceLayer,
        }),
        text = report.ToPlainText(),
    };
}
