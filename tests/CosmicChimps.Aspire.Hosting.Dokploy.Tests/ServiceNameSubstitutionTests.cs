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
    public void LeavesTelemetryLabelsAlone()
    {
        // OTEL_SERVICE_NAME is a label, not a DNS name. Substituting it renamed every resource in
        // the Aspire dashboard to its Dokploy app name — "api" became "i2t-api-olfgr7".
        Assert.Equal("OTEL_SERVICE_NAME=api",
            DokployComposeParser.ApplyServiceNameSubstitution("OTEL_SERVICE_NAME=api", Map));

        // ...while the endpoint on the same service must still be rewritten, or nothing resolves.
        Assert.Equal("OTEL_EXPORTER_OTLP_ENDPOINT=http://i2t-postgres-u6npej:18889",
            DokployComposeParser.ApplyServiceNameSubstitution(
                "OTEL_EXPORTER_OTLP_ENDPOINT=http://postgres:18889", Map));
    }

    [Fact]
    public void DoesNotSubstitutePartialWordMatches()
    {
        // "martendb" contains "db"; "postgresql" contains "postgres".
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["db"] = "app-db-1" };

        Assert.Equal("X=Database=martendb",
            DokployComposeParser.ApplyServiceNameSubstitution("X=Database=martendb", map));
    }

    /// <summary>
    /// A semicolon-separated allow-list is substituted segment by segment, so a service name listed
    /// in it reaches the deployed container as the Dokploy app name the senders actually resolve.
    /// </summary>
    /// <remarks>
    /// This is what makes the dashboard's <c>AllowedHosts</c> workable. ASP.NET Core host filtering
    /// runs <b>before</b> authentication and applies to the whole app, OTLP ingest ports included —
    /// so an allow-list naming only the public domain and loopback rejected every telemetry sender
    /// with <c>400 Bad Request - Invalid Hostname</c>, on both 18889 and 18890, before the API key or
    /// the payload was ever looked at. The symptom was an empty dashboard with no error anywhere:
    /// senders were rejected at the front door, and OpenTelemetry reports export failures on an
    /// EventSource rather than to the logger.
    /// </remarks>
    [Fact]
    public void SubstitutesServiceNamesInsideASemicolonSeparatedAllowList()
    {
        const string line =
            "AllowedHosts=dash.example.com;localhost;127.0.0.1;imperva2terraform-compose-dashboard";

        var result = DokployComposeParser.ApplyServiceNameSubstitution(
            line,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["imperva2terraform-compose-dashboard"] =
                    "i2t-imperva2terraform-compose-dashboard-fh9lhz",
            }
        );

        Assert.Contains("i2t-imperva2terraform-compose-dashboard-fh9lhz", result);
        // The rest of the list must survive untouched — the domain is how a browser reaches it and
        // loopback is how an SSH tunnel does.
        Assert.Contains("dash.example.com", result);
        Assert.Contains("localhost", result);
        Assert.Contains("127.0.0.1", result);
    }

    /// <summary>
    /// A URL path is not a hostname, however much of it looks like a service name.
    /// </summary>
    /// <remarks>
    /// A gateway routing <c>/api</c> to a service named <c>api</c> emits the route path and the web
    /// client's API base URL as env values. Substituting the word inside them rewrote the public
    /// path to the Dokploy app name, so <c>/api/mcp</c> became <c>/i2t-api-olfgr7/mcp</c>. Both
    /// sides moved together, so the deployment kept working and nothing looked wrong — the only
    /// symptom was that a client using the documented <c>/api</c> path fell through to the SPA
    /// catch-all and received HTML with <b>HTTP 200</b>, which reads as success.
    /// </remarks>
    [Fact]
    public void LeavesUrlPathsAlone()
    {
        // The YARP route that carries the public prefix.
        Assert.Equal(
            "REVERSEPROXY__ROUTES__route0__MATCH__PATH=/api/{**catch-all}",
            DokployComposeParser.ApplyServiceNameSubstitution(
                "REVERSEPROXY__ROUTES__route0__MATCH__PATH=/api/{**catch-all}", Map));

        // The web client's base URL, which has to agree with that route.
        Assert.Equal("NUXT_PUBLIC_API_BASE_URL=/api",
            DokployComposeParser.ApplyServiceNameSubstitution("NUXT_PUBLIC_API_BASE_URL=/api", Map));

        // A bare path segment that happens to equal a service name.
        Assert.Equal("SOME_PATH=/postgres/backups",
            DokployComposeParser.ApplyServiceNameSubstitution("SOME_PATH=/postgres/backups", Map));
    }

    /// <summary>
    /// Within a URI, only the authority is a host — not the path that follows it.
    /// </summary>
    [Fact]
    public void SubstitutesTheAuthorityButNotThePathOfAUri()
    {
        Assert.Equal(
            "GATEWAY__UPSTREAM=http://i2t-api-ab12cd:8080/api/v1",
            DokployComposeParser.ApplyServiceNameSubstitution(
                "GATEWAY__UPSTREAM=http://api:8080/api/v1", Map));

        // With credentials as well: scheme, user, and path all left alone.
        Assert.Equal(
            "DB__URI=postgresql://postgres:pw@i2t-postgres-u6npej:5432/postgres",
            DokployComposeParser.ApplyServiceNameSubstitution(
                "DB__URI=postgresql://postgres:pw@postgres:5432/postgres", Map));

        // A query string is not an authority either.
        Assert.Equal(
            "PROBE=http://i2t-api-ab12cd:8080/health?service=api",
            DokployComposeParser.ApplyServiceNameSubstitution(
                "PROBE=http://api:8080/health?service=api", Map));
    }
}
