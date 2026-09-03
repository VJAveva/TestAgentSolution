using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TestControllerGrpc.Ado.Reporting;

/// <summary>DI wiring for the ClosedXML-backed xlsx report builder. Called only by export-capable hosts.</summary>
public static class ReportingServiceCollectionExtensions
{
    public static IServiceCollection AddChurnXlsxReporting(this IServiceCollection services)
    {
        services.TryAddSingleton<IChurnXlsxBuilder, ChurnXlsxBuilder>();
        return services;
    }
}
