using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards the deferred-configuration contract introduced for issue #1.
/// </summary>
/// <remarks>
/// A <see cref="DokployResource"/>'s string properties are empty until
/// <c>ResolveConfigurationAsync</c> runs. That is a real trap: a shipped release validated the
/// configuration <i>before</i> resolving it and failed every deploy with
/// "DokployUrl is not set on resource '…'" even though the URL was configured correctly. These tests
/// pin both halves — that resolution populates the properties, and that validation is only
/// meaningful afterwards.
/// </remarks>
public class ConfigurationResolutionTests
{
    private static IResourceBuilder<DokployResource> BuildTarget(
        Action<DokploySettings> configure,
        Action<IDistributedApplicationBuilder>? seed = null
    )
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        seed?.Invoke(builder);
        return builder.PublishToDokploy("test-app", configure);
    }

    [Fact]
    public async Task ResolveConfiguration_MaterialisesLiteralSettings()
    {
        var dokploy = BuildTarget(s =>
        {
            s.DokployUrl = "https://paas.example.com";
            s.ApiToken = "token-value";
            s.ProjectName = "explicit-project";
            s.EnvironmentName = "staging";
            s.AppNamePrefix = "tp-";
        });

        await dokploy.Resource.ResolveConfigurationAsync(CancellationToken.None);

        Assert.Equal("https://paas.example.com", dokploy.Resource.DokployUrl);
        Assert.Equal("token-value", dokploy.Resource.ApiToken);
        Assert.Equal("explicit-project", dokploy.Resource.ProjectName);
        Assert.Equal("staging", dokploy.Resource.EnvironmentName);
        Assert.Equal("tp-", dokploy.Resource.AppNamePrefix);
    }

    [Fact]
    public async Task ResolveConfiguration_MaterialisesAspireParameters()
    {
        // The point of issue #1: settings that are not knowable when the model is built.
        var dokploy = BuildTarget(
            s => { },
            builder =>
            {
                builder.Configuration["Parameters:dokploy-url"] = "https://from-parameter.example.com";
                builder.Configuration["Parameters:dokploy-token"] = "token-from-parameter";
            }
        );

        var host = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        host.Configuration["Parameters:dokploy-url"] = "https://from-parameter.example.com";
        host.Configuration["Parameters:dokploy-token"] = "token-from-parameter";
        var urlParam = host.AddParameter("dokploy-url");
        var tokenParam = host.AddParameter("dokploy-token", secret: true);
        var target = host.PublishToDokploy("test-app", s =>
        {
            s.DokployUrl = urlParam.AsDokployValue();
            s.ApiToken = tokenParam.AsDokployValue();
        });

        await target.Resource.ResolveConfigurationAsync(CancellationToken.None);

        Assert.Equal("https://from-parameter.example.com", target.Resource.DokployUrl);
        Assert.Equal("token-from-parameter", target.Resource.ApiToken);
    }

    [Fact]
    public async Task ResolveConfiguration_AppliesDefaults_WhenSettingsAreOmitted()
    {
        // Defaults cannot be applied when the model is built: a parameter's emptiness is not
        // knowable then. They belong in the resolve step, and this pins them there.
        var dokploy = BuildTarget(s =>
        {
            s.DokployUrl = "https://paas.example.com";
            s.ApiToken = "token-value";
        });

        await dokploy.Resource.ResolveConfigurationAsync(CancellationToken.None);

        Assert.Equal("test-app", dokploy.Resource.ProjectName); // falls back to the resource name
        Assert.Equal("production", dokploy.Resource.EnvironmentName);
    }

    [Fact]
    public void Properties_AreEmpty_BeforeResolution()
    {
        // This is the state that made the shipped ordering bug possible. If this ever starts
        // failing because the properties are eagerly populated, the ordering guard below becomes
        // moot — and that would be a good thing, but it must be a deliberate change.
        var dokploy = BuildTarget(s =>
        {
            s.DokployUrl = "https://paas.example.com";
            s.ApiToken = "token-value";
        });

        Assert.Equal(string.Empty, dokploy.Resource.DokployUrl);
        Assert.Equal(string.Empty, dokploy.Resource.ApiToken);
    }

    [Fact]
    public void Validate_Throws_WhenCalledBeforeResolution()
    {
        // The regression, reproduced exactly: correct configuration, validation run too early.
        var dokploy = BuildTarget(s =>
        {
            s.DokployUrl = "https://paas.example.com";
            s.ApiToken = "token-value";
        });

        var infrastructure = new DokployInfrastructure(
            NullLogger<DokployInfrastructure>.Instance,
            new EmptyServiceProvider()
        );

        var ex = Assert.Throws<InvalidOperationException>(
            () => infrastructure.Validate(dokploy.Resource)
        );
        Assert.Contains("DokployUrl is not set", ex.Message);
    }

    [Fact]
    public async Task Validate_Passes_AfterResolution()
    {
        var dokploy = BuildTarget(s =>
        {
            s.DokployUrl = "https://paas.example.com";
            s.ApiToken = "token-value";
        });

        await dokploy.Resource.ResolveConfigurationAsync(CancellationToken.None);

        var infrastructure = new DokployInfrastructure(
            NullLogger<DokployInfrastructure>.Instance,
            new EmptyServiceProvider()
        );

        infrastructure.Validate(dokploy.Resource); // must not throw
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
