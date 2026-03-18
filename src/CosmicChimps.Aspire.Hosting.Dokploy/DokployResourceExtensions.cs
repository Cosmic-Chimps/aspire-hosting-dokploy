using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Extension methods for publishing an Aspire application to Dokploy as individual per-service resources.
/// </summary>
public static class DokployResourceExtensions
{
    /// <summary>
    /// Registers Dokploy as the publish target. At publish time, each Aspire resource is created
    /// as an individual Dokploy Application or native Redis service (not a single Compose resource).
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">Name of the Dokploy resource (used as project name by default).</param>
    /// <param name="configure">Action to configure <see cref="DokploySettings"/>.</param>
    /// <returns>A resource builder for the <see cref="DokployResource"/>.</returns>
    /// <example>
    /// <code>
    /// builder.PublishToDokploy("bella-baxter", s =>
    /// {
    ///     s.DokployUrl  = "https://paas.example.com";
    ///     s.ApiToken    = builder.Configuration["Dokploy:ApiToken"]!;
    ///     s.ProjectName = "bella-baxter";         // optional override
    ///     s.AppNamePrefix = "bb-";                // optional prefix for appName
    /// });
    /// </code>
    /// </example>
    public static IResourceBuilder<DokployResource> PublishToDokploy(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<DokploySettings>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var settings = new DokploySettings { ProjectName = name };
        configure?.Invoke(settings);

        // AddDockerComposeEnvironment resolves all service references, images, and env vars
        // into a standard docker-compose.yaml that we then parse and push per-service.
        var composeEnv = builder.AddDockerComposeEnvironment($"{name}-compose");

        var dokployResource = new DokployResource(name)
        {
            DokployUrl = settings.DokployUrl,
            ApiToken = settings.ApiToken,
            ProjectName = settings.ProjectName,
            AppNamePrefix = settings.AppNamePrefix,
            ServerId = settings.ServerId,
            Registry = settings.Registry,
            ComposeEnvironment = composeEnv.Resource,
        };

        builder.Services.TryAddEventingSubscriber<DokployInfrastructure>();

        return builder.AddResource(dokployResource);
    }

    /// HTTPS endpoint (via Let's Encrypt by default).
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resourceBuilder">The resource builder.</param>
    /// <param name="dokployBuilder">The Dokploy resource builder (returned by <see cref="PublishToDokploy"/>).</param>
    /// <param name="host">The public hostname (e.g. <c>api.example.com</c>).</param>
    /// <param name="https">Whether to enable HTTPS. Defaults to <c>true</c>.</param>
    /// <param name="certificateType">Certificate type: <c>letsencrypt</c>, <c>none</c>, or <c>custom</c>. Defaults to <c>letsencrypt</c>.</param>
    /// <param name="port">Upstream container port. Null = use the first exposed port.</param>
    public static IResourceBuilder<T> WithDokployDomain<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource> dokployBuilder,
        string host,
        bool https = true,
        string certificateType = "letsencrypt",
        int? port = null
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(dokployBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceDomainAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
                Domain = new DokployDomainAnnotation
                {
                    Host = host,
                    Https = https,
                    CertificateType = certificateType,
                    Port = port,
                },
            }
        );

        return resourceBuilder;
    }
}

