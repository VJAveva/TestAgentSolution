using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Ado;

/// <summary>
/// Logs, once at startup, which IRegressionDataProvider is live (mock vs real ADO) and which
/// credential source resolved — so the app tells you at launch whether the Regression tab is on
/// mock or live Azure DevOps data, and why. Read-only: never triggers an actual ADO call.
/// </summary>
public sealed class AdoStartupDiagnostics : IHostedService
{
    private const string Category = "Ado";

    private readonly IServiceProvider _services;
    private readonly IRegressionDataProvider _provider;
    private readonly AdoOptions _options;
    private readonly IAppLogger _logger;

    public AdoStartupDiagnostics(
        IServiceProvider services,
        IRegressionDataProvider provider,
        IOptions<AdoOptions> options,
        IAppLogger logger)
    {
        _services = services;
        _provider = provider;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var providerName = _provider.GetType().Name;
        var live = _provider is AdoRegressionDataProvider;
        var (spDefinitionId, spLogId) = _options.ResolveSpBuild();

        _logger.Info(Category,
            $"Regression data provider = {providerName} ({(live ? "LIVE Azure DevOps" : "MOCK data")}). " +
            $"Ado:Enabled={_options.Enabled}, Org={_options.Organization}, Mode={_options.CollectionMode}, " +
            $"Project={_options.Project}, OmiProject={_options.OmiProject}, AuthMode={_options.AuthMode}, " +
            $"SpAnchored={_options.IsSpAnchored} (active='{_options.ActiveSpBuild ?? "(first)"}', def={spDefinitionId}, log={spLogId}).");

        if (!live)
        {
            if (!_options.Enabled)
                _logger.Warn(Category,
                    "Serving MOCK regression data. Set Ado:Enabled=true in appsettings.json and restart to pull real Azure DevOps data.");
            return Task.CompletedTask;
        }

        // Live: resolve the token provider so credential problems surface at startup instead of
        // on the first UI request. Constructor validation (missing tenant/client/secret) throws here.
        try
        {
            var tokenProvider = _services.GetRequiredService<IAdoTokenProvider>();
            _logger.Info(Category, $"ADO credential source = {tokenProvider.Describe()}.");
        }
        catch (AdoCredentialMissingException ex)
        {
            _logger.Error(Category,
                "ADO is enabled but no valid credential could be resolved — the Regression tab will fail to load live data. " +
                ex.Message, ex);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
