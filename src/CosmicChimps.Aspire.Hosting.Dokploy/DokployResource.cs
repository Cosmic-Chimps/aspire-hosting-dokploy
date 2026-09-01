using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Represents the Dokploy deployment target for an Aspire application.
/// Holds connection settings and tracks state across publish runs.
/// </summary>
/// <remarks>
/// The string properties below are the <b>resolved</b> configuration, populated by
/// <see cref="ResolveConfigurationAsync"/> at the start of the deploy step. Their deferred sources —
/// literals or Aspire parameters — live in the internal <c>*Source</c> fields, set by
/// <c>PublishToDokploy</c> from <see cref="DokploySettings"/>. Nothing reads a parameter while the
/// application model is being built (issue #1).
/// </remarks>
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

    /// <summary>
    /// Target environment within the Dokploy project (e.g. "production", "staging").
    /// The environment is created automatically if it doesn't exist.
    /// Defaults to "production".
    /// </summary>
    public string EnvironmentName { get; set; } = "production";

    /// <summary>App name prefix for all services created in Dokploy.</summary>
    public string AppNamePrefix { get; set; } = string.Empty;

    /// <summary>Optional target server ID. Null = default server.</summary>
    public string? ServerId { get; set; }

    /// <summary>Optional registry credentials applied to all application services (resolved).</summary>
    public ResolvedRegistryCredentials? Registry { get; set; }

    /// <summary>
    /// Optional secret token sent as <c>X-Deploy-Token</c> on every API request.
    /// Configure your reverse proxy / WAF in front of the Dokploy VPS to bypass
    /// bot protection or rate limiting when this header is present.
    /// Works with Cloudflare (WAF Custom Rule), Nginx, Traefik, Caddy, HAProxy, etc.
    /// Store the value in a CI secret and pass it via <see cref="DokploySettings.DeployBypassToken"/>.
    /// </summary>
    public string? DeployBypassToken { get; set; }

    // ── Deferred sources ─────────────────────────────────────────────────────
    // Set from DokploySettings; each may be a literal or an Aspire parameter. Resolved exactly once
    // by ResolveConfigurationAsync when the deploy step runs.
    internal DokployValue? DokployUrlSource { get; set; }
    internal DokployValue? ApiTokenSource { get; set; }
    internal DokployValue? ProjectNameSource { get; set; }
    internal DokployValue? EnvironmentNameSource { get; set; }
    internal DokployValue? AppNamePrefixSource { get; set; }
    internal DokployValue? ServerIdSource { get; set; }
    internal DokployValue? DeployBypassTokenSource { get; set; }
    internal RegistryCredentials? RegistrySource { get; set; }

    /// <summary>Fallback project name — the resource name — when no project name is configured.</summary>
    internal string DefaultProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Resolves every deferred setting into the string properties above.
    /// </summary>
    /// <remarks>
    /// Must run before anything else in the deploy step. Parameters can only be resolved
    /// asynchronously and only once the deployment is under way, which is exactly why the settings
    /// are not read at model-build time.
    /// </remarks>
    internal async Task ResolveConfigurationAsync(CancellationToken ct)
    {
        DokployUrl = await DokployUrlSource.ResolveRequiredAsync("DokployUrl", ct);
        ApiToken = await ApiTokenSource.ResolveRequiredAsync("ApiToken", ct);
        ProjectName = await ProjectNameSource.ResolveOrDefaultAsync(DefaultProjectName, ct);
        EnvironmentName = await EnvironmentNameSource.ResolveOrDefaultAsync("production", ct);
        AppNamePrefix = await AppNamePrefixSource.ResolveOrDefaultAsync(string.Empty, ct);
        ServerId = await ServerIdSource.ResolveAsync(ct);
        DeployBypassToken = await DeployBypassTokenSource.ResolveAsync(ct);
        Registry = await ResolvedRegistryCredentials.ResolveAsync(RegistrySource, ct);
    }

    /// <summary>
    /// The Docker Compose environment resource that generates the resolved YAML.
    /// Set by DokployResourceExtensions.
    /// </summary>
    public DockerComposeEnvironmentResource ComposeEnvironment { get; set; } = null!;

    /// <summary>
    /// Builder for <see cref="ComposeEnvironment"/>. Needed because the compose environment is
    /// created inside <c>PublishToDokploy</c>, so a caller never holds its builder — and
    /// <c>WithDashboard</c> is declared on the builder, not the resource. Without this the
    /// dashboard could not be configured at all for a Dokploy target.
    /// </summary>
    internal IResourceBuilder<DockerComposeEnvironmentResource> ComposeEnvironmentBuilder { get; set; } = null!;

    /// <summary>
    /// Log full HTTP bodies for failed Dokploy API calls. See
    /// <see cref="DokploySettings.VerboseHttpLogging"/>.
    /// </summary>
    public bool VerboseHttpLogging { get; set; }

    /// <summary>
    /// When true, the Aspire dashboard is deployed to Dokploy rather than filtered out.
    /// See <see cref="DokploySettings.DeployDashboard"/> for the full rationale and the security
    /// caveats. Set it through <c>PublishToDokploy</c>'s settings or
    /// <c>WithDokployDashboard()</c>.
    /// </summary>
    public bool DeployDashboard { get; set; }
}

