using Microsoft.Extensions.DependencyInjection;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Extension methods for configuring Dokploy services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Dokploy API client to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="baseUrl">The base URL of the Dokploy instance.</param>
    /// <param name="apiToken">The API token for authentication.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDokployApiClient(
        this IServiceCollection services,
        string baseUrl,
        string apiToken
    )
    {
        services.AddHttpClient<DokployApiClient>(client =>
        {
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
        });

        return services;
    }
}
