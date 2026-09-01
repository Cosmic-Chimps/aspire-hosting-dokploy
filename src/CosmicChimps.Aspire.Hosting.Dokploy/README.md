# CosmicChimps.Aspire.Hosting.Dokploy

Deploy .NET Aspire applications to Dokploy using Docker Stack format.

## Overview

This package extends .NET Aspire to deploy applications to [Dokploy](https://dokploy.com), a self-hosted PaaS built on Docker Swarm. It automatically configures your Aspire application for Docker Stack deployment with full control over all Docker Compose service settings.

## Key Discovery

**Aspire.Hosting.Docker already includes full Docker Stack/Swarm support!** This package builds on that foundation to provide seamless Dokploy integration via API. You use the built-in `PublishAsDockerComposeService()` method to configure all Swarm settings with complete flexibility.

## Installation

```bash
dotnet add package CosmicChimps.Aspire.Hosting.Dokploy
```

## Usage

### Basic Configuration with Swarm Deploy Settings

```csharp
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;

var builder = DistributedApplication.CreateBuilder(args);

// Configure Docker Compose environment for Stack/Swarm format
builder.AddDockerComposeEnvironment("env")
    .ConfigureComposeFile(composeFile =>
    {
        // Set version for Docker Stack compatibility
        composeFile.Version = "3.8";

        // Change networks from bridge to overlay for Swarm
        foreach (var network in composeFile.Networks.Values)
        {
            if (network.Driver == "bridge" || network.Driver is null)
            {
                network.Driver = "overlay";
            }
        }

        // Clean up all services for Stack compatibility
        foreach (var service in composeFile.Services.Values)
        {
            // Remove depends_on (not supported in Stack format)
            service.DependsOn.Clear();

            // Remove restart if deploy section exists
            if (service.Deploy is not null)
            {
                service.Restart = null;
            }
        }
    });

// Configure Redis with Swarm deploy settings
var cache = builder.AddRedis("cache")
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Deploy = new Deploy
        {
            Replicas = 1,
            RestartPolicy = new RestartPolicy
            {
                Condition = "on-failure",
                Delay = "5s",
                MaxAttempts = 3
            },
            Placement = new Placement
            {
                // Pin to manager node for data persistence
                Constraints = new List<string> { "node.role == manager" }
            }
        };
        // Remove compose-specific restart
        service.Restart = null;
    });

// Configure API service with Swarm deploy settings
var api = builder.AddProject<Projects.ApiService>("api")
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Deploy = new Deploy
        {
            Replicas = 2, // Scale to 2 instances
            RestartPolicy = new RestartPolicy
            {
                Condition = "on-failure",
                Delay = "5s",
                MaxAttempts = 3
            }
        };
        service.Restart = null;
    });

// Configure web with Traefik labels for sticky sessions
builder.AddProject<Projects.Web>("web")
    .WithReference(cache)
    .WithReference(api)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Deploy = new Deploy
        {
            Replicas = 3,
            RestartPolicy = new RestartPolicy
            {
                Condition = "on-failure",
                Delay = "5s",
                MaxAttempts = 3
            },
            Labels = new LabelSpecs
            {
                { "traefik.http.services.blazor.loadbalancer.sticky.cookie", "true" },
                { "traefik.http.services.blazor.loadbalancer.sticky.cookie.name", "blazor_affinity" }
            }
        };
        service.Restart = null;
    });

builder.Build().Run();
```

### Deploy to Dokploy (Future Feature)

```csharp
using CosmicChimps.Aspire.Hosting.Dokploy;

// This will be implemented in a future version
builder.PublishToDokploy("myapp", settings =>
{
    settings.DokployUrl = "https://your-dokploy-instance.com";
    settings.ApiToken = builder.Configuration["Dokploy:ApiToken"]!;
    settings.ProjectId = builder.Configuration["Dokploy:ProjectId"]!;
});
```

## Why Use PublishAsDockerComposeService Directly?

By using `PublishAsDockerComposeService()` directly instead of wrapper methods, you get:

✅ **Full Control**: Configure any Docker Compose/Stack setting  
✅ **Flexibility**: Add labels, volumes, networks, environment variables, etc.  
✅ **Composability**: Chain multiple configurations together  
✅ **No Limitations**: Not restricted to predefined wrapper methods  

### Example: Complete Service Configuration

```csharp
builder.AddProject<Projects.Web>("web")
    .PublishAsDockerComposeService((_, service) =>
    {
        // Deploy settings
        service.Deploy = new Deploy
        {
            Replicas = 3,
            RestartPolicy = new RestartPolicy
            {
                Condition = "on-failure",
                Delay = "10s",
                MaxAttempts = 5
            },
            Placement = new Placement
            {
                Constraints = new List<string> 
                { 
                    "node.role == worker",
                    "node.labels.region == us-east"
                }
            },
            // Add Traefik labels
            Labels = new LabelSpecs
            {
                { "traefik.enable", "true" },
                { "traefik.http.routers.web.rule", "Host(`example.com`)" },
                { "traefik.http.services.web.loadbalancer.sticky.cookie", "true" }
            },
            // Resource limits
            Resources = new Resources
            {
                Limits = new ResourceLimits
                {
                    Cpus = "0.5",
                    Memory = "512M"
                },
                Reservations = new ResourceReservations
                {
                    Cpus = "0.25",
                    Memory = "256M"
                }
            }
        };
        
        // Remove compose-specific settings
        service.Restart = null;
        
        // Add volumes, networks, or other settings as needed
    });
```

## Docker Stack vs Docker Compose

### ports vs expose

- **`ports`**: Exposes ports to the host machine (external access)
  ```yaml
  ports:
    - "${WEBFRONTEND_PORT}:8080"
  ```
  Use when you need external access (e.g., web frontend via reverse proxy)

- **`expose`**: Makes ports available only to other services in the same network (internal access)
  ```yaml
  expose:
    - "${APISERVICE_PORT}"
  ```
  Use for internal services that only need to communicate with other services

### Stack Format Requirements

Docker Stack has different requirements than Docker Compose:

#### ✅ Supported

- Pre-built images in a registry
- Environment variables
- Named volumes
- Overlay networks
- Deploy sections (replicas, restart policy, placement, resources, labels)
- `expose` and `ports` for networking

#### ❌ Not Supported

- `build:` sections (images must be pre-built)
- `depends_on:` (Swarm has built-in service discovery)
- `container_name:` (Swarm manages naming)
- Top-level `restart:` (use `deploy.restart_policy`)
- Extended `depends_on` format with conditions

## Configuration

Store your Dokploy credentials in `appsettings.json`:

```json
{
  "Dokploy": {
    "ApiToken": "your-api-token",
    "ProjectId": "your-project-id"
  }
}
```

## Troubleshooting

### Error: "services.webfrontend.depends_on.0 must be a string"

Docker Stack doesn't support the extended `depends_on` format with conditions. Use the `ConfigureComposeFile` method shown above to remove `depends_on` entries, or convert them to simple string format.

### Error: "Service has a build section"

Stack files don't support `build:`. Ensure your project images are pre-built and pushed to a registry accessible by your Swarm cluster.

### Networks not working between services

Ensure networks use `driver: overlay` for multi-host Swarm networking. The example above shows how to convert bridge networks to overlay in the `ConfigureComposeFile` method.

### When to use `ports` vs `expose`

- Use `ports` for services that need external access (e.g., web frontend accessible via Traefik)
- Use `expose` for internal services that only communicate with other services (e.g., API, databases)

## Diagnosing a failed API call

Every failed Dokploy API call logs, at Warning level:

```
Dokploy API POST https://paas.example.com/api/project.create → 400
  request  content-type  : application/json; charset=UTF-8
  request  content-length: 28
  request  body          : {"name":"myapp"}
  final    uri           : https://paas.example.com/api/project.create
  response server        : traefik
  response content-type  : application/json
  response body          : {"message":"Input failed",...}
```

Three of those fields exist for a specific reason:

- **`content-length`** tells "we never sent the field" apart from "we sent it and it did not arrive".
  A Dokploy zod error reading `expected string, received undefined` is ambiguous without it.
- **`final uri`** is the URI the request actually reached. If it differs from the configured URL the
  line is flagged `⚠ REDIRECTED` — a 301/302/303 makes `HttpClient` turn POST into GET and **drop the
  body**, which produces exactly that zod error. 307/308 preserve both.
- **`response server`** identifies what answered, so a proxy error page is not mistaken for Dokploy.

The request body is **redacted** by default: values whose JSON key looks secret, and `KEY=value`
assignments inside the `env` blob, are replaced with `***`. To see it verbatim while chasing a
specific failure:

```csharp
builder.PublishToDokploy("myapp", s => { s.VerboseHttpLogging = true; });
```

Request bodies carry registry credentials and every service environment variable, so turn it off
again afterwards.

Set the log level to `Debug` for `CosmicChimps.Aspire.Hosting.Dokploy` to also see each outgoing
request and the resolved API base address (never the token — only whether one is present, and its
length, which is enough to spot a truncated secret).

## Configuring with Aspire parameters

Every deployment setting accepts either a literal string or an Aspire **parameter**, resolved when
the deployment runs rather than when the application model is built. Parameters can be prompted for,
marked secret, varied per environment, and appear in the manifest — none of which `IConfiguration`
gives you ([#1](https://github.com/Cosmic-Chimps/aspire-hosting-dokploy/issues/1)).

```csharp
var portalUrl    = builder.AddParameter("portal-url");
var dokployUrl   = builder.AddParameter("dokploy-url");
var dokployToken = builder.AddParameter("dokploy-token", secret: true);
var registryPw   = builder.AddParameter("registry-password", secret: true);

var dokploy = builder.PublishToDokploy("myapp", s =>
{
    s.DokployUrl = dokployUrl.AsDokployValue();
    s.ApiToken   = dokployToken.AsDokployValue();

    s.Registry = new RegistryCredentials
    {
        RegistryUrl = "ghcr.io",             // literals still work everywhere
        ImagePrefix = "ghcr.io/myorg",
        Username    = "myorg",
        Password    = registryPw.AsDokployValue(),
    };
});

builder.AddNextJsApp("web", "./apps/web")
       .WithDokployDomain(dokploy, portalUrl, https: true, certificateType: "letsencrypt");
```

**Both forms are supported on every setting.** Strings convert implicitly, so existing code is
unchanged:

```csharp
s.DokployUrl = "https://paas.example.com";   // still fine
```

Two ways to pass a parameter, because C# does not allow implicit conversions from an interface type
and `AddParameter` returns `IResourceBuilder<ParameterResource>`:

| Form | Use |
|---|---|
| `param.AsDokployValue()` | assigning to a `DokploySettings` / `RegistryCredentials` property |
| `param.Resource` | same thing, via the implicit `ParameterResource` conversion |
| `WithDokployDomain(dokploy, param, ...)` | domains take the builder directly — no helper needed |

Resolution happens exactly once, at the start of the deploy step. Nothing downstream of that ever
sees an unresolved parameter, and a deferred value's `ToString()` renders as `<parameter:name>` — so
a secret cannot leak into a log line even by accident.

## Deploying the Aspire dashboard (opt-in)

By default every service recognised as Aspire infrastructure is stripped from the published output:
an image containing `aspire-dashboard`, or a service name ending in `-dashboard`. Every environment
value that refers to a stripped service is dropped along with it, so `OTEL_EXPORTER_OTLP_ENDPOINT`
disappears too. That is the right default — a local dashboard has no place in a deployment.

It is the wrong default for a self-hosted install with no external telemetry service, where the
dashboard is the only place to read logs and traces. Opt in:

```csharp
var dokploy = builder.PublishToDokploy("myapp", s => { /* ... */ })
    .WithDokployDashboard(d => d.WithHostPort(18888)
                                .WithForwardedHeaders(true));

// or, equivalently, in the settings lambda:
//   settings.DeployDashboard = true;
```

The dashboard then becomes an ordinary Dokploy application: it gets an app name, and the other
services' `OTEL_EXPORTER_OTLP_ENDPOINT` resolves to it like any other service reference.

`WithDokployDashboard` exists because `WithDashboard` is declared on
`IResourceBuilder<DockerComposeEnvironmentResource>`, and `PublishToDokploy` creates that
environment internally — so a caller never holds the builder it needs.

### ⚠️ Do not give the dashboard a public domain

The dashboard shows telemetry from every service, and **its OTLP ingest endpoint is unauthenticated
by default** — anything that can reach it can inject or spoof telemetry. Aspire's own guidance is
explicit: *don't expose an anonymously accessible dashboard or its endpoints to an untrusted
network.*

When you deploy it, at minimum:

| Setting | Why |
|---|---|
| no `WithDokployDomain(...)` on it | reach it over the platform's internal network instead |
| `Dashboard:Otlp:AuthMode=ApiKey` + `Dashboard:Otlp:PrimaryApiKey` | the default is `Unsecured` |
| `Dashboard:Frontend:BrowserToken` set from a secret | otherwise it regenerates on every restart and you must read it back out of container logs |
| `AllowedHosts` | DNS-rebinding defence |

Note also that the published dashboard image comes from a **pre-release** image repository, and that
its telemetry retention is bounded and in-memory — it is a live diagnostic window, not an archive.

See [Aspire dashboard security considerations](https://aspire.dev/dashboard/security-considerations/)
and [dashboard configuration](https://aspire.dev/dashboard/configuration/).

## Examples

See the `example/CosmicChimps.Aspire.AppHost/` folder in the repository for a complete working example with Redis, API service, and Blazor web frontend configured for Docker Stack deployment.

## License

MIT

## Contributing

Contributions welcome! Please open an issue or PR on GitHub.

## Links

- [Dokploy Documentation](https://docs.dokploy.com)
- [.NET Aspire Documentation](https://learn.microsoft.com/dotnet/aspire/)
- [Docker Stack Documentation](https://docs.docker.com/engine/swarm/stack-deploy/)
- [Docker Compose Specification](https://docs.docker.com/compose/compose-file/)


