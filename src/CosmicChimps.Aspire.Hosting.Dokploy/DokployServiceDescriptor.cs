namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Describes a single service parsed from the generated compose YAML,
/// ready to be mapped to a Dokploy Application or native managed resource.
/// </summary>
public class DokployServiceDescriptor
{
    /// <summary>Service name as it appears in the compose YAML.</summary>
    public required string Name { get; init; }

    /// <summary>Full container image reference, e.g. "myrepo.azurecr.io/bella-api:abc123".</summary>
    public required string Image { get; init; }

    /// <summary>
    /// Environment variables as a newline-separated KEY=VALUE string,
    /// matching the format expected by Dokploy's saveEnvironment API.
    /// </summary>
    public string? EnvString { get; init; }

    /// <summary>Exposed ports as a list of "hostPort:containerPort" strings.</summary>
    public IReadOnlyList<string> Ports { get; init; } = [];

    /// <summary>
    /// Non-null when this service maps to a Dokploy native managed resource
    /// (Redis, MariaDB, MongoDB, MySQL, Postgres) instead of a generic Application.
    /// </summary>
    public Models.DokployNativeServiceType? NativeServiceType { get; init; }

    /// <summary>Convenience: true when this service is a Dokploy native managed resource.</summary>
    public bool IsNativeService => NativeServiceType.HasValue;

    /// <summary>
    /// True when the service has an external HTTP endpoint and should get a domain entry in Dokploy.
    /// </summary>
    public bool HasExternalEndpoint { get; init; }

    /// <summary>Optional domain hostname for public-facing services.</summary>
    public string? Domain { get; init; }

    /// <summary>Optional per-service registry credential override.</summary>
    public Models.RegistryCredentials? Registry { get; init; }

    /// <summary>
    /// Compose <c>entrypoint:</c> override, if any. Dokploy's API has NO entrypoint field, so an
    /// override cannot be reproduced — which matters because Aspire uses one to change how a
    /// container is configured, not merely how it starts. Captured so the deployer can react instead
    /// of silently dropping it.
    /// </summary>
    public IReadOnlyList<string> Entrypoint { get; init; } = [];

    /// <summary>Compose <c>command:</c> override, if any.</summary>
    public IReadOnlyList<string> Command { get; init; } = [];
}
