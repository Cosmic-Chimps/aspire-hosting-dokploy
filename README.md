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

## Request content type

Requests are sent as `Content-Type: application/json`, with **no `charset` parameter**.

This matters. Isolated against a live Dokploy v0.30.3 instance with two requests identical in host,
token, body and protocol, differing only in this header:

```
Content-Type: application/json                  → 200, project created
Content-Type: application/json; charset=UTF-8   → 400
  {"zodError":{"fieldErrors":{"name":["Invalid input: expected string, received undefined"]}}}
```

Dokploy's body parser matches the content type strictly, skips parsing on the parameter, and the
procedure then runs against an empty object. The body is on the wire in both cases, so the failure
presents as a lost payload rather than a rejected header.

This worked for a long time with `PostJsonAsync` — earlier Dokploy versions parsed the body
regardless — so treat it as a v0.30.x behaviour change rather than a long-standing bug.

Flurl's `PostJsonAsync` always appends the charset and it **cannot** be stripped in a `BeforeCall`
hook — the header reads correctly there and the charset is still on the socket. So every POST goes
through explicit `StringContent` with the header set by hand. Do not "simplify" these back to
`PostJsonAsync`.

Dropping the parameter is correct regardless of Dokploy: JSON is UTF-8 by definition
(RFC 8259 §8.1) and `charset` is not a defined parameter for `application/json`.

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

## Volumes

Use `WithDokployMount` for anything that must survive a redeploy:

```csharp
builder.AddPostgres("postgres")
       .WithDokployMount(dokploy, "/var/lib/postgresql/data", "myapp-postgres-data");
```

**`WithDataVolume()` is not enough.** Dokploy application services run on Docker Swarm, which does
not honour it — the container comes up healthy on empty storage and the deploy reports success. A
database that starts on an empty volume is a data-loss event, not a first run, so register the
volume through Dokploy's own mounts API with `WithDokployMount`.

### Mount a path that exists in the image

This one is easy to get wrong and fails in a way that does not point at the mount.

Docker initialises a fresh named volume from the image **only when the mount path already exists
there**, copying that directory's ownership along with it. Mount over a path the image does not
have, and Docker creates it — **owned by root**. Most .NET images run as a non-root user, so the
application then cannot write to its own volume.

The symptom is a permission error deep inside whatever uses that directory, with nothing naming the
volume:

```
System.UnauthorizedAccessException: Access to the path '/home/app/.aspnet/DataProtection-Keys/….tmp' is denied.
 ---> System.IO.IOException: Permission denied
```

Check the image before choosing a path:

```bash
docker image inspect <image> --format '{{.Config.User}}'          # e.g. 1654
CID=$(docker create <image>); docker export $CID | tar -tv | grep home/app
```

Then mount the **existing** parent rather than the nested path you actually care about:

```csharp
// ✗ /home/app/.aspnet/DataProtection-Keys is absent from the image → root-owned → unwritable
// ✓ /home/app exists, owned by the runtime user → volume inherits that ownership
service.WithDokployMount(dokploy, "/home/app", "myapp-home");
```

Chiseled images have no shell, so you cannot `docker run … sh -c 'ls -la'` to check. `docker export`
piped through `tar -tv`, as above, works on any image.

## YARP gateways

An Aspire YARP gateway needs no extra API — the publisher already compensates for three things that
would otherwise break it on Dokploy. Worth knowing they happen, because when one *cannot* be
satisfied you get a warning rather than a failure.

**Handled for you:**

| Problem | What the publisher does |
|---|---|
| Cluster destinations are emitted **without a port** (`http://api`), because Aspire resolves them through service discovery at run time | Fills the port in from other env values on the same service (`services__api__http__0`, `API_HTTP`). Logs a warning naming the destination if no port can be found |
| Cluster **IDs** look like service names (`CLUSTERID=cluster_api`) and would be rewritten to Dokploy app names, leaving routes pointing at clusters that do not exist | Values of `*__CLUSTERID` keys are exempt from hostname substitution |
| Aspire's compose **overrides the entrypoint** to read `/etc/yarp.config`, and Dokploy has no entrypoint field | Mounts a stub `{}` at that path so the image entrypoint is satisfied. Routes keep coming from the `REVERSEPROXY__*` env vars, which stay the single source of truth |
| YARP routes are named **positionally** (`route0…routeN`) and the env merge preserved keys a deploy no longer wrote, so a deploy with FEWER routes left a stale `route4=/api/{**rest}` beside the new `route2=/api/{**rest}` — every request failed with `AmbiguousMatchException`, and nothing reproduced locally | Prefixes in `DokploySettings.ReplacedEnvPrefixes` (default `REVERSEPROXY__`) are replaced as a family: existing keys under them that the deploy did not write are dropped and logged by name. Hand-set keys outside those prefixes are still preserved |

