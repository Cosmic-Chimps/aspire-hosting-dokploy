using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Models;

public class CreateProjectRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Response from project.create — wraps the created project and its default environment.
/// Shape: { project: { projectId, name, ... }, environment: { environmentId, ... } }
/// </summary>
public class CreateProjectResponse
{
    [JsonPropertyName("project")]
    public ProjectResponse? Project { get; set; }

    [JsonPropertyName("environment")]
    public EnvironmentInfo? Environment { get; set; }
}

public class EnvironmentInfo
{
    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("isDefault")]
    public bool? IsDefault { get; set; }
}

/// <summary>
/// Flat project object returned by project.all and project.one.
/// Includes the nested environments list (each with environmentId).
/// </summary>
public class ProjectResponse
{
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("environments")]
    public List<EnvironmentInfo>? Environments { get; set; }

    /// <summary>Returns the default environment ID, or the first environment ID if no default is marked.</summary>
    public string? DefaultEnvironmentId =>
        Environments?.FirstOrDefault(e => e.IsDefault == true)?.EnvironmentId
        ?? Environments?.FirstOrDefault()?.EnvironmentId;
}
