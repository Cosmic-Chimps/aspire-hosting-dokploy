namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

/// <summary>
/// Settings for configuring per-service Dokploy deployment.
/// </summary>
public class DokploySettings
{
    /// <summary>
    /// The base URL of the Dokploy instance (e.g., "https://paas.example.com").
    /// </summary>
    public string DokployUrl { get; set; } = string.Empty;

    /// <summary>
    /// The API token. Generate at Dokploy → Settings → Profile → API/CLI.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// The Dokploy project name. Created automatically if it doesn't exist.
    /// Find existing project names in the Dokploy dashboard URL.
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Target environment within the Dokploy project (e.g. "production", "staging").
    /// Created automatically if it doesn't exist within the project.
    /// Defaults to "production".
    /// </summary>
    public string EnvironmentName { get; set; } = "production";

    /// <summary>
    /// App name prefix used when auto-generating Dokploy app names (default: "").
    /// </summary>
    public string AppNamePrefix { get; set; } = string.Empty;

    /// <summary>
    /// Optional server ID for deployment to a specific Dokploy server.
    /// Leave null to use the default server.
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Optional secret token sent as <c>X-Deploy-Token</c> on every API request.
    /// Configure your reverse proxy / WAF in front of the Dokploy VPS to bypass
    /// bot protection or rate limiting when this header is present.
    /// Works with Cloudflare (WAF Custom Rule), Nginx, Traefik, Caddy, HAProxy, etc.
    /// Store the value in a CI secret and pass it via this setting.
    /// </summary>
    public string? DeployBypassToken { get; set; }

    /// <summary>
    /// Optional registry credentials applied to all application services.
    /// Can be overridden per-service via WithRegistryCredentials().
    /// </summary>
    public RegistryCredentials? Registry { get; set; }
}

/// <summary>
/// Container registry credentials for pulling private images in Dokploy.
/// </summary>
public class RegistryCredentials
{
    /// <summary>
    /// Registry hostname shown in Dokploy's "Registry URL" field.
    /// Examples: <c>"docker.io"</c>, <c>"ghcr.io"</c>, <c>"myregistry.azurecr.io"</c>.
    /// </summary>
    public string? RegistryUrl { get; set; }

    /// <summary>
    /// Prefix prepended to local image names to form the fully-qualified image reference
    /// that Dokploy will pull. Must match the <c>repository</c> argument passed to
    /// <c>builder.AddContainerRegistry(...)</c>.
    /// <list type="bullet">
    ///   <item>Docker Hub: <c>"myusername"</c>  → <c>myusername/apiservice:latest</c></item>
    ///   <item>GHCR:       <c>"ghcr.io/myorg"</c> → <c>ghcr.io/myorg/apiservice:latest</c></item>
    ///   <item>ACR:        <c>"myregistry.azurecr.io"</c> → <c>myregistry.azurecr.io/apiservice:latest</c></item>
    /// </list>
    /// </summary>
    public string? ImagePrefix { get; set; }

    /// <summary>Registry username for Dokploy pull authentication.</summary>
    public string? Username { get; set; }

    /// <summary>Registry password or access token for Dokploy pull authentication.</summary>
    public string? Password { get; set; }
}
