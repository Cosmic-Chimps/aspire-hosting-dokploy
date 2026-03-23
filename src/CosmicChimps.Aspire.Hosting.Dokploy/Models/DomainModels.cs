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

public class UpdateDomainRequest
{
    [JsonPropertyName("domainId")]
    public required string DomainId { get; set; }

    [JsonPropertyName("host")]
    public required string Host { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("https")]
    public bool Https { get; set; } = true;

    [JsonPropertyName("certificateType")]
    public string CertificateType { get; set; } = "letsencrypt";

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public class DomainListItem
{
    [JsonPropertyName("domainId")]
    public string? DomainId { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("https")]
    public bool Https { get; set; }

    [JsonPropertyName("certificateType")]
    public string? CertificateType { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}
