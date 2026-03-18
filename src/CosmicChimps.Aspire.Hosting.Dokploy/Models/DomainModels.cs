using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

public class CreateDomainRequest
{
    [JsonPropertyName("applicationId")]
    public string? ApplicationId { get; set; }

    [JsonPropertyName("host")]
    public required string Host { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("https")]
    public bool Https { get; set; } = true;

    /// <summary>
    /// "letsencrypt", "none", or "custom".
    /// </summary>
    [JsonPropertyName("certificateType")]
    public string CertificateType { get; set; } = "letsencrypt";

    [JsonPropertyName("customCertResolver")]
    public string? CustomCertResolver { get; set; }
}
