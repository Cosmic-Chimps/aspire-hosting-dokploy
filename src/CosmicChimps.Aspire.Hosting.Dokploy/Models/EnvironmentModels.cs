using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

/// <summary>
/// Response shape for GET /api/environment.one?environmentId={id}.
/// All service lists are embedded directly in the environment response.
/// </summary>
public class EnvironmentOneResponse
{
    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("applications")]
    public List<ApplicationListItem> Applications { get; set; } = [];

    [JsonPropertyName("redis")]
    public List<RedisListItem> Redis { get; set; } = [];

    [JsonPropertyName("mariadb")]
    public List<MariaDbListItem> MariaDb { get; set; } = [];

    [JsonPropertyName("mongo")]
    public List<MongoListItem> Mongo { get; set; } = [];

    [JsonPropertyName("mysql")]
    public List<MySqlListItem> MySql { get; set; } = [];

    [JsonPropertyName("postgres")]
    public List<PostgresListItem> Postgres { get; set; } = [];
}
