using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using CosmicChimps.Aspire.Hosting.Dokploy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards the call ORDER inside <c>DeployAsync</c>: resolve first, validate second.
/// </summary>
/// <remarks>
/// <para>
/// A released version had these the other way round and failed every deploy with
/// "DokployUrl is not set on resource '…'" despite correct configuration, because validation
/// inspected properties that resolution had not yet populated.
/// </para>
/// <para>
/// The tests in <see cref="ConfigurationResolutionTests"/> call <c>Validate</c> directly, so they
/// pin the contract but pass either way — reintroducing the bug did not fail them. Only exercising
/// the real entry point catches it, which is what this does: with a fully configured resource,
/// <c>DeployAsync</c> must get past validation and fail later (on the missing compose artifacts).
/// If it reports missing configuration, the order has regressed.
/// </para>
/// </remarks>
public class DeployOrderingTests
{
    [Fact]
    public async Task DeployAsync_ResolvesConfiguration_BeforeValidatingIt()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.Configuration["Parameters:dokploy-url"] = "https://paas.example.com";
        var urlParameter = builder.AddParameter("dokploy-url");

        var dokploy = builder.PublishToDokploy("test-app", s =>
        {
            // Deferred on purpose: this is empty until the resolve step runs, which is exactly the
            // condition the regression tripped over.
            s.DokployUrl = urlParameter.AsDokployValue();
            s.ApiToken = "token-value";
        });

        var infrastructure = new DokployInfrastructure(
            NullLogger<DokployInfrastructure>.Instance,
            new EmptyServiceProvider()
        );

        var ex = await Record.ExceptionAsync(
            () => infrastructure.DeployAsync(dokploy.Resource, new NoOpReportingStep(), CancellationToken.None)
        );

        // It must fail — there are no publish artifacts here — but never for this reason.
        Assert.NotNull(ex);
        Assert.DoesNotContain("DokployUrl is not set", Flatten(ex));
        Assert.DoesNotContain("ApiToken is not set", Flatten(ex));
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Never reached: DeployAsync fails locating compose artifacts long before reporting.</summary>
    private sealed class NoOpReportingStep : IReportingStep
    {
        public Task CompleteAsync(string text, CompletionState state = default, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(MarkdownString text, CompletionState state = default, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReportingTask> CreateTaskAsync(string statusText, CancellationToken ct = default) =>
            throw new NotSupportedException("not reached");

        public Task<IReportingTask> CreateTaskAsync(MarkdownString statusText, CancellationToken ct = default) =>
            throw new NotSupportedException("not reached");

        public void Log(LogLevel level, string message) { }

        public void Log(LogLevel level, string message, bool isMarkdown) { }

        public void Log(LogLevel level, MarkdownString message) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
