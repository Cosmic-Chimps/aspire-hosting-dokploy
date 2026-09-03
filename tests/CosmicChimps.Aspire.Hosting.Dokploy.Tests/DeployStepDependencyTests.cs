using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using CosmicChimps.Aspire.Hosting.Dokploy;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards what the <c>dokploy-deploy-{name}</c> pipeline step waits for.
/// </summary>
/// <remarks>
/// <para>
/// The step declared <c>DependsOn("publish")</c> and <c>DependsOn("build")</c> — the compose YAML
/// and the images — but not the compose environment's own <c>prepare-{name}-compose</c> step, which
/// is what resolves the YAML's <c>${VAR}</c> image placeholders into <c>.env</c>. So the deploy
/// raced it and read unresolved images.
/// </para>
/// <para>
/// The race is invisible wherever prepare is fast, which is why it went unnoticed. Measured on
/// 2026-09-03 with the same publisher, registry settings and app, on two deployment targets:
/// prepare took <b>0.5 ms</b> on one (publisher read resolved values, correct images) and
/// <b>5 m 29 s</b> on the other (publisher read <c>""</c> 2.4 s in, wrote
/// <c>cosmic-chimps/:latest</c> for every service, and reported success).
/// </para>
/// <para>
/// A timing-dependent bug cannot be pinned by a timing test, so this asserts the DECLARATION: the
/// dependency either exists or it does not.
/// </para>
/// </remarks>
public class DeployStepDependencyTests
{
    private static PipelineStep BuildDeployStep(string appName)
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);

        var dokploy = builder.PublishToDokploy(appName, s =>
        {
            s.DokployUrl = "https://paas.example.com";
            s.ApiToken = "token-value";
        });

        var annotation = Assert.Single(
            dokploy.Resource.Annotations.OfType<PipelineStepAnnotation>()
        );

        // The factory ignores its context, so a null is safe here and keeps the test free of
        // pipeline plumbing that would obscure what is being asserted.
        var steps = annotation.CreateStepsAsync(null!).GetAwaiter().GetResult();

        return Assert.Single(steps, s => s.Name == $"dokploy-deploy-{appName}");
    }

    [Fact]
    public void TheDeployStep_WaitsForTheComposeEnvironmentToBePrepared()
    {
        var step = BuildDeployStep("bella-baxter");

        Assert.Contains("prepare-bella-baxter-compose", step.DependsOnSteps);
    }

    [Fact]
    public void TheDependencyIsDerivedFromTheAppName()
    {
        // The compose environment is named "{app}-compose" by convention (the same convention the
        // publisher already relies on to no-op "docker-compose-up-{app}-compose"). A hard-coded
        // name would silently stop matching for any other app.
        var step = BuildDeployStep("other-app");

        Assert.Contains("prepare-other-app-compose", step.DependsOnSteps);
    }

    [Fact]
    public void TheExistingDependenciesAreStillDeclared()
    {
        // prepare is an addition, not a replacement: the images still have to be built and pushed,
        // and the compose YAML still has to be written.
        var step = BuildDeployStep("bella-baxter");

        Assert.Contains("publish", step.DependsOnSteps);
        Assert.Contains("build", step.DependsOnSteps);
        Assert.Contains("deploy", step.RequiredBySteps);
    }
}
