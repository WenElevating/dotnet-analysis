using Microsoft.Extensions.DependencyInjection;

namespace DotnetAnalysis.Diagnostics.DependencyInjection;

public static class DiagnosticsServiceCollectionExtensions
{
    public static IServiceCollection AddDiagnosticsContracts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
