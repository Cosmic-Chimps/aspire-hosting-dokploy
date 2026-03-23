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
public class DokployApiClient
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

    public DokployApiClient(HttpClient httpClient, ILogger<DokployApiClient> logger)
    {
        _logger = logger;
        _client = new FlurlClient(httpClient).WithSettings(s =>
        {
            s.JsonSerializer = new DefaultJsonSerializer(JsonSerializerOptions);
        });

        _client.OnError(async call =>
        {
            if (call.Response is not null)
            {
                var body = await call.Response.GetStringAsync();
                logger.LogWarning(
                    call.Exception,
                    "Dokploy API {Method} {Url} → {Status}. Body: {Body}",
                    call.Request.Verb,
                    call.Request.Url,
                    (int)call.Response.StatusCode,
                    body
                );
            }
        });
    }

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
            .PostJsonAsync(request, cancellationToken: ct)
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
            .PostJsonAsync(
                new { name = environmentName, projectId },
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
            .PostJsonAsync(request, cancellationToken: ct)
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
            .PostJsonAsync(request, cancellationToken: ct);
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
            .PostJsonAsync(request, cancellationToken: ct);
    }

    public async Task DeployApplicationAsync(
        DeployApplicationRequest request,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("POST application.deploy applicationId={Id}", request.ApplicationId);
        await _client
            .Request("api", "application.deploy")
            .PostJsonAsync(request, cancellationToken: ct);
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
            .PostJsonAsync(request, cancellationToken: ct)
            .ReceiveJson<RedisResponse>();

        return response ?? throw new InvalidOperationException("No response from redis.create");
    }

    public async Task DeployRedisAsync(DeployRedisRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("POST redis.deploy redisId={Id}", request.RedisId);
        await _client.Request("api", "redis.deploy").PostJsonAsync(request, cancellationToken: ct);
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
            .PostJsonAsync(request, cancellationToken: ct)
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
            .PostJsonAsync(request, cancellationToken: ct);
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
            .PostJsonAsync(request, cancellationToken: ct)
            .ReceiveJson<MongoResponse>();

        return response ?? throw new InvalidOperationException("No response from mongo.create");
    }

    public async Task DeployMongoAsync(DeployMongoRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("POST mongo.deploy mongoId={Id}", request.MongoId);
        await _client.Request("api", "mongo.deploy").PostJsonAsync(request, cancellationToken: ct);
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
            .PostJsonAsync(request, cancellationToken: ct)
            .ReceiveJson<MySqlResponse>();

        return response ?? throw new InvalidOperationException("No response from mysql.create");
    }

    public async Task DeployMySqlAsync(DeployMySqlRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("POST mysql.deploy mysqlId={Id}", request.MySqlId);
        await _client.Request("api", "mysql.deploy").PostJsonAsync(request, cancellationToken: ct);
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
            .PostJsonAsync(request, cancellationToken: ct)
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
            .PostJsonAsync(request, cancellationToken: ct);
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
        await _client.Request("api", "domain.update").PostJsonAsync(request, cancellationToken: ct);
    }

    public async Task CreateDomainAsync(CreateDomainRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "POST domain.create host={Host} applicationId={Id}",
            request.Host, request.ApplicationId
        );
        await _client.Request("api", "domain.create").PostJsonAsync(request, cancellationToken: ct);
    }
}
