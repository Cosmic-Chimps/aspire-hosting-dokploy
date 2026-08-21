using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

public class CreateMountRequest
{
    /// <summary>"bind", "volume", or "file".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>Container path where the mount is exposed.</summary>
    [JsonPropertyName("mountPath")]
    public required string MountPath { get; set; }

    /// <summary>Service ID — applicationId for Application services.</summary>
    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; set; }

    /// <summary>"application", "postgres", "redis", etc.</summary>
    [JsonPropertyName("serviceType")]
    public required string ServiceType { get; set; }

    /// <summary>Named volume name (used when type = "volume").</summary>
    [JsonPropertyName("volumeName")]
    public string? VolumeName { get; set; }

    /// <summary>Host path (used when type = "bind").</summary>
    [JsonPropertyName("hostPath")]
    public string? HostPath { get; set; }

    /// <summary>
    /// File CONTENT (type = "file"). Dokploy stores the text and materialises it on the Docker host
    /// before mounting — the only way to give a container a file that does not already exist on that
    /// host, since a bind mount just names a path.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Name Dokploy gives the materialised file (type = "file").</summary>
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }
}

/// <summary>Update an existing mount in place, so generated content can change between deploys.</summary>
public class UpdateMountRequest
{
    [JsonPropertyName("mountId")]
    public required string MountId { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("mountPath")]
    public required string MountPath { get; set; }

    [JsonPropertyName("serviceType")]
    public string ServiceType { get; set; } = "application";

    [JsonPropertyName("volumeName")]
    public string? VolumeName { get; set; }

    [JsonPropertyName("hostPath")]
    public string? HostPath { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }
}

public class MountListItem
{
    /// <summary>Current file content, so a deploy can tell unchanged config from stale config.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("mountId")]
    public string? MountId { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("mountPath")]
    public string? MountPath { get; set; }

    [JsonPropertyName("volumeName")]
    public string? VolumeName { get; set; }

    [JsonPropertyName("hostPath")]
    public string? HostPath { get; set; }

    [JsonPropertyName("serviceType")]
    public string? ServiceType { get; set; }
}
