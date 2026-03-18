using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Aspire lifecycle subscriber that deploys each service in the app as an individual
/// Dokploy Application or native Redis resource — one per Aspire resource.
///
/// Subscribes to <see cref="BeforeStartEvent"/> (same as DockerComposeInfrastructure).
/// Because DockerComposeInfrastructure is registered first (via AddDockerComposeEnvironment),
/// it writes the compose YAML before this subscriber runs — guaranteed by Aspire's
/// BlockingSequential event dispatch.
/// </summary>
internal sealed class DokployInfrastructure(
    ILogger<DokployInfrastructure> logger,
    DistributedApplicationExecutionContext executionContext,
    IServiceProvider serviceProvider
) : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }

    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken ct = default)
    {
        if (executionContext.IsRunMode)
            return;

        var dokployResources = @event.Model.Resources.OfType<DokployResource>().ToArray();
        if (dokployResources.Length == 0)
            return;

        foreach (var resource in dokployResources)
            await DeployAsync(resource, ct);
    }

    private async Task DeployAsync(DokployResource resource, CancellationToken ct)
    {
        Validate(resource);

        logger.LogInformation(
            "Starting per-service Dokploy deployment for '{Name}' → {Url}",
            resource.Name,
            resource.DokployUrl
        );

        // ── 1. Find compose YAML ──────────────────────────────────────────────
        var composeYamlPath = FindComposePath(resource);
        var outputDir = Path.GetDirectoryName(composeYamlPath)!;
        var composeYaml = await File.ReadAllTextAsync(composeYamlPath, ct);
        logger.LogDebug("Read compose YAML from {Path}", composeYamlPath);

        // ── 2. Load .env.Production for ${VAR} substitution ───────────────────
        var envVars = LoadEnvVars(outputDir);
        logger.LogDebug("Loaded {Count} env vars for substitution", envVars.Count);

        // ── 3. Collect domain annotations (from WithDomain() calls) ──────────
        var domainAnnotations = CollectDomainAnnotations(resource);

        // ── 4. Parse compose into per-service descriptors ─────────────────────
        var services = DokployComposeParser.Parse(composeYaml, envVars, domainAnnotations);

        // ── 5. Filter internal Aspire services (dashboard, etc.) ─────────────
        var servicesToDeploy = services.Where(s => !IsAspireInternalService(s)).ToList();

        var skipped = services.Except(servicesToDeploy).Select(s => s.Name).ToList();
        if (skipped.Count > 0)
            logger.LogInformation(
                "Skipping internal Aspire service(s): {Names}",
                string.Join(", ", skipped)
            );

        logger.LogInformation(
            "Deploying {Count} service(s): {Names}",
            servicesToDeploy.Count,
            string.Join(", ", servicesToDeploy.Select(s => s.Name))
        );

        // ── 6. Load idempotency state ─────────────────────────────────────────
        var stateStore = new DokployStateStore(outputDir, logger);
        await stateStore.LoadAsync(ct);

        // ── 7. Build API client ───────────────────────────────────────────────
        var apiClient = BuildApiClient(resource);

        // ── 8. Find or create Dokploy project + default environment ──────────
        string projectId;
        string environmentId;

        var savedProjectId = stateStore.GetProjectId();
        var savedEnvId = stateStore.GetEnvironmentId();

        if (savedProjectId is not null && savedEnvId is not null)
        {
            logger.LogInformation(
                "Reusing saved project '{ProjectId}' / environment '{EnvId}'",
                savedProjectId,
                savedEnvId
            );
            projectId = savedProjectId;
            environmentId = savedEnvId;
        }
        else
        {
            (projectId, environmentId) = await apiClient.FindOrCreateProjectAsync(
                resource.ProjectName,
                ct
            );
            stateStore.SetProjectId(projectId);
            stateStore.SetEnvironmentId(environmentId);
        }

        // ── 9. PASS 1: Create all services, collect composeName → appName map ─
        //    We must do this before setting env vars because each service's env
        //    vars may reference other services by their Dokploy appName (DNS name).
        var serviceNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var svc in servicesToDeploy)
        {
            string appName;
            try
            {
                appName = svc.IsNativeService
                    ? await EnsureNativeServiceCreatedAsync(
                        svc,
                        environmentId,
                        resource,
                        apiClient,
                        stateStore,
                        ct
                    )
                    : await EnsureApplicationCreatedAsync(
                        svc,
                        environmentId,
                        resource,
                        apiClient,
                        stateStore,
                        ct
                    );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create service '{Service}'", svc.Name);
                throw;
            }
            serviceNameMap[svc.Name] = appName;
        }

        logger.LogDebug(
            "Service name map: {Map}",
            string.Join(", ", serviceNameMap.Select(kv => $"{kv.Key}→{kv.Value}"))
        );

        // ── 10. PASS 2: Configure each service (env vars + image) then deploy ─
        foreach (var svc in servicesToDeploy)
        {
            // Replace compose service names with Dokploy appNames in env values,
            // also strip lines referencing skipped services (e.g. OTEL dashboard).
            var envString = svc.EnvString is not null
                ? DokployComposeParser.ApplyServiceNameSubstitution(svc.EnvString, serviceNameMap, skipped)
                : null;

            try
            {
                if (svc.IsNativeService)
                {
                    var nativeId = stateStore.GetNativeServiceId(svc.Name)!;
                    logger.LogInformation(
                        "Deploying {Type} '{Service}' ({Id})",
                        svc.NativeServiceType, svc.Name, nativeId);
                    await DeployNativeServiceAsync(svc, nativeId, apiClient, ct);
                }
                else
                {
                    var applicationId = stateStore.GetApplicationId(svc.Name)!;
                    await ConfigureAndDeployApplicationAsync(
                        svc,
                        applicationId,
                        envString,
                        resource,
                        apiClient,
                        domainAnnotations,
                        ct
                    );
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure/deploy service '{Service}'", svc.Name);
                throw;
            }
        }

        // ── 11. Persist state ─────────────────────────────────────────────────
        await stateStore.SaveAsync(ct);
        logger.LogInformation("Dokploy deployment complete for '{Name}'", resource.Name);
    }

    // ── Pass 1: Create services ───────────────────────────────────────────────

    /// <summary>Creates the Dokploy application (or reuses existing). Returns the Dokploy appName.</summary>
    private async Task<string> EnsureApplicationCreatedAsync(
        DokployServiceDescriptor svc,
        string environmentId,
        DokployResource resource,
        DokployApiClient apiClient,
        DokployStateStore stateStore,
        CancellationToken ct
    )
    {
        var existingId = stateStore.GetApplicationId(svc.Name);
        var existingAppName = stateStore.GetAppName(svc.Name);

        if (existingId is not null && existingAppName is not null)
        {
            logger.LogInformation(
                "Application '{Service}' already exists ({Id}), reusing",
                svc.Name,
                existingId
            );
            return existingAppName;
        }

        logger.LogInformation("Creating application '{Service}'", svc.Name);
        var requestedAppName = string.IsNullOrEmpty(resource.AppNamePrefix)
            ? svc.Name
            : $"{resource.AppNamePrefix}{svc.Name}";

        var created = await apiClient.CreateApplicationAsync(
            new CreateApplicationRequest
            {
                Name = svc.Name,
                EnvironmentId = environmentId,
                AppName = requestedAppName,
                ServerId = resource.ServerId,
            },
            ct
        );

        var applicationId =
            created.ApplicationId
            ?? throw new InvalidOperationException(
                $"application.create returned no applicationId for '{svc.Name}'"
            );

        // Use the appName Dokploy actually assigned (may include a random suffix like "-pakivg")
        var assignedAppName = created.AppName ?? requestedAppName;

        logger.LogInformation(
            "Created application '{Service}': id={Id}, appName={AppName}",
            svc.Name,
            applicationId,
            assignedAppName
        );

        stateStore.SetApplicationId(svc.Name, applicationId);
        stateStore.SetAppName(svc.Name, assignedAppName);
        return assignedAppName;
    }

    /// <summary>
    /// Creates the Dokploy native managed resource (Redis/MariaDB/MongoDB/MySQL/Postgres),
    /// or reuses the existing one from state. Returns the Dokploy appName.
    /// </summary>
    private async Task<string> EnsureNativeServiceCreatedAsync(
        DokployServiceDescriptor svc,
        string environmentId,
        DokployResource resource,
        DokployApiClient apiClient,
        DokployStateStore stateStore,
        CancellationToken ct
    )
    {
        var existingId = stateStore.GetNativeServiceId(svc.Name);
        var existingAppName = stateStore.GetAppName(svc.Name);

        if (existingId is not null && existingAppName is not null)
        {
            logger.LogInformation(
                "{Type} '{Service}' already exists ({Id}), reusing",
                svc.NativeServiceType, svc.Name, existingId);
            return existingAppName;
        }

        logger.LogInformation("Creating {Type} service '{Service}'", svc.NativeServiceType, svc.Name);

        var password = ExtractDbPassword(svc) ?? Guid.NewGuid().ToString("N")[..16];
        var requestedAppName = string.IsNullOrEmpty(resource.AppNamePrefix)
            ? svc.Name
            : $"{resource.AppNamePrefix}{svc.Name}";

        var (nativeServiceId, assignedAppName) = svc.NativeServiceType switch
        {
            DokployNativeServiceType.Redis => await CreateRedisAsync(
                svc, requestedAppName, environmentId, password, resource, apiClient, ct),

            DokployNativeServiceType.MariaDb => await CreateMariaDbAsync(
                svc, requestedAppName, environmentId, password, resource, apiClient, ct),

            DokployNativeServiceType.Mongo => await CreateMongoAsync(
                svc, requestedAppName, environmentId, password, resource, apiClient, ct),

            DokployNativeServiceType.MySql => await CreateMySqlAsync(
                svc, requestedAppName, environmentId, password, resource, apiClient, ct),

            DokployNativeServiceType.Postgres => await CreatePostgresAsync(
                svc, requestedAppName, environmentId, password, resource, apiClient, ct),

            _ => throw new InvalidOperationException($"Unknown native service type: {svc.NativeServiceType}"),
        };

        logger.LogInformation(
            "Created {Type} '{Service}': id={Id}, appName={AppName}",
            svc.NativeServiceType, svc.Name, nativeServiceId, assignedAppName);

        stateStore.SetNativeServiceId(svc.Name, nativeServiceId);
        stateStore.SetAppName(svc.Name, assignedAppName);
        return assignedAppName;
    }

    private async Task<(string id, string appName)> CreateRedisAsync(
        DokployServiceDescriptor svc, string requestedAppName, string environmentId,
        string password, DokployResource resource, DokployApiClient apiClient, CancellationToken ct)
    {
        var created = await apiClient.CreateRedisAsync(new CreateRedisRequest
        {
            Name = svc.Name,
            AppName = requestedAppName,
            EnvironmentId = environmentId,
            DatabasePassword = password,
            DockerImage = svc.Image,
            ServerId = resource.ServerId,
        }, ct);
        var id = created.RedisId ?? throw new InvalidOperationException(
            $"redis.create returned no redisId for '{svc.Name}'");
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task<(string id, string appName)> CreateMariaDbAsync(
        DokployServiceDescriptor svc, string requestedAppName, string environmentId,
        string password, DokployResource resource, DokployApiClient apiClient, CancellationToken ct)
    {
        var created = await apiClient.CreateMariaDbAsync(new CreateMariaDbRequest
        {
            Name = svc.Name,
            AppName = requestedAppName,
            EnvironmentId = environmentId,
            DatabasePassword = password,
            DockerImage = svc.Image,
            ServerId = resource.ServerId,
        }, ct);
        var id = created.MariaDbId ?? throw new InvalidOperationException(
            $"mariadb.create returned no mariadbId for '{svc.Name}'");
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task<(string id, string appName)> CreateMongoAsync(
        DokployServiceDescriptor svc, string requestedAppName, string environmentId,
        string password, DokployResource resource, DokployApiClient apiClient, CancellationToken ct)
    {
        var created = await apiClient.CreateMongoAsync(new CreateMongoRequest
        {
            Name = svc.Name,
            AppName = requestedAppName,
            EnvironmentId = environmentId,
            DatabasePassword = password,
            DockerImage = svc.Image,
            ServerId = resource.ServerId,
        }, ct);
        var id = created.MongoId ?? throw new InvalidOperationException(
            $"mongo.create returned no mongoId for '{svc.Name}'");
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task<(string id, string appName)> CreateMySqlAsync(
        DokployServiceDescriptor svc, string requestedAppName, string environmentId,
        string password, DokployResource resource, DokployApiClient apiClient, CancellationToken ct)
    {
        var created = await apiClient.CreateMySqlAsync(new CreateMySqlRequest
        {
            Name = svc.Name,
            AppName = requestedAppName,
            EnvironmentId = environmentId,
            DatabasePassword = password,
            DockerImage = svc.Image,
            ServerId = resource.ServerId,
        }, ct);
        var id = created.MySqlId ?? throw new InvalidOperationException(
            $"mysql.create returned no mysqlId for '{svc.Name}'");
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task<(string id, string appName)> CreatePostgresAsync(
        DokployServiceDescriptor svc, string requestedAppName, string environmentId,
        string password, DokployResource resource, DokployApiClient apiClient, CancellationToken ct)
    {
        var created = await apiClient.CreatePostgresAsync(new CreatePostgresRequest
        {
            Name = svc.Name,
            AppName = requestedAppName,
            EnvironmentId = environmentId,
            DatabasePassword = password,
            DockerImage = svc.Image,
            ServerId = resource.ServerId,
        }, ct);
        var id = created.PostgresId ?? throw new InvalidOperationException(
            $"postgres.create returned no postgresId for '{svc.Name}'");
        return (id, created.AppName ?? requestedAppName);
    }

    private static async Task DeployNativeServiceAsync(
        DokployServiceDescriptor svc,
        string nativeId,
        DokployApiClient apiClient,
        CancellationToken ct)
    {
        switch (svc.NativeServiceType)
        {
            case DokployNativeServiceType.Redis:
                await apiClient.DeployRedisAsync(new DeployRedisRequest { RedisId = nativeId }, ct);
                break;
            case DokployNativeServiceType.MariaDb:
                await apiClient.DeployMariaDbAsync(new DeployMariaDbRequest { MariaDbId = nativeId }, ct);
                break;
            case DokployNativeServiceType.Mongo:
                await apiClient.DeployMongoAsync(new DeployMongoRequest { MongoId = nativeId }, ct);
                break;
            case DokployNativeServiceType.MySql:
                await apiClient.DeployMySqlAsync(new DeployMySqlRequest { MySqlId = nativeId }, ct);
                break;
            case DokployNativeServiceType.Postgres:
                await apiClient.DeployPostgresAsync(new DeployPostgresRequest { PostgresId = nativeId }, ct);
                break;
        }
    }

    /// <summary>
    /// Extracts a suitable database password from the service's env vars.
    /// Falls back to null (caller generates a random one).
    /// </summary>
    private static string? ExtractDbPassword(DokployServiceDescriptor svc)
    {
        if (svc.EnvString is null) return null;
        // Check common password env var names across all DB types
        return ExtractEnvValue(svc.EnvString, "REDIS_PASSWORD")
            ?? ExtractEnvValue(svc.EnvString, "MYSQL_ROOT_PASSWORD")
            ?? ExtractEnvValue(svc.EnvString, "MYSQL_PASSWORD")
            ?? ExtractEnvValue(svc.EnvString, "MARIADB_ROOT_PASSWORD")
            ?? ExtractEnvValue(svc.EnvString, "MARIADB_PASSWORD")
            ?? ExtractEnvValue(svc.EnvString, "MONGO_INITDB_ROOT_PASSWORD")
            ?? ExtractEnvValue(svc.EnvString, "POSTGRES_PASSWORD");
    }

    // ── Pass 2: Configure + Deploy ────────────────────────────────────────────

    private async Task ConfigureAndDeployApplicationAsync(
        DokployServiceDescriptor svc,
        string applicationId,
        string? envString,
        DokployResource resource,
        DokployApiClient apiClient,
        IReadOnlyDictionary<string, DokployDomainAnnotation> domainAnnotations,
        CancellationToken ct
    )
    {
        var registry = svc.Registry ?? resource.Registry;

        // Determine the image reference Dokploy will pull.
        // Aspire's build pipeline already pushed the image before BeforeStartEvent fires, so
        // .env.Production may already contain a registry-qualified name (e.g. "jjchiw/apiservice:latest").
        // If it's still a bare local name (e.g. "apiservice:latest"), qualify it using ImagePrefix.
        var imageToUse = svc.Image;
        var isLocalImage = !svc.Image.Contains('/') ||
            svc.Image[..svc.Image.IndexOf('/')] is var host && !host.Contains('.') && !host.Contains(':');

        if (isLocalImage && registry?.ImagePrefix is { Length: > 0 } prefix)
        {
            var colon = svc.Image.LastIndexOf(':');
            var name = colon > 0 ? svc.Image[..colon] : svc.Image;
            var tag  = colon > 0 ? svc.Image[(colon + 1)..] : "latest";
            imageToUse = $"{prefix.TrimEnd('/')}/{name}:{tag}";
            logger.LogInformation(
                "Qualified image '{Local}' → '{Qualified}' using registry prefix",
                svc.Image, imageToUse);
        }
        else if (isLocalImage && registry is null)
        {
            logger.LogWarning(
                "Service '{Service}' uses a local image '{Image}'. "
                    + "Add 'builder.AddContainerRegistry(...)' (Aspire push) and "
                    + "set DokploySettings.Registry.ImagePrefix (Dokploy pull).",
                svc.Name, svc.Image);
        }

        // Save Docker image + pull credentials
        await apiClient.SaveDockerProviderAsync(
            new SaveDockerProviderRequest
            {
                ApplicationId = applicationId,
                DockerImage = imageToUse,
                Username = registry?.Username,
                Password = registry?.Password,
                RegistryUrl = registry?.RegistryUrl,
            },
            ct
        );

        // Save environment variables (with service names already substituted)
        if (!string.IsNullOrWhiteSpace(envString))
        {
            await apiClient.SaveEnvironmentAsync(
                new SaveEnvironmentRequest { ApplicationId = applicationId, Env = envString },
                ct
            );
        }

        // Register domain for public-facing services
        if (svc.HasExternalEndpoint && svc.Domain is not null)
        {
            var domainAnnotation = domainAnnotations.GetValueOrDefault(svc.Name);
            await apiClient.CreateDomainAsync(
                new CreateDomainRequest
                {
                    ApplicationId = applicationId,
                    Host = svc.Domain,
                    Https = domainAnnotation?.Https ?? true,
                    CertificateType = domainAnnotation?.CertificateType ?? "letsencrypt",
                    Port = domainAnnotation?.Port,
                },
                ct
            );
            logger.LogInformation(
                "Registered domain {Domain} for '{Service}'",
                svc.Domain,
                svc.Name
            );
        }

        logger.LogInformation("Deploying application '{Service}' ({Id})", svc.Name, applicationId);
        await apiClient.DeployApplicationAsync(
            new DeployApplicationRequest { ApplicationId = applicationId },
            ct
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DokployApiClient BuildApiClient(DokployResource resource)
    {
        using var scope = serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(resource.DokployUrl);
        http.DefaultRequestHeaders.Add("x-api-key", resource.ApiToken);

        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        return new DokployApiClient(http, loggerFactory.CreateLogger<DokployApiClient>());
    }

    private string FindComposePath(DokployResource resource)
    {
        var candidates = new[]
        {
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "aspire-output",
                resource.ComposeEnvironment.Name,
                "docker-compose.yaml"
            ),
            Path.Combine(Directory.GetCurrentDirectory(), "aspire-output", "docker-compose.yaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "aspire-output", "docker-compose.yml"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            $"Could not find docker-compose.yaml. Checked:\n  {string.Join("\n  ", candidates)}"
        );
    }

    private static Dictionary<string, string> LoadEnvVars(string outputDir)
    {
        // Prefer .env.Production (Aspire's environment-specific file with actual values)
        // Fall back to .env (which may have empty values in Aspire's output)
        var candidates = new[]
        {
            Path.Combine(outputDir, ".env.Production"),
            Path.Combine(outputDir, ".env.production"),
            Path.Combine(outputDir, ".env"),
        };

        foreach (var path in candidates)
        {
            var vars = DokployComposeParser.ParseEnvFile(path);
            if (vars.Count > 0)
                return vars;
        }

        return [];
    }

    private static Dictionary<string, DokployDomainAnnotation> CollectDomainAnnotations(
        DokployResource resource
    )
    {
        var result = new Dictionary<string, DokployDomainAnnotation>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var annotation in resource.Annotations.OfType<DokployServiceDomainAnnotation>())
            result[annotation.ServiceName] = annotation.Domain;
        return result;
    }

    private static bool IsAspireInternalService(DokployServiceDescriptor svc)
    {
        if (svc.Image?.Contains("aspire-dashboard", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (svc.Name.EndsWith("-dashboard", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string? ExtractEnvValue(string? envString, string key)
    {
        if (envString is null)
            return null;

        foreach (var line in envString.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf('=');
            if (idx > 0 && line[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(idx + 1)..].Trim();
        }

        return null;
    }

    private void Validate(DokployResource resource)
    {
        if (string.IsNullOrWhiteSpace(resource.DokployUrl))
            throw new InvalidOperationException(
                $"DokployUrl is not set on resource '{resource.Name}'"
            );
        if (string.IsNullOrWhiteSpace(resource.ApiToken))
            throw new InvalidOperationException(
                $"ApiToken is not set on resource '{resource.Name}'"
            );
        if (string.IsNullOrWhiteSpace(resource.ProjectName))
            throw new InvalidOperationException(
                $"ProjectName is not set on resource '{resource.Name}'"
            );
    }
}

/// <summary>
/// Annotation attached to a DokployResource to register a domain for a specific service.
/// </summary>
public class DokployServiceDomainAnnotation : IResourceAnnotation
{
    public required string ServiceName { get; init; }
    public required DokployDomainAnnotation Domain { get; init; }
}
