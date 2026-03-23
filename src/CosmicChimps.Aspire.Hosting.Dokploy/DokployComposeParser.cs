using System.Text.RegularExpressions;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using YamlDotNet.RepresentationModel;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Parses a Docker Compose YAML file into a list of <see cref="DokployServiceDescriptor"/>s.
/// </summary>
public static class DokployComposeParser
{
    /// <summary>
    /// Parses a .env file (KEY=VALUE lines, # comments) into a dictionary.
    /// </summary>
    public static Dictionary<string, string> ParseEnvFile(string filePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath))
            return result;

        foreach (var raw in File.ReadAllLines(filePath))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || !line.Contains('='))
                continue;

            var idx = line.IndexOf('=');
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (!string.IsNullOrEmpty(key))
                result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Parse the compose YAML text into service descriptors.
    /// </summary>
    /// <param name="yaml">The contents of docker-compose.yaml.</param>
    /// <param name="envVars">
    /// Variable substitution map loaded from .env.Production (or similar).
    /// Used to resolve <c>${VAR}</c> references in image names and environment values.
    /// </param>
    /// <param name="domainAnnotations">
    /// Optional map of serviceName → domain hostname, populated from WithDomain() calls.
    /// </param>
    public static List<DokployServiceDescriptor> Parse(
        string yaml,
        IReadOnlyDictionary<string, string>? envVars = null,
        IReadOnlyDictionary<string, DokployDomainAnnotation>? domainAnnotations = null
    )
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));

        if (stream.Documents.Count == 0)
            return [];

        var root = (YamlMappingNode)stream.Documents[0].RootNode;

        if (!root.Children.TryGetValue(new YamlScalarNode("services"), out var servicesNode))
            return [];

        var services = (YamlMappingNode)servicesNode;
        var result = new List<DokployServiceDescriptor>();

        foreach (var entry in services.Children)
        {
            var name = ((YamlScalarNode)entry.Key).Value ?? string.Empty;
            var serviceNode = (YamlMappingNode)entry.Value;

            var rawImage = GetScalar(serviceNode, "image") ?? string.Empty;
            var image = SubstituteVars(rawImage, envVars);
            var envString = ParseEnvString(serviceNode, envVars);
            var ports = ParsePorts(serviceNode);
            var nativeType = DetectNativeServiceType(image);

            DokployDomainAnnotation? domainAnnotation = null;
            domainAnnotations?.TryGetValue(name, out domainAnnotation);

            result.Add(new DokployServiceDescriptor
            {
                Name = name,
                Image = image,
                EnvString = envString,
                Ports = ports,
                NativeServiceType = nativeType,
                HasExternalEndpoint = domainAnnotation is not null,
                Domain = domainAnnotation?.Host,
                Registry = domainAnnotation?.Registry,
            });
        }

        return result;
    }

    /// <summary>
    /// Substitutes compose service name references (DNS hostnames) in an env string with
    /// the actual Dokploy-assigned app names. Only replaces in the VALUE part of each line.
    /// Lines whose values reference a <paramref name="skippedServiceNames"/> hostname
    /// (e.g. the Aspire dashboard) are removed entirely.
    /// Lines whose KEY is in <paramref name="noSubstitutionKeys"/> are kept verbatim —
    /// use this for env vars that happen to contain a resource name but are NOT hostnames
    /// (e.g. Keycloak client IDs, OAuth scopes, feature flag names).
    /// </summary>
    public static string ApplyServiceNameSubstitution(
        string envString,
        IReadOnlyDictionary<string, string> serviceNameMap,
        IReadOnlyCollection<string>? skippedServiceNames = null,
        IReadOnlySet<string>? noSubstitutionKeys = null
    )
    {
        if (serviceNameMap.Count == 0 && (skippedServiceNames is null || skippedServiceNames.Count == 0))
            return envString;

        // Process longest names first to avoid partial-match issues
        var ordered = serviceNameMap.OrderByDescending(kv => kv.Key.Length).ToList();

        var lines = envString.Split('\n');
        var result = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var eqIdx = line.IndexOf('=');
            if (eqIdx <= 0)
            {
                result.Add(line);
                continue;
            }

            var key = line[..eqIdx];
            var value = line[(eqIdx + 1)..];

            // Keep lines for exempt env var keys verbatim (no hostname substitution)
            if (noSubstitutionKeys is not null && noSubstitutionKeys.Contains(key))
            {
                result.Add(line);
                continue;
            }

            // Drop lines whose values reference a skipped/filtered service hostname
            if (skippedServiceNames is not null)
            {
                var skip = false;
                foreach (var skipped in skippedServiceNames)
                {
                    if (Regex.IsMatch(
                            value,
                            $@"(?<![a-zA-Z0-9\-]){Regex.Escape(skipped)}(?![a-zA-Z0-9\-])"
                        ))
                    {
                        skip = true;
                        break;
                    }
                }
                if (skip)
                    continue;
            }

            // Substitute deployed service names with their Dokploy appNames
            foreach (var (composeName, appName) in ordered)
            {
                value = Regex.Replace(
                    value,
                    $@"(?<![a-zA-Z0-9\-]){Regex.Escape(composeName)}(?![a-zA-Z0-9\-])",
                    appName
                );
            }

            result.Add($"{key}={value}");
        }

        return string.Join('\n', result);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Detects whether an image belongs to a Dokploy native managed resource type.
    /// Returns null for regular application images.
    /// Excludes Aspire infrastructure images (e.g. aspire-dashboard).
    /// </summary>
    private static DokployNativeServiceType? DetectNativeServiceType(string image)
    {
        if (image.Contains("aspire", StringComparison.OrdinalIgnoreCase))
            return null;

        // Extract the image name (strip registry prefix and tag)
        var nameWithTag = image.Contains('/') ? image[(image.LastIndexOf('/') + 1)..] : image;
        var name = nameWithTag.Contains(':') ? nameWithTag[..nameWithTag.IndexOf(':')] : nameWithTag;

        return name.ToLowerInvariant() switch
        {
            "redis"     => DokployNativeServiceType.Redis,
            "mariadb"   => DokployNativeServiceType.MariaDb,
            "mongo"     => DokployNativeServiceType.Mongo,
            "mongodb"   => DokployNativeServiceType.Mongo,
            "mysql"     => DokployNativeServiceType.MySql,
            "postgres"  => DokployNativeServiceType.Postgres,
            "postgresql" => DokployNativeServiceType.Postgres,
            _ => null,
        };
    }

    private static string? GetScalar(YamlMappingNode node, string key)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            && value is YamlScalarNode scalar)
            return scalar.Value;
        return null;
    }

    private static string SubstituteVars(string value, IReadOnlyDictionary<string, string>? envVars)
    {
        if (envVars is null || !value.Contains('$'))
            return value;

        // Replace ${VAR} patterns (not $${VAR} — double-dollar is Docker Compose escape)
        return Regex.Replace(
            value,
            @"(?<!\$)\$\{([^}]+)\}",
            m => envVars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value
        );
    }

    private static string? ParseEnvString(
        YamlMappingNode service,
        IReadOnlyDictionary<string, string>? envVars
    )
    {
        if (!service.Children.TryGetValue(new YamlScalarNode("environment"), out var envNode))
            return null;

        var lines = new List<string>();

        // environment as a mapping: KEY: VALUE
        if (envNode is YamlMappingNode envMap)
        {
            foreach (var kv in envMap.Children)
            {
                var k = ((YamlScalarNode)kv.Key).Value ?? string.Empty;
                var raw = kv.Value is YamlScalarNode sv ? sv.Value ?? string.Empty : string.Empty;
                var v = SubstituteVars(raw, envVars);
                lines.Add($"{k}={v}");
            }
        }
        // environment as a sequence: - KEY=VALUE
        else if (envNode is YamlSequenceNode envSeq)
        {
            foreach (var item in envSeq.Children)
            {
                if (item is YamlScalarNode s && s.Value is not null)
                {
                    // Substitute vars in the value part
                    var eq = s.Value.IndexOf('=');
                    if (eq > 0 && envVars is not null)
                    {
                        var k = s.Value[..eq];
                        var v = SubstituteVars(s.Value[(eq + 1)..], envVars);
                        lines.Add($"{k}={v}");
                    }
                    else
                    {
                        lines.Add(s.Value);
                    }
                }
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private static List<string> ParsePorts(YamlMappingNode service)
    {
        var ports = new List<string>();

        if (!service.Children.TryGetValue(new YamlScalarNode("ports"), out var portsNode))
            return ports;

        if (portsNode is YamlSequenceNode portSeq)
        {
            foreach (var item in portSeq.Children)
            {
                if (item is YamlScalarNode s && s.Value is not null)
                    ports.Add(s.Value);
                else if (item is YamlMappingNode m)
                {
                    var target = GetScalar(m, "target");
                    var published = GetScalar(m, "published");
                    if (target is not null)
                        ports.Add(published is not null ? $"{published}:{target}" : target);
                }
            }
        }

        return ports;
    }
}

/// <summary>
/// Carries domain + registry info attached to a service via WithDomain().
/// </summary>
public class DokployDomainAnnotation
{
    public required string Host { get; init; }
    public bool Https { get; init; } = true;
    public string CertificateType { get; init; } = "letsencrypt";
    public int? Port { get; init; }
    public Models.RegistryCredentials? Registry { get; init; }
}
