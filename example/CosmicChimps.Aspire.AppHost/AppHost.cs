using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;

var builder = DistributedApplication.CreateBuilder(args);

// ── Push: Aspire-native registry (handles docker build + push) ────────────────
// Aspire's pipeline builds images and pushes them to this registry BEFORE deploying.
// The registry-qualified image name (e.g. "docker.io/cosmicchimps/apiservice:tag")
// is then written to .env.Production and our Dokploy deployer reads it from there.
//
// For login, either:
//   a) Pre-authenticate: `docker login docker.io` before running `aspire deploy`
//   b) Set DOTNET_DOCKER_REGISTRY_USERNAME / DOTNET_DOCKER_REGISTRY_PASSWORD env vars
//
#pragma warning disable ASPIRECOMPUTE003
builder.AddContainerRegistry(
    "my-registry",
    endpoint: builder.Configuration["DockerHub:RegistryUrl"]
        ?? Environment.GetEnvironmentVariable("DOCKERHUB_REGISTRY_URL")
        ?? throw new InvalidOperationException("DockerHub Registry Url not configured"),
    repository: builder.Configuration["DockerHub:Username"]
        ?? Environment.GetEnvironmentVariable("DOCKERHUB_USERNAME")
        ?? throw new InvalidOperationException("DockerHub Username not configured")
);
#pragma warning restore ASPIRECOMPUTE003

// ── Dokploy: register as publish target ──────────────────────────────────────
// One project in Dokploy can hold multiple environments (production, staging, dev).
// Set EnvironmentName to target a specific environment — it is created automatically
// if it doesn't exist inside the project.
//
//   aspire deploy  →  deploys to "production"
//   DOKPLOY_ENVIRONMENT=staging aspire deploy  →  deploys to "staging"
//
var environmentName =
    builder.Configuration["Dokploy:EnvironmentName"]
    ?? Environment.GetEnvironmentVariable("DOKPLOY_ENVIRONMENT")
    ?? "production";

var dokploy = builder.PublishToDokploy(
    "demo-aspire",
    settings =>
    {
        settings.DokployUrl =
            builder.Configuration["Dokploy:Url"]
            ?? Environment.GetEnvironmentVariable("DOKPLOY_URL")
            ?? throw new InvalidOperationException("Dokploy URL not configured");
        settings.ApiToken =
            builder.Configuration["Dokploy:ApiToken"]
            ?? Environment.GetEnvironmentVariable("DOKPLOY_API_TOKEN")
            ?? throw new InvalidOperationException("Dokploy API token not configured");

        settings.ProjectName = "demo-aspire";
        settings.EnvironmentName = environmentName; // "production" | "staging" | "dev"
        settings.AppNamePrefix = "da-";

        // Pull credentials: Dokploy server needs these to `docker pull` from a private registry.
        // ImagePrefix must match the `repository` argument passed to AddContainerRegistry above.
        var username = builder.Configuration["DockerHub:Username"]
            ?? Environment.GetEnvironmentVariable("DOCKERHUB_USERNAME");
        var password = builder.Configuration["DockerHub:Password"]
            ?? Environment.GetEnvironmentVariable("DOCKERHUB_PASSWORD");
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            settings.Registry = new RegistryCredentials
            {
                RegistryUrl = "docker.io",
                ImagePrefix = username,
                Username = username,
                Password = password,
            };
        }
    }
);

// Redis is detected automatically by image name → redis.create in Dokploy.
var cache = builder.AddRedis("cache");

// Seq — structured log/trace viewer.
// Deployed to Dokploy as a regular application; all services send OTEL data to it.
var seq = builder.AddSeq("seq").WithDataVolume();

var apiService = builder
    .AddProject<Projects.CosmicChimps_Aspire_ApiService>("apiservice")
    .WithReference(seq)
    .WithHttpHealthCheck("/health");

builder
    .AddProject<Projects.CosmicChimps_Aspire_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithDokployDomain(dokploy, $"web-{environmentName}.example.com")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
