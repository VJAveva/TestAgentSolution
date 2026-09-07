using System.IO;
using System.Net.Mail;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestController.Api.Security;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Reporting;
using TestControllerGrpc.Ado.Reporting.Llm;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.Api.Controllers;

/// <summary>
/// Regression tab / CIRP endpoints (docs/AzureIntegration/FEATURE-ARCHITECTURE.md §5, §7).
/// Backed by <see cref="MockRegressionDataProvider"/> until real Azure DevOps ingest
/// (Phases 0-5) lands — see the class doc there for why. The response shapes are the
/// real v2 contract; only the data source is temporary.
/// </summary>
[ApiController]
[Route("api/impact")]
[Authorize(Policy = SecurityPolicies.User)]
public class ImpactController : ControllerBase
{
    private readonly IRegressionDataProvider _provider;
    private readonly IRegressionSourceCatalog _catalog;
    private readonly IChurnReportBuilder _reportBuilder;
    private readonly IChurnXlsxBuilder _xlsx;
    private readonly IChurnSummarizer _summarizer;
    private readonly ILlmChangeSummarizer _llm;
    private readonly IConfiguration _config;
    private readonly IAppLogger _logger;

    public ImpactController(
        IRegressionDataProvider provider,
        IRegressionSourceCatalog catalog,
        IChurnReportBuilder reportBuilder,
        IChurnXlsxBuilder xlsx,
        IChurnSummarizer summarizer,
        ILlmChangeSummarizer llm,
        IConfiguration config,
        IAppLogger logger)
    {
        _provider = provider;
        _catalog = catalog;
        _reportBuilder = reportBuilder;
        _xlsx = xlsx;
        _summarizer = summarizer;
        _llm = llm;
        _config = config;
        _logger = logger;
    }

    private string Corr => HttpContext.Items["CorrelationId"] as string ?? "";

