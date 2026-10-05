using System.Net;
using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Flurl.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards the request timeout on native database deploys.
/// </summary>
/// <remarks>
/// <para>
/// <c>postgres.deploy</c> and its siblings block until Dokploy has pulled the image and the Swarm
/// service has converged. Under the default 100 s timeout a slow first pull failed the step while
/// Dokploy went on to succeed.
/// </para>
/// <para>
/// Timing is scaled down: a 200 ms per-call timeout against a 5 s server proves the per-call
/// timeout is what governs these calls, and the paired test proves a longer one is not cut short.
/// </para>
/// </remarks>
public class NativeDeployTimeoutTests
{
    public static TheoryData<string> NativeDeploys => ["redis", "mariadb", "mongo", "mysql", "postgres"];

    [Theory]
    [MemberData(nameof(NativeDeploys))]
    public async Task NativeDeploy_UsesNativeDeployTimeout(string type)
    {
        var client = Client(serverDelay: TimeSpan.FromSeconds(5), nativeTimeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<FlurlHttpTimeoutException>(() => DeployAsync(client, type));
    }

    [Theory]
    [MemberData(nameof(NativeDeploys))]
    public async Task NativeDeploy_OutlastingAShortTimeout_SucceedsWithinNativeDeployTimeout(string type)
    {
        var client = Client(serverDelay: TimeSpan.FromMilliseconds(300), nativeTimeout: TimeSpan.FromSeconds(30));

        await DeployAsync(client, type);
    }

    private static Task DeployAsync(DokployApiClient client, string type)
    {
        var ct = TestContext.Current.CancellationToken;
        return type switch
        {
            "redis" => client.DeployRedisAsync(new DeployRedisRequest { RedisId = "id" }, ct),
            "mariadb" => client.DeployMariaDbAsync(new DeployMariaDbRequest { MariaDbId = "id" }, ct),
            "mongo" => client.DeployMongoAsync(new DeployMongoRequest { MongoId = "id" }, ct),
            "mysql" => client.DeployMySqlAsync(new DeployMySqlRequest { MySqlId = "id" }, ct),
            "postgres" => client.DeployPostgresAsync(new DeployPostgresRequest { PostgresId = "id" }, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    private static DokployApiClient Client(TimeSpan serverDelay, TimeSpan nativeTimeout)
    {
        // Infinite, as the deploy step configures it: HttpClient.Timeout would otherwise cap the call.
        var http = new HttpClient(new DelayingHandler(serverDelay))
        {
            BaseAddress = new Uri("http://dokploy.test/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new DokployApiClient(http, NullLogger<DokployApiClient>.Instance)
        {
            NativeDeployTimeout = nativeTimeout,
        };
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
