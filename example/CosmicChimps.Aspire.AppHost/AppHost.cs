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

// ── Telemetry: deploy the Aspire dashboard (opt-in) ──────────────────────────
// By default any dashboard service is stripped from the published output, along with every env
// value that references it. That is right for a local dashboard. It is wrong for a self-hosted
// install with no external telemetry service, where the dashboard is the only place to read logs
// and traces — so opt in.
//
// Comment this block out to keep the historical behaviour (dashboard filtered, Seq below used
// instead).
var dashboardDomain = $"dashboard-{environmentName}.example.com";

// The dashboard's own compose service name, "<PublishToDokploy name>-compose-dashboard". This is
// the hostname every telemetry sender resolves, and it MUST appear in AllowedHosts — see the note
// on that line. Written as one value so the allow-list entry and the exporter endpoint cannot drift.
var dashboardHost = "demo-aspire-compose-dashboard";
var dashboardOtlpKey = builder.AddParameter("dashboard-otlp-key", secret: true);
var dashboardBrowserToken = builder.AddParameter("dashboard-browser-token", secret: true);

dokploy.WithDokployDashboard(dashboard =>
{
    dashboard
        .WithHostPort(18888)
        // Both forwarded-header switches. Each is sufficient alone (verified against the image);
        // without either, TLS terminates at the proxy, the dashboard sees http, and the https
        // Origin is rejected — "Rejecting Blazor WebSocket upgrade with disallowed Origin". The page
        // renders and the live connection never opens.
        .WithForwardedHeaders(true)
        .WithEnvironment("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")
        // Telemetry ingest. The default is Unsecured — anything able to reach the port could
        // inject or impersonate telemetry.
        .WithEnvironment("Dashboard__Otlp__AuthMode", "ApiKey")
        .WithEnvironment("Dashboard__Otlp__PrimaryApiKey", dashboardOtlpKey)
        // Browser sign-in. Pin the token, or the dashboard mints a new one on every restart and
        // you must dig it back out of the container logs.
        .WithEnvironment("Dashboard__Frontend__AuthMode", "BrowserToken")
        .WithEnvironment("Dashboard__Frontend__BrowserToken", dashboardBrowserToken)
        // Host filtering is global to the app and runs BEFORE authentication, so this one list
        // gates the browser AND both OTLP ingest ports. Two distinct failures:
        //
        //   AllowedHosts missing            → 400 "Bad Request - Invalid Hostname" in the browser
        //   AllowedHosts without the ingest → dashboard looks perfect and stays permanently EMPTY;
        //   host                              every sender gets 400 before its key or payload is
        //                                     read, and nothing is logged on either side
        //
        // The second is the expensive one. It also makes a wrong key return 400 instead of 401,
        // which reads like a healthy endpoint with broken senders. To tell them apart, send a
        // request with a deliberately wrong key: 400 means something answered before auth did.
        .WithEnvironment(
            "AllowedHosts",
            $"{dashboardDomain};localhost;127.0.0.1;{dashboardHost}"
        )
        // For links the dashboard builds about itself, including the login URL it logs at startup —
        // without it they point at localhost. It does NOT govern the Blazor origin check; forwarded
        // headers above do. Verified both ways against the image.
        .WithEnvironment("Dashboard__Frontend__PublicUrl", $"https://{dashboardDomain}")
        // Optional. Omit the domain to keep the dashboard internal-only and reach it over an SSH
        // tunnel instead; the OTLP ingest ports stay on the container network either way.
        .WithDokployDomain(dokploy, dashboardDomain, port: 18888)
        // Optional: persist DataProtection keys so sign-in survives a restart.
        //
        // Mount /home/app, NOT the nested .aspnet/DataProtection-Keys. Docker seeds a fresh volume
        // from the image only when the mount path exists there, ownership included; over a missing
        // path it creates a root-owned directory. This image runs as UID 1654 and has /home/app
        // owned by 1654 but no .aspnet subtree — so the nested path is unwritable and every page
        // render fails, since Blazor encrypts component state with DataProtection.
        .WithDokployMount(dokploy, "/home/app", "demo-dashboard-home");
});

// Redis is detected automatically by image name → redis.create in Dokploy.
var cache = builder.AddRedis("cache");

// Seq — structured log/trace viewer.
// Deployed to Dokploy as a regular application; all services send OTEL data to it.
var seq = builder.AddSeq("seq").WithDataVolume();

// Senders must present the ingest key, or the dashboard rejects their telemetry once OTLP auth is
// on. The endpoint/protocol/service-name are injected by the dashboard resource itself; only the
// header has to be added here — and it must go on EVERY sender, the gateway-style services
// included, or that one service's telemetry is silently dropped.
var otlpHeaders = ReferenceExpression.Create($"x-otlp-api-key={dashboardOtlpKey.Resource}");

var apiService = builder
    .AddProject<Projects.CosmicChimps_Aspire_ApiService>("apiservice")
    .WithReference(seq)
    .WithEnvironment("OTEL_EXPORTER_OTLP_HEADERS", otlpHeaders)
    .WithHttpHealthCheck("/health");

builder
    .AddProject<Projects.CosmicChimps_Aspire_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithEnvironment("OTEL_EXPORTER_OTLP_HEADERS", otlpHeaders)
    .WithHttpHealthCheck("/health")
    .WithDokployDomain(dokploy, $"web-{environmentName}.example.com")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
