using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;

var builder = DistributedApplication.CreateBuilder(args);

// ── Deployment configuration as Aspire parameters (issue #1) ─────────────────
// Every Dokploy setting takes either a literal or an Aspire parameter. Parameters are the better
// home for deployment configuration: they are prompted for when missing, can be marked secret,
// vary per environment, and appear in the manifest — none of which reading IConfiguration at
// model-build time gives you.
//
// Supply them however Aspire parameters are normally supplied, e.g.
//   dotnet user-secrets set Parameters:dokploy-url https://paas.example.com
//   Parameters__dokploy-token=... aspire deploy
//
var dokployUrl = builder.AddParameter("dokploy-url");
var dokployToken = builder.AddParameter("dokploy-token", secret: true);
var registryUrl = builder.AddParameter("registry-url");
var registryUsername = builder.AddParameter("registry-username");
var registryPassword = builder.AddParameter("registry-password", secret: true);

// ── Push: Aspire-native registry (handles docker build + push) ────────────────
// Aspire's pipeline builds images and pushes them to this registry BEFORE deploying.
// The registry-qualified image name (e.g. "docker.io/cosmicchimps/apiservice:tag")
// is then written to .env.Production and our Dokploy deployer reads it from there.
//
// AddContainerRegistry takes NO credentials — its overloads are (name, endpoint, repository). The
// push is authenticated by the ambient docker login, either:
//   a) Pre-authenticate: `docker login docker.io` before running `aspire deploy`
//   b) Set DOTNET_DOCKER_REGISTRY_USERNAME / DOTNET_DOCKER_REGISTRY_PASSWORD env vars
// The credentials on settings.Registry below are a different thing: they are what Dokploy stores
// and reuses to PULL, including when it restarts a service long after the deploy finished.
//
#pragma warning disable ASPIRECOMPUTE003
// AddContainerRegistry has its own parameter overload — endpoint and repository must both be
// parameters or both be strings, they cannot be mixed.
builder.AddContainerRegistry("my-registry", endpoint: registryUrl, repository: registryUsername);
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
        // Parameters, resolved when the deployment runs — not now, while the model is built.
        // `.AsDokployValue()` is needed because C# forbids implicit conversions from an interface
        // type and AddParameter returns IResourceBuilder<ParameterResource>. `.Resource` works too.
        settings.DokployUrl = dokployUrl.AsDokployValue();
        settings.ApiToken = dokployToken.AsDokployValue();

        // Literals still assign directly — mix the two freely.
        settings.ProjectName = "demo-aspire";
        settings.EnvironmentName = environmentName; // "production" | "staging" | "dev"
        settings.AppNamePrefix = "da-";

        // Pull credentials: Dokploy needs these to `docker pull` from a private registry.
        // ImagePrefix must match the `repository` argument passed to AddContainerRegistry above.
        settings.Registry = new RegistryCredentials
        {
            RegistryUrl = registryUrl.AsDokployValue(),
            ImagePrefix = registryUsername.AsDokployValue(),
            Username = registryUsername.AsDokployValue(),
            Password = registryPassword.AsDokployValue(),
        };
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