A non-YARP service that overrides its entrypoint gets a warning instead: Dokploy will run the image
default, so it starts *misconfigured* rather than failing.

**What you still do yourself:**

```csharp
var gateway = builder.AddYarp("gateway")
    .WithExternalHttpEndpoints()
    .WithConfiguration(yarp => { /* routes */ });

// Pin the listening port. The stock YARP image presets ASPNETCORE_URLS itself, so without this the
// port is the image's choice — and the public domain, the endpoint and the listener are three
// independent facts that only happen to agree. String concatenation, not interpolation: an
// interpolated string binds to Aspire's ReferenceExpression overload, which takes only
// IValueProvider holes.
gateway
    .WithEndpoint("http", e => e.TargetPort = 8080, createIfNotExists: true)
    .WithEnvironment("ASPNETCORE_URLS", "http://+:" + 8080);
```

**Do not give it a container health check.** The stock YARP image is chiseled — no `/bin/sh`, no
`curl`, nothing to probe with — so any `WithDokployHealthCheck` on it fails every interval and Swarm
restart-loops a healthy gateway. It is a stateless proxy: it either holds the port or the process
exits, and Swarm restarts it on exit anyway.

Probing the gateway is also the wrong shape even where tooling exists: a request to `/` is proxied
to an upstream, so a slow upstream start would kill a perfectly good gateway.

### Reading a 502

| Where it comes from | Tell |
|---|---|
| The platform's proxy cannot reach the gateway | `Server: traefik` on the response; the gateway is down, restarting, or its domain points at the wrong port |
| The gateway cannot reach an upstream | Gateway is up and logging; the destination is down or its cluster address is wrong |

`curl -sI https://your-host/ | head -5` distinguishes the two in one command.

## Deploying the Aspire dashboard (opt-in)

By default every service recognised as Aspire infrastructure is stripped from the published output:
an image containing `aspire-dashboard`, or a service name ending in `-dashboard`. Every environment
value that refers to a stripped service is dropped along with it, so `OTEL_EXPORTER_OTLP_ENDPOINT`
disappears too. That is the right default — a local dashboard has no place in a deployment.

It is the wrong default for a self-hosted install with no external telemetry service, where the
dashboard is the only place to read logs and traces. Opt in:

```csharp
var otlpKey = builder.AddParameter("dashboard-otlp-key", secret: true);
var browserToken = builder.AddParameter("dashboard-browser-token", secret: true);
var dashboardDomain = "dashboard.example.com";

dokploy.WithDokployDashboard(dashboard =>
{
    dashboard
        .WithHostPort(18888)
        .WithForwardedHeaders(true)

        // Ingest auth. The default is Unsecured.
        .WithEnvironment("Dashboard__Otlp__AuthMode", "ApiKey")
        .WithEnvironment("Dashboard__Otlp__PrimaryApiKey", otlpKey)

        // Browser auth. Pin the token or it regenerates on every restart.
        .WithEnvironment("Dashboard__Frontend__AuthMode", "BrowserToken")
        .WithEnvironment("Dashboard__Frontend__BrowserToken", browserToken)

        // Required together behind a proxy — see below. You do not need to list the dashboard's own
        // service name here; WithDokployDashboard appends it, without which every sender is
        // silently rejected.
        .WithEnvironment("AllowedHosts", $"{dashboardDomain};localhost;127.0.0.1")
        .WithEnvironment("Dashboard__Frontend__PublicUrl", $"https://{dashboardDomain}")

        // Optional: omit to keep it internal-only.
        .WithDokployDomain(dokploy, dashboardDomain, port: 18888);
});

// or, equivalently, in the settings lambda:
//   settings.DeployDashboard = true;
```

Every sender also needs the ingest key, or its telemetry is dropped once OTLP auth is on:

```csharp
var otlpHeaders = ReferenceExpression.Create($"x-otlp-api-key={otlpKey.Resource}");
service.WithEnvironment("OTEL_EXPORTER_OTLP_HEADERS", otlpHeaders);
```

Put it on **every** sender. Aspire instruments gateway/proxy services too, and a service missing the
header has its telemetry rejected silently.

