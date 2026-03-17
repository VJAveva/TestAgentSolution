using System.Text;
using TestController.WebApi.Services;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Endpoints;

public static class WatchListEndpoints
{
    public static RouteGroupBuilder MapWatchListEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetWatchList);
        group.MapGet("/xml", GetWatchListXml);
        group.MapPut("/", SaveWatchList);
        group.MapPost("/import", ImportWatchItems);
        group.MapGet("/export", ExportWatchItems);
        group.MapPost("/refresh", RefreshWatchList);
        return group;
    }

    /// <summary>GET /api/watchlist — returns WatchListConfig as JSON.</summary>
    private static IResult GetWatchList(WatchListFileService fileService, IAppLogger logger)
    {
        try
        {
            if (!fileService.Exists())
                return Results.Ok(new WatchListConfig());

            var config = fileService.Load();
            return Results.Ok(config);
        }
        catch (Exception ex)
        {
            logger.Error("WatchList", "Failed to load WatchList config", ex);
            return Results.Problem($"Failed to load WatchList: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>GET /api/watchlist/xml — returns the raw XML string.</summary>
    private static IResult GetWatchListXml(WatchListFileService fileService, IAppLogger logger)
    {
        try
        {
            if (!fileService.Exists())
                return Results.NotFound("No WatchList file found.");

            var xml = fileService.GetXml();
            return Results.Text(xml, "application/xml", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger.Error("WatchList", "Failed to read WatchList XML", ex);
            return Results.Problem($"Failed to read WatchList XML: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>PUT /api/watchlist — saves the updated config from JSON body.</summary>
    private static IResult SaveWatchList(WatchListConfig config, WatchListFileService fileService, IAppLogger logger)
    {
        try
        {
            config.FilePath = fileService.FilePath;
            fileService.Save(config);
            return Results.Ok(config);
        }
        catch (Exception ex)
        {
            logger.Error("WatchList", "Failed to save WatchList config", ex);
            return Results.Problem($"Failed to save WatchList: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>POST /api/watchlist/import — import WatchItems from uploaded XML.</summary>
    private static async Task<IResult> ImportWatchItems(
        HttpRequest request,
        WatchListFileService fileService,
        ExecutionSessionManager sessionManager,
        IAppLogger logger)
    {
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var xml = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(xml))
                return Results.BadRequest("Empty XML body.");

            var (imported, importedTemplates) = WatchListXmlParser.ParseWatchItemsFromXml(xml);
            if (imported.Count == 0)
                return Results.Ok(new { message = "No WatchItems found in XML.", added = 0, updated = 0, skipped = 0 });

            var config = fileService.Load();
            int added = 0, updated = 0, skipped = 0;

            foreach (var item in imported)
            {
                var existing = config.WatchItems
                    .FirstOrDefault(w => string.Equals(w.Tag, item.Tag, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    if (sessionManager.HasActiveExecution(item.Tag))
                    {
                        skipped++;
                        continue;
                    }
                    var idx = config.WatchItems.IndexOf(existing);
                    config.WatchItems[idx] = item;
                    updated++;
                }
                else
                {
                    config.WatchItems.Add(item);
                    added++;
                }
            }

            // Merge templates
            foreach (var template in importedTemplates)
            {
                if (!config.Templates.Any(t =>
                    string.Equals(t.ID, template.ID, StringComparison.OrdinalIgnoreCase)))
                {
                    config.Templates.Add(template);
                }
            }

            config.FilePath = fileService.FilePath;
            fileService.Save(config);

            logger.Info("WatchList", $"Import complete: added={added}, updated={updated}, skipped={skipped}");
            return Results.Ok(new { message = "Import complete.", added, updated, skipped });
        }
        catch (Exception ex)
        {
            logger.Error("WatchList", "Failed to import WatchItems", ex);
            return Results.Problem($"Failed to import WatchItems: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>GET /api/watchlist/export?tags=Tag1,Tag2 — export selected items as XML.</summary>
    private static IResult ExportWatchItems(HttpContext context, WatchListFileService fileService, IAppLogger logger)
    {
        try
        {
            var tagsParam = context.Request.Query["tags"].ToString();
            var config = fileService.Load();

            List<WatchItemConfig> items;
            if (string.IsNullOrWhiteSpace(tagsParam))
            {
                items = config.WatchItems;
            }
            else
            {
                var tags = tagsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                items = config.WatchItems
                    .Where(w => tags.Any(t => string.Equals(t, w.Tag, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            if (items.Count == 0)
                return Results.NotFound("No matching WatchItems found.");

            // Collect referenced templates
            var refIds = items.SelectMany(WatchListXmlParser.CollectRefTemplateIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var templates = config.Templates
                .Where(t => refIds.Contains(t.ID))
                .ToList();

            var xml = WatchListXmlParser.SerializeWatchItemsToXml(items, templates.Count > 0 ? templates : null);
            return Results.Text(xml, "application/xml", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger.Error("WatchList", "Failed to export WatchItems", ex);
            return Results.Problem($"Failed to export WatchItems: {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>POST /api/watchlist/refresh — reload from disk.</summary>
    private static IResult RefreshWatchList(WatchListFileService fileService, IAppLogger logger)
    {
        try
        {
            if (!fileService.Exists())
                return Results.NotFound("No WatchList file found.");

            var config = fileService.Load();
            return Results.Ok(new
            {
                message = "Refreshed from disk.",
                watchItems = config.WatchItems.Count,
                templates = config.Templates.Count
            });
        }
        catch (Exception ex)
        {
            logger.Error("WatchList", "Failed to refresh WatchList from disk", ex);
            return Results.Problem($"Failed to refresh WatchList: {ex.Message}", statusCode: 500);
        }
    }
}
