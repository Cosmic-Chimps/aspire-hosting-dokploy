using CosmicChimps.Aspire.Hosting.Dokploy;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// The env merge's replace-vs-preserve rule, pinned on the incident that produced it.
/// </summary>
/// <remarks>
/// <para>A Bella Baxter gateway deploy (2026-09-02) failed every request with
/// <c>AmbiguousMatchException: route2, route4</c>. The merge preserved
/// <c>REVERSEPROXY__ROUTES__route4__MATCH__PATH=/api/{**rest}</c> from an earlier five-route deploy;
/// the new deploy declared three routes and wrote its own <c>/api/{**rest}</c> at <c>route2</c>. Two
/// identical catch-alls, equal order, no error until the first request. Locally nothing reproduced —
/// there is no preserved environment on a developer machine.</para>
/// <para>The fix is scoped, not blanket: only prefixes the deploy owns outright are replaced as a
/// family. A blanket "first segment" rule would also purge a hand-set <c>ConnectionStrings__other</c>
/// whenever Aspire wrote a sibling, which is the behaviour the merge exists to protect.</para>
/// </remarks>
public class EnvMergeTests
{
    private static readonly string[] Yarp = ["REVERSEPROXY__"];

    [Fact]
    public void A_stale_positional_route_from_a_larger_deploy_is_dropped()
    {
        // Earlier deploy: five routes (cert-manager + scout + api). This deploy: three (cert-manager + api).
        var existing = string.Join('\n',
            "REVERSEPROXY__ROUTES__route0__MATCH__PATH=/api/v1/projects/{projectRef}/environments/{envSlug}/certificates",
            "REVERSEPROXY__ROUTES__route1__MATCH__PATH=/api/v1/projects/{projectRef}/environments/{envSlug}/certificates/{**rest}",
            "REVERSEPROXY__ROUTES__route2__MATCH__PATH=/api/v1/projects/{projectRef}/environments/{envSlug}/scout",
            "REVERSEPROXY__ROUTES__route3__MATCH__PATH=/api/v1/projects/{projectRef}/environments/{envSlug}/scout/{**rest}",
            "REVERSEPROXY__ROUTES__route4__MATCH__PATH=/api/{**rest}",
            "REVERSEPROXY__ROUTES__route4__CLUSTERID=cluster_bella-api",
            "REVERSEPROXY__CLUSTERS__cluster_scout-api__DESTINATIONS__destination1__ADDRESS=http://scout-api:8080",
            "Stripe__SecretKey=sk_live_hand_set");
        var aspire = string.Join('\n',
            "REVERSEPROXY__ROUTES__route0__MATCH__PATH=/api/v1/projects/{projectRef}/environments/{envSlug}/certificates",
            "REVERSEPROXY__ROUTES__route1__MATCH__PATH=/api/v1/projects/{projectRef}/environments/{envSlug}/certificates/{**rest}",
            "REVERSEPROXY__ROUTES__route2__MATCH__PATH=/api/{**rest}",
            "REVERSEPROXY__ROUTES__route2__CLUSTERID=cluster_bella-api",
            "REVERSEPROXY__CLUSTERS__cluster_bella-api__DESTINATIONS__destination1__ADDRESS=http://bella-api:8080");

        var result = DokployInfrastructure.MergeEnvStrings(existing, aspire, Yarp);
        var merged = Parse(result.Merged);

        // The collision is gone: exactly one /api/{**rest}, at the index this deploy declared.
        Assert.Equal("/api/{**rest}", merged["REVERSEPROXY__ROUTES__route2__MATCH__PATH"]);
        Assert.DoesNotContain("REVERSEPROXY__ROUTES__route3__MATCH__PATH", merged.Keys);
        Assert.DoesNotContain("REVERSEPROXY__ROUTES__route4__MATCH__PATH", merged.Keys);
        Assert.DoesNotContain("REVERSEPROXY__ROUTES__route4__CLUSTERID", merged.Keys);
        Assert.DoesNotContain("REVERSEPROXY__CLUSTERS__cluster_scout-api__DESTINATIONS__destination1__ADDRESS", merged.Keys);

        // The hand-set secret outside the replaced family is still preserved — the merge's whole point.
        Assert.Equal("sk_live_hand_set", merged["Stripe__SecretKey"]);

        Assert.Equal(4, result.DroppedStaleKeys.Count);
        Assert.Equal(1, result.PreservedCount);
    }

    [Fact]
    public void Keys_outside_a_replaced_prefix_are_still_preserved_even_when_aspire_writes_a_sibling()
    {
        // The reason the rule is scoped: Aspire sets one connection string, the operator hand-set another.
        var existing = "ConnectionStrings__bella-db=old\nConnectionStrings__reporting=hand-set\nStripe__SecretKey=sk";
        var aspire = "ConnectionStrings__bella-db=new";

        var merged = Parse(DokployInfrastructure.MergeEnvStrings(existing, aspire, Yarp).Merged);

        Assert.Equal("new", merged["ConnectionStrings__bella-db"]);
        Assert.Equal("hand-set", merged["ConnectionStrings__reporting"]);
        Assert.Equal("sk", merged["Stripe__SecretKey"]);
    }

    [Fact]
    public void A_replaced_prefix_only_acts_when_it_matches_and_matching_ignores_case()
    {
        var existing = "reverseproxy__ROUTES__route9__MATCH__PATH=/{**catchall}\nOther__Key=keep";
        var aspire = "REVERSEPROXY__ROUTES__route0__MATCH__PATH=/api/{**rest}";

        var result = DokployInfrastructure.MergeEnvStrings(existing, aspire, Yarp);
        var merged = Parse(result.Merged);

        Assert.DoesNotContain("reverseproxy__ROUTES__route9__MATCH__PATH", merged.Keys);
        Assert.Equal("keep", merged["Other__Key"]);
        Assert.Equal(["reverseproxy__ROUTES__route9__MATCH__PATH"], result.DroppedStaleKeys);
    }

    [Fact]
    public void With_no_replaced_prefixes_the_historical_merge_is_unchanged()
    {
        var existing = "REVERSEPROXY__ROUTES__route4__MATCH__PATH=/api/{**rest}\nA=1";
        var aspire = "REVERSEPROXY__ROUTES__route2__MATCH__PATH=/api/{**rest}\nA=2";

        var result = DokployInfrastructure.MergeEnvStrings(existing, aspire, []);
        var merged = Parse(result.Merged);

        Assert.Equal("/api/{**rest}", merged["REVERSEPROXY__ROUTES__route4__MATCH__PATH"]); // preserved, as before
        Assert.Equal("2", merged["A"]);
        Assert.Empty(result.DroppedStaleKeys);
    }

    [Fact]
    public void The_default_settings_replace_the_yarp_family()
    {
        var settings = new Models.DokploySettings();
        Assert.Equal(["REVERSEPROXY__"], settings.ReplacedEnvPrefixes);
    }

    private static Dictionary<string, string> Parse(string env) =>
        env.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
}
