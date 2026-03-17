using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Background service that:
///   1. On startup, loads the default vocabulary XML from appsettings
///   2. Wires up templates + file watchers
///   3. Subscribes to VocabularyMonitor for hot-reload
///
/// The MainViewModel can also trigger loads via the UI — both paths
/// converge through VocabularyMonitor → ConfigReloaded.
/// </summary>
public sealed class ControllerHostedService : IHostedService
{
    private readonly IVocabularyMonitor _vocabMonitor;
    private readonly IFileWatcherManager _watcherManager;
    private readonly IActionPipelineExecutor _executor;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly IConfiguration _config;
    private readonly ILogger<ControllerHostedService> _logger;

    public ControllerHostedService(
        IVocabularyMonitor vocabMonitor,
        IFileWatcherManager watcherManager,
        IActionPipelineExecutor executor,
        IAgentGrpcDispatcher dispatcher,
        IConfiguration config,
        ILogger<ControllerHostedService> logger)
    {
        _vocabMonitor = vocabMonitor;
        _watcherManager = watcherManager;
        _executor = executor;
        _dispatcher = dispatcher;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TestController starting…");

        // Subscribe to hot-reload (fires when vocabulary file is modified)
        _vocabMonitor.ConfigReloaded += OnConfigReloaded;

        // Register agents from appsettings
        var agents = _config.GetSection("Agents").GetChildren();
        foreach (var agent in agents)
        {
            var name = agent.GetValue<string>("Name") ?? "";
            var address = agent.GetValue<string>("Address") ?? "";
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(address))
            {
                _dispatcher.RegisterAgent(name, address);
                _logger.LogInformation("Registered agent from config: {Name} → {Address}", name, address);
            }
        }

        // Load vocabulary from config (if specified)
        var vocabPath = _config.GetValue<string>("VocabularyFile") ?? "";
        if (!string.IsNullOrEmpty(vocabPath) && File.Exists(vocabPath))
        {
            try
            {
                var config = _vocabMonitor.StartMonitoring(vocabPath);
                ApplyConfig(config);
                _logger.LogInformation("Loaded vocabulary from config: {Path}", vocabPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load vocabulary: {Path}", vocabPath);
            }
        }
        else
        {
            _logger.LogInformation("No vocabulary file configured. Use File → Open in the UI.");
        }

        return Task.CompletedTask;
    }

    private void OnConfigReloaded(WatchListConfig config)
    {
        _logger.LogInformation("Vocabulary reloaded — {WatchItems} WatchItems, {Templates} Templates",
            config.WatchItems.Count, config.Templates.Count);
        ApplyConfig(config);
    }

    private void ApplyConfig(WatchListConfig config)
    {
        _executor.LoadTemplates(config.Templates);
        _watcherManager.ApplyConfig(config);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TestController stopping…");
        _vocabMonitor.ConfigReloaded -= OnConfigReloaded;
        return Task.CompletedTask;
    }
}
