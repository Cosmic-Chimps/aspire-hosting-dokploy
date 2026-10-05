using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

/// <summary>
/// Item returned by <c>deployment.all</c> — one row per deploy of an application.
/// </summary>
/// <remarks>
/// Dokploy creates the row when its queue worker picks the job up, not when
/// <c>application.deploy</c> returns, so a just-triggered deploy is briefly absent from the list.
/// Native databases (postgres, redis, …) never get rows: their <c>*.deploy</c> call runs inline and
/// fails the HTTP request itself.
/// </remarks>
public class DeploymentListItem
{
    [JsonPropertyName("deploymentId")]
    public string? DeploymentId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary><c>running</c>, <c>done</c>, <c>error</c> or <c>cancelled</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>ISO-8601 text, not a timestamp column — sorts correctly as a string.</summary>
    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }
}
