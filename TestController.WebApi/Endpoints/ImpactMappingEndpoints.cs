using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TestController.Api.Security;
using TestController.WebApi.Hubs;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Core.Impact.Learning;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Endpoints;

/// <summary>Request to run the impact-mapping engine over a change.</summary>
public sealed record ImpactMapRequest(ImpactedArea Area, ChangePayload Payload, SelectionTier Tier, string? CorrelationId);

/// <summary>One observed execution outcome to record against a run.</summary>
public sealed record ExecutionOutcomeDto(int TestCaseId, string Result, double DurationSeconds, string? FailureSignature);

/// <summary>Request to record execution outcomes for a run.</summary>
public sealed record ImpactOutcomeRequest(IReadOnlyList<ExecutionOutcomeDto> Outcomes);

/// <summary>Request to record a field escape.</summary>
public sealed record ImpactEscapeRequest(string AreaId, int TestCaseId, string Source, string? Notes);

/// <summary>
/// REST + SSE surface for the impact-mapping engine (P29), hosted only on the standalone WebApi (ReaderWriter).
/// The engine is host-agnostic; these handlers adapt HTTP and SignalR to it and never leak host types into Core.
/// Routed under <c>/api/impact-mapping</c> to avoid colliding with the legacy regression <c>ImpactController</c>.
/// </summary>
public static class ImpactMappingEndpoints
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    /// <summary>Maps the impact-mapping endpoints onto a route group.</summary>
    public static RouteGroupBuilder MapImpactMappingEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/run", RunAsync);
        group.MapPost("/stream", StreamAsync);
        group.MapPost("/runs/{runId:guid}/outcomes", RecordOutcomesAsync);
        group.MapPost("/escapes", RecordEscapeAsync);
        group.MapGet("/index/status", GetIndexStatusAsync);
        group.MapPost("/index/rebuild", RebuildIndexAsync).RequireAuthorization(SecurityPolicies.Admin);
        return group;
    }

    private static async Task<Ok<ImpactMappingResult>> RunAsync(
        ImpactMapRequest request, IImpactTestMappingService engine, CancellationToken ct)
        => TypedResults.Ok(await engine.MapAsync(request.Area, request.Payload, request.Tier, ct));

    private static async Task StreamAsync(
        ImpactMapRequest request, IImpactTestMappingService engine, IHubContext<ImpactProgressHub> hub,
        HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        await foreach (ImpactMappingProgress tick in engine.MapWithProgressAsync(request.Area, request.Payload, request.Tier, ct))
        {
            await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(tick, StreamJson)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                await hub.Clients.Group(request.CorrelationId).SendAsync("ImpactProgress", tick, ct);
            }
        }
    }

    private static async Task<Accepted> RecordOutcomesAsync(
        Guid runId, ImpactOutcomeRequest request, IOutcomeStore outcomes, CancellationToken ct)
    {
        List<ExecutionOutcome> mapped = request.Outcomes
            .Select(o => new ExecutionOutcome
            {
                TestCaseId = o.TestCaseId,
                Result = o.Result,
                DurationSeconds = o.DurationSeconds,
                FailureSignature = o.FailureSignature,
            })
            .ToList();

        await outcomes.RecordExecutionAsync(runId, mapped, ct);
        return TypedResults.Accepted($"/api/impact-mapping/runs/{runId}");
    }

    private static async Task<Accepted> RecordEscapeAsync(
        ImpactEscapeRequest request, IOutcomeStore outcomes, CancellationToken ct)
    {
        await outcomes.RecordEscapeAsync(request.AreaId, request.TestCaseId, request.Source, request.Notes, ct);
        return TypedResults.Accepted("/api/impact-mapping/escapes");
    }

    private static async Task<Ok<object>> GetIndexStatusAsync(
        IDbContextFactory<ImpactIndexDbContext> contextFactory, CancellationToken ct)
    {
        await using ImpactIndexDbContext ctx = await contextFactory.CreateDbContextAsync(ct);
        // Status is a read; it must not drop the index as a side effect of being polled.
        await ImpactIndexInitializer.EnsureCreatedAsync(ctx, rebuildOnSchemaChange: false, ct);

        Dictionary<string, string> metadata = await ctx.Metadata.ToDictionaryAsync(m => m.Key, m => m.Value, ct);
        int documentCount = await ctx.Documents.CountAsync(ct);

        return TypedResults.Ok<object>(new
        {
            documentCount,
            builtUtc = metadata.GetValueOrDefault("BuiltUtc"),
            embeddingModel = metadata.GetValueOrDefault("EmbeddingModel"),
            tokenizerVersion = metadata.GetValueOrDefault("TokenizerVersion"),
        });
    }

    private static Accepted RebuildIndexAsync(RetrievalIndexBuilder builder, IAppLogger logger)
    {
        // Fire-and-forget: a full rebuild must not block the request thread.
        _ = Task.Run(async () =>
        {
            try
            {
                await builder.BuildAsync(fullRebuild: true, progress: null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.Error("ImpactIndex", "Manual index rebuild failed.", ex);
            }
        });

        return TypedResults.Accepted("/api/impact-mapping/index/status");
    }
}
