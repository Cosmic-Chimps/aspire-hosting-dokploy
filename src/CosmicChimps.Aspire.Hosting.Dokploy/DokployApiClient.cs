using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Flurl.Http;
using Flurl.Http.Configuration;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// HTTP client for interacting with the Dokploy API (per-service operations).
/// </summary>
public partial class DokployApiClient
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly FlurlClient _client;
    private readonly ILogger<DokployApiClient> _logger;
    private readonly bool _verboseHttp;

    public DokployApiClient(
        HttpClient httpClient,
        ILogger<DokployApiClient> logger,
        bool verboseHttp = false
    )
    {
        _logger = logger;
        _verboseHttp = verboseHttp;
        _client = new FlurlClient(httpClient).WithSettings(s =>
        {
            s.JsonSerializer = new DefaultJsonSerializer(JsonSerializerOptions);
        });

        // Request-side diagnostics. content-length is the load-bearing field: a Dokploy validation
        // error saying a field is "undefined" is ambiguous between "we never sent it" and "we sent it
        // and something in between dropped it", and only the length distinguishes those.
        // ONE BeforeCall handler, deliberately: Flurl's BeforeCall replaces the previously
        // registered action rather than chaining, so a second registration silently disables the
        // first.
        _client.BeforeCall(call =>
        {
            var content = call.HttpRequestMessage.Content;

            logger.LogDebug(
                "→ Dokploy {Method} {Url} content-type={ContentType} content-length={Length}",
                call.Request.Verb,
                call.Request.Url,
                content?.Headers.ContentType?.ToString() ?? "(none)",
                content?.Headers.ContentLength?.ToString() ?? "(unset)"
            );
        });

        _client.OnError(async call =>
        {
            if (call.Response is null)
            {
                logger.LogWarning(
                    call.Exception,
                    "Dokploy API {Method} {Url} failed with no response (transport-level)",
                    call.Request.Verb,
                    call.Request.Url
                );
                return;
            }

            var responseBody = await call.Response.GetStringAsync();

            // The URI the request FINALLY reached. A difference from the configured URL is a prime
            // suspect for a lost body: HttpClient turns POST into GET on a 301/302/303 and drops the
            // payload doing it, while 307/308 preserve both.
            var finalUri = call.Response.ResponseMessage?.RequestMessage?.RequestUri?.ToString();
            var redirected =
                finalUri is not null
                && !string.Equals(
                    finalUri,
                    call.Request.Url.ToString(),
                    StringComparison.OrdinalIgnoreCase
                );

            string requestBody;
            var requestContent = call.HttpRequestMessage.Content;
            try
            {
                var raw =
                    requestContent is null ? "(no content)" : await requestContent.ReadAsStringAsync();
                requestBody = _verboseHttp ? raw : Redact(raw);
            }
            catch (Exception ex)
            {
                requestBody = $"(could not read request body: {ex.Message})";
            }

            logger.LogWarning(
                call.Exception,
                "Dokploy API {Method} {Url} → {Status}\n"
                    + "  request  content-type  : {ContentType}\n"
                    + "  request  content-length: {Length}\n"
                    + "  request  body          : {RequestBody}\n"
                    + "  final    uri           : {FinalUri}{RedirectNote}\n"
                    + "  response server        : {Server}\n"
                    + "  response content-type  : {ResponseContentType}\n"
                    + "  response body          : {ResponseBody}",
                call.Request.Verb,
                call.Request.Url,
                (int)call.Response.StatusCode,
                requestContent?.Headers.ContentType?.ToString() ?? "(none)",
                requestContent?.Headers.ContentLength?.ToString() ?? "(unset)",
                requestBody,
                finalUri ?? "(unknown)",
                redirected ? "   ⚠ REDIRECTED — a 301/302/303 drops the POST body" : string.Empty,
                HeaderOrNone(call.Response, "Server"),
                call.Response.ResponseMessage?.Content?.Headers.ContentType?.ToString() ?? "(none)",
                responseBody
            );

            if (!_verboseHttp)
                logger.LogInformation(
                    "The request body above is redacted. Set DokploySettings.VerboseHttpLogging = "
                        + "true to log it verbatim — it may contain registry credentials and service "
                        + "environment variables, so do not leave it on."
                );
        });
    }

    /// <summary>
    /// Builds a JSON request body with <c>Content-Type: application/json</c> and <b>no charset
    /// parameter</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dokploy (v0.30.3) rejects the charset parameter. Isolated against a live instance with two
    /// requests identical in host, token, body, and HTTP/2 — differing only in this header:
    /// </para>
    /// <code>
    /// Content-Type: application/json                  → 200, project created
    /// Content-Type: application/json; charset=UTF-8   → 400
    ///   {"zodError":{"fieldErrors":{"name":["Invalid input: expected string, received undefined"]}}}
    /// </code>
    /// <para>
    /// Its body parser matches the content type strictly, skips parsing on the parameter, and the
    /// procedure then runs against an empty object. The body is on the wire in both cases — which
    /// is what made this present as a lost payload rather than a rejected header, and cost a long
    /// detour through WAF and redirect theories.
    /// </para>
    /// <para>
    /// Note this worked for a long time with <c>PostJsonAsync</c>: earlier Dokploy versions parsed
    /// the body regardless. Treat it as a v0.30.x behaviour change, not a long-standing bug.
    /// </para>
    /// <para>
    /// Flurl's <c>PostJsonAsync</c> always appends the charset, and it cannot be removed from a
    /// <c>BeforeCall</c> hook: the header reads correctly there and the charset is still on the
    /// socket (confirmed by capturing raw request bytes). Hence explicit content — the only place
    /// the header survives to the wire.
    /// </para>
    /// <para>
    /// Dropping the parameter is correct independently of Dokploy: JSON is UTF-8 by definition
    /// (RFC 8259 §8.1) and <c>charset</c> is not a defined parameter for <c>application/json</c>.
    /// </para>
    /// </remarks>
    private static StringContent JsonBody(object body)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(body, JsonSerializerOptions),
            Encoding.UTF8
        );
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string HeaderOrNone(IFlurlResponse response, string name) =>
        response.Headers.TryGetFirst(name, out var value) ? value : "(none)";

    /// <summary>
    /// Masks secrets so a failed call can be diagnosed from a shared CI log. Structure is preserved,
    /// which is the part that matters for "the server says this field is missing".
    /// </summary>
    /// <remarks>
    /// Two passes, because secrets reach Dokploy in two shapes: as JSON properties
    /// (<c>"password": "..."</c>) and — the one that is easy to miss — as <c>KEY=value</c> lines
    /// packed inside a single <c>env</c> string, which is how application environment variables are
    /// sent. Redacting only JSON property names leaks every service secret in the deploy payload.
    /// </remarks>
    private static string Redact(string body) =>
        string.IsNullOrEmpty(body)
            ? "(empty)"
            : SecretEnvAssignment()
                .Replace(SecretJsonProperty().Replace(body, "$1\"***\""), "$1=***");

    /// <summary>JSON properties whose NAME looks secret: <c>"password": "x"</c> → <c>"password": "***"</c>.</summary>
    /// <remarks>
    /// Ends with <c>["]</c> rather than a bare <c>"</c> deliberately. Written as a bare quote, the
    /// raw-string delimiter absorbs it, the match stops short of the closing quote, and the
    /// replacement emits <c>"key":"***""</c> — invalid JSON in the very diagnostic meant to reveal a
    /// malformed body.
    /// </remarks>
    [System.Text.RegularExpressions.GeneratedRegex(
        """("(?:[^"]*(?:password|passwd|token|secret|apikey|api_key|signingkey|key)[^"]*)"\s*:\s*)"[^"]*["]""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    )]
    private static partial System.Text.RegularExpressions.Regex SecretJsonProperty();

    /// <summary>
    /// <c>KEY=value</c> assignments inside a string value, as Dokploy's <c>env</c> blob carries them.
    /// The value runs to the next escaped newline or the closing quote.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        """([A-Za-z0-9_.:-]*(?:PASSWORD|PASSWD|TOKEN|SECRET|APIKEY|API_KEY|SIGNINGKEY|KEY)[A-Za-z0-9_.:-]*)=(?:(?!\\n|\\r|["])[^"])*""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    )]
    private static partial System.Text.RegularExpressions.Regex SecretEnvAssignment();



    // ─── Projects ────────────────────────────────────────────────────────────

    public async Task<List<ProjectResponse>> GetAllProjectsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("GET project.all");
        return await _client
                .Request("api", "project.all")
                .GetJsonAsync<List<ProjectResponse>>(cancellationToken: ct)
            ?? [];
    }

    public async Task<CreateProjectResponse> CreateProjectAsync(
        CreateProjectRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("POST project.create name={Name}", request.Name);
        var response = await _client
            .Request("api", "project.create")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct)
            .ReceiveJson<CreateProjectResponse>();

        return response ?? throw new InvalidOperationException("No response from project.create");
    }

    /// <summary>
    /// Finds an existing project by name, or creates it if not found.
    /// Returns both the <c>projectId</c> and the default <c>environmentId</c>.
    /// </summary>
    public async Task<(string projectId, string environmentId)> FindOrCreateProjectAsync(
        string name,
        CancellationToken ct = default
    )
    {
        var projects = await GetAllProjectsAsync(ct);
        var existing = projects.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
        );

        if (existing?.ProjectId is not null)
        {
            _logger.LogInformation(
                "Found existing Dokploy project '{Name}' ({Id})",
                name,
                existing.ProjectId
            );
            var envId =
                existing.DefaultEnvironmentId
                ?? throw new InvalidOperationException(
                    $"Project '{name}' ({existing.ProjectId}) has no environments"
                );
            return (existing.ProjectId, envId);
        }

        _logger.LogInformation("Creating Dokploy project '{Name}'", name);
        var created = await CreateProjectAsync(new CreateProjectRequest { Name = name }, ct);

        var projectId = created.Project?.ProjectId;
        var environmentId = created.Environment?.EnvironmentId;

        if (projectId is null || environmentId is null)
        {
            // Fallback: re-fetch all and find by name
            _logger.LogWarning(
                "project.create response missing projectId/environmentId — falling back to project.all lookup"
            );
            projects = await GetAllProjectsAsync(ct);
            var found = projects.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            );
            projectId ??= found?.ProjectId;
            environmentId ??= found?.DefaultEnvironmentId;
        }

        if (projectId is null)
            throw new InvalidOperationException(
                $"project.create returned no projectId for '{name}'"
            );
        if (environmentId is null)
            throw new InvalidOperationException(
                $"project.create returned no environmentId for '{name}'"
            );

        return (projectId, environmentId);
    }

    // ─── Environment ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all environments for a given project via <c>environment.byProjectId</c>.
    /// </summary>
    public async Task<List<EnvironmentInfo>> GetEnvironmentsByProjectAsync(
        string projectId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET environment.byProjectId projectId={ProjectId}", projectId);
        return await _client
            .Request("api", "environment.byProjectId")
            .SetQueryParam("projectId", projectId)
            .GetJsonAsync<List<EnvironmentInfo>>(cancellationToken: ct)
            ?? [];
    }

    /// <summary>
    /// Finds an existing environment by name within a project, or creates it if not found.
    /// Returns the <c>environmentId</c>.
    /// </summary>
    public async Task<string> FindOrCreateEnvironmentAsync(
        string projectId,
        string environmentName,
        CancellationToken ct = default
    )
    {
        var environments = await GetEnvironmentsByProjectAsync(projectId, ct);
        var existing = environments.FirstOrDefault(e =>
            string.Equals(e.Name, environmentName, StringComparison.OrdinalIgnoreCase)
        );

        if (existing?.EnvironmentId is not null)
        {
            _logger.LogInformation(
                "Found existing Dokploy environment '{Name}' ({Id})",
                environmentName,
                existing.EnvironmentId
            );
            return existing.EnvironmentId;
        }

        _logger.LogInformation(
            "Creating Dokploy environment '{Name}' in project '{ProjectId}'",
            environmentName,
            projectId
        );
        await _client
            .Request("api", "environment.create")
            .SendAsync(
                HttpMethod.Post,
                JsonBody(new { name = environmentName, projectId }),
                cancellationToken: ct
            );

        // Re-fetch to get the new environmentId
        environments = await GetEnvironmentsByProjectAsync(projectId, ct);
        var created = environments.FirstOrDefault(e =>
            string.Equals(e.Name, environmentName, StringComparison.OrdinalIgnoreCase)
        );

        return created?.EnvironmentId
            ?? throw new InvalidOperationException(
                $"environment.create succeeded but '{environmentName}' not found in project '{projectId}'"
            );
    }

    /// <summary>
    /// Fetches environment details including all embedded service lists
    /// (applications, redis, mariadb, mongo, mysql, postgres).
    /// This is the canonical way to list services — Dokploy has no separate *.all endpoints.
    /// </summary>
    public async Task<EnvironmentOneResponse> GetEnvironmentAsync(
        string environmentId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET environment.one environmentId={EnvironmentId}", environmentId);
        var rawJson = await _client
            .Request("api", "environment.one")
            .SetQueryParam("environmentId", environmentId)
            .GetStringAsync(cancellationToken: ct);

        _logger.LogDebug(
            "environment.one response (first 500 chars): {Response}",
            rawJson.Length > 500 ? rawJson[..500] : rawJson
        );

        return JsonSerializer.Deserialize<EnvironmentOneResponse>(rawJson, JsonSerializerOptions)
            ?? new EnvironmentOneResponse();
    }

    // ─── Applications ────────────────────────────────────────────────────────

    public async Task<ApplicationResponse> GetApplicationAsync(
        string applicationId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET application.one applicationId={Id}", applicationId);
        var json = await _client
            .Request("api", "application.one")
            .SetQueryParam("applicationId", applicationId)
            .GetStringAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<ApplicationResponse>(json, JsonSerializerOptions)
            ?? throw new InvalidOperationException($"No response from application.one for '{applicationId}'");
    }

    public async Task<ApplicationResponse> CreateApplicationAsync(
        CreateApplicationRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST application.create name={Name} appName={AppName} environmentId={EnvId}",
            request.Name, request.AppName, request.EnvironmentId
        );
        var response = await _client
            .Request("api", "application.create")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct)
            .ReceiveJson<ApplicationResponse>();

        return response
            ?? throw new InvalidOperationException("No response from application.create");
    }

    public async Task SaveDockerProviderAsync(
        SaveDockerProviderRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST application.saveDockerProvider applicationId={Id} image={Image}",
            request.ApplicationId, request.DockerImage
        );
        await _client
            .Request("api", "application.saveDockerProvider")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    public async Task SaveEnvironmentAsync(
        SaveEnvironmentRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST application.saveEnvironment applicationId={Id}",
            request.ApplicationId
        );
        await _client
            .Request("api", "application.saveEnvironment")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    public async Task DeployApplicationAsync(
        DeployApplicationRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("POST application.deploy applicationId={Id}", request.ApplicationId);
        await _client
            .Request("api", "application.deploy")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    public async Task UpdateApplicationAsync(
        UpdateApplicationRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST application.update applicationId={Id} healthCheck={HasHealthCheck}",
            request.ApplicationId,
            request.HealthCheckSwarm is not null
        );
        await _client
            .Request("api", "application.update")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    // ─── Redis ───────────────────────────────────────────────────────────────

    public async Task<RedisResponse> GetRedisAsync(
        string redisId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET redis.one redisId={Id}", redisId);
        var json = await _client
            .Request("api", "redis.one")
            .SetQueryParam("redisId", redisId)
            .GetStringAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<RedisResponse>(json, JsonSerializerOptions)
            ?? throw new InvalidOperationException($"No response from redis.one for '{redisId}'");
    }

    public async Task<RedisResponse> CreateRedisAsync(
        CreateRedisRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST redis.create name={Name} appName={AppName} environmentId={EnvId}",
            request.Name, request.AppName, request.EnvironmentId
        );
        var response = await _client
            .Request("api", "redis.create")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct)
            .ReceiveJson<RedisResponse>();

        return response ?? throw new InvalidOperationException("No response from redis.create");
    }

    public async Task DeployRedisAsync(DeployRedisRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("POST redis.deploy redisId={Id}", request.RedisId);
        await _client.Request("api", "redis.deploy").SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    // ─── MariaDB ─────────────────────────────────────────────────────────────

    public async Task<MariaDbResponse> GetMariaDbAsync(
        string mariadbId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET mariadb.one mariadbId={Id}", mariadbId);
        var json = await _client
            .Request("api", "mariadb.one")
            .SetQueryParam("mariadbId", mariadbId)
            .GetStringAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<MariaDbResponse>(json, JsonSerializerOptions)
            ?? throw new InvalidOperationException($"No response from mariadb.one for '{mariadbId}'");
    }

    public async Task<MariaDbResponse> CreateMariaDbAsync(
        CreateMariaDbRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST mariadb.create name={Name} appName={AppName} environmentId={EnvId}",
            request.Name, request.AppName, request.EnvironmentId
        );
        var response = await _client
            .Request("api", "mariadb.create")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct)
            .ReceiveJson<MariaDbResponse>();

        return response ?? throw new InvalidOperationException("No response from mariadb.create");
    }

    public async Task DeployMariaDbAsync(
        DeployMariaDbRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("POST mariadb.deploy mariadbId={Id}", request.MariaDbId);
        await _client
            .Request("api", "mariadb.deploy")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    // ─── MongoDB ─────────────────────────────────────────────────────────────

    public async Task<MongoResponse> GetMongoAsync(
        string mongoId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET mongo.one mongoId={Id}", mongoId);
        var json = await _client
            .Request("api", "mongo.one")
            .SetQueryParam("mongoId", mongoId)
            .GetStringAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<MongoResponse>(json, JsonSerializerOptions)
            ?? throw new InvalidOperationException($"No response from mongo.one for '{mongoId}'");
    }

    public async Task<MongoResponse> CreateMongoAsync(
        CreateMongoRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST mongo.create name={Name} appName={AppName} environmentId={EnvId}",
            request.Name, request.AppName, request.EnvironmentId
        );
        var response = await _client
            .Request("api", "mongo.create")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct)
            .ReceiveJson<MongoResponse>();

        return response ?? throw new InvalidOperationException("No response from mongo.create");
    }

    public async Task DeployMongoAsync(DeployMongoRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("POST mongo.deploy mongoId={Id}", request.MongoId);
        await _client.Request("api", "mongo.deploy").SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    // ─── MySQL ───────────────────────────────────────────────────────────────

    public async Task<MySqlResponse> GetMySqlAsync(
        string mysqlId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET mysql.one mysqlId={Id}", mysqlId);
        var json = await _client
            .Request("api", "mysql.one")
            .SetQueryParam("mysqlId", mysqlId)
            .GetStringAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<MySqlResponse>(json, JsonSerializerOptions)
            ?? throw new InvalidOperationException($"No response from mysql.one for '{mysqlId}'");
    }

    public async Task<MySqlResponse> CreateMySqlAsync(
        CreateMySqlRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST mysql.create name={Name} appName={AppName} environmentId={EnvId}",
            request.Name, request.AppName, request.EnvironmentId
        );
        var response = await _client
            .Request("api", "mysql.create")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct)
            .ReceiveJson<MySqlResponse>();

        return response ?? throw new InvalidOperationException("No response from mysql.create");
    }

    public async Task DeployMySqlAsync(DeployMySqlRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("POST mysql.deploy mysqlId={Id}", request.MySqlId);
        await _client.Request("api", "mysql.deploy").SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    // ─── PostgreSQL ───────────────────────────────────────────────────────────

    public async Task<PostgresResponse> GetPostgresAsync(
        string postgresId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET postgres.one postgresId={Id}", postgresId);
        var json = await _client
            .Request("api", "postgres.one")
            .SetQueryParam("postgresId", postgresId)
            .GetStringAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<PostgresResponse>(json, JsonSerializerOptions)
            ?? throw new InvalidOperationException($"No response from postgres.one for '{postgresId}'");
    }

    public async Task<PostgresResponse> CreatePostgresAsync(
        CreatePostgresRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "POST postgres.create name={Name} appName={AppName} environmentId={EnvId}",
            request.Name, request.AppName, request.EnvironmentId
        );
        var response = await _client
            .Request("api", "postgres.create")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct)
            .ReceiveJson<PostgresResponse>();

        return response ?? throw new InvalidOperationException("No response from postgres.create");
    }

    public async Task DeployPostgresAsync(
        DeployPostgresRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("POST postgres.deploy postgresId={Id}", request.PostgresId);
        await _client
            .Request("api", "postgres.deploy")
            .SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    // ─── Domains ─────────────────────────────────────────────────────────────

    public async Task<List<DomainListItem>> GetDomainsByApplicationIdAsync(
        string applicationId,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("GET domain.byApplicationId applicationId={Id}", applicationId);
        var json = await _client
            .Request("api", "domain.byApplicationId")
            .SetQueryParam("applicationId", applicationId)
            .GetStringAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<List<DomainListItem>>(json, JsonSerializerOptions) ?? [];
    }

    public async Task UpdateDomainAsync(UpdateDomainRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("POST domain.update domainId={Id} host={Host}", request.DomainId, request.Host);
        await _client.Request("api", "domain.update").SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    public async Task CreateDomainAsync(CreateDomainRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "POST domain.create host={Host} applicationId={Id}",
            request.Host, request.ApplicationId
        );
        await _client.Request("api", "domain.create").SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    // ─── Mounts ──────────────────────────────────────────────────────────────

    public async Task<List<MountListItem>> GetMountsByApplicationIdAsync(
        string applicationId,
        CancellationToken ct = default
    )
    {
        // Use listByServiceId which reads from the DB (includes bind mounts).
        // allNamedByApplicationId queries live Docker container mounts and filters
        // to Type=="volume" only — it never returns bind mounts, breaking dedup.
        _logger.LogDebug("GET mounts.listByServiceId serviceId={Id}", applicationId);
        var json = await _client
            .Request("api", "mounts.listByServiceId")
            .SetQueryParam("serviceId", applicationId)
            .SetQueryParam("serviceType", "application")
            .GetStringAsync(cancellationToken: ct);
        return JsonSerializer.Deserialize<List<MountListItem>>(json, JsonSerializerOptions) ?? [];
    }

    public async Task CreateMountAsync(CreateMountRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "POST mounts.create serviceId={Id} mountPath={Path} type={Type}",
            request.ServiceId, request.MountPath, request.Type
        );
        await _client.Request("api", "mounts.create").SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }

    /// <summary>Updates a mount in place — required so generated file content can change per deploy.</summary>
    public async Task UpdateMountAsync(UpdateMountRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "POST mounts.update mountId={Id} mountPath={Path} type={Type}",
            request.MountId, request.MountPath, request.Type
        );
        await _client.Request("api", "mounts.update").SendAsync(HttpMethod.Post, JsonBody(request), cancellationToken: ct);
    }
}
