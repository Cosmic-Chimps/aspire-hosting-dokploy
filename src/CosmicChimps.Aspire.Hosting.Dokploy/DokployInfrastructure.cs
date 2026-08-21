using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// Encapsulates the per-service Dokploy deployment logic.
/// Instantiated and invoked from the pipeline step registered by
/// <see cref="DokployResourceExtensions.PublishToDokploy"/> — the step runs after the
/// "publish" and "build" pipeline stages so that the generated docker-compose.yaml and
/// pushed container images are both available before any API calls are made.
/// </summary>
internal sealed class DokployInfrastructure(
    ILogger<DokployInfrastructure> logger,
    IServiceProvider serviceProvider
)
{
    internal async Task DeployAsync(
        DokployResource resource,
        IReportingStep reportingStep,
        CancellationToken ct
    )
    {
        Validate(resource);

        logger.LogInformation(
            "Starting per-service Dokploy deployment for '{Name}' → {Url}",
            resource.Name,
            resource.DokployUrl
        );
        logger.LogDebug(
            "Settings — project:{Project} environment:{Env} prefix:{Prefix} server:{Server}",
            resource.ProjectName,
            resource.EnvironmentName,
            resource.AppNamePrefix.Length > 0 ? resource.AppNamePrefix : "(none)",
            resource.ServerId ?? "(default)"
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
        if (domainAnnotations.Count > 0)
            logger.LogDebug(
                "Domain annotations: {Domains}",
                string.Join(", ", domainAnnotations.Select(kv => $"{kv.Key}→{kv.Value.Host}"))
            );
        else
            logger.LogDebug("No domain annotations configured");

        // ── 3b. Collect health check annotations (from WithDokployHealthCheck() calls) ─
        var healthCheckAnnotations = CollectHealthCheckAnnotations(resource);
        if (healthCheckAnnotations.Count > 0)
            logger.LogDebug(
                "Health check annotations: {Services}",
                string.Join(", ", healthCheckAnnotations.Keys)
            );

        // ── 3c. Collect no-substitution annotations (from WithDokployNoSubstitution() calls) ─
        var noSubstitutionAnnotations = CollectNoSubstitutionAnnotations(resource);
        if (noSubstitutionAnnotations.Count > 0)
            logger.LogDebug(
                "No-substitution env keys: {Keys}",
                string.Join(
                    ", ",
                    noSubstitutionAnnotations.SelectMany(kv =>
                        kv.Value.Select(k => $"{kv.Key}.{k}")
                    )
                )
            );

        // ── 3d. Collect stop grace period annotations (from WithDokployStopGracePeriod() calls) ─
        var stopGracePeriodAnnotations = CollectStopGracePeriodAnnotations(resource);
        if (stopGracePeriodAnnotations.Count > 0)
            logger.LogDebug(
                "Stop grace period annotations: {Services}",
                string.Join(", ", stopGracePeriodAnnotations.Select(kv => $"{kv.Key}={kv.Value / 1_000_000_000}s"))
            );

        // ── 3e. Collect update order annotations (from WithDokployUpdateOrder() calls) ─
        var updateOrderAnnotations = CollectUpdateOrderAnnotations(resource);
        if (updateOrderAnnotations.Count > 0)
            logger.LogDebug(
                "Update order annotations: {Services}",
                string.Join(", ", updateOrderAnnotations.Select(kv => $"{kv.Key}={kv.Value}"))
            );

        // ── 4. Parse compose into per-service descriptors ─────────────────────
        var services = DokployComposeParser.Parse(composeYaml, envVars, domainAnnotations);
        logger.LogDebug("Parsed {Count} service(s) from compose YAML", services.Count);

        // ── 4b. Compensate for entrypoint overrides Dokploy cannot express ───
        CompensateForEntrypointOverrides(resource, services);

        // ── 5. Filter internal Aspire services (dashboard, etc.) ─────────────
        var servicesToDeploy = services.Where(s => !IsAspireInternalService(s)).ToList();

        var skipped = services.Except(servicesToDeploy).Select(s => s.Name).ToList();
        if (skipped.Count > 0)
            logger.LogInformation(
                "Skipping internal Aspire service(s): {Names}",
                string.Join(", ", skipped)
            );

        // ── 5b. Filter explicitly excluded services (WithDokployExclude) ─────
        var excludedNames = resource.Annotations
            .OfType<DokployServiceExcludeAnnotation>()
            .Select(a => a.ServiceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Excluded services are NOT deployed, but we still need their Dokploy appNames
        // so that env vars in deployed services (e.g. ConnectionStrings__bella-db=Host=<appName>)
        // are substituted correctly. Without this, excluded services' compose names remain
        // unresolved and the deployed services cannot connect to them.
        var excludedServices = excludedNames.Count > 0
            ? servicesToDeploy.Where(s => excludedNames.Contains(s.Name)).ToList()
            : [];

        if (excludedNames.Count > 0)
        {
            servicesToDeploy = servicesToDeploy
                .Where(s => !excludedNames.Contains(s.Name))
                .ToList();
            logger.LogInformation(
                "Excluding service(s) from deploy (WithDokployExclude): {Names}",
                string.Join(", ", excludedNames)
            );
        }

        logger.LogInformation(
            "Deploying {Count} service(s): {Names}",
            servicesToDeploy.Count,
            string.Join(", ", servicesToDeploy.Select(s => s.Name))
        );

        // ── 6. Build in-memory state store (always populated from live Dokploy below) ─
        var stateStore = new DokployStateStore(logger);

        // ── 7. Build API client ───────────────────────────────────────────────
        var apiClient = BuildApiClient(resource);

        // ── 8. Find or create Dokploy project + named environment ────────────
#pragma warning disable ASPIREPIPELINES001
        await using var setupTask = await reportingStep.CreateTaskAsync(
            $"Setting up Dokploy project '{resource.ProjectName}' ({resource.EnvironmentName})...",
            ct
        );
#pragma warning restore ASPIREPIPELINES001

        // Always look up project and environment from Dokploy (no local state file).
        var (projectId, _) = await apiClient.FindOrCreateProjectAsync(resource.ProjectName, ct);
        var environmentId = await apiClient.FindOrCreateEnvironmentAsync(
            projectId,
            resource.EnvironmentName,
            ct
        );

#pragma warning disable ASPIREPIPELINES001
        await setupTask.CompleteAsync(
            $"Project '{resource.ProjectName}' ready (env: {resource.EnvironmentName})"
        );
#pragma warning restore ASPIREPIPELINES001

        // ── 8b. Always load live service IDs from Dokploy ────────────────────
        //       No local state file — idempotency is driven by querying the live API.
        //       Also reconcile excluded services so their appNames are in the state store
        //       for env var substitution in deployed services.
        var allServicesToReconcile = excludedServices.Count > 0
            ? servicesToDeploy.Concat(excludedServices).ToList()
            : servicesToDeploy;
        await ReconcileStateAsync(environmentId, allServicesToReconcile, apiClient, stateStore, ct);

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

        // ── 9b. Add excluded services' appNames to service name map ──────────
        //    Excluded services are not deployed, but their compose names appear in
        //    env vars of deployed services (e.g. ConnectionStrings__bella-db=Host=postgres).
        //    Without substitution, those names stay as compose names and fail to resolve
        //    in Docker Swarm (where only Dokploy appNames are valid DNS entries).
        if (excludedServices.Count > 0)
        {
            foreach (var excludedSvc in excludedServices)
            {
                var appName = stateStore.GetAppName(excludedSvc.Name);
                if (appName is not null)
                {
                    serviceNameMap[excludedSvc.Name] = appName;
                    logger.LogInformation(
                        "Excluded service '{Name}' → appName '{AppName}' added to service name map",
                        excludedSvc.Name,
                        appName
                    );
                }
                else
                {
                    logger.LogWarning(
                        "Excluded service '{Name}' not found in Dokploy — env vars referencing it may be incorrect",
                        excludedSvc.Name
                    );
                }
            }
        }

        // ── 10. PASS 2: Configure each service (env vars + image) then deploy ─
        foreach (var svc in servicesToDeploy)
        {
            // Replace compose service names with Dokploy appNames in env values,
            // also strip lines referencing skipped services (e.g. OTEL dashboard).
            var noSubstKeys = noSubstitutionAnnotations.TryGetValue(svc.Name, out var keys)
                ? keys
                : null;
            var envString = svc.EnvString is not null
                ? DokployComposeParser.ApplyServiceNameSubstitution(
                    svc.EnvString,
                    serviceNameMap,
                    skipped,
                    noSubstKeys
                )
                : null;

            // YARP cluster destinations must be concrete origins here — Aspire's service-discovery
            // scheme cannot resolve once hostnames have been rewritten to Dokploy app names.
            if (envString is not null)
            {
                envString = DokployComposeParser.NormalizeYarpClusterAddresses(
                    envString,
                    out var unresolvedClusterAddresses
                );

                foreach (var unresolved in unresolvedClusterAddresses)
                {
                    // Left as-is deliberately: inventing a port would turn a clear "no destination"
                    // into a confusing 502 against the wrong one.
                    logger.LogWarning(
                        "Service '{Service}': could not determine a port for YARP cluster destination "
                            + "{Address}. It is left using Aspire's service-discovery scheme, which does "
                            + "not resolve here, so requests through that cluster will fail. Give the "
                            + "target service an http endpoint, or set the address explicitly.",
                        svc.Name,
                        unresolved
                    );
                }
            }

            var svcLabel = svc.IsNativeService
                ? $"{svc.Name} ({svc.NativeServiceType})"
                : $"{svc.Name}";

#pragma warning disable ASPIREPIPELINES001
            await using var svcTask = await reportingStep.CreateTaskAsync(
                $"Deploying {svcLabel}...",
                ct
            );
#pragma warning restore ASPIREPIPELINES001

            try
            {
                if (svc.IsNativeService)
                {
                    var nativeId = stateStore.GetNativeServiceId(svc.Name)!;
                    logger.LogInformation(
                        "Deploying {Type} '{Service}' ({Id})",
                        svc.NativeServiceType,
                        svc.Name,
                        nativeId
                    );
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
                        healthCheckAnnotations,
                        stopGracePeriodAnnotations,
                        updateOrderAnnotations,
                        ct
                    );
                }

                var appName = serviceNameMap[svc.Name];
#pragma warning disable ASPIREPIPELINES001
                await svcTask.CompleteAsync($"Deployed {svc.Name} → {appName}");
#pragma warning restore ASPIREPIPELINES001
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure/deploy service '{Service}'", svc.Name);
#pragma warning disable ASPIREPIPELINES001
                await svcTask.CompleteAsync(
                    $"Failed to deploy {svc.Name}: {ex.Message}",
                    CompletionState.CompletedWithError
                );
#pragma warning restore ASPIREPIPELINES001
                throw;
            }
        }

        logger.LogInformation("Dokploy deployment complete for '{Name}'", resource.Name);
    }

    // ── Pass 1: Create services ───────────────────────────────────────────────

    /// <summary>
    /// Queries Dokploy for all existing services via <c>environment.one</c> and populates
    /// the in-memory state store with their IDs. Called on every run — there is no local
    /// state file, so Dokploy is always the source of truth.
    /// </summary>
    private async Task ReconcileStateAsync(
        string environmentId,
        IReadOnlyList<DokployServiceDescriptor> servicesToDeploy,
        DokployApiClient apiClient,
        DokployStateStore stateStore,
        CancellationToken ct
    )
    {
        logger.LogInformation(
            "Loading live service state from Dokploy (environmentId={EnvId})...",
            environmentId
        );

        // Single call: environment.one returns ALL service lists embedded.
        EnvironmentOneResponse env;
        try
        {
            env = await apiClient.GetEnvironmentAsync(environmentId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to query Dokploy environment state — services will be created fresh (duplicates possible)"
            );
            return;
        }

        logger.LogInformation(
            "Dokploy live counts — apps:{Apps} redis:{Redis} mariadb:{MariaDb} mongo:{Mongo} mysql:{MySql} postgres:{Postgres}",
            env.Applications.Count,
            env.Redis.Count,
            env.MariaDb.Count,
            env.Mongo.Count,
            env.MySql.Count,
            env.Postgres.Count
        );

        if (env.Applications.Count > 0)
            logger.LogDebug(
                "Live apps: {Names}",
                string.Join(", ", env.Applications.Select(a => $"{a.Name}|{a.AppName}"))
            );

        foreach (var svc in servicesToDeploy)
        {
            bool MatchName(string? name) =>
                string.Equals(name, svc.Name, StringComparison.OrdinalIgnoreCase);

            if (!svc.IsNativeService)
            {
                // environment.one does NOT include appName — match by name only,
                // then call application.one to retrieve appName.
                var match = env.Applications.FirstOrDefault(a => MatchName(a.Name));
                if (match?.ApplicationId is not null)
                {
                    stateStore.SetApplicationId(svc.Name, match.ApplicationId);
                    try
                    {
                        var details = await apiClient.GetApplicationAsync(match.ApplicationId, ct);
                        var appName = details.AppName ?? match.ApplicationId;
                        stateStore.SetAppName(svc.Name, appName);
                        logger.LogInformation(
                            "Found existing application '{Name}' → id={Id} appName={AppName}",
                            svc.Name,
                            match.ApplicationId,
                            appName
                        );
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Could not fetch appName for application '{Name}' ({Id})",
                            svc.Name,
                            match.ApplicationId
                        );
                    }
                }
                else
                    logger.LogInformation(
                        "No existing application found for '{Name}' — will create",
                        svc.Name
                    );
                continue;
            }

            switch (svc.NativeServiceType)
            {
                case DokployNativeServiceType.Redis:
                {
                    var match = env.Redis.FirstOrDefault(r => MatchName(r.Name));
                    if (match?.RedisId is not null)
                    {
                        stateStore.SetNativeServiceId(svc.Name, match.RedisId);
                        try
                        {
                            var details = await apiClient.GetRedisAsync(match.RedisId, ct);
                            var appName = details.AppName ?? match.RedisId;
                            stateStore.SetAppName(svc.Name, appName);
                            logger.LogInformation(
                                "Found existing Redis '{Name}' → id={Id} appName={AppName}",
                                svc.Name,
                                match.RedisId,
                                appName
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Could not fetch appName for Redis '{Name}' ({Id})",
                                svc.Name,
                                match.RedisId
                            );
                        }
                    }
                    else
                        logger.LogInformation(
                            "No existing Redis found for '{Name}' — will create",
                            svc.Name
                        );
                    break;
                }
                case DokployNativeServiceType.MariaDb:
                {
                    var match = env.MariaDb.FirstOrDefault(r => MatchName(r.Name));
                    if (match?.MariaDbId is not null)
                    {
                        stateStore.SetNativeServiceId(svc.Name, match.MariaDbId);
                        try
                        {
                            var details = await apiClient.GetMariaDbAsync(match.MariaDbId, ct);
                            var appName = details.AppName ?? match.MariaDbId;
                            stateStore.SetAppName(svc.Name, appName);
                            logger.LogInformation(
                                "Found existing MariaDB '{Name}' → id={Id} appName={AppName}",
                                svc.Name,
                                match.MariaDbId,
                                appName
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Could not fetch appName for MariaDB '{Name}' ({Id})",
                                svc.Name,
                                match.MariaDbId
                            );
                        }
                    }
                    else
                        logger.LogInformation(
                            "No existing MariaDB found for '{Name}' — will create",
                            svc.Name
                        );
                    break;
                }
                case DokployNativeServiceType.Mongo:
                {
                    var match = env.Mongo.FirstOrDefault(r => MatchName(r.Name));
                    if (match?.MongoId is not null)
                    {
                        stateStore.SetNativeServiceId(svc.Name, match.MongoId);
                        try
                        {
                            var details = await apiClient.GetMongoAsync(match.MongoId, ct);
                            var appName = details.AppName ?? match.MongoId;
                            stateStore.SetAppName(svc.Name, appName);
                            logger.LogInformation(
                                "Found existing MongoDB '{Name}' → id={Id} appName={AppName}",
                                svc.Name,
                                match.MongoId,
                                appName
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Could not fetch appName for MongoDB '{Name}' ({Id})",
                                svc.Name,
                                match.MongoId
                            );
                        }
                    }
                    else
                        logger.LogInformation(
                            "No existing MongoDB found for '{Name}' — will create",
                            svc.Name
                        );
                    break;
                }
                case DokployNativeServiceType.MySql:
                {
                    var match = env.MySql.FirstOrDefault(r => MatchName(r.Name));
                    if (match?.MySqlId is not null)
                    {
                        stateStore.SetNativeServiceId(svc.Name, match.MySqlId);
                        try
                        {
                            var details = await apiClient.GetMySqlAsync(match.MySqlId, ct);
                            var appName = details.AppName ?? match.MySqlId;
                            stateStore.SetAppName(svc.Name, appName);
                            logger.LogInformation(
                                "Found existing MySQL '{Name}' → id={Id} appName={AppName}",
                                svc.Name,
                                match.MySqlId,
                                appName
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Could not fetch appName for MySQL '{Name}' ({Id})",
                                svc.Name,
                                match.MySqlId
                            );
                        }
                    }
                    else
                        logger.LogInformation(
                            "No existing MySQL found for '{Name}' — will create",
                            svc.Name
                        );
                    break;
                }
                case DokployNativeServiceType.Postgres:
                {
                    var match = env.Postgres.FirstOrDefault(r => MatchName(r.Name));
                    if (match?.PostgresId is not null)
                    {
                        stateStore.SetNativeServiceId(svc.Name, match.PostgresId);
                        try
                        {
                            var details = await apiClient.GetPostgresAsync(match.PostgresId, ct);
                            var appName = details.AppName ?? match.PostgresId;
                            stateStore.SetAppName(svc.Name, appName);
                            logger.LogInformation(
                                "Found existing Postgres '{Name}' → id={Id} appName={AppName}",
                                svc.Name,
                                match.PostgresId,
                                appName
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Could not fetch appName for Postgres '{Name}' ({Id})",
                                svc.Name,
                                match.PostgresId
                            );
                        }
                    }
                    else
                        logger.LogInformation(
                            "No existing Postgres found for '{Name}' — will create",
                            svc.Name
                        );
                    break;
                }
            }
        }
    }

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
                svc.NativeServiceType,
                svc.Name,
                existingId
            );
            return existingAppName;
        }

        logger.LogInformation(
            "Creating {Type} service '{Service}'",
            svc.NativeServiceType,
            svc.Name
        );

        var password = ExtractDbPassword(svc) ?? Guid.NewGuid().ToString("N")[..16];
        var requestedAppName = string.IsNullOrEmpty(resource.AppNamePrefix)
            ? svc.Name
            : $"{resource.AppNamePrefix}{svc.Name}";

        var (nativeServiceId, assignedAppName) = svc.NativeServiceType switch
        {
            DokployNativeServiceType.Redis => await CreateRedisAsync(
                svc,
                requestedAppName,
                environmentId,
                password,
                resource,
                apiClient,
                ct
            ),

            DokployNativeServiceType.MariaDb => await CreateMariaDbAsync(
                svc,
                requestedAppName,
                environmentId,
                password,
                resource,
                apiClient,
                ct
            ),

            DokployNativeServiceType.Mongo => await CreateMongoAsync(
                svc,
                requestedAppName,
                environmentId,
                password,
                resource,
                apiClient,
                ct
            ),

            DokployNativeServiceType.MySql => await CreateMySqlAsync(
                svc,
                requestedAppName,
                environmentId,
                password,
                resource,
                apiClient,
                ct
            ),

            DokployNativeServiceType.Postgres => await CreatePostgresAsync(
                svc,
                requestedAppName,
                environmentId,
                password,
                resource,
                apiClient,
                ct
            ),

            _ => throw new InvalidOperationException(
                $"Unknown native service type: {svc.NativeServiceType}"
            ),
        };

        logger.LogInformation(
            "Created {Type} '{Service}': id={Id}, appName={AppName}",
            svc.NativeServiceType,
            svc.Name,
            nativeServiceId,
            assignedAppName
        );

        stateStore.SetNativeServiceId(svc.Name, nativeServiceId);
        stateStore.SetAppName(svc.Name, assignedAppName);
        return assignedAppName;
    }

    private async Task<(string id, string appName)> CreateRedisAsync(
        DokployServiceDescriptor svc,
        string requestedAppName,
        string environmentId,
        string password,
        DokployResource resource,
        DokployApiClient apiClient,
        CancellationToken ct
    )
    {
        var created = await apiClient.CreateRedisAsync(
            new CreateRedisRequest
            {
                Name = svc.Name,
                AppName = requestedAppName,
                EnvironmentId = environmentId,
                DatabasePassword = password,
                DockerImage = svc.Image,
                ServerId = resource.ServerId,
            },
            ct
        );
        var id =
            created.RedisId
            ?? throw new InvalidOperationException(
                $"redis.create returned no redisId for '{svc.Name}'"
            );
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task<(string id, string appName)> CreateMariaDbAsync(
        DokployServiceDescriptor svc,
        string requestedAppName,
        string environmentId,
        string password,
        DokployResource resource,
        DokployApiClient apiClient,
        CancellationToken ct
    )
    {
        var created = await apiClient.CreateMariaDbAsync(
            new CreateMariaDbRequest
            {
                Name = svc.Name,
                AppName = requestedAppName,
                EnvironmentId = environmentId,
                DatabasePassword = password,
                DatabaseName = ExtractEnvValue(svc.EnvString, "MARIADB_DATABASE") ?? svc.Name,
                DatabaseUser = ExtractEnvValue(svc.EnvString, "MARIADB_USER") ?? "mariadb",
                DockerImage = svc.Image,
                ServerId = resource.ServerId,
            },
            ct
        );
        var id =
            created.MariaDbId
            ?? throw new InvalidOperationException(
                $"mariadb.create returned no mariadbId for '{svc.Name}'"
            );
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task<(string id, string appName)> CreateMongoAsync(
        DokployServiceDescriptor svc,
        string requestedAppName,
        string environmentId,
        string password,
        DokployResource resource,
        DokployApiClient apiClient,
        CancellationToken ct
    )
    {
        var created = await apiClient.CreateMongoAsync(
            new CreateMongoRequest
            {
                Name = svc.Name,
                AppName = requestedAppName,
                EnvironmentId = environmentId,
                DatabasePassword = password,
                DockerImage = svc.Image,
                ServerId = resource.ServerId,
            },
            ct
        );
        var id =
            created.MongoId
            ?? throw new InvalidOperationException(
                $"mongo.create returned no mongoId for '{svc.Name}'"
            );
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task<(string id, string appName)> CreateMySqlAsync(
        DokployServiceDescriptor svc,
        string requestedAppName,
        string environmentId,
        string password,
        DokployResource resource,
        DokployApiClient apiClient,
        CancellationToken ct
    )
    {
        var created = await apiClient.CreateMySqlAsync(
            new CreateMySqlRequest
            {
                Name = svc.Name,
                AppName = requestedAppName,
                EnvironmentId = environmentId,
                DatabasePassword = password,
                DatabaseName = ExtractEnvValue(svc.EnvString, "MYSQL_DATABASE") ?? svc.Name,
                DatabaseUser = ExtractEnvValue(svc.EnvString, "MYSQL_USER") ?? "mysql",
                DockerImage = svc.Image,
                ServerId = resource.ServerId,
            },
            ct
        );
        var id =
            created.MySqlId
            ?? throw new InvalidOperationException(
                $"mysql.create returned no mysqlId for '{svc.Name}'"
            );
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task<(string id, string appName)> CreatePostgresAsync(
        DokployServiceDescriptor svc,
        string requestedAppName,
        string environmentId,
        string password,
        DokployResource resource,
        DokployApiClient apiClient,
        CancellationToken ct
    )
    {
        var created = await apiClient.CreatePostgresAsync(
            new CreatePostgresRequest
            {
                Name = svc.Name,
                AppName = requestedAppName,
                EnvironmentId = environmentId,
                DatabasePassword = password,
                DatabaseName = ExtractEnvValue(svc.EnvString, "POSTGRES_DB") ?? svc.Name,
                DatabaseUser = ExtractEnvValue(svc.EnvString, "POSTGRES_USER") ?? "postgres",
                DockerImage = svc.Image,
                ServerId = resource.ServerId,
            },
            ct
        );
        var id =
            created.PostgresId
            ?? throw new InvalidOperationException(
                $"postgres.create returned no postgresId for '{svc.Name}'"
            );
        return (id, created.AppName ?? requestedAppName);
    }

    private async Task DeployNativeServiceAsync(
        DokployServiceDescriptor svc,
        string nativeId,
        DokployApiClient apiClient,
        CancellationToken ct
    )
    {
        switch (svc.NativeServiceType)
        {
            case DokployNativeServiceType.Redis:
                logger.LogDebug("Deploying Redis '{Service}' ({Id})", svc.Name, nativeId);
                await apiClient.DeployRedisAsync(new DeployRedisRequest { RedisId = nativeId }, ct);
                break;
            case DokployNativeServiceType.MariaDb:
                logger.LogDebug("Deploying MariaDB '{Service}' ({Id})", svc.Name, nativeId);
                await apiClient.DeployMariaDbAsync(
                    new DeployMariaDbRequest { MariaDbId = nativeId },
                    ct
                );
                break;
            case DokployNativeServiceType.Mongo:
                logger.LogDebug("Deploying MongoDB '{Service}' ({Id})", svc.Name, nativeId);
                await apiClient.DeployMongoAsync(new DeployMongoRequest { MongoId = nativeId }, ct);
                break;
            case DokployNativeServiceType.MySql:
                logger.LogDebug("Deploying MySQL '{Service}' ({Id})", svc.Name, nativeId);
                await apiClient.DeployMySqlAsync(new DeployMySqlRequest { MySqlId = nativeId }, ct);
                break;
            case DokployNativeServiceType.Postgres:
                logger.LogDebug("Deploying Postgres '{Service}' ({Id})", svc.Name, nativeId);
                await apiClient.DeployPostgresAsync(
                    new DeployPostgresRequest { PostgresId = nativeId },
                    ct
                );
                break;
        }
    }

    /// <summary>
    /// Extracts a suitable database password from the service's env vars.
    /// Falls back to null (caller generates a random one).
    /// </summary>
    private static string? ExtractDbPassword(DokployServiceDescriptor svc)
    {
        if (svc.EnvString is null)
            return null;
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
        IReadOnlyDictionary<string, HealthCheckSwarm> healthCheckAnnotations,
        IReadOnlyDictionary<string, long> stopGracePeriodAnnotations,
        IReadOnlyDictionary<string, string> updateOrderAnnotations,
        CancellationToken ct
    )
    {
        var registry = svc.Registry ?? resource.Registry;

        // Determine the image reference Dokploy will pull.
        // Aspire's build pipeline already pushed the image before BeforeStartEvent fires, so
        // .env.Production may already contain a registry-qualified name (e.g. "jjchiw/apiservice:latest").
        // If it's still a bare local name (e.g. "apiservice:latest"), qualify it using ImagePrefix.
        var imageToUse = svc.Image;
        var isLocalImage =
            !svc.Image.Contains('/')
            || svc.Image[..svc.Image.IndexOf('/')] is var host
                && !host.Contains('.')
                && !host.Contains(':');

        if (isLocalImage && registry?.ImagePrefix is { Length: > 0 } prefix)
        {
            var colon = svc.Image.LastIndexOf(':');
            var name = colon > 0 ? svc.Image[..colon] : svc.Image;
            var tag = colon > 0 ? svc.Image[(colon + 1)..] : "latest";
            imageToUse = $"{prefix.TrimEnd('/')}/{name}:{tag}";
            logger.LogInformation(
                "Qualified image '{Local}' → '{Qualified}' using registry prefix",
                svc.Image,
                imageToUse
            );
        }
        else if (isLocalImage && registry is null)
        {
            logger.LogWarning(
                "Service '{Service}' uses a local image '{Image}'. "
                    + "Add 'builder.AddContainerRegistry(...)' (Aspire push) and "
                    + "set DokploySettings.Registry.ImagePrefix (Dokploy pull).",
                svc.Name,
                svc.Image
            );
        }

        // Check skip-redeploy flag early — we need to fetch current state to compare image tag.
        // If set, and if the service is running with the same image tag, we skip both
        // SaveDockerProviderAsync and DeployApplicationAsync (no-op deploy).
        var skipIfRunning = resource.Annotations
            .OfType<DokployServiceSkipRedeployAnnotation>()
            .Any(a => string.Equals(a.ServiceName, svc.Name, StringComparison.OrdinalIgnoreCase));

        if (skipIfRunning)
        {
            var current = await apiClient.GetApplicationAsync(applicationId, ct);
            var isRunning = string.Equals(current.ApplicationStatus, "running", StringComparison.OrdinalIgnoreCase);
            var sameTag = string.Equals(current.DockerImage, imageToUse, StringComparison.Ordinal);

            if (isRunning && sameTag)
            {
                logger.LogInformation(
                    "Skipping redeploy for '{Service}' — already running with image {Image} (WithDokploySkipRedeploy)",
                    svc.Name, imageToUse
                );
                goto afterDockerSave;
            }

            if (isRunning && !sameTag)
            {
                // Tags differ — compare content digests via the registry API.
                // If the digest is the same, the image content didn't change (only the tag did),
                // so we can still skip. This is the common case with timestamp-based CI tags.
                logger.LogInformation(
                    "Service '{Service}' is running but image tag changed ({Old} → {New}) — comparing digests",
                    svc.Name, current.DockerImage ?? "?", imageToUse
                );

                var currentDigest = current.DockerImage is null ? null
                    : await DockerRegistryDigestChecker.GetImageDigestAsync(
                        current.DockerImage, registry?.Username, registry?.Password, logger, ct);
                var newDigest = await DockerRegistryDigestChecker.GetImageDigestAsync(
                    imageToUse, registry?.Username, registry?.Password, logger, ct);

                if (currentDigest is not null && newDigest is not null && currentDigest == newDigest)
                {
                    logger.LogInformation(
                        "Skipping redeploy for '{Service}' — digest unchanged ({Digest}) despite new tag (WithDokploySkipRedeploy)",
                        svc.Name, currentDigest
                    );
                    goto afterDockerSave;
                }

                logger.LogInformation(
                    "Service '{Service}' image content changed (old={OldDigest}, new={NewDigest}) — deploying",
                    svc.Name,
                    currentDigest ?? "unknown",
                    newDigest ?? "unknown"
                );
            }
            else if (!isRunning)
            {
                logger.LogInformation(
                    "Service '{Service}' is not running (status={Status}) — deploying despite WithDokploySkipRedeploy",
                    svc.Name, current.ApplicationStatus ?? "unknown"
                );
            }
        }

        // Save Docker image + pull credentials
        logger.LogDebug(
            "Saving docker provider for '{Service}': image={Image} registry={Registry}",
            svc.Name,
            imageToUse,
            registry?.RegistryUrl ?? "(none)"
        );
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

        // Save environment variables (with service names already substituted).
        // Strategy: MERGE with existing Dokploy env vars so manually-set values
        // (e.g. Stripe keys, cloud function URLs set outside of Aspire) are preserved.
        // Aspire-provided keys always win; Dokploy-only keys are kept as-is.
        if (!string.IsNullOrWhiteSpace(envString))
        {
            var mergedEnvString = await MergeWithExistingEnvAsync(
                applicationId,
                envString,
                apiClient,
                svc.Name,
                ct
            );

            logger.LogDebug(
                "Saving {Lines} env var line(s) for '{Service}'",
                mergedEnvString.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                svc.Name
            );
            await apiClient.SaveEnvironmentAsync(
                new SaveEnvironmentRequest { ApplicationId = applicationId, Env = mergedEnvString },
                ct
            );
        }

        // Register domain for public-facing services — update if already exists, create if not.
        if (svc.HasExternalEndpoint && svc.Domain is not null)
        {
            var domainAnnotation = domainAnnotations.GetValueOrDefault(svc.Name);
            var existing = await apiClient.GetDomainsByApplicationIdAsync(applicationId, ct);
            var existingDomain = existing.FirstOrDefault(d =>
                string.Equals(d.Host, svc.Domain, StringComparison.OrdinalIgnoreCase)
            );

            if (existingDomain?.DomainId is not null)
            {
                await apiClient.UpdateDomainAsync(
                    new UpdateDomainRequest
                    {
                        DomainId = existingDomain.DomainId,
                        Host = svc.Domain,
                        Https = domainAnnotation?.Https ?? true,
                        CertificateType = domainAnnotation?.CertificateType ?? "letsencrypt",
                        Port = domainAnnotation?.Port,
                    },
                    ct
                );
                logger.LogInformation(
                    "Updated domain {Domain} for '{Service}'",
                    svc.Domain,
                    svc.Name
                );
            }
            else
            {
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
                    "Created domain {Domain} for '{Service}'",
                    svc.Domain,
                    svc.Name
                );
            }
        }

        // Apply Swarm health check if configured via WithDokployHealthCheck().
        var hasHealthCheck = healthCheckAnnotations.TryGetValue(svc.Name, out var healthCheck);
        var hasStopGrace = stopGracePeriodAnnotations.TryGetValue(svc.Name, out var stopGraceNs);
        var hasUpdateOrder = updateOrderAnnotations.TryGetValue(svc.Name, out var updateOrder);

        // HealthCheckSwarm: Dokploy supports this field in application.update — fail hard on error.
        if (hasHealthCheck)
        {
            await apiClient.UpdateApplicationAsync(
                new UpdateApplicationRequest
                {
                    ApplicationId = applicationId,
                    HealthCheckSwarm = healthCheck,
                },
                ct
            );
            logger.LogInformation(
                "Configured health check for '{Service}': {Test} (interval={Interval}s timeout={Timeout}s startPeriod={StartPeriod}s retries={Retries})",
                svc.Name,
                string.Join(" ", healthCheck!.Test),
                healthCheck.Interval / 1_000_000_000,
                healthCheck.Timeout / 1_000_000_000,
                healthCheck.StartPeriod / 1_000_000_000,
                healthCheck.Retries
            );
        }

        // StopGracePeriod / UpdateConfig: optional Swarm settings — some Dokploy versions don't
        // expose these fields in application.update and return "No values to set" (500).
        // Swallow that error so it never breaks a deployment; log a warning instead.
        if (hasStopGrace || hasUpdateOrder)
        {
            try
            {
                await apiClient.UpdateApplicationAsync(
                    new UpdateApplicationRequest
                    {
                        ApplicationId = applicationId,
                        StopGracePeriod = hasStopGrace ? stopGraceNs : null,
                        UpdateConfig = hasUpdateOrder ? new SwarmUpdateConfig { Order = updateOrder } : null,
                    },
                    ct
                );
                if (hasStopGrace)
                    logger.LogInformation(
                        "Configured stop grace period for '{Service}': {Seconds}s",
                        svc.Name,
                        stopGraceNs / 1_000_000_000
                    );
                if (hasUpdateOrder)
                    logger.LogInformation(
                        "Configured update order for '{Service}': {Order}",
                        svc.Name,
                        updateOrder
                    );
            }
            catch (Exception ex) when (ex.Message.Contains("No values to set") || ex.Message.Contains("500"))
            {
                logger.LogWarning(
                    "Could not apply Swarm settings (stopGracePeriod/updateConfig) for '{Service}' — "
                    + "Dokploy version may not support these fields via application.update. "
                    + "Set them manually in the Dokploy UI if needed. Error: {Error}",
                    svc.Name,
                    ex.Message
                );
            }
        }

        // Deploy the application (trigger Swarm rolling update).
        logger.LogInformation("Deploying application '{Service}' ({Id})", svc.Name, applicationId);
        await apiClient.DeployApplicationAsync(
            new DeployApplicationRequest { ApplicationId = applicationId },
            ct
        );

        afterDockerSave:

        // Configure persistent volume mounts (idempotent — skips if already exists).
        var mountAnnotations = resource
            .Annotations.OfType<DokployServiceMountAnnotation>()
            .Where(a => string.Equals(a.ServiceName, svc.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mountAnnotations.Count > 0)
        {
            var existingMounts = await apiClient.GetMountsByApplicationIdAsync(applicationId, ct);
            // Dedup by MountPath (container path) — each container path is unique per service.
            // The DB stores the actual hostPath Dokploy assigned, so MountPath-based dedup
            // reliably prevents creating the same bind mount twice on re-deploy.
            var existingByPath = existingMounts
                .Where(m => m.MountPath is not null)
                .GroupBy(m => m.MountPath!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var mountAnnotation in mountAnnotations)
            {
                var isFileMount = string.Equals(
                    mountAnnotation.Type, "file", StringComparison.OrdinalIgnoreCase);

                // A FILE mount carries content that can change between deploys, so "already exists"
                // is not the same as "already correct" — skipping it would leave the container running
                // stale config with nothing logged anywhere.
                if (isFileMount
                    && existingByPath.TryGetValue(mountAnnotation.ContainerPath, out var existingFile)
                    && !string.Equals(existingFile.Content, mountAnnotation.Content, StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(existingFile.MountId))
                    {
                        // Delete-then-recreate could leave the service with NO config if the second
                        // half fails, so refuse loudly instead of risking that.
                        throw new InvalidOperationException(
                            $"File mount '{mountAnnotation.ContainerPath}' on '{svc.Name}' changed but "
                                + "Dokploy returned no mountId, so it cannot be updated in place. Remove "
                                + "the mount in the Dokploy UI and re-deploy."
                        );
                    }

                    logger.LogInformation(
                        "Updating file mount '{Path}' on '{Service}' — content changed",
                        mountAnnotation.ContainerPath, svc.Name
                    );

                    await apiClient.UpdateMountAsync(
                        new UpdateMountRequest
                        {
                            MountId = existingFile.MountId!,
                            Type = mountAnnotation.Type,
                            MountPath = mountAnnotation.ContainerPath,
                            ServiceType = "application",
                            Content = mountAnnotation.Content,
                            FilePath = mountAnnotation.FilePath
                                ?? Path.GetFileName(mountAnnotation.ContainerPath),
                        },
                        ct
                    );
                    continue;
                }

                if (existingByPath.ContainsKey(mountAnnotation.ContainerPath))
                {
                    logger.LogInformation(
                        "Mount '{Path}' on '{Service}' already exists — skipping",
                        mountAnnotation.ContainerPath,
                        svc.Name
                    );
                    continue;
                }

                logger.LogInformation(
                    "Creating volume mount '{Volume}' → '{Path}' on '{Service}'",
                    mountAnnotation.VolumeName,
                    mountAnnotation.ContainerPath,
                    svc.Name
                );

                await apiClient.CreateMountAsync(
                    new CreateMountRequest
                    {
                        Type = mountAnnotation.Type,
                        MountPath = mountAnnotation.ContainerPath,
                        ServiceId = applicationId,
                        ServiceType = "application",
                        VolumeName = mountAnnotation.VolumeName,
                        HostPath = mountAnnotation.HostPath,
                        Content = mountAnnotation.Content,
                        FilePath = isFileMount
                            ? mountAnnotation.FilePath ?? Path.GetFileName(mountAnnotation.ContainerPath)
                            : null,
                    },
                    ct
                );
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DokployApiClient BuildApiClient(DokployResource resource)
    {
        using var scope = serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(resource.DokployUrl);
        http.DefaultRequestHeaders.Add("x-api-key", resource.ApiToken);
        if (!string.IsNullOrWhiteSpace(resource.DeployBypassToken))
            http.DefaultRequestHeaders.Add("X-Deploy-Token", resource.DeployBypassToken);

        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        return new DokployApiClient(http, loggerFactory.CreateLogger<DokployApiClient>());
    }

    /// <summary>
    /// Dokploy's API has no <c>entrypoint</c> field, so a compose <c>entrypoint:</c> override is
    /// silently lost — and Aspire uses one to change how a container is CONFIGURED, not just how it
    /// starts. Every such service is reported, and the one shape we can repair is repaired.
    ///
    /// <para><b>YARP.</b> The image entrypoint is
    /// <c>["dotnet","/app/yarp.dll","/etc/yarp.config"]</c> — the config path is an ARGUMENT. Aspire's
    /// compose output replaces the entrypoint with <c>["dotnet"]</c> + <c>["/app/yarp.dll"]</c>,
    /// dropping that argument, and supplies the route table through <c>REVERSEPROXY__*</c> environment
    /// variables instead. On Dokploy the original entrypoint survives, so the container demands
    /// <c>/etc/yarp.config</c>, cannot find it, and exits 2 — which is exactly how the gateway failed.</para>
    ///
    /// <para>The repair is to satisfy that argument with a STUB file. Verified against the real image:
    /// with <c>{}</c> at <c>/etc/yarp.config</c> and the same env vars, YARP starts, logs
    /// "Loading proxy data from config", and routes (<c>Executed endpoint 'route0'</c>). The env vars
    /// remain the single source of truth — the publisher already carries them — so nothing here has to
    /// understand, duplicate, or keep up with the route table.</para>
    /// </summary>
    private void CompensateForEntrypointOverrides(
        DokployResource resource,
        List<DokployServiceDescriptor> services
    )
    {
        const string YarpConfigPath = "/etc/yarp.config";

        foreach (var svc in services.Where(s => s.Entrypoint.Count > 0))
        {
            var isYarp =
                svc.Command.Any(c => c.Contains("yarp.dll", StringComparison.OrdinalIgnoreCase))
                || svc.Entrypoint.Any(e => e.Contains("yarp.dll", StringComparison.OrdinalIgnoreCase))
                || svc.Image.Contains("/yarp", StringComparison.OrdinalIgnoreCase);

            if (!isYarp)
            {
                logger.LogWarning(
                    "Service '{Service}' overrides its entrypoint ({Entrypoint}) in compose, but "
                        + "Dokploy has no entrypoint field — the override will NOT be applied and the "
                        + "container will run its image default. If that changes how the service reads "
                        + "its configuration, it will start misconfigured rather than fail.",
                    svc.Name,
                    string.Join(" ", svc.Entrypoint)
                );
                continue;
            }

            var alreadyDeclared = resource.Annotations
                .OfType<DokployServiceMountAnnotation>()
                .Any(a =>
                    string.Equals(a.ServiceName, svc.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.ContainerPath, YarpConfigPath, StringComparison.Ordinal));

            if (alreadyDeclared)
                continue;

            logger.LogInformation(
                "Service '{Service}' is a YARP gateway whose compose entrypoint override cannot be "
                    + "applied on Dokploy. Mounting a stub '{Path}' so the image entrypoint is "
                    + "satisfied; routes continue to come from the REVERSEPROXY__* environment variables.",
                svc.Name,
                YarpConfigPath
            );

            resource.Annotations.Add(new DokployServiceMountAnnotation
            {
                ServiceName = svc.Name,
                ContainerPath = YarpConfigPath,
                Type = "file",
                // Deliberately empty: the route table lives in the env vars the publisher already
                // carries. Writing routes here too would create a second source of truth that has to
                // be kept in step with Aspire's generator.
                Content = "{}",
                FilePath = "yarp.config",
            });
        }
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
            logger.LogDebug("Checking for compose YAML at: {Path}", path);
            if (File.Exists(path))
            {
                logger.LogDebug("Found compose YAML at: {Path}", path);
                return path;
            }
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

    private static Dictionary<string, HealthCheckSwarm> CollectHealthCheckAnnotations(
        DokployResource resource
    )
    {
        var result = new Dictionary<string, HealthCheckSwarm>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var annotation in resource.Annotations.OfType<DokployServiceHealthCheckAnnotation>()
        )
            result[annotation.ServiceName] = annotation.HealthCheck;
        return result;
    }

    private static Dictionary<string, long> CollectStopGracePeriodAnnotations(
        DokployResource resource
    )
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var annotation in resource.Annotations.OfType<DokployServiceStopGracePeriodAnnotation>()
        )
            result[annotation.ServiceName] = annotation.Nanoseconds;
        return result;
    }

    private static Dictionary<string, string> CollectUpdateOrderAnnotations(
        DokployResource resource
    )
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var annotation in resource.Annotations.OfType<DokployServiceUpdateOrderAnnotation>()
        )
            result[annotation.ServiceName] = annotation.Order;
        return result;
    }

    private static Dictionary<string, IReadOnlySet<string>> CollectNoSubstitutionAnnotations(
        DokployResource resource
    )
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var annotation in resource.Annotations.OfType<DokployServiceNoSubstitutionAnnotation>()
        )
        {
            if (result.TryGetValue(annotation.ServiceName, out var existing))
                result[annotation.ServiceName] = existing
                    .Union(annotation.EnvKeys)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            else
                result[annotation.ServiceName] = annotation.EnvKeys;
        }
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

    /// <summary>
    /// Merges Aspire-generated env vars with the existing env vars already saved in Dokploy.
    /// Aspire's values win for any key they provide; keys that only exist in Dokploy are preserved.
    /// This ensures manually-set values (e.g. Stripe keys, cloud function URLs) survive re-deploys.
    /// </summary>
    private async Task<string> MergeWithExistingEnvAsync(
        string applicationId,
        string aspireEnvString,
        DokployApiClient apiClient,
        string serviceName,
        CancellationToken ct
    )
    {
        string? existingEnv = null;
        try
        {
            var app = await apiClient.GetApplicationAsync(applicationId, ct);
            existingEnv = app.Env;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Could not fetch existing env vars for '{Service}' — will overwrite: {Message}",
                serviceName,
                ex.Message
            );
        }

        if (string.IsNullOrWhiteSpace(existingEnv))
            return aspireEnvString;

        // Parse existing Dokploy env vars into a dict (preserves order, last-wins on duplicates)
        var existing = ParseEnvString(existingEnv);

        // Parse Aspire env vars — these override existing values
        var aspire = ParseEnvString(aspireEnvString);

        // Merge: start with existing, overwrite/add Aspire keys
        foreach (var (key, value) in aspire)
            existing[key] = value;

        var merged = string.Join('\n', existing.Select(kv => $"{kv.Key}={kv.Value}"));

        var preservedCount = existing.Count - aspire.Count;
        if (preservedCount > 0)
        {
            logger.LogInformation(
                "Merged env vars for '{Service}': {Aspire} from Aspire + {Preserved} preserved from Dokploy",
                serviceName,
                aspire.Count,
                preservedCount
            );
        }

        return merged;
    }

    private static Dictionary<string, string> ParseEnvString(string envString)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in envString.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
                continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0)
                continue;
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..]; // don't trim value — it may be intentionally padded
            if (!string.IsNullOrEmpty(key))
                result[key] = value;
        }
        return result;
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
        if (string.IsNullOrWhiteSpace(resource.EnvironmentName))
            throw new InvalidOperationException(
                $"EnvironmentName is not set on resource '{resource.Name}'. "
                    + "Set it via DokploySettings.EnvironmentName or leave unset to use the default 'production'."
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

/// <summary>
/// Annotation attached to a DokployResource to configure a Swarm health check for a specific service.
/// </summary>
public class DokployServiceHealthCheckAnnotation : IResourceAnnotation
{
    public required string ServiceName { get; init; }
    public required HealthCheckSwarm HealthCheck { get; init; }
}

/// <summary>
/// Annotation attached to a DokployResource to set a Docker Swarm stop grace period for a specific service.
/// The stop grace period is the time Docker waits after sending SIGTERM before sending SIGKILL on redeploy.
/// Increase this for services that need time for clean shutdown (e.g. PostgreSQL WAL flush, RabbitMQ quorum sync).
/// </summary>
public class DokployServiceStopGracePeriodAnnotation : IResourceAnnotation
{
    public required string ServiceName { get; init; }

    /// <summary>Stop grace period in nanoseconds (1 second = 1_000_000_000 ns).</summary>
    public required long Nanoseconds { get; init; }
}

/// <summary>
/// Annotation attached to a DokployResource to set the Docker Swarm update order for a specific service.
/// Use "stop-first" for stateful single-replica services (PostgreSQL, RabbitMQ) to prevent the new
/// container from starting before the old one releases its data directory lock.
/// </summary>
public class DokployServiceUpdateOrderAnnotation : IResourceAnnotation
{
    public required string ServiceName { get; init; }

    /// <summary>"stop-first" or "start-first" (Swarm default).</summary>
    public required string Order { get; init; }
}

/// <summary>
/// Annotation that marks a service as "skip redeploy if already running".
/// When set, the Aspire deployment pipeline still saves the latest image and env vars to Dokploy,
/// but skips calling DeployApplicationAsync if the service status is already "running".
/// This prevents unnecessary container restarts for stateful infrastructure services
/// (PostgreSQL, RabbitMQ, etc.) where a restart can cause data-directory lock races or WAL corruption.
/// If the service is NOT running (first deploy, or crashed), it is always deployed regardless.
/// </summary>
public class DokployServiceSkipRedeployAnnotation : IResourceAnnotation
{
    public required string ServiceName { get; init; }
}

/// <summary>
/// Annotation that completely excludes a service from the Dokploy deployment pipeline.
/// When set, the service is removed from the services-to-deploy list before any Dokploy API
/// calls are made — the service is not updated, not redeployed, and not touched in any way.
/// Use this for services you want to manage independently (e.g. skip openbao/keycloak during
/// an app-only deploy). Typically driven by a CI environment variable set from a workflow input.
/// </summary>
public class DokployServiceExcludeAnnotation : IResourceAnnotation
{
    public required string ServiceName { get; init; }
}

/// <summary>
/// Annotation that marks specific env var keys as exempt from Dokploy service-name substitution.
/// Use this for env vars whose values happen to match a resource name but are NOT hostnames
/// (e.g. Keycloak client IDs, OAuth client names, feature flags).
/// </summary>
public class DokployServiceNoSubstitutionAnnotation : IResourceAnnotation
{
    public required string ServiceName { get; init; }
    public required IReadOnlySet<string> EnvKeys { get; init; }
}

/// <summary>
/// Annotation that requests a persistent volume mount for a Dokploy Application service.
/// Applied via <c>WithDokployMount()</c> and consumed by
/// <c>ConfigureAndDeployApplicationAsync</c> in <see cref="DokployInfrastructure"/>.
/// </summary>
public class DokployServiceMountAnnotation : IResourceAnnotation
{
    public required string ServiceName { get; init; }

    /// <summary>Absolute path inside the container (e.g. /var/lib/postgresql/data).</summary>
    public required string ContainerPath { get; init; }

    /// <summary>Stable named Docker volume to create/reuse across deploys (used when Type = "volume").</summary>
    public string? VolumeName { get; init; }

    /// <summary>Absolute path on the Docker host (used when Type = "bind").</summary>
    public string? HostPath { get; init; }

    /// <summary>"volume" (default), "bind", or "file".</summary>
    public string Type { get; init; } = "volume";

    /// <summary>File content for Type = "file" — Dokploy materialises it on the Docker host.</summary>
    public string? Content { get; init; }

    /// <summary>Name Dokploy gives the materialised file (Type = "file").</summary>
    public string? FilePath { get; init; }
}
