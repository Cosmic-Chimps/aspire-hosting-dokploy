using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
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
            ComposeEnvironmentBuilder = composeEnv,
            DeployDashboard = settings.DeployDashboard,
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

    /// <summary>
    /// Deploys the Aspire dashboard to Dokploy as an ordinary application service, instead of
    /// filtering it out of the published output, and optionally configures it.
    /// </summary>
    /// <param name="dokployBuilder">The Dokploy resource builder returned by <see cref="PublishToDokploy"/>.</param>
    /// <param name="configure">
    /// Optional dashboard configuration — for example <c>d => d.WithHostPort(18888).WithForwardedHeaders(true)</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is the opt-in described on <see cref="DokploySettings.DeployDashboard"/>. Equivalent to
    /// setting that property; use whichever reads better at the call site.
    /// </para>
    /// <para>
    /// <b>Do not give the dashboard a public domain.</b> It displays telemetry from every service,
    /// its OTLP ingest endpoint accepts anything by default, and telemetry spoofing is a documented
    /// threat. Reach it over the platform's internal network, and configure
    /// <c>Dashboard:Otlp:AuthMode=ApiKey</c> with a key. See
    /// <see href="https://aspire.dev/dashboard/security-considerations/"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var dokploy = builder.PublishToDokploy("myapp", s => { /* ... */ })
    ///     .WithDokployDashboard(d => d.WithHostPort(18888).WithForwardedHeaders(true));
    /// </code>
    /// </example>
    public static IResourceBuilder<DokployResource> WithDokployDashboard(
        this IResourceBuilder<DokployResource> dokployBuilder,
        Action<IResourceBuilder<DockerComposeAspireDashboardResource>>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(dokployBuilder);

        dokployBuilder.Resource.DeployDashboard = true;

        var composeEnv = dokployBuilder.Resource.ComposeEnvironmentBuilder;
        if (configure is not null)
            composeEnv.WithDashboard(configure);
        else
            composeEnv.WithDashboard(enabled: true);

        return dokployBuilder;
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
    /// Sets the Docker Swarm rolling update order for this resource when deployed to Dokploy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default Docker Swarm uses <c>start-first</c>: it starts the new container before stopping
    /// the old one. For stateful single-replica services with a data-directory lock (PostgreSQL, RabbitMQ),
    /// this causes a race where both containers try to own the same volume simultaneously, resulting in
    /// <c>postmaster.pid invalid</c> errors and container crashes.
    /// </para>
    /// <para>
    /// Use <c>stop-first</c> to make Swarm stop the old container fully before starting the new one.
    /// For most stateful services, combine with <see cref="WithDokployStopGracePeriod{T}"/> so the
    /// old container has time to flush WAL / do a clean checkpoint before being killed.
    /// </para>
    /// <para>
    /// Or simply call <see cref="WithDokployStatefulService{T}"/> which sets both at once.
    /// </para>
    /// </remarks>
    /// <param name="order">"stop-first" (recommended for stateful services) or "start-first" (Swarm default).</param>
    public static IResourceBuilder<T> WithDokployUpdateOrder<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource> dokployBuilder,
        string order = "stop-first"
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(dokployBuilder);
        if (order is not ("stop-first" or "start-first"))
            throw new ArgumentException("Order must be 'stop-first' or 'start-first'.", nameof(order));

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceUpdateOrderAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
                Order = order,
            }
        );

        return resourceBuilder;
    }

    /// <summary>
    /// Convenience method that configures a stateful single-replica service (PostgreSQL, RabbitMQ, etc.)
    /// for safe Dokploy redeployment by combining three settings:
    /// <list type="bullet">
    ///   <item><description><b>skip-redeploy if running</b> — if the service is already running, the Aspire pipeline updates its config but does NOT trigger a Swarm redeploy. This prevents unnecessary restarts on every app deployment.</description></item>
    ///   <item><description><b>stop-first update order</b> — when a redeploy IS needed, Swarm stops the old container before starting the new one, preventing data-directory lock races.</description></item>
    ///   <item><description><b>stop grace period</b> — gives the old container time to flush WAL / quorum sync before being killed.</description></item>
    /// </list>
    /// </summary>
    /// <param name="stopGracePeriod">
    /// How long Docker waits after SIGTERM before sending SIGKILL.
    /// Defaults to 2 minutes — sufficient for PostgreSQL to complete a clean checkpoint.
    /// </param>
    public static IResourceBuilder<T> WithDokployStatefulService<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource> dokployBuilder,
        TimeSpan? stopGracePeriod = null
    )
        where T : IResource
    {
        return resourceBuilder
            .WithDokploySkipRedeploy(dokployBuilder)
            .WithDokployUpdateOrder(dokployBuilder, "stop-first")
            .WithDokployStopGracePeriod(dokployBuilder, stopGracePeriod ?? TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Marks this service as "skip redeploy if already running" in Dokploy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set, the Aspire deployment pipeline still saves the latest docker image and env vars
    /// to Dokploy (so configuration stays up to date), but skips calling <c>DeployApplicationAsync</c>
    /// if the service is already running. If the service is NOT running (first deploy, crashed, or
    /// manually stopped), it is always deployed regardless of this flag.
    /// </para>
    /// <para>
    /// Use this for stateful infrastructure services (PostgreSQL, RabbitMQ, Keycloak) where
    /// a container restart on every app deployment is undesirable. When you genuinely need to
    /// redeploy the service (e.g. after an image upgrade), trigger it manually from the Dokploy UI.
    /// </para>
    /// <para>
    /// Typically combined with <see cref="WithDokployUpdateOrder{T}"/> and
    /// <see cref="WithDokployStopGracePeriod{T}"/> — or simply use
    /// <see cref="WithDokployStatefulService{T}"/> which sets all three at once.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<T> WithDokploySkipRedeploy<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource> dokployBuilder
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(dokployBuilder);

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceSkipRedeployAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
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

    /// <summary>
    /// Completely excludes this service from the Dokploy deployment pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set, the service is removed from the services-to-deploy list before any Dokploy API
    /// calls are made. The service is not updated, not redeployed, and not touched in any way.
    /// </para>
    /// <para>
    /// Use this for services you want to deploy independently — e.g. skip openbao/keycloak/postgres
    /// during an app-only CI deploy. Typically driven by a CI environment variable from a manual
    /// workflow dispatch input:
    /// <code>
    /// if (config["Deploy:Postgres"] != "false")
    ///     postgres.WithDokployExclude(dokploy);
    /// </code>
    /// </para>
    /// <para>
    /// Unlike <see cref="WithDokploySkipRedeploy{T}"/> (which still updates config in Dokploy),
    /// this annotation skips the service entirely — no config update, no redeploy check.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<T> WithDokployExclude<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<DokployResource>? dokployBuilder
    )
        where T : IResource
    {
        if (dokployBuilder is null)
            return resourceBuilder;

        dokployBuilder.Resource.Annotations.Add(
            new DokployServiceExcludeAnnotation
            {
                ServiceName = resourceBuilder.Resource.Name,
            }
        );

        return resourceBuilder;
    }
}
