using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        var composeEnv = builder.AddDockerComposeEnvironment($"{name}-compose");

        var dokployResource = new DokployResource(name)
        {
            DokployUrl = settings.DokployUrl,
            ApiToken = settings.ApiToken,
            ProjectName = settings.ProjectName,
            EnvironmentName = string.IsNullOrWhiteSpace(settings.EnvironmentName)
                ? "production"
                : settings.EnvironmentName,
            AppNamePrefix = settings.AppNamePrefix,
            ServerId = settings.ServerId,
            Registry = settings.Registry,
            DeployBypassToken = settings.DeployBypassToken,
            ComposeEnvironment = composeEnv.Resource,
        };

        // Register a pipeline step on the DokployResource that runs AFTER the compose YAML
        // has been written (publish stage) and images have been pushed (build stage).
        // DistributedApplicationPipeline scans all resources for PipelineStepAnnotation, so
        // this annotation is picked up automatically during `aspire deploy`.
#pragma warning disable ASPIREPIPELINES001
        dokployResource.Annotations.Add(
            new PipelineStepAnnotation(factoryContext =>
            {
                var step = new PipelineStep
                {
                    Name = $"dokploy-deploy-{name}",
                    Description = $"Deploys Aspire app '{name}' to Dokploy.",
                    Action = async ctx =>
                    {
                        var infra = new DokployInfrastructure(
                            ctx.Services.GetRequiredService<ILogger<DokployInfrastructure>>(),
                            ctx.Services
                        );
#pragma warning disable ASPIREPIPELINES001
                        await infra.DeployAsync(dokployResource, ctx.ReportingStep, ctx.CancellationToken);
#pragma warning restore ASPIREPIPELINES001
                    },
                };

                // Compose YAML is written in the "publish" stage;
                // container images are built/pushed in the "build" stage.
                step.DependsOn("publish");
                step.DependsOn("build");

                // Participate in the standard "deploy" aggregate step.
                step.RequiredBy("deploy");

                return step;
            })
        );
#pragma warning restore ASPIREPIPELINES001

        // Disable the built-in docker-compose-up step that DockerComposeEnvironmentResource
        // always registers. When Dokploy is the deployment target, there is no local Docker
        // daemon to run `docker compose up` against — Dokploy handles service startup itself.
        // We use reflection to swap the action with a no-op because PipelineStep.Action has
        // an `init` accessor (enforced by C# compiler only, not by the CLR).
#pragma warning disable ASPIREPIPELINES001
        dokployResource.Annotations.Add(
            new PipelineConfigurationAnnotation(ctx =>
            {
                var composeUpStepName = $"docker-compose-up-{name}-compose";
                var composeUpStep = ctx.Steps.FirstOrDefault(s => s.Name == composeUpStepName);
                if (composeUpStep is not null)
                {
                    typeof(PipelineStep)
                        .GetProperty(nameof(PipelineStep.Action))!
                        .SetValue(
                            composeUpStep,
                            (Func<PipelineStepContext, Task>)(_ => Task.CompletedTask)
                        );
                }

                return Task.CompletedTask;
            })
        );