    /// <summary>GET /api/impact/consolidated?from&amp;to — R1/R2/R5/R9 subsystem rollup.</summary>
    [HttpGet("consolidated")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<ConsolidatedImpact>> GetConsolidated(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? branch, CancellationToken ct)
    {
        var to_ = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from_ = from ?? to_.AddDays(-7);
        if (from_ > to_)
            return ValidationProblem("'from' must not be after 'to'.");

        var result = await _provider.GetConsolidatedAsync(from_, to_, branch, ct);
        return Ok(result);
    }

    /// <summary>GET /api/impact/scope?from&amp;to&amp;category — R11-13 recommended plan.</summary>
    [HttpGet("scope")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<RegressionScope>> GetScope(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] RegressionCategoryKind? category, [FromQuery] string? branch, CancellationToken ct)
    {
        var to_ = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from_ = from ?? to_.AddDays(-7);
        if (from_ > to_)
            return ValidationProblem("'from' must not be after 'to'.");

        var result = await _provider.GetScopeAsync(from_, to_, category, branch, ct);
        return Ok(result);
    }

    /// <summary>GET /api/impact/sync-status — R14 status bar.</summary>
    [HttpGet("sync-status")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<RegressionSyncStatus>> GetSyncStatus(CancellationToken ct)
    {
        var result = await _provider.GetSyncStatusAsync(ct);
        return Ok(result);
    }

    /// <summary>POST /api/impact/suites — R7/R9 col 8-9 inline suite chip edit.</summary>
    [HttpPost("suites")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<IActionResult> PostSuiteEdit([FromBody] RegressionSuiteEdit edit, CancellationToken ct)
    {
        var corr = Corr;
        if (string.IsNullOrWhiteSpace(edit.Subsystem) || string.IsNullOrWhiteSpace(edit.SuiteId))
            return ValidationProblem("Both 'subsystem' and 'suiteId' are required.");

        try
        {
            await _provider.ApplySuiteEditAsync(edit, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            _logger.Warn("Regression", $"[{corr}] Suite edit rejected: {ex.Message}");
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>POST /api/impact/test-matches — impact-mapped Test Cases + change-analysis fallback for one component (grid row-expand).</summary>
    [HttpPost("test-matches")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<ImpactedComponentAnalysis>> PostTestMatches([FromBody] SubsystemRow row, CancellationToken ct)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Component))
            return ValidationProblem("A subsystem row with a component is required.");

        var matcher = HttpContext.RequestServices.GetService<IRegressionImpactMatcher>();
        IReadOnlyList<ImpactedTestCaseMatch> matches = [];
        string? indexHealthMessage = null;
        if (matcher is not null)
        {
            try
            {
                matches = await matcher.MatchAsync(row, ct);
            }
            catch (ImpactIndexUnavailableException ex)
            {
                // Keep the deterministic change analysis usable while clearly identifying why indexed
                // test-case matches are unavailable. Do not turn an operational index problem into a blank pane.
                indexHealthMessage = ex.Health.Message;
            }
        }

        // Always return the offline change summary + recommended tests so an empty match set is still useful.
        return Ok(new ImpactedComponentAnalysis(
            matches,
            RegressionChangeAnalyzer.Summarize(row),
            RegressionChangeAnalyzer.RecommendTests(row),
            indexHealthMessage));
    }

    /// <summary>GET /api/impact/connection — ADO connection/credential banner state.</summary>
    [HttpGet("connection")]
    [RequirePermission(Permission.CodeChurn_View)]
    public ActionResult<RegressionConnectionInfo> GetConnection() => Ok(_catalog.GetConnectionInfo());

    /// <summary>GET /api/impact/components — component/definition picker list.</summary>
    [HttpGet("components")]
    [RequirePermission(Permission.CodeChurn_View)]
    public ActionResult<IReadOnlyList<RegressionComponentRef>> GetComponents() => Ok(_catalog.GetComponents());

    /// <summary>GET /api/impact/components/{definitionId}/builds — recent builds for the build picker.</summary>
    [HttpGet("components/{definitionId:int}/builds")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<IReadOnlyList<RegressionBuildRef>>> GetComponentBuilds(int definitionId, CancellationToken ct)
        => Ok(await _catalog.GetComponentBuildsAsync(definitionId, ct));

    /// <summary>GET /api/impact/components/{definitionId}/builds/{buildId} — one build's impact row.</summary>
    [HttpGet("components/{definitionId:int}/builds/{buildId:int}")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<SubsystemRow>> GetComponentBuild(int definitionId, int buildId, CancellationToken ct)
    {
        var row = await _catalog.GetComponentBuildImpactAsync(definitionId, buildId, ct);
        return row is null ? NotFound() : Ok(row);
    }

    /// <summary>GET /api/impact/branches — release branches for the parallel-dev switcher.</summary>
    [HttpGet("branches")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<IReadOnlyList<string>>> GetBranches(CancellationToken ct)
        => Ok(await _catalog.GetBranchesAsync(ct));

    /// <summary>GET /api/impact/health — verified ADO probe + loaded-component count (deploy sanity check).</summary>
    [HttpGet("health")]
    public async Task<ActionResult<RegressionHealth>> GetHealth(CancellationToken ct)
        => Ok(await _catalog.CheckHealthAsync(ct));

    /// <summary>GET /api/impact/summary?from&amp;to&amp;branch — computed AI summary of the scope.</summary>
    [HttpGet("summary")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<ChurnSummary>> GetSummary(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? branch,
        [FromQuery] RegressionRowFilterOptions filter, CancellationToken ct)
    {
        var (from_, to_) = ResolveRange(from, to);
        if (from_ > to_)
            return ValidationProblem("'from' must not be after 'to'.");
        var report = await BuildReportAsync(from_, to_, branch, filter, ct);
        return Ok(_summarizer.Summarize(report));
    }

    /// <summary>GET /api/impact/ai-summary?from&amp;to&amp;branch — diff-grounded LLM summary (falls back to the offline summary when the model is disabled).</summary>
    [HttpGet("ai-summary")]
    [RequirePermission(Permission.CodeChurn_View)]
    public async Task<ActionResult<ChurnSummary>> GetAiSummary(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? branch,
        [FromQuery] RegressionRowFilterOptions filter, CancellationToken ct)
    {
        var (from_, to_) = ResolveRange(from, to);
        if (from_ > to_)
            return ValidationProblem("'from' must not be after 'to'.");
        var report = await BuildReportAsync(from_, to_, branch, filter, ct);
        return Ok(await _llm.SummarizeReleaseAsync(report, ct));
    }

    /// <summary>GET /api/impact/report?from&amp;to&amp;branch&amp;format=csv|html — downloadable churn report.</summary>
    [HttpGet("report")]
    [RequirePermission(Permission.CodeChurn_Export)]
    public async Task<IActionResult> GetReport(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? branch, [FromQuery] string? format,
        [FromQuery] RegressionRowFilterOptions filter, CancellationToken ct)
    {
        var (from_, to_) = ResolveRange(from, to);
        if (from_ > to_)
            return ValidationProblem("'from' must not be after 'to'.");
        var report = await BuildReportAsync(from_, to_, branch, filter, ct);
        CodeChurnReportModel? policy = await BuildPolicyModelAsync(report, ct);
        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            var matches = await MatchTestCasesAsync(report.Rows, ct);
            return File(_xlsx.BuildXlsx(report, matches, policy),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"churn-report-{from_:yyyyMMdd}-{to_:yyyyMMdd}.xlsx");
        }
        var isCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        var content = isCsv ? _reportBuilder.BuildCsv(report, policy) : _reportBuilder.BuildHtml(report, policy);
        var fileName = $"churn-report-{from_:yyyyMMdd}-{to_:yyyyMMdd}.{(isCsv ? "csv" : "html")}";
        return File(Encoding.UTF8.GetBytes(content), isCsv ? "text/csv" : "text/html", fileName);
    }

    /// <summary>POST /api/impact/email — email the churn report (HTML body + CSV attachment) via the host SMTP.</summary>
    [HttpPost("email")]
    [RequirePermission(Permission.CodeChurn_Export)]
    public async Task<IActionResult> EmailReport([FromBody] EmailReportRequest req, [FromQuery] RegressionRowFilterOptions filter, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Recipients))
            return ValidationProblem("At least one recipient is required.");
        var (from_, to_) = ResolveRange(req.From, req.To);
        if (from_ > to_)
            return ValidationProblem("'from' must not be after 'to'.");

        var fromAddress = _config["BuildResults:FromAddress"];
        if (string.IsNullOrWhiteSpace(fromAddress))
            return ValidationProblem("BuildResults:FromAddress is not configured on the server.");

        var report = await BuildReportAsync(from_, to_, req.Branch, filter, ct);
        CodeChurnReportModel? policy = await BuildPolicyModelAsync(report, ct);
        var smtpServer = _config["BuildResults:SmtpServer"] ?? "smtp";
        var smtpPort = int.TryParse(_config["BuildResults:SmtpPort"], out var p) ? p : 25;
        try
        {
            using var smtp = new SmtpClient(smtpServer, smtpPort) { UseDefaultCredentials = true };
            using var message = new MailMessage(fromAddress, req.Recipients.Replace(';', ','))
            {
                Subject = $"Code churn report \u00b7 {report.RangeText} \u00b7 {report.Rows.Count} component(s)",
                Body = _reportBuilder.BuildHtml(report, policy),
                IsBodyHtml = true,
            };
            var csvName = $"churn-report-{from_:yyyyMMdd}-{to_:yyyyMMdd}.xlsx";
            var matches = await MatchTestCasesAsync(report.Rows, ct);
            message.Attachments.Add(new Attachment(new MemoryStream(_xlsx.BuildXlsx(report, matches, policy)), csvName, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
            await smtp.SendMailAsync(message, ct);
            _logger.Info("Regression", $"[{Corr}] Churn report emailed to {req.Recipients}.");
            return Ok(new { sent = true, recipients = req.Recipients });
        }
        catch (Exception ex)
        {
            _logger.Error("Regression", $"[{Corr}] Failed to email churn report.", ex);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private async Task<ChurnReport> BuildReportAsync(DateOnly from, DateOnly to, string? branch, RegressionRowFilterOptions filter, CancellationToken ct)
    {
        var consolidated = await _provider.GetConsolidatedAsync(from, to, branch, ct);
        // Apply the grid's active filters so exports/emails match what the user sees.
        var rows = RegressionRowFilter.Apply(consolidated.Rows, filter);
        return new ChurnReport(
            ScopeLabel: consolidated.Summary.ScopeLabel,
            RangeText: consolidated.Summary.RangeText,
            From: from,
            To: to,
            GeneratedUtc: DateTimeOffset.UtcNow,
            Rows: rows);
    }

    // Feature roll-up + Task-exclusion accounting. Resolved lazily so a host without the ADO reporting
    // stack still exports, just without the Features section. A hierarchy failure must not kill the report.
    private async Task<CodeChurnReportModel?> BuildPolicyModelAsync(ChurnReport report, CancellationToken ct)
    {
        var builder = HttpContext.RequestServices.GetService<ICodeChurnReportBuilder>();
        if (builder is null) return null;

        try
        {
            return await builder.BuildAsync(report, ct);
        }
        catch (Exception ex)
        {
            _logger.Warn("Regression", $"[{Corr}] Feature roll-up unavailable: {ex.Message}");
            return null;
        }
    }

    // Runs the impact-mapping engine per component when it is registered in this host; degrades to no
    // matches (null) when it is not, so the workbook still exports with just the Code Churn sheet.
    private async Task<IReadOnlyList<ImpactedTestCaseMatch>?> MatchTestCasesAsync(IReadOnlyList<SubsystemRow> rows, CancellationToken ct)
    {
        var matcher = HttpContext.RequestServices.GetService<IRegressionImpactMatcher>();
        if (matcher is null)
            return null;
        try
        {
            return await matcher.MatchManyAsync(rows, ct);
        }
        catch (Exception ex)
        {
            _logger.Warn("Regression", $"[{Corr}] Test-case matching for the workbook failed: {ex.Message}");
            return null;
        }
    }

    private static (DateOnly From, DateOnly To) ResolveRange(DateOnly? from, DateOnly? to)
    {
        var to_ = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from_ = from ?? to_.AddDays(-7);
        return (from_, to_);
    }
}

/// <summary>Body for POST /api/impact/email.</summary>
public sealed record EmailReportRequest(DateOnly? From, DateOnly? To, string? Branch, string Recipients);
