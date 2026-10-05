# CosmicChimps.Aspire.Hosting.Dokploy

Deploy an Aspire application to a self-hosted [Dokploy](https://dokploy.com) with `aspire deploy`.

## How it works

Every Aspire resource becomes its own Dokploy service, created and updated through Dokploy's REST
API:

| Aspire resource | Becomes in Dokploy |
|---|---|
| Project or container | An **application** (Docker provider) pulling the image Aspire pushed |
| `postgres`, `redis`, `mysql`, `mariadb` or `mongo` image | A **managed database**; see [Native databases](#native-databases) |
| The Aspire dashboard | Nothing, unless you [opt in](#deploying-the-aspire-dashboard-opt-in) |

`aspire deploy` builds and pushes your images and generates a compose file. This package reads that
file and brings Dokploy in line with it:

1. finds or creates the project and environment,
2. creates or updates each service,
3. sets its image, environment variables, domains and mounts,
4. triggers the deploy and [waits for it to finish](#waiting-for-deploys-to-finish).

Re-running is idempotent: services are matched by name, so a second deploy updates them in place
rather than creating duplicates.

**Dokploy is treated as shared with the people who use its UI.** Environment variables set there by
hand (a Stripe key, say) survive every redeploy, except under prefixes the AppHost owns outright
(`ReplacedEnvPrefixes`, by default the YARP `REVERSEPROXY__` family). No service is ever deleted.

## Installation

```bash
dotnet add package CosmicChimps.Aspire.Hosting.Dokploy
```

## Quick start

```csharp
using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;

var builder = DistributedApplication.CreateBuilder(args);

var dokployUrl   = builder.AddParameter("dokploy-url");
var dokployToken = builder.AddParameter("dokploy-token", secret: true);
var registryUser = builder.AddParameter("registry-username");
var registryPw   = builder.AddParameter("registry-password", secret: true);

// Aspire builds and pushes the images here. The push uses your ambient `docker login`.
#pragma warning disable ASPIRECOMPUTE003
builder.AddContainerRegistry("registry", endpoint: "ghcr.io", repository: "myorg");
#pragma warning restore ASPIRECOMPUTE003

var dokploy = builder.PublishToDokploy("myapp", s =>
{
    s.DokployUrl      = dokployUrl.AsDokployValue();
    s.ApiToken        = dokployToken.AsDokployValue();
    s.EnvironmentName = "production";   // created if missing; this is the default

    // How Dokploy pulls the images. ImagePrefix must match the registry's repository above.
    s.Registry = new RegistryCredentials
    {
        RegistryUrl = "ghcr.io",
        ImagePrefix = "ghcr.io/myorg",
        Username    = registryUser.AsDokployValue(),
        Password    = registryPw.AsDokployValue(),
    };
});

var db = builder.AddPostgres("postgres").AddDatabase("appdb");   // → Dokploy-managed Postgres

builder.AddProject<Projects.Api>("api")
       .WithReference(db)
       .WaitFor(db)
       .WithDokployDomain(dokploy, "api.example.com", port: 8080); // HTTPS + Let's Encrypt

builder.Build().Run();
```

Then run `aspire deploy`. Parameters left unset are prompted for. The Dokploy API token is generated
at **Settings → Profile → API/CLI**. Every setting accepts a literal or an Aspire parameter; see
[Configuring with Aspire parameters](#configuring-with-aspire-parameters).

`example/CosmicChimps.Aspire.AppHost/` is a complete, CI-built version of this, including the
deployed dashboard.

## Per-service settings

Called on any resource, passing the builder returned by `PublishToDokploy`:

| Method | Effect in Dokploy |
|---|---|
| `WithDokployDomain(dokploy, host, https, certificateType, port)` | Domain on the application, created or updated. **Pass `port`**: without it Dokploy routes to 3000, while .NET images listen on 8080 |
| `WithDokployMount(dokploy, containerPath, volumeName)` | Named volume that survives redeploys; see [Volumes](#volumes) |
| `WithDokployBindMount(dokploy, hostPath, containerPath)` | Bind mount from the Docker host |
| `WithDokployHealthCheck(dokploy, cmd, interval, timeout, startPeriod, retries)` | Swarm health check |
| `WithDokployStopGracePeriod(dokploy, duration)` | Swarm stop grace period |
| `WithDokployUpdateOrder(dokploy, "stop-first" \| "start-first")` | Swarm rolling-update order |
| `WithDokploySkipRedeploy(dokploy)` | Skip the redeploy when the running image is unchanged (by tag, then digest) |
| `WithDokployStatefulService(dokploy)` | Skip-redeploy + `stop-first` + a stop grace period, for single-replica stateful apps |
| `WithDokployNoSubstitution(dokploy, keys...)` | Keep these env values verbatim, never rewriting service names in them |
| `WithDokployExclude(dokploy)` | Leave the service out of the deploy (references to it still resolve) |

These apply to **applications only**. On a [native database](#native-databases) they have no
effect, and the deploy logs a warning saying so.

**Compose `deploy:` settings are not forwarded.** Replicas, placement, resource limits, Swarm labels
and restart policy set through `PublishAsDockerComposeService` are ignored, because Dokploy services
are created through its API rather than from the compose file. Set them in the Dokploy UI for now.

## Native databases

A service whose image is `postgres`, `redis`, `mysql`, `mariadb` or `mongo` (the last path segment,
so `docker.io/library/postgres:17` counts) becomes a **Dokploy-managed database**, not an
application:

- **Credentials** come from the service's own environment (`POSTGRES_PASSWORD`, `REDIS_PASSWORD`, …),
  which Aspire fills from its generated password parameter. The connection strings Aspire gives
  consumers therefore match. Database name and user come from `POSTGRES_DB` / `POSTGRES_USER` and
  their equivalents.
- **Consumers** reach it by its Dokploy app name. References are rewritten for you: `Host=postgres`
  in a connection string becomes `Host=<the database's app name>`.
- **Storage** is Dokploy's own: every managed database gets a persistent `<appName>-data` volume
  when it is created. You need no `WithDokployMount`.
- **Created once, then only redeployed.** An existing database is never recreated, so a password
  changed in the AppHost after the first deploy is **not** applied to it.
- **Failures surface directly.** Dokploy's database deploy runs inside the API call, so an error
  fails the step on the spot. That call is allowed `DeploymentTimeout` (10 minutes by default),
  since a first deploy pulls the image.

The `WithDokploy*` settings above don't apply here. To run a database as an ordinary application
instead (for your own mounts or health checks), use an image whose name isn't on the list, such as
`pgvector/pgvector`.

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

## Waiting for deploys to finish

`application.deploy` only **queues** a deploy in Dokploy. By default the deploy step then waits for
each application deploy to finish, and fails if any ends in `error` or `cancelled`. An image that
can't be pulled, or a Swarm update that fails, shows up as a red step rather than a down service.

Each failure is logged with Dokploy's error message and the last 50 lines of the deployment log:

```
api deploy finished with status 'error': ...
  deployment id: x7Kq...
  last log lines:
  ...
```

The waits run concurrently after every service is configured, so the step takes as long as the
slowest deploy, not the sum. Native databases are not polled: their deploy call already blocks until
it finishes and fails the step on its own.

```csharp
builder.PublishToDokploy("myapp", s =>
{
    s.DeploymentTimeout = TimeSpan.FromMinutes(20); // default 10 minutes, per deploy
    // s.WaitForDeployments = false;                // fire-and-forget, the old behaviour
});
```

A deploy still running at the timeout fails the step, but it is **not** cancelled in Dokploy. `done`
means Dokploy finished the deploy, not that the app is healthy; use `WithDokployHealthCheck` for that.

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

Use `WithDokployMount` for anything in an **application** that must survive a redeploy:

```csharp
builder.AddSeq("seq")
       .WithDokployMount(dokploy, "/data", "myapp-seq-data");
```

**`WithDataVolume()` is not enough.** Dokploy application services run on Docker Swarm, which does
not honour it — the container comes up healthy on empty storage and the deploy reports success. A
service that starts on an empty volume is a data-loss event, not a first run, so register the volume
through Dokploy's own mounts API with `WithDokployMount`.

Mounts are created **before** the deploy is triggered, so a new volume is in place for the deploy
that introduces it.

**Not for native databases.** `AddPostgres`, `AddRedis` and the others become Dokploy-managed
databases, which already get a persistent volume; a `WithDokployMount` on them has no effect and
logs a warning. See [Native databases](#native-databases). To mount into a database you run as an
application, use an image outside the native list.

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
an image containing `aspire-dashboard` (by image only — an application service whose NAME ends in
`-dashboard`, such as `jobs-dashboard`, is deployed like any other). Every environment
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

See `example/CosmicChimps.Aspire.AppHost/` for a complete working example: a Dokploy-managed Redis,
an API service, Seq and a Blazor web frontend with a domain, plus

- **Aspire parameters** for every deployment setting ([#1](https://github.com/Cosmic-Chimps/aspire-hosting-dokploy/issues/1)) — the Dokploy URL and token, and the registry credentials;
- **the Aspire dashboard deployed**, with ingest and browser authentication, the two reverse-proxy settings, and the ingest header on each sender.

The example is built by CI, so it is compile-checked against the current API rather than being prose
that drifts.

## License

[MIT](LICENSE)

## Contributing

Contributions welcome! Please open an issue or PR on GitHub.

## Links

- [Dokploy Documentation](https://docs.dokploy.com)
- [Dokploy API reference](https://docs.dokploy.com/docs/api)
- [Aspire Documentation](https://aspire.dev)


