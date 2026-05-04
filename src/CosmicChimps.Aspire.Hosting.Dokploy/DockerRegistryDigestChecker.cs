using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Queries Docker/OCI registries for image content digests (sha256:...).
/// Used by WithDokploySkipRedeploy to compare the currently-deployed image
/// against the newly-built image — even when the tags differ — so that a
/// Swarm rolling update is only triggered when the image content has actually
/// changed, not just because the CI pipeline generated a new timestamp tag.
/// </summary>
internal static class DockerRegistryDigestChecker
{
    // OCI + legacy Docker manifest media types — must all be listed so the
    // registry returns a consistent manifest digest regardless of image type.
    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
    ];

    /// <summary>
    /// Returns the content digest (sha256:...) for the given image reference,
    /// or <c>null</c> if the digest cannot be determined (registry unreachable,
    /// auth failure, unsupported registry, etc.).
    /// Failures are logged at Debug level and never throw — callers treat
    /// a null digest as "unable to compare, assume changed".
    /// </summary>
    public static async Task<string?> GetImageDigestAsync(
        string imageReference,
        string? username,
        string? password,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            if (!TryParseImageRef(imageReference, out var registry, out var repository, out var tag))
            {
                logger.LogDebug(
                    "Cannot parse image reference '{Ref}' for digest comparison",
                    imageReference
                );
                return null;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CosmicChimps.Aspire.Hosting.Dokploy/1.0");

            // First attempt without auth (works for public images on Docker Hub).
            var (digest, wwwAuth) = await FetchManifestDigestAsync(
                http, registry, repository, tag, token: null, ct
            );
            if (digest is not null)
                return digest;

            // Registry returned 401 — perform the standard Bearer token challenge.
            if (wwwAuth is null)
            {
                logger.LogDebug(
                    "Registry {Registry} did not return WWW-Authenticate for {Image}",
                    registry, imageReference
                );
                return null;
            }

            var token = await FetchBearerTokenAsync(
                http, wwwAuth, repository, username, password, ct
            );
            if (token is null)
            {
                logger.LogDebug(
                    "Could not obtain Bearer token for {Registry}/{Repo} — credentials may be missing",
                    registry, repository
                );
                return null;
            }

            (digest, _) = await FetchManifestDigestAsync(
                http, registry, repository, tag, token, ct
            );
            return digest;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Could not fetch image digest for '{Image}' — falling back to tag comparison",
                imageReference
            );
            return null;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Sends a HEAD request for the manifest.
    /// Returns (digest, null) on success, (null, wwwAuth) on 401, (null, null) otherwise.
    /// </summary>
    private static async Task<(string? Digest, string? WwwAuthenticate)> FetchManifestDigestAsync(
        HttpClient http,
        string registry,
        string repository,
        string tag,
        string? token,
        CancellationToken ct)
    {
        var url = $"https://{registry}/v2/{repository}/manifests/{tag}";
        using var req = new HttpRequestMessage(HttpMethod.Head, url);

        foreach (var mt in ManifestMediaTypes)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mt));

        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.IsSuccessStatusCode)
        {
            var digest = resp.Headers.TryGetValues("Docker-Content-Digest", out var vals)
                ? vals.FirstOrDefault()
                : null;
            return (digest, null);
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var wwwAuth = resp.Headers.WwwAuthenticate.ToString();
            return (null, string.IsNullOrWhiteSpace(wwwAuth) ? null : wwwAuth);
        }

        return (null, null);
    }

    /// <summary>
    /// Performs the Bearer token challenge flow described in the Docker Registry
    /// v2 authentication spec and the OCI Distribution Specification.
    /// Parses the realm, service, and scope from the WWW-Authenticate header,
    /// requests a pull token, and returns it.
    /// </summary>
    private static async Task<string?> FetchBearerTokenAsync(
        HttpClient http,
        string wwwAuthenticate,
        string repository,
        string? username,
        string? password,
        CancellationToken ct)
    {
        var realm = ExtractWwwAuthParam(wwwAuthenticate, "realm");
        if (realm is null)
            return null;

        var service = ExtractWwwAuthParam(wwwAuthenticate, "service") ?? "";
        var scope = $"repository:{repository}:pull";

        var uriBuilder = new UriBuilder(realm);
        var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
        if (!string.IsNullOrEmpty(service)) query["service"] = service;
        query["scope"] = scope;
        uriBuilder.Query = query.ToString();

        using var req = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        // Both "token" and "access_token" are valid per the spec.
        if (doc.RootElement.TryGetProperty("token", out var tokenProp))
            return tokenProp.GetString();
        if (doc.RootElement.TryGetProperty("access_token", out var accessProp))
            return accessProp.GetString();

        return null;
    }

    /// <summary>
    /// Parses a Docker/OCI image reference into (registry, repository, tag).
    /// Examples:
    ///   ghcr.io/cosmic-chimps/bella-postgres:main-20260504 → ghcr.io, cosmic-chimps/bella-postgres, main-20260504
    ///   postgres:16-alpine                                  → registry-1.docker.io, library/postgres, 16-alpine
    ///   myuser/myimage:latest                               → registry-1.docker.io, myuser/myimage, latest
    /// </summary>
    internal static bool TryParseImageRef(
        string imageRef,
        out string registry,
        out string repository,
        out string tag)
    {
        registry = repository = tag = "";

        if (string.IsNullOrWhiteSpace(imageRef))
            return false;

        // Strip digest suffix (e.g. @sha256:...) — we want the tag
        var atIdx = imageRef.IndexOf('@');
        if (atIdx > 0)
            imageRef = imageRef[..atIdx];

        // Split tag from the last colon (but only if the colon comes after any slash)
        var colonIdx = imageRef.LastIndexOf(':');
        var lastSlash = imageRef.LastIndexOf('/');

        if (colonIdx > lastSlash) // colon is in the tag segment, not the port
        {
            tag = imageRef[(colonIdx + 1)..];
            imageRef = imageRef[..colonIdx];
        }
        else
        {
            tag = "latest";
        }

        if (string.IsNullOrEmpty(tag))
            tag = "latest";

        // Determine registry vs repository path
        var firstSlash = imageRef.IndexOf('/');
        if (firstSlash > 0)
        {
            var possibleHost = imageRef[..firstSlash];
            var hasPort = possibleHost.Contains(':');
            var hasDot = possibleHost.Contains('.');
            var isLocalhost = string.Equals(possibleHost, "localhost", StringComparison.OrdinalIgnoreCase);

            if (hasDot || hasPort || isLocalhost)
            {
                // e.g. ghcr.io/... or localhost:5000/...
                registry = possibleHost;
                repository = imageRef[(firstSlash + 1)..];
            }
            else
            {
                // Docker Hub user image: "username/image"
                registry = "registry-1.docker.io";
                repository = imageRef;
            }
        }
        else
        {
            // Docker Hub official image: "postgres"
            registry = "registry-1.docker.io";
            repository = $"library/{imageRef}";
        }

        return !string.IsNullOrEmpty(repository);
    }

    /// <summary>
    /// Extracts a named parameter value from a WWW-Authenticate Bearer header.
    /// e.g. from: Bearer realm="https://ghcr.io/token",service="ghcr.io"
    /// ExtractWwwAuthParam(..., "realm") → "https://ghcr.io/token"
    /// </summary>
    private static string? ExtractWwwAuthParam(string wwwAuthenticate, string paramName)
    {
        var key = $"{paramName}=\"";
        var start = wwwAuthenticate.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += key.Length;
        var end = wwwAuthenticate.IndexOf('"', start);
        return end > start ? wwwAuthenticate[start..end] : null;
    }
}
