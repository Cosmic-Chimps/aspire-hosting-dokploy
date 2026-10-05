using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards the warning for <c>WithDokploy*</c> settings placed on a Dokploy-managed database.
/// </summary>
/// <remarks>
/// A postgres/redis/mysql/mariadb/mongo image is deployed as a Dokploy-managed database, whose
/// deploy path never reads mounts, domains, health checks or the other per-application settings.
/// The README's own volume example — <c>AddPostgres(...).WithDokployMount(...)</c> — did nothing,
/// and nothing said so.
/// </remarks>
public class NativeServiceSettingsTests
{
    private static readonly DokployServiceDescriptor Postgres = new()
    {
        Name = "postgres",
        Image = "docker.io/library/postgres:17",
        NativeServiceType = DokployNativeServiceType.Postgres,
    };

    private static readonly DokployServiceDescriptor Api = new()
    {
        Name = "api",
        Image = "ghcr.io/cosmic-chimps/api:1",
    };

    [Fact]
    public void MountOnANativeDatabase_IsReported()
    {
        var resource = new DokployResource("test");
        resource.Annotations.Add(Mount("postgres"));

        var warning = Assert.Single(DokployInfrastructure.FindSettingsIgnoredOnNativeServices(resource, [Postgres]));

        Assert.Contains("'postgres'", warning);
        Assert.Contains("WithDokployMount", warning);
        Assert.Contains("Postgres", warning);
    }

    [Fact]
    public void EverySettingOnTheService_IsNamedInOneWarning()
    {
        var resource = new DokployResource("test");
        resource.Annotations.Add(Mount("postgres"));
        resource.Annotations.Add(Mount("postgres", type: "bind"));
        resource.Annotations.Add(new DokployServiceSkipRedeployAnnotation { ServiceName = "postgres" });
        resource.Annotations.Add(new DokployServiceUpdateOrderAnnotation { ServiceName = "postgres", Order = "stop-first" });

        var warning = Assert.Single(DokployInfrastructure.FindSettingsIgnoredOnNativeServices(resource, [Postgres]));

        Assert.Contains("WithDokployMount", warning);
        Assert.Contains("WithDokployBindMount", warning);
        Assert.Contains("WithDokploySkipRedeploy", warning);
        Assert.Contains("WithDokployUpdateOrder", warning);
    }

    [Fact]
    public void SettingsOnAnApplication_AreNotReported()
    {
        var resource = new DokployResource("test");
        resource.Annotations.Add(Mount("api"));

        Assert.Empty(DokployInfrastructure.FindSettingsIgnoredOnNativeServices(resource, [Api, Postgres]));
    }

    [Fact]
    public void ANativeDatabaseWithoutSettings_IsNotReported()
    {
        Assert.Empty(DokployInfrastructure.FindSettingsIgnoredOnNativeServices(new DokployResource("test"), [Postgres]));
    }

    [Fact]
    public void ServiceNames_MatchCaseInsensitively()
    {
        // Service names are matched case-insensitively everywhere else in the deployer.
        var resource = new DokployResource("test");
        resource.Annotations.Add(Mount("Postgres"));

        Assert.Single(DokployInfrastructure.FindSettingsIgnoredOnNativeServices(resource, [Postgres]));
    }

    private static DokployServiceMountAnnotation Mount(string service, string type = "volume") =>
        new()
        {
            ServiceName = service,
            ContainerPath = type == "bind" ? "/backup" : "/var/lib/postgresql/data",
            VolumeName = type == "volume" ? "pg-data" : null,
            HostPath = type == "bind" ? "/srv/backup" : null,
            Type = type,
        };
}
