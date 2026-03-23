using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// In-memory store for Dokploy resource IDs discovered or created during a single deployment run.
/// Idempotency across runs is achieved by always querying the live Dokploy API state via
/// <c>environment.one</c> at the start of each run — no local file is written or read.
/// </summary>
public class DokployStateStore(ILogger logger)
{
    private readonly Dictionary<string, ServiceState> _services = new(StringComparer.OrdinalIgnoreCase);

    public string? GetAppName(string serviceName) =>
        _services.TryGetValue(serviceName, out var s) ? s.AppName : null;

    public void SetAppName(string serviceName, string appName)
    {
        if (!_services.TryGetValue(serviceName, out var s))
            s = _services[serviceName] = new ServiceState();
        s.AppName = appName;
    }

    public string? GetApplicationId(string serviceName) =>
        _services.TryGetValue(serviceName, out var s) ? s.ApplicationId : null;

    public string? GetNativeServiceId(string serviceName) =>
        _services.TryGetValue(serviceName, out var s) ? s.NativeServiceId : null;

    public void SetApplicationId(string serviceName, string applicationId)
    {
        if (!_services.TryGetValue(serviceName, out var s))
            s = _services[serviceName] = new ServiceState();
        s.ApplicationId = applicationId;
        logger.LogDebug("State: set applicationId={Id} for '{Service}'", applicationId, serviceName);
    }

    public void SetNativeServiceId(string serviceName, string nativeServiceId)
    {
        if (!_services.TryGetValue(serviceName, out var s))
            s = _services[serviceName] = new ServiceState();
        s.NativeServiceId = nativeServiceId;
        logger.LogDebug("State: set nativeServiceId={Id} for '{Service}'", nativeServiceId, serviceName);
    }
}

internal class ServiceState
{
    public string? ApplicationId { get; set; }
    public string? NativeServiceId { get; set; }
    public string? AppName { get; set; }
}
