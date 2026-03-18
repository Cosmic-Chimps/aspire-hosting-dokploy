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
}

public class DeployApplicationRequest
{
    [JsonPropertyName("applicationId")]
    public required string ApplicationId { get; set; }
}
