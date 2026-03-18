using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Persists Dokploy resource IDs between publish runs so services are updated
/// (not recreated) on subsequent deployments.
/// </summary>
public class DokployStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ILogger _logger;
    private DokployState _state = new();

    public DokployStateStore(string outputDirectory, ILogger logger)
    {
        _filePath = Path.Combine(outputDirectory, ".dokploy-state.json");
        _logger = logger;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            _state = new DokployState();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            _state = JsonSerializer.Deserialize<DokployState>(json, JsonOptions) ?? new DokployState();
            _logger.LogDebug("Loaded Dokploy state from {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load Dokploy state from {Path}, starting fresh", _filePath);
            _state = new DokployState();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(_state, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct);
        _logger.LogDebug("Saved Dokploy state to {Path}", _filePath);
    }

    public string? GetProjectId() => _state.ProjectId;
    public void SetProjectId(string projectId) => _state.ProjectId = projectId;

    public string? GetEnvironmentId() => _state.EnvironmentId;
    public void SetEnvironmentId(string environmentId) => _state.EnvironmentId = environmentId;

    public string? GetAppName(string serviceName) =>
        _state.Services.TryGetValue(serviceName, out var s) ? s.AppName : null;

    public void SetAppName(string serviceName, string appName)
    {
        if (!_state.Services.TryGetValue(serviceName, out var s))
            s = _state.Services[serviceName] = new ServiceState();
        s.AppName = appName;
    }

    public string? GetApplicationId(string serviceName) =>
        _state.Services.TryGetValue(serviceName, out var s) ? s.ApplicationId : null;

    public string? GetNativeServiceId(string serviceName) =>
        _state.Services.TryGetValue(serviceName, out var s) ? s.NativeServiceId : null;

    public void SetApplicationId(string serviceName, string applicationId)
    {
        if (!_state.Services.TryGetValue(serviceName, out var s))
            s = _state.Services[serviceName] = new ServiceState();
        s.ApplicationId = applicationId;
    }

    public void SetNativeServiceId(string serviceName, string nativeServiceId)
    {
        if (!_state.Services.TryGetValue(serviceName, out var s))
            s = _state.Services[serviceName] = new ServiceState();
        s.NativeServiceId = nativeServiceId;
    }
}

public class DokployState
{
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; set; }

    [JsonPropertyName("services")]
    public Dictionary<string, ServiceState> Services { get; set; } = new();
}

public class ServiceState
{
    [JsonPropertyName("applicationId")]
    public string? ApplicationId { get; set; }

    /// <summary>
    /// Dokploy resource ID for native managed services (Redis, MariaDB, MongoDB, MySQL, Postgres).
    /// Previously stored as "redisId"; the field was generalized when additional DB types were added.
    /// Existing state files using "redisId" will lose the cached ID on first run (will be recreated).
    /// </summary>
    [JsonPropertyName("nativeServiceId")]
    public string? NativeServiceId { get; set; }

    /// <summary>The Dokploy-assigned app name (e.g. "bb-apiservice-pakivg") used for internal DNS.</summary>
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }
}
