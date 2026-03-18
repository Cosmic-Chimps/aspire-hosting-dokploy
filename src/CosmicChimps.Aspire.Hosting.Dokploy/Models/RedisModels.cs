using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

public class CreateRedisRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("appName")]
    public required string AppName { get; set; }

    [JsonPropertyName("environmentId")]
    public required string EnvironmentId { get; set; }

    [JsonPropertyName("databasePassword")]
    public required string DatabasePassword { get; set; }

    [JsonPropertyName("dockerImage")]
    public string? DockerImage { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }
}

public class RedisResponse
{
    [JsonPropertyName("redisId")]
    public string? RedisId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("applicationStatus")]
    public string? ApplicationStatus { get; set; }
}

/// <summary>Item returned by redis.all.</summary>
public class RedisListItem : RedisResponse { }

public class DeployRedisRequest
{
    [JsonPropertyName("redisId")]
    public required string RedisId { get; set; }
}