The dashboard then becomes an ordinary Dokploy application: it gets an app name, and the other
services' `OTEL_EXPORTER_OTLP_ENDPOINT` resolves to it like any other service reference.

`WithDokployDashboard` exists because `WithDashboard` is declared on
`IResourceBuilder<DockerComposeEnvironmentResource>`, and `PublishToDokploy` creates that
environment internally — so a caller never holds the builder it needs.

### Behind a reverse proxy: three settings, three different failures

Giving the dashboard a domain needs all three. They guard different things and fail in ways that
look unrelated:

| Missing | Symptom |
|---|---|
| `AllowedHosts` (unset) | `400 Bad Request — Invalid Hostname` in the browser. Obvious, if cryptic |
| `AllowedHosts` (set, but missing the ingest host) | Dashboard looks perfect and stays permanently empty. Handled for you — see below |
| **forwarded headers** (`WithForwardedHeaders(true)`) | Page loads, then *"Rejecting Blazor WebSocket upgrade with disallowed Origin"* and the UI never connects |
| `Dashboard:Frontend:PublicUrl` | Dashboard works, but links it constructs — including the login URL it prints at startup — point at `localhost` |

The forwarded-headers one renders a working-looking dashboard with a dead live connection. The
second row is worse still: nothing looks wrong at all.

**Forwarded headers is the control, not `PublicUrl`.** The origin validator compares `Origin`
against the request's own scheme and host; with TLS terminating at the proxy the dashboard sees
`http`, so an `https` Origin mismatches unless `X-Forwarded-Proto` is honoured. Verified by running
the image four ways with `Origin: https://…` and `X-Forwarded-Proto: https`:

```
nothing extra                                    → rejected
ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED=true   → accepted
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true         → accepted
both + PublicUrl                                 → accepted
```

`PublicUrl` alone does not help — it is worth setting for the links, but it is not what unblocks
Blazor.

If you still see rejections with `WithForwardedHeaders(true)` set, the proxy is not sending
`X-Forwarded-Proto`. Confirm what actually reaches the container before changing dashboard
configuration.

### Silent telemetry loss: `AllowedHosts` also gates OTLP ingest

**`WithDokployDashboard` handles this for you** — it appends the dashboard's own service name to any
`AllowedHosts` you set. This section explains why that exists, because the failure it prevents is
invisible and the same trap applies to any dashboard you configure by other means.

Setting `AllowedHosts` to fix the browser 400 breaks telemetry unless the dashboard's own service
name is in the list, and nothing reports it.

ASP.NET Core host filtering is global to the application and runs **before authentication**, so the
allow-list added for the UI also governs the OTLP ingest ports. Senders reach the dashboard by its
Dokploy service name, which an allow-list of `domain;localhost;127.0.0.1` does not contain — so every
sender is rejected at the front door, on `18889` and `18890` alike.

Measured from inside the deployed network, with a valid `ExportLogsServiceRequest` on `/v1/logs`:

| `Host` header | API key | Result |
|---|---|---|
| service name | correct | `400` — body reads `Bad Request - Invalid Hostname` |
| `localhost` | correct | `200` |
| `localhost` | wrong | `401` |

Three consequences, each of which sends you the wrong way:

- **A `400` from an OTLP endpoint tells you nothing about the key.** A *deliberately wrong* key also
  returns `400`, because host filtering answers first. A `400` where you expected `401` reads like
  "reached, authenticated, body rejected" — that is, like a healthy endpoint and broken senders. It
  is neither.
- **Nothing is logged, on either side.** The dashboard treats it as a routine bad request, and the
  .NET OpenTelemetry SDK reports export failures on an `EventSource`, not through `ILogger`. The
  only symptom is an empty dashboard.
- **A TCP connect proves nothing.** The port accepts connections the whole time while rejecting
  every request on them, so `/dev/tcp` and `nc` checks come back clean.

`WithDokployDashboard` therefore appends the ingest host to whatever `AllowedHosts` you set,
including an explicit override — omitting it does not degrade the deployment, it disables telemetry
entirely. Listing it yourself is harmless; the entry is de-duplicated. The name is
`<PublishToDokploy name>-compose-dashboard`, read from the dashboard resource rather than rebuilt, so
it cannot drift from the value senders resolve.

An allow-list is **only** extended, never introduced: if you set no `AllowedHosts`, the dashboard
keeps its own default. Opting in to host filtering stays your decision.

The publisher substitutes service names in a `;`-separated list segment by segment, so the allow-list
entry and `OTEL_EXPORTER_OTLP_ENDPOINT` are rewritten to the Dokploy app name together.

