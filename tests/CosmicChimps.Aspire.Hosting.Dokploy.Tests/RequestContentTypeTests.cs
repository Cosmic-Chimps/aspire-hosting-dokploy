using System.Net.Sockets;
using System.Text;
using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards the request content type <b>on the wire</b>.
/// </summary>
/// <remarks>
/// <para>
/// Dokploy v0.30.3 answers <c>400</c> — <c>{"name":["expected string, received undefined"]}</c> — to
/// a POST sent as <c>application/json; charset=UTF-8</c>, and <c>200</c> to the identical body sent
/// as <c>application/json</c>. Its body parser matches the content type strictly and skips parsing
/// on the parameter, so every write silently fails.
/// </para>
/// <para>
/// This asserts against a raw TCP socket rather than the <c>HttpRequestMessage</c> on purpose:
/// Flurl re-adds the charset <i>after</i> a <c>BeforeCall</c> hook runs, so the header can read
/// correctly in-process while the wrong bytes go out. Only reading the socket catches that.
/// </para>
/// </remarks>
public class RequestContentTypeTests
{
    [Fact]
    public async Task PostRequests_SendApplicationJson_WithoutCharsetParameter()
    {
        using var server = new CapturingServer();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var client = new DokployApiClient(http, NullLogger<DokployApiClient>.Instance);

        await client.CreateProjectAsync(new CreateProjectRequest { Name = "guard-test" });

        var request = await server.Captured;
        var contentType = HeaderValue(request, "Content-Type");

        Assert.Equal("application/json", contentType);
        Assert.DoesNotContain("charset", contentType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostRequests_StillCarryTheBody()
    {
        // Paired with the header assertion so a future "fix" cannot satisfy the content type by
        // dropping the payload.
        using var server = new CapturingServer();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var client = new DokployApiClient(http, NullLogger<DokployApiClient>.Instance);

        await client.CreateProjectAsync(new CreateProjectRequest { Name = "guard-test" });

        var request = await server.Captured;

        var body = request.Split("\r\n\r\n", 2)[1];

        Assert.Contains("\"name\":\"guard-test\"", body);
        // Derived, not hard-coded: the assertion is "the declared length matches the bytes sent",
        // which is what would break if a future change dropped or truncated the payload.
        Assert.Equal(
            Encoding.UTF8.GetByteCount(body).ToString(),
            HeaderValue(request, "Content-Length")
        );
    }

    private static string HeaderValue(string rawRequest, string name)
    {
        var head = rawRequest.Split("\r\n\r\n")[0];
        var line = head.Split("\r\n")
            .FirstOrDefault(l => l.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase));
        return line?[(name.Length + 1)..].Trim() ?? string.Empty;
    }

    /// <summary>Accepts one request, returns a canned project.create response, exposes the raw bytes.</summary>
    private sealed class CapturingServer : IDisposable
    {
        private readonly TcpListener _listener;

        public CapturingServer()
        {
            _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            Captured = Task.Run(AcceptOneAsync);
        }

        public int Port { get; }

        public Task<string> Captured { get; }

        private async Task<string> AcceptOneAsync()
        {
            using var socket = await _listener.AcceptSocketAsync();
            var buffer = new byte[16 * 1024];
            var read = await socket.ReceiveAsync(buffer);
            var request = Encoding.UTF8.GetString(buffer, 0, read);

            const string body =
                """{"project":{"projectId":"p1","name":"guard-test"},"environment":{"environmentId":"e1"}}""";
            var response =
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            await socket.SendAsync(Encoding.UTF8.GetBytes(response));
            return request;
        }

        public void Dispose() => _listener.Stop();
    }
}
