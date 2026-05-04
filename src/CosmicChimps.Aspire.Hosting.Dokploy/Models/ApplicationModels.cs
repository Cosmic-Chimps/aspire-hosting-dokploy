using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

public class CreateApplicationRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("environmentId")]
    public required string EnvironmentId { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }
}

public class ApplicationResponse
{
    [JsonPropertyName("applicationId")]
    public string? ApplicationId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("applicationStatus")]
    public string? ApplicationStatus { get; set; }

    [JsonPropertyName("env")]
    public string? Env { get; set; }
}

/// <summary>Item returned by application.all — same shape as ApplicationResponse.</summary>
public class ApplicationListItem : ApplicationResponse { }

public class SaveDockerProviderRequest
{
    [JsonPropertyName("applicationId")]
    public required string ApplicationId { get; set; }

    [JsonPropertyName("dockerImage")]
    public string? DockerImage { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("registryUrl")]
    public string? RegistryUrl { get; set; }
}

public class SaveEnvironmentRequest
{
    [JsonPropertyName("applicationId")]
    public required string ApplicationId { get; set; }

    /// <summary>
    /// Newline-separated KEY=VALUE pairs, e.g. "FOO=bar\nBAZ=qux".
    /// </summary>
    [JsonPropertyName("env")]
    public string? Env { get; set; }

    /// <summary>Required by Dokploy's Zod schema — send empty string by default.</summary>
    [JsonPropertyName("buildArgs")]
    public string BuildArgs { get; set; } = string.Empty;

    /// <summary>Required by Dokploy's Zod schema — send empty string by default.</summary>
    [JsonPropertyName("buildSecrets")]
    public string BuildSecrets { get; set; } = string.Empty;

    /// <summary>Required by Dokploy's Zod schema — false unless caller overrides.</summary>
    [JsonPropertyName("createEnvFile")]
    public bool CreateEnvFile { get; set; } = false;
}

public class DeployApplicationRequest
{
    [JsonPropertyName("applicationId")]
    public required string ApplicationId { get; set; }
}

/// <summary>
/// Docker Swarm health check configuration.
/// All time values are in nanoseconds (1 second = 1_000_000_000 ns).
/// </summary>
public class HealthCheckSwarm
{
    /// <summary>
    /// The test command, e.g. ["CMD", "curl", "-f", "http://localhost:8080/health"].
    /// First element must be "CMD" or "CMD-SHELL".
    /// </summary>
    [JsonPropertyName("Test")]
    public required List<string> Test { get; set; }

    /// <summary>Time between health checks in nanoseconds.</summary>
    [JsonPropertyName("Interval")]
    public long Interval { get; set; } = 30_000_000_000L;

    /// <summary>Maximum time to wait for a health check response in nanoseconds.</summary>
    [JsonPropertyName("Timeout")]
    public long Timeout { get; set; } = 10_000_000_000L;

    /// <summary>Grace period before health checks start in nanoseconds.</summary>
    [JsonPropertyName("StartPeriod")]
    public long StartPeriod { get; set; } = 10_000_000_000L;

    /// <summary>Number of consecutive failures before the container is considered unhealthy.</summary>
    [JsonPropertyName("Retries")]
    public int Retries { get; set; } = 3;
}

/// <summary>
/// Request body for POST /api/application.update — only sets healthCheckSwarm and/or stopGracePeriod.
/// Other fields are omitted (WhenWritingNull) so existing values are preserved.
/// </summary>
public class UpdateApplicationRequest
{
    [JsonPropertyName("applicationId")]
    public required string ApplicationId { get; set; }

    [JsonPropertyName("healthCheckSwarm")]
    public HealthCheckSwarm? HealthCheckSwarm { get; set; }

    /// <summary>
    /// Docker Swarm stop grace period in nanoseconds.
    /// This is the time Docker waits after sending SIGTERM before sending SIGKILL.
    /// Increase for services that need time for clean shutdown (e.g. PostgreSQL, RabbitMQ).
    /// null means "do not change the existing value".
    /// </summary>
    [JsonPropertyName("stopGracePeriod")]
    public long? StopGracePeriod { get; set; }
}