#### Diagnosing it

Two requests separate "senders are broken" from "the dashboard is refusing them". Run them from any
container on the network:

```bash
# 1. CONTROL — a deliberately wrong key. 401 means auth ran; 400 means something answered first.
curl -sS -o /dev/null -w '%{http_code}\n' -XPOST \
  -H 'content-type: application/x-protobuf' -H 'x-otlp-api-key: definitely-wrong' \
  --data-binary '' http://<dashboard-service>:18890/v1/metrics

# 2. Same request with the Host forced to a value you know is allowed.
curl -sS -o /dev/null -w '%{http_code}\n' -XPOST -H 'Host: localhost' \
  -H 'content-type: application/x-protobuf' -H 'x-otlp-api-key: definitely-wrong' \
  --data-binary '' http://<dashboard-service>:18890/v1/metrics
```

`400` then `401` is conclusive: the endpoint is healthy, the key is being checked, and host filtering
is what stands between your senders and the dashboard. Add `-i` to read the body — it says
`Bad Request - Invalid Hostname` in plain text.

The general rule this cost us several days to learn: **a status code is evidence about a request, not
about a component.** Before concluding "the endpoint works, so the senders are at fault", send one
request you *expect* to fail. If it fails the same way, you have measured nothing.

### Security posture — what exposure does and does not risk

The headline guidance says not to expose the dashboard. The precise position is narrower:

- **Only the UI port is published.** The OTLP ingest ports (18889/18890) stay on the container
  network, so telemetry spoofing — the threat that guidance leads with — is not reachable from
  outside whether or not you set a domain.
- The UI sits behind a **256-bit browser token over TLS**. That is real security, not a token in
  name only.
- What remains: the token travels in the `/login?t=…` **query string** (browser history, proxy
  access logs, `Referer`); it is one shared secret with no per-user identity or audit; and the image
  comes from a **pre-release** repository.

Exposing it is therefore a tradeoff, not a defect — but if you do, add a second factor. Dokploy has
**Basic Authentication** built in (application → Advanced), which gives per-person credentials in
front of the shared token and, unlike an IP allowlist, does not break when your address changes.

If you do not need browser access from outside, keep it internal-only (omit the domain) and reach it
over an SSH tunnel:

```bash
ssh -N -L 18888:localhost:18888 user@docker-host
```

Note also that telemetry retention is bounded and in-memory — it is a live diagnostic window, not an
archive, and a restart loses it.

### Optional: keep sign-in alive across restarts

The dashboard stores its DataProtection keys on disk. Without a volume they are lost on every
restart, the auth cookie is invalidated, and operators re-open the `/login?t=…` URL — putting the
token through browser history and proxy logs more often than necessary.

```csharp
dashboard.WithDokployMount(dokploy, "/home/app", "myapp-dashboard-home");
```

Mount `/home/app`, **not** `/home/app/.aspnet/DataProtection-Keys`. The image runs as UID 1654 and
contains `/home/app` owned by 1654 but no `.aspnet` subtree, so the nested path yields a root-owned
directory the dashboard cannot write — and *every* page render then fails, because Blazor uses
DataProtection to encrypt component state. See [Mount a path that exists in the
image](#mount-a-path-that-exists-in-the-image).

This is optional. Without it the dashboard works; you just sign in again after each restart.

See [Aspire dashboard security considerations](https://aspire.dev/dashboard/security-considerations/)
and [dashboard configuration](https://aspire.dev/dashboard/configuration/).

## Examples

See `example/CosmicChimps.Aspire.AppHost/` for a complete working example: Redis, an API service and
a Blazor web frontend configured for Docker Stack deployment, plus

- **Aspire parameters** for every deployment setting ([#1](https://github.com/Cosmic-Chimps/aspire-hosting-dokploy/issues/1)) — the Dokploy URL and token, and the registry credentials;
- **the Aspire dashboard deployed**, with ingest and browser authentication, the two reverse-proxy settings, and the ingest header on each sender.

The example is built by CI, so it is compile-checked against the current API rather than being prose
that drifts.

## License

MIT

## Contributing

Contributions welcome! Please open an issue or PR on GitHub.

## Links

- [Dokploy Documentation](https://docs.dokploy.com)
- [.NET Aspire Documentation](https://learn.microsoft.com/dotnet/aspire/)
- [Docker Stack Documentation](https://docs.docker.com/engine/swarm/stack-deploy/)
- [Docker Compose Specification](https://docs.docker.com/compose/compose-file/)


