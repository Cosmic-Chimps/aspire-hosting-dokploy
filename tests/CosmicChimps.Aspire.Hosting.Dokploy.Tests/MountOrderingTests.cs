using System.Net;
using System.Text;
using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards that mounts reach Dokploy BEFORE <c>application.deploy</c>.
/// </summary>
/// <remarks>
/// Mounts used to be created after the deploy was triggered. Dokploy's queue worker reads an
/// application's mounts from its database when it runs the job, so a new volume or a changed file
/// mount could miss the deploy that was meant to apply it and only take effect on the next one —
/// a first deploy of a database-backed service without its data volume.
/// </remarks>
public class MountOrderingTests
{
    private const string Image = "ghcr.io/cosmic-chimps/api:1";

    [Fact]
    public async Task Mounts_AreCreated_BeforeTheDeployIsTriggered()
    {
        var handler = new RecordingHandler();
        var resource = ResourceWithMount();

        var pending = await Deploy(resource, handler);

        Assert.NotNull(pending);
        var mount = handler.Calls.IndexOf("POST mounts.create");
        var deploy = handler.Calls.IndexOf("POST application.deploy");
        Assert.True(mount >= 0, Describe(handler));
        Assert.True(deploy >= 0, Describe(handler));
        Assert.True(mount < deploy, $"mounts.create must precede application.deploy. {Describe(handler)}");
    }

    [Fact]
    public async Task SkippedRedeploy_StillAppliesMounts_AndDoesNotDeploy()
    {
        // WithDokploySkipRedeploy + already running the same image → no deploy, but the mount
        // configuration must still be brought up to date, as it was before the reordering.
        var handler = new RecordingHandler
        {
            ApplicationOne = $$"""{"applicationStatus":"running","dockerImage":"{{Image}}"}""",
        };
        var resource = ResourceWithMount();
        resource.Annotations.Add(new DokployServiceSkipRedeployAnnotation { ServiceName = "api" });

        var pending = await Deploy(resource, handler);

        Assert.Null(pending);
        Assert.Contains("POST mounts.create", handler.Calls);
        Assert.DoesNotContain("POST application.deploy", handler.Calls);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DokployResource ResourceWithMount()
    {
        var resource = new DokployResource("test");
        resource.Annotations.Add(
            new DokployServiceMountAnnotation
            {
                ServiceName = "api",
                ContainerPath = "/data",
                VolumeName = "api-data",
            }
        );
        return resource;
    }

    private static Task<PendingDeployment?> Deploy(DokployResource resource, RecordingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://dokploy.test/") };
        var apiClient = new DokployApiClient(http, NullLogger<DokployApiClient>.Instance);
        var infrastructure = new DokployInfrastructure(
            NullLogger<DokployInfrastructure>.Instance,
            new EmptyServiceProvider()
        );

        return infrastructure.ConfigureAndDeployApplicationAsync(
            new DokployServiceDescriptor { Name = "api", Image = Image },
            "app-1",
            envString: null,
            resource,
            apiClient,
            new Dictionary<string, DokployDomainAnnotation>(),
            new Dictionary<string, HealthCheckSwarm>(),
            new Dictionary<string, long>(),
            new Dictionary<string, string>(),
            "run-1",
            TestContext.Current.CancellationToken
        );
    }

    private static string Describe(RecordingHandler handler) =>
        "Calls: " + string.Join(" → ", handler.Calls);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Answers every Dokploy endpoint with a minimal body and records the call order.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        public string ApplicationOne { get; init; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var endpoint = request.RequestUri!.AbsolutePath.Split('/').Last();
            Calls.Add($"{request.Method} {endpoint}");

            var body = endpoint switch
            {
                "application.one" => ApplicationOne,
                "deployment.all" or "mounts.listByServiceId" => "[]",
                _ => "{}",
            };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
