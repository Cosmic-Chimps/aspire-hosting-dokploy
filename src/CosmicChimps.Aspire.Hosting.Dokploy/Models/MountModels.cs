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
}

public class MountListItem
{
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
