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
            _logger.LogInformation("Found existing Dokploy project '{Name}' ({Id})", name, existing.ProjectId);
            var envId = existing.DefaultEnvironmentId
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
            throw new InvalidOperationException($"project.create returned no projectId for '{name}'");
        if (environmentId is null)
            throw new InvalidOperationException($"project.create returned no environmentId for '{name}'");

        return (projectId, environmentId);
    }

    // ─── Applications ────────────────────────────────────────────────────────

    public async Task<ApplicationResponse> CreateApplicationAsync(
        CreateApplicationRequest request,
        CancellationToken ct = default
    )
    {
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
        await _client
            .Request("api", "application.saveDockerProvider")
            .PostJsonAsync(request, cancellationToken: ct);
    }

    public async Task SaveEnvironmentAsync(
        SaveEnvironmentRequest request,
        CancellationToken ct = default
    )
    {
        await _client
            .Request("api", "application.saveEnvironment")
            .PostJsonAsync(request, cancellationToken: ct);
    }

    public async Task DeployApplicationAsync(
        DeployApplicationRequest request,
        CancellationToken ct = default
    )
    {
        await _client
            .Request("api", "application.deploy")
            .PostJsonAsync(request, cancellationToken: ct);
    }

    // ─── Redis ───────────────────────────────────────────────────────────────

    public async Task<RedisResponse> CreateRedisAsync(
        CreateRedisRequest request,
        CancellationToken ct = default
    )
    {
        var response = await _client
            .Request("api", "redis.create")
            .PostJsonAsync(request, cancellationToken: ct)
            .ReceiveJson<RedisResponse>();

        return response ?? throw new InvalidOperationException("No response from redis.create");
    }

    public async Task DeployRedisAsync(DeployRedisRequest request, CancellationToken ct = default)
    {
        await _client.Request("api", "redis.deploy").PostJsonAsync(request, cancellationToken: ct);
    }

    // ─── MariaDB ─────────────────────────────────────────────────────────────

    public async Task<MariaDbResponse> CreateMariaDbAsync(
        CreateMariaDbRequest request,
        CancellationToken ct = default
    )
    {
        var response = await _client
            .Request("api", "mariadb.create")
            .PostJsonAsync(request, cancellationToken: ct)
            .ReceiveJson<MariaDbResponse>();

        return response ?? throw new InvalidOperationException("No response from mariadb.create");
    }

    public async Task DeployMariaDbAsync(DeployMariaDbRequest request, CancellationToken ct = default)
    {
        await _client.Request("api", "mariadb.deploy").PostJsonAsync(request, cancellationToken: ct);
    }

    // ─── MongoDB ─────────────────────────────────────────────────────────────

    public async Task<MongoResponse> CreateMongoAsync(
        CreateMongoRequest request,
        CancellationToken ct = default
    )
    {
        var response = await _client
            .Request("api", "mongo.create")
            .PostJsonAsync(request, cancellationToken: ct)
            .ReceiveJson<MongoResponse>();

        return response ?? throw new InvalidOperationException("No response from mongo.create");
    }

    public async Task DeployMongoAsync(DeployMongoRequest request, CancellationToken ct = default)
    {
        await _client.Request("api", "mongo.deploy").PostJsonAsync(request, cancellationToken: ct);
    }

    // ─── MySQL ───────────────────────────────────────────────────────────────

    public async Task<MySqlResponse> CreateMySqlAsync(
        CreateMySqlRequest request,
        CancellationToken ct = default
    )
    {
        var response = await _client
            .Request("api", "mysql.create")
            .PostJsonAsync(request, cancellationToken: ct)
            .ReceiveJson<MySqlResponse>();

        return response ?? throw new InvalidOperationException("No response from mysql.create");
    }

    public async Task DeployMySqlAsync(DeployMySqlRequest request, CancellationToken ct = default)
    {
        await _client.Request("api", "mysql.deploy").PostJsonAsync(request, cancellationToken: ct);
    }

    // ─── PostgreSQL ───────────────────────────────────────────────────────────

    public async Task<PostgresResponse> CreatePostgresAsync(
        CreatePostgresRequest request,
        CancellationToken ct = default
    )
    {
        var response = await _client
            .Request("api", "postgres.create")
            .PostJsonAsync(request, cancellationToken: ct)
            .ReceiveJson<PostgresResponse>();

        return response ?? throw new InvalidOperationException("No response from postgres.create");
    }

    public async Task DeployPostgresAsync(DeployPostgresRequest request, CancellationToken ct = default)
    {
        await _client.Request("api", "postgres.deploy").PostJsonAsync(request, cancellationToken: ct);
    }

    // ─── Domains ─────────────────────────────────────────────────────────────

    public async Task CreateDomainAsync(CreateDomainRequest request, CancellationToken ct = default)
    {
        await _client.Request("api", "domain.create").PostJsonAsync(request, cancellationToken: ct);
    }
}
