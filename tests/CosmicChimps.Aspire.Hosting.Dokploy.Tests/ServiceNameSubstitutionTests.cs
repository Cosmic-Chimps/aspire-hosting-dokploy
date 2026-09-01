using CosmicChimps.Aspire.Hosting.Dokploy;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards service-name substitution: a compose service name must be rewritten to its Dokploy app
/// name where it is a <b>hostname</b>, and nowhere else.
/// </summary>
/// <remarks>
/// The canonical <c>AddPostgres("postgres")</c> produces
/// <c>Host=postgres;Username=postgres;…</c>, and the default Postgres superuser is also called
/// <c>postgres</c>. Substituting every occurrence rewrote the username too, and the deployed API
/// died at startup with <c>28P01: password authentication failed for user "i2t-postgres-u6npej"</c>
/// — which reads like a wrong password rather than a mangled connection string.
/// </remarks>
public class ServiceNameSubstitutionTests
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = "i2t-postgres-u6npej",
        ["api"] = "i2t-api-ab12cd",
    };

    [Fact]
    public void SubstitutesTheHost_ButNotTheUsername()
    {
        // The exact value that broke the deployment.
        const string line =
            "ConnectionStrings__martendb=Host=postgres;Port=5432;Username=postgres;Password=s3cret;Database=martendb";

        var result = DokployComposeParser.ApplyServiceNameSubstitution(line, Map);

        Assert.Contains("Host=i2t-postgres-u6npej", result);
        Assert.Contains("Username=postgres", result);
        Assert.DoesNotContain("Username=i2t-postgres-u6npej", result);
    }

    [Fact]
    public void LeavesPasswordAndDatabaseSegmentsAlone()
    {
        const string line = "ConnectionStrings__db=Host=postgres;Password=postgres;Database=postgres";

        var result = DokployComposeParser.ApplyServiceNameSubstitution(line, Map);

        Assert.Contains("Host=i2t-postgres-u6npej", result);
        Assert.Contains("Password=postgres", result);
        Assert.Contains("Database=postgres", result);
    }

    [Fact]
    public void LeavesNonHostEnvKeysAlone()
    {
        // Aspire emits these alongside the connection string.
        Assert.Equal("MARTENDB_USERNAME=postgres",
            DokployComposeParser.ApplyServiceNameSubstitution("MARTENDB_USERNAME=postgres", Map));
        Assert.Equal("MARTENDB_PASSWORD=postgres",
            DokployComposeParser.ApplyServiceNameSubstitution("MARTENDB_PASSWORD=postgres", Map));

        // ...but the host key must still be rewritten, or nothing resolves in Swarm.
        Assert.Equal("MARTENDB_HOST=i2t-postgres-u6npej",
            DokployComposeParser.ApplyServiceNameSubstitution("MARTENDB_HOST=postgres", Map));
    }

    [Fact]
    public void SubstitutesOnlyTheAuthorityOfAUri()
    {
        // postgresql://<user>:<pw>@<host>:5432/<db> — only the host is a service name.
        const string line = "MARTENDB_URI=postgresql://postgres:pw@postgres:5432/martendb";

        var result = DokployComposeParser.ApplyServiceNameSubstitution(line, Map);

        Assert.Equal("MARTENDB_URI=postgresql://postgres:pw@i2t-postgres-u6npej:5432/martendb", result);
    }

    [Fact]
    public void StillSubstitutesPlainHostValues()
    {
        // The behaviour everything else depends on must survive the narrowing.
        Assert.Equal("services__api__http__0=http://i2t-api-ab12cd:8080",
            DokployComposeParser.ApplyServiceNameSubstitution("services__api__http__0=http://api:8080", Map));
    }

    [Fact]
    public void DoesNotSubstitutePartialWordMatches()
    {
        // "martendb" contains "db"; "postgresql" contains "postgres".
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["db"] = "app-db-1" };

        Assert.Equal("X=Database=martendb",
            DokployComposeParser.ApplyServiceNameSubstitution("X=Database=martendb", map));
    }
}
