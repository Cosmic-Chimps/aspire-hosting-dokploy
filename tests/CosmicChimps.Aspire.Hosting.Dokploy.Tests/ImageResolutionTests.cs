using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards the image reference written into a Dokploy service's Docker provider.
/// </summary>
/// <remarks>
/// <para>
/// On 2026-09-03 a deploy wrote <c>cosmic-chimps/:latest</c> as the image for all four of an app's
/// services and <b>reported success</b>. Dokploy stored it without complaint; nothing could pull it.
/// The value came from an empty image: the compose YAML still held an unresolved <c>${VAR}</c>
/// placeholder when <see cref="DokployComposeParser"/> read it, and the qualification logic happily
/// turned <c>""</c> into <c>&lt;prefix&gt;/:latest</c> — <c>name</c> empty, <c>tag</c> defaulted to
/// <c>latest</c>.
/// </para>
/// <para>
/// The logic had lived inline in a private method and had never been exercised by a test, which is
/// how a case this obvious survived. The empty case now throws; these tests pin that and the
/// surrounding qualification rules it sits in.
/// </para>
/// </remarks>
public class ImageResolutionTests
{
    private static ResolvedRegistryCredentials Registry(string? prefix) =>
        new() { ImagePrefix = prefix };

    // ── the defect ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyImage_IsRefused_RatherThanQualifiedIntoNonsense(string image)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DokployInfrastructure.ResolveImageReference("baxter-api", image, Registry("cosmic-chimps"))
        );

        // The message has to point at the cause, because the symptom (a service that will not start)
        // shows up somewhere else entirely, hours later.
        Assert.Contains("baxter-api", ex.Message);
        Assert.Contains("prepare", ex.Message);
    }

    [Fact]
    public void AnEmptyImage_IsRefused_EvenWithNoRegistry()
    {
        // Without a registry the old code fell through and saved "" verbatim — equally unpullable,
        // just without the misleading prefix.
        Assert.Throws<InvalidOperationException>(
            () => DokployInfrastructure.ResolveImageReference("baxter-api", "", registry: null)
        );
    }

    // ── the behaviour that must not regress ──────────────────────────────────

    [Theory]
    // bare local name → prefixed, tag preserved
    [InlineData("baxter-api:aspire-deploy-20260903", "cosmic-chimps", "cosmic-chimps/baxter-api:aspire-deploy-20260903")]
    // no tag → latest
    [InlineData("baxter-api", "cosmic-chimps", "cosmic-chimps/baxter-api:latest")]
    // trailing slash on the prefix is not doubled
    [InlineData("baxter-api:tag", "cosmic-chimps/", "cosmic-chimps/baxter-api:tag")]
    public void ALocalName_IsQualifiedWithTheRegistryPrefix(string image, string prefix, string expected)
    {
        Assert.Equal(
            expected,
            DokployInfrastructure.ResolveImageReference("baxter-api", image, Registry(prefix))
        );
    }

    [Theory]
    // Already carries a registry HOST (contains a dot) — leave it alone.
    [InlineData("ghcr.io/cosmic-chimps/baxter-api:tag")]
    // Host with a port (contains a colon before the first slash).
    [InlineData("localhost:5000/baxter-api:tag")]
    public void AnAlreadyQualifiedImage_IsLeftUntouched(string image)
    {
        Assert.Equal(
            image,
            DokployInfrastructure.ResolveImageReference("baxter-api", image, Registry("cosmic-chimps"))
        );
    }

    [Fact]
    public void AnOwnerSlashRepoName_IsStillPrefixed()
    {
        // "owner/repo" has a slash but no host, so it is still a local name as far as the registry
        // is concerned. Treating it as qualified would push to the wrong place.
        Assert.Equal(
            "cosmic-chimps/owner/repo:tag",
            DokployInfrastructure.ResolveImageReference("svc", "owner/repo:tag", Registry("cosmic-chimps"))
        );
    }

    [Fact]
    public void WithNoRegistryPrefix_ALocalNameIsPassedThroughUnchanged()
    {
        // Nothing to qualify with; the caller is warned elsewhere. It must not become "/name:tag".
        Assert.Equal(
            "baxter-api:tag",
            DokployInfrastructure.ResolveImageReference("baxter-api", "baxter-api:tag", registry: null)
        );

        Assert.Equal(
            "baxter-api:tag",
            DokployInfrastructure.ResolveImageReference("baxter-api", "baxter-api:tag", Registry(prefix: null))
        );
    }
}