#pragma warning restore ASPIREPIPELINES001

        return builder.AddResource(dokployResource);
    }

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

    /// <summary>
    /// Configures a Docker Swarm health check for this resource when deployed to Dokploy.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resourceBuilder">The resource builder.</param>
    /// <param name="dokployBuilder">The Dokploy resource builder (returned by <see cref="PublishToDokploy"/>).</param>
    /// <param name="cmd">
    /// The health check command. First element must be <c>"CMD"</c> or <c>"CMD-SHELL"</c>.
    /// Example: <c>["CMD", "curl", "-f", "http://localhost:8080/health"]</c>
    /// </param>
    /// <param name="interval">Time between health checks. Defaults to 30 seconds.</param>
    /// <param name="timeout">Maximum time to wait for a response. Defaults to 10 seconds.</param>
    /// <param name="startPeriod">Grace period before checks begin. Defaults to 10 seconds.</param>
    /// <param name="retries">Consecutive failures before unhealthy. Defaults to 3.</param>
    public static IResourceBuilder<T> WithDokployHealthCheck<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource> dokployBuilder,
        IReadOnlyList<string> cmd,
        TimeSpan? interval = null,
        TimeSpan? timeout = null,
        TimeSpan? startPeriod = null,
        int retries = 3
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(dokployBuilder);
        ArgumentNullException.ThrowIfNull(cmd);
        if (cmd.Count == 0)
            throw new ArgumentException("cmd must have at least one element", nameof(cmd));

        static long ToNs(TimeSpan ts) => (long)(ts.TotalSeconds * 1_000_000_000L);

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceHealthCheckAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
                HealthCheck = new HealthCheckSwarm
                {
                    Test = [..cmd],
                    Interval = ToNs(interval ?? TimeSpan.FromSeconds(30)),
                    Timeout = ToNs(timeout ?? TimeSpan.FromSeconds(10)),
                    StartPeriod = ToNs(startPeriod ?? TimeSpan.FromSeconds(10)),
                    Retries = retries,
                },
            }
        );

        return resourceBuilder;
    }

    /// <summary>
    /// Marks specific environment variable keys as exempt from Dokploy's service-name substitution.
    /// Use this for env vars whose values happen to match an Aspire resource name but are NOT
    /// DNS hostnames — for example Keycloak client IDs, OAuth scopes, or other identifiers.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resourceBuilder">The resource builder.</param>
    /// <param name="dokployBuilder">The Dokploy resource builder.</param>
    /// <param name="envVarKeys">
    /// One or more env var key names (case-insensitive) to exclude from substitution.
    /// Example: <c>"NUXT_PUBLIC_KEYCLOAK_CLIENT_ID"</c>
    /// </param>
    public static IResourceBuilder<T> WithDokployNoSubstitution<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource> dokployBuilder,
        params string[] envVarKeys
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(dokployBuilder);
        if (envVarKeys is null || envVarKeys.Length == 0)
            throw new ArgumentException("At least one env var key must be specified.", nameof(envVarKeys));

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceNoSubstitutionAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
                EnvKeys = new HashSet<string>(envVarKeys, StringComparer.OrdinalIgnoreCase),
            }
        );

        return resourceBuilder;
    }


    /// <summary>
    /// Sets the Docker Swarm stop grace period for this resource when deployed to Dokploy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When Docker Swarm redeploys a service, it sends <c>SIGTERM</c> and waits up to
    /// <paramref name="duration"/> before sending <c>SIGKILL</c>. If the container does not
    /// exit cleanly within that window, the process is killed forcefully — which can corrupt
    /// write-ahead logs for databases (PostgreSQL, RabbitMQ, etc.).
    /// </para>
    /// <para>
    /// The Dokploy UI default is 30 s. For PostgreSQL and other databases with write-ahead
    /// logs, 2 minutes is recommended to ensure a clean checkpoint before shutdown.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resourceBuilder">The resource builder to configure.</param>
    /// <param name="dokployBuilder">The Dokploy resource builder (returned by <see cref="PublishToDokploy"/>).</param>
    /// <param name="duration">
    /// How long Docker waits for the container to exit after SIGTERM before sending SIGKILL.
    /// Recommended minimum for databases: <c>TimeSpan.FromMinutes(2)</c>.
    /// </param>
    public static IResourceBuilder<T> WithDokployStopGracePeriod<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource> dokployBuilder,
        TimeSpan duration
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(dokployBuilder);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Stop grace period must be positive.");

        var nanoseconds = (long)(duration.TotalSeconds * 1_000_000_000L);

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceStopGracePeriodAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
                Nanoseconds = nanoseconds,
            }
        );

        return resourceBuilder;
    }

    /// <summary>
    /// Configures a persistent volume mount for this resource in Dokploy.
    /// Dokploy Application services (Docker Swarm) do not automatically persist data from
    /// <c>WithDataVolume()</c> — the volume must be registered via the Dokploy mounts API.
    /// This extension ensures the mount is created on first deploy and skipped on subsequent
    /// deploys if a mount for the same <paramref name="containerPath"/> already exists.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="resourceBuilder">The resource builder to configure.</param>
    /// <param name="dokployBuilder">The Dokploy resource builder (returned by <see cref="PublishToDokploy"/>).</param>
    /// <param name="containerPath">Absolute path inside the container (e.g. <c>/var/lib/postgresql/data</c>).</param>
    /// <param name="volumeName">
    /// Named Docker volume to create/reuse (e.g. <c>"bb-postgres-data"</c>).
    /// Use a stable name so the same volume is reused across deploys.
    /// </param>
    public static IResourceBuilder<T> WithDokployMount<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource>? dokployBuilder,
        string containerPath,
        string volumeName
    )
        where T : IResource
    {
        if (dokployBuilder is null)
            return resourceBuilder; // dev mode — no-op

        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceMountAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
                ContainerPath = containerPath,
                VolumeName = volumeName,
                Type = "volume",
            }
        );

        return resourceBuilder;
    }

    /// <summary>
    /// Registers a bind mount from a host path into the container for a Dokploy application service.
    /// The host directory must exist on the Docker host before deployment.
    /// </summary>
    public static IResourceBuilder<T> WithDokployBindMount<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource>? dokployBuilder,
        string hostPath,
        string containerPath
    )
        where T : IResource
    {
        if (dokployBuilder is null)
            return resourceBuilder; // dev mode — no-op

        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceMountAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
                ContainerPath = containerPath,
                HostPath = hostPath,
                Type = "bind",
            }
        );

        return resourceBuilder;
    }
}
