using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

public class CreatePostgresRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("appName")]
    public required string AppName { get; set; }

    [JsonPropertyName("environmentId")]
    public required string EnvironmentId { get; set; }

    [JsonPropertyName("databasePassword")]
    public required string DatabasePassword { get; set; }

    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; set; }

    [JsonPropertyName("databaseUser")]
    public string? DatabaseUser { get; set; }

    [JsonPropertyName("dockerImage")]
    public string? DockerImage { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }
}

public class PostgresResponse
{
    [JsonPropertyName("postgresId")]
    public string? PostgresId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("applicationStatus")]
    public string? ApplicationStatus { get; set; }
}

/// <summary>Item returned by postgres.all.</summary>
public class PostgresListItem : PostgresResponse { }

public class DeployPostgresRequest
{
    [JsonPropertyName("postgresId")]
    public required string PostgresId { get; set; }
}
