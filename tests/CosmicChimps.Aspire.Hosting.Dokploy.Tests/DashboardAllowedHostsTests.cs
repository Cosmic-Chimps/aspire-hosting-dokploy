using Aspire.Hosting.ApplicationModel;
using CosmicChimps.Aspire.Hosting.Dokploy;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards that deploying the dashboard cannot silently disable telemetry.
/// </summary>
/// <remarks>
/// <para>
/// Host filtering is global to the application and runs <b>before</b> authentication, so the
/// <c>AllowedHosts</c> an operator adds to fix the browser's <c>400 Bad Request - Invalid
/// Hostname</c> also gates both OTLP ingest ports. Senders arrive as the dashboard's service name;
/// if it is missing from the list, every one of them is rejected with a 400 before its API key or
/// payload is read.
/// </para>
/// <para>
/// Nothing reports it. The dashboard logs a routine bad request, and the .NET OpenTelemetry SDK
/// writes export failures to an <c>EventSource</c> rather than <c>ILogger</c>, so the symptom is a
/// dashboard that looks healthy and stays empty. It took several deployments to find, which is why
/// the package appends the host instead of documenting the requirement.
/// </para>
/// <para>
/// These exercise <c>AppendIngestHost</c> directly, because the dashboard resource is not created
/// until publish and so is absent from a built application model. End-to-end behaviour was confirmed
/// separately by publishing the example and reading the generated compose: the dashboard service key
/// is <c>demo-aspire-compose-dashboard</c> and its <c>AllowedHosts</c> ends with that same value,
/// with no entry for it in the example's own configuration.
/// </para>
/// </remarks>
public class DashboardAllowedHostsTests
{
    private const string IngestHost = "demo-aspire-compose-dashboard";

    /// <summary>
    /// The shape produced by <c>WithEnvironment(key, string)</c> — and the one an earlier
    /// implementation missed entirely, because it matched only on <see cref="string"/> and returned
    /// without touching anything. It failed exactly like the bug it was written to fix: no error, no
    /// log, no telemetry.
    /// </summary>
    [Fact]
    public async Task AppendsTheIngestHost_ToAReferenceExpression()
    {
        var current = ReferenceExpression.Create($"dash.example.com;localhost;127.0.0.1");

        var result = DokployResourceExtensions.AppendIngestHost(current, IngestHost);

        // Resolved, not inspected: Format is only the template — a literal is held as a value
        // provider and shows up there as "{0}". Asserting on the resolved value is what proves the
        // operator's own entries survive; the domain is how a browser reaches the dashboard, and
        // loopback is how an SSH tunnel does.
        var expression = Assert.IsType<ReferenceExpression>(result);
        Assert.Equal(
            $"dash.example.com;localhost;127.0.0.1;{IngestHost}",
            await expression.GetValueAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public void AppendsTheIngestHost_ToAPlainString()
    {
        var result = DokployResourceExtensions.AppendIngestHost(
            "dash.example.com;localhost;127.0.0.1",
            IngestHost
        );

        Assert.Equal($"dash.example.com;localhost;127.0.0.1;{IngestHost}", result);
    }

    [Fact]
    public void LeavesTheListAlone_WhenTheHostIsAlreadyThere()
    {
        var result = DokployResourceExtensions.AppendIngestHost(
            $"{IngestHost};localhost",
            IngestHost
        );

        Assert.Equal($"{IngestHost};localhost", result);
    }

    [Fact]
    public void MatchesWholeEntriesOnly_NotSubstrings()
    {
        // A different service whose name merely contains the ingest host must not be mistaken for
        // it, or the entry that matters is never added.
        var result = DokployResourceExtensions.AppendIngestHost(
            $"{IngestHost}-replica;localhost",
            IngestHost
        );

        Assert.Equal($"{IngestHost}-replica;localhost;{IngestHost}", result);
    }

    /// <summary>
    /// An unrecognised value is returned untouched rather than replaced, so a caller using a shape
    /// this does not understand keeps whatever they configured.
    /// </summary>
    [Fact]
    public void LeavesUnrecognisedValuesUntouched()
    {
        var current = new object();

        Assert.Same(current, DokployResourceExtensions.AppendIngestHost(current, IngestHost));
    }
}
