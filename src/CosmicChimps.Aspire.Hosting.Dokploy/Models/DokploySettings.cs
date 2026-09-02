namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

/// <summary>
/// Settings for configuring per-service Dokploy deployment.
/// </summary>
public class DokploySettings
{
    /// <summary>
    /// The base URL of the Dokploy instance (e.g., "https://paas.example.com").
    /// </summary>
    public DokployValue? DokployUrl { get; set; }

    /// <summary>
    /// The API token. Generate at Dokploy → Settings → Profile → API/CLI.
    /// </summary>
    public DokployValue? ApiToken { get; set; }

    /// <summary>
    /// The Dokploy project name. Created automatically if it doesn't exist.
    /// Find existing project names in the Dokploy dashboard URL.
    /// </summary>
    public DokployValue? ProjectName { get; set; }

    /// <summary>
    /// Target environment within the Dokploy project (e.g. "production", "staging").
    /// Created automatically if it doesn't exist within the project.
    /// Defaults to "production".
    /// </summary>
    public DokployValue? EnvironmentName { get; set; }

    /// <summary>
    /// App name prefix used when auto-generating Dokploy app names (default: "").
    /// </summary>
    public DokployValue? AppNamePrefix { get; set; }

    /// <summary>
    /// Optional server ID for deployment to a specific Dokploy server.
    /// Leave null to use the default server.
    /// </summary>
    public DokployValue? ServerId { get; set; }

    /// <summary>
    /// Optional secret token sent as <c>X-Deploy-Token</c> on every API request.
    /// Configure your reverse proxy / WAF in front of the Dokploy VPS to bypass
    /// bot protection or rate limiting when this header is present.
    /// Works with Cloudflare (WAF Custom Rule), Nginx, Traefik, Caddy, HAProxy, etc.
    /// Store the value in a CI secret and pass it via this setting.
    /// </summary>
    public DokployValue? DeployBypassToken { get; set; }

    /// <summary>
    /// Optional registry credentials applied to all application services.
    /// Can be overridden per-service via WithRegistryCredentials().
    /// </summary>
    public RegistryCredentials? Registry { get; set; }

    /// <summary>
    /// Deploy the Aspire dashboard to Dokploy instead of filtering it out of the published output.
    /// Default <c>false</c>, which is the historical behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default every service recognised as Aspire infrastructure — an image containing
    /// <c>aspire-dashboard</c>, or a service name ending in <c>-dashboard</c> — is dropped before
    /// anything is created in Dokploy, and every environment value referring to it is dropped with
    /// it. That is right for a local dashboard, which has no place in a deployment.
    /// </para>
    /// <para>
    /// It is wrong for a self-hosted install with no external telemetry service, where the
    /// dashboard is the only place to read logs and traces. Setting this to <c>true</c> makes the
    /// dashboard an ordinary application service: it is created in Dokploy, and the
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> of the other services resolves to its Dokploy app name
    /// like any other service reference.
    /// </para>
    /// <para>
    /// <b>The dashboard displays sensitive telemetry and its OTLP endpoint is unauthenticated by
    /// default.</b> Deploy it without a public domain, and set <c>Dashboard:Otlp:AuthMode=ApiKey</c>
    /// plus a frontend authentication mode. See
    /// <see href="https://aspire.dev/dashboard/security-considerations/"/>.
    /// </para>
    /// </remarks>
    public bool DeployDashboard { get; set; }

    /// <summary>
    /// Log full HTTP request and response bodies for failed Dokploy API calls. Default <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failed calls are always logged with the status, the final URI, the request's content-type and
    /// content-length, and the response body — enough to tell "we never sent the field" apart from
    /// "we sent it and it did not arrive". The request body is <b>redacted</b> in that output.
    /// </para>
    /// <para>
    /// Set this to <c>true</c> to log it verbatim. Request bodies carry registry credentials and
    /// service environment variables, so this is for diagnosing a specific failure, not for leaving
    /// on.
    /// </para>
    /// </remarks>
    public bool VerboseHttpLogging { get; set; }

    /// <summary>
    /// Environment-variable key prefixes the deploy OWNS outright. Existing Dokploy keys under one of
    /// these prefixes are dropped before merging, so the family is fully replaced on every deploy.
    /// Default: <c>REVERSEPROXY__</c> (the Aspire YARP route/cluster configuration).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The env merge preserves every key that only exists in Dokploy, so that hand-set values (a Stripe
    /// key, a cloud function URL) survive redeploys. That contract has one failure mode: a family of
    /// <b>positional</b> keys. Aspire's YARP integration names routes <c>route0…routeN</c> in
    /// declaration order and flattens them to <c>REVERSEPROXY__ROUTES__route{n}__…</c>. When a later
    /// deploy declares FEWER routes, the higher-numbered keys from the earlier deploy are preserved,
    /// still valid, and still catch-alls — and the gateway fails every request with
    /// <c>AmbiguousMatchException</c> because two routes now match the same path. Nothing reproduces
    /// locally, where there is no preserved environment.
    /// </para>
    /// <para>
    /// A family listed here is replaced, not merged: every existing key starting with the prefix is
    /// removed, then the deploy's keys are written. Add a prefix only for configuration the AppHost
    /// generates in full; a hand-set key under a listed prefix will not survive. Matching is
    /// case-insensitive, because .NET configuration keys are.
    /// </para>
    /// </remarks>
    public List<string> ReplacedEnvPrefixes { get; set; } = ["REVERSEPROXY__"];
}

/// <summary>
/// Container registry credentials for pulling private images in Dokploy.
/// </summary>
public class RegistryCredentials
{
    // Each of these accepts a literal string or an Aspire parameter (issue #1). The password in
    // particular belongs in a secret parameter rather than IConfiguration.

    /// <summary>
    /// Registry hostname shown in Dokploy's "Registry URL" field.
    /// Examples: <c>"docker.io"</c>, <c>"ghcr.io"</c>, <c>"myregistry.azurecr.io"</c>.
    /// </summary>
    public DokployValue? RegistryUrl { get; set; }

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
    public DokployValue? ImagePrefix { get; set; }

    /// <summary>Registry username for Dokploy pull authentication.</summary>
    public DokployValue? Username { get; set; }

    /// <summary>Registry password or access token for Dokploy pull authentication.</summary>
    public DokployValue? Password { get; set; }
}

/// <summary>
/// Registry credentials after resolution, as the deployment consumes them.
/// </summary>
/// <remarks>
/// Deliberately mirrors <see cref="RegistryCredentials"/> property-for-property. The split is what
/// keeps deferred values (parameters) out of the deployment code path entirely: everything past the
/// resolve step at the start of <c>DeployAsync</c> sees plain strings and cannot accidentally
/// stringify an unresolved parameter into a request.
/// </remarks>
public sealed class ResolvedRegistryCredentials
{
    public string? RegistryUrl { get; init; }
    public string? ImagePrefix { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    internal static async ValueTask<ResolvedRegistryCredentials?> ResolveAsync(
        RegistryCredentials? source,
        CancellationToken ct
    )
    {
        if (source is null)
            return null;

        return new ResolvedRegistryCredentials
        {
            RegistryUrl = await source.RegistryUrl.ResolveAsync(ct).ConfigureAwait(false),
            ImagePrefix = await source.ImagePrefix.ResolveAsync(ct).ConfigureAwait(false),
            Username = await source.Username.ResolveAsync(ct).ConfigureAwait(false),
            Password = await source.Password.ResolveAsync(ct).ConfigureAwait(false),
        };
    }
}
