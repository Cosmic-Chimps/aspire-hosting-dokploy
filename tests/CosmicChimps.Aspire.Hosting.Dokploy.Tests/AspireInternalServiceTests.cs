using CosmicChimps.Aspire.Hosting.Dokploy;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Which services are dropped as Aspire infrastructure before anything is created in Dokploy. The IMAGE decides:
/// a service name is the application's to choose. A name rule (<c>*-dashboard</c>) once silently skipped an
/// application's own job scheduler, <c>bella-jobs-dashboard</c> — built, pushed, never deployed, reported as a
/// single Information line.
/// </summary>
public class AspireInternalServiceTests
{
    private static DokployServiceDescriptor Service(string name, string image) => new() { Name = name, Image = image };

    [Theory]
    [InlineData("demo-aspire-compose-dashboard", "mcr.microsoft.com/dotnet/nightly/aspire-dashboard:latest")]
    [InlineData("anything-at-all", "mcr.microsoft.com/dotnet/aspire-dashboard:9.0")] // renaming does not smuggle it past
    [InlineData("dash", "myregistry.azurecr.io/mirror/ASPIRE-DASHBOARD:13.5")]
    public void The_Aspire_dashboard_is_recognised_by_its_image(string name, string image) =>
        Assert.True(DokployInfrastructure.IsAspireInternalService(Service(name, image)));

    [Theory]
    [InlineData("bella-jobs-dashboard", "ghcr.io/cosmic-chimps/bella-jobs-dashboard:aspire-deploy-20260924185745")]
    [InlineData("grafana-dashboard", "grafana/grafana:11.0.0")]
    [InlineData("admin-dashboard", "ghcr.io/acme/admin-dashboard:1.0")]
    public void An_application_service_named_like_a_dashboard_is_deployed(string name, string image) =>
        Assert.False(DokployInfrastructure.IsAspireInternalService(Service(name, image)));
}
