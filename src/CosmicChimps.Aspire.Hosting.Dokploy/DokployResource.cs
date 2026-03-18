using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Represents the Dokploy deployment target for an Aspire application.
/// Holds connection settings and tracks state across publish runs.
/// </summary>
public class DokployResource : Resource
{
    public DokployResource(string name)
        : base(name) { }

    /// <summary>Dokploy instance base URL.</summary>
    public string DokployUrl { get; set; } = string.Empty;

    /// <summary>Dokploy API token (x-api-key header).</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Dokploy project name. The project is created automatically if not found.
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>App name prefix for all services created in Dokploy.</summary>
    public string AppNamePrefix { get; set; } = string.Empty;

    /// <summary>Optional target server ID. Null = default server.</summary>
    public string? ServerId { get; set; }

    /// <summary>Optional registry credentials applied to all application services.</summary>
    public Models.RegistryCredentials? Registry { get; set; }

    /// <summary>
    /// The Docker Compose environment resource that generates the resolved YAML.
    /// Set by DokployResourceExtensions.
    /// </summary>
    public DockerComposeEnvironmentResource ComposeEnvironment { get; set; } = null!;
}

