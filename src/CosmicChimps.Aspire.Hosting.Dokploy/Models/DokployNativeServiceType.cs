namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

/// <summary>
/// Identifies services that map to a Dokploy native managed resource
/// instead of a generic Docker Application.
/// </summary>
public enum DokployNativeServiceType
{
    Redis,
    MariaDb,
    Mongo,
    MySql,
    Postgres,
}
