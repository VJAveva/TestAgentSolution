using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TestController.Api.Interceptors;
using TestController.Persistence;
using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;

namespace TestController.Api.Controllers;

/// <summary>
/// Read-only REST endpoints for the RBAC audit log (AuditEntries table).
/// Admin-only via Audit_View / Audit_Export permissions.
/// Per 01_System_Design.md §7.1 and 04_UI_Mockup_Catalog.md Mockup 8.
/// </summary>
[ApiController]
[Route("api/audit")]
public class AuditController : ControllerBase
{
    private readonly IDbContextFactory<OrchestratorDbContext> _dbFactory;
    private readonly SessionAuthInterceptor _authInterceptor;

    public AuditController(
        IDbContextFactory<OrchestratorDbContext> dbFactory,
        SessionAuthInterceptor authInterceptor)
    {
        _dbFactory = dbFactory;
        _authInterceptor = authInterceptor;
    }

    /// <summary>GET /api/audit — paginated, filtered query of AuditEntries.</summary>
    [HttpGet]
    public async Task<IActionResult> Query([FromQuery] AuditQueryParams query, CancellationToken ct)
    {
        var user = await ResolveAdmin(Permission.Audit_View, ct);
        if (user is null) return Forbid();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = BuildQuery(db, query);

        var total = await q.CountAsync(ct);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);

        var items = await q
            .OrderByDescending(a => a.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditEntryDto
            {
                AuditId = a.AuditId,
                UserId = a.UserId,
                GuestId = a.GuestId,
                RoleAtTime = a.RoleAtTime != null ? a.RoleAtTime.Value.ToString() : null,
                ActionName = a.ActionName,
                ResourceId = a.ResourceId,
                Allowed = a.Allowed,
                ReasonCode = a.ReasonCode,
                TimestampUtc = a.TimestampUtc,
                ClientKind = a.ClientKind.ToString(),
                CorrelationId = a.CorrelationId,
            })
            .ToListAsync(ct);

        return Ok(new AuditPageResponse
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>GET /api/audit/export — CSV export of filtered AuditEntries (streamed).</summary>
    [HttpGet("export")]
    public async Task ExportCsv([FromQuery] AuditQueryParams query, CancellationToken ct)
    {
        var user = await ResolveAdmin(Permission.Audit_Export, ct);
        if (user is null)
        {
            Response.StatusCode = 403;
            return;
        }

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"audit_export.csv\"";

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = BuildQuery(db, query).OrderByDescending(a => a.TimestampUtc);

        // Cap export to 100K rows to prevent OOM
        const int maxExportRows = 100_000;
        await using var writer = new StreamWriter(Response.Body, leaveOpen: true);
        await writer.WriteLineAsync("AuditId,TimestampUtc,UserId,GuestId,Role,ActionName,ResourceId,Allowed,ReasonCode,ClientKind,CorrelationId");

        var count = 0;
        await foreach (var a in q.Take(maxExportRows).AsAsyncEnumerable().WithCancellation(ct))
        {
            var line = $"{a.AuditId},{a.TimestampUtc:O},{Escape(a.UserId)},{Escape(a.GuestId)},{a.RoleAtTime},{a.ActionName},{Escape(a.ResourceId)},{a.Allowed},{a.ReasonCode},{a.ClientKind},{Escape(a.CorrelationId)}";
            await writer.WriteLineAsync(line);
            count++;
            if (count % 1000 == 0)
                await writer.FlushAsync(ct);
        }
    }

    private static IQueryable<AuditEntry> BuildQuery(OrchestratorDbContext db, AuditQueryParams p)
    {
        IQueryable<AuditEntry> q = db.AuditEntries;

        if (p.From.HasValue)
            q = q.Where(a => a.TimestampUtc >= p.From.Value);
        if (p.To.HasValue)
        {
            // A date-only "To" (midnight) is treated as inclusive of that whole day,
            // so selecting the same From/To date still returns that day's entries.
            var to = p.To.Value;
            if (to.TimeOfDay == TimeSpan.Zero)
            {
                var toExclusive = to.AddDays(1);
                q = q.Where(a => a.TimestampUtc < toExclusive);
            }
            else
            {
                q = q.Where(a => a.TimestampUtc <= to);
            }
        }
        if (!string.IsNullOrWhiteSpace(p.UserId))
            q = q.Where(a => a.UserId == p.UserId || a.GuestId == p.UserId);
        if (!string.IsNullOrWhiteSpace(p.ActionName))
        {
            if (p.ActionName.Contains('*'))
            {
                var pattern = p.ActionName.Replace('*', '%');
                q = q.Where(a => EF.Functions.Like(a.ActionName, pattern));
            }
            else
            {
                q = q.Where(a => a.ActionName == p.ActionName);
            }
        }
        if (p.Decision.HasValue)
            q = q.Where(a => a.Allowed == p.Decision.Value);
        if (!string.IsNullOrWhiteSpace(p.ResourceId))
            q = q.Where(a => a.ResourceId == p.ResourceId);
        if (!string.IsNullOrWhiteSpace(p.ReasonCode))
            q = q.Where(a => a.ReasonCode == p.ReasonCode);

        return q;
    }

    private async Task<IUserContext?> ResolveAdmin(Permission requiredPermission, CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        var user = await _authInterceptor.ResolveUserAsync(authHeader, ClientKind.Web, ct);
        if (user is null) return null;

        // Admin-only check
        var role = user.Roles.FirstOrDefault();
        if (!string.Equals(role, Role.Administrator.ToString(), StringComparison.OrdinalIgnoreCase))
            return null;

        return user;
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

public sealed class AuditQueryParams
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? UserId { get; set; }
    public string? ActionName { get; set; }
    public bool? Decision { get; set; }
    public string? ResourceId { get; set; }
    public string? ReasonCode { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 200;
}

public sealed class AuditEntryDto
{
    public long AuditId { get; set; }
    public string? UserId { get; set; }
    public string? GuestId { get; set; }
    public string? RoleAtTime { get; set; }
    public string ActionName { get; set; } = "";
    public string? ResourceId { get; set; }
    public bool Allowed { get; set; }
    public string ReasonCode { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string ClientKind { get; set; } = "";
    public string? CorrelationId { get; set; }
}

public sealed class AuditPageResponse
{
    public List<AuditEntryDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
