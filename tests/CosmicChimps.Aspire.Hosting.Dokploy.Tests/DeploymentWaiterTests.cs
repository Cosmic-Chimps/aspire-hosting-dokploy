using CosmicChimps.Aspire.Hosting.Dokploy;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards the wait for a queued application deploy to finish.
/// </summary>
/// <remarks>
/// <para>
/// <c>application.deploy</c> only enqueues the job. Before this wait existed, a deploy whose image
/// could not be pulled was reported as "Deployed" and the step went green with the service down.
/// </para>
/// <para>
/// The case most worth pinning is the fallback match: when Dokploy drops the title, the waiter must
/// never mistake a PREVIOUS run's <c>done</c> row for this deploy, or a failed deploy reads as success.
/// </para>
/// </remarks>
public class DeploymentWaiterTests
{
    private const string Title = "Aspire deploy of api (run-1)";
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Done_IsSuccess()
    {
        var waiter = Waiter(Polls([Row("d1", Title, "done")]));

        var outcome = await waiter.WaitAsync(Pending(), LongTimeout, Ct);

        Assert.Equal(DeploymentOutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal("d1", outcome.DeploymentId);
    }

    [Fact]
    public async Task KeepsPolling_WhileQueuedOrRunning()
    {
        var waiter = Waiter(Polls([], [Row("d1", Title, "running")], [Row("d1", Title, "done")]));

        var outcome = await waiter.WaitAsync(Pending(), LongTimeout, Ct);

        Assert.Equal(DeploymentOutcomeKind.Succeeded, outcome.Kind);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("cancelled")]
    public async Task ErrorOrCancelled_IsFailure_WithMessageAndLogTail(string status)
    {
        var waiter = Waiter(
            Polls([Row("d1", Title, status, error: "pull access denied")]),
            readLogs: (id, _, _) => Task.FromResult($"log of {id}\n")
        );

        var outcome = await waiter.WaitAsync(Pending(), LongTimeout, Ct);

        Assert.Equal(DeploymentOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("pull access denied", outcome.ErrorMessage);
        Assert.Equal("log of d1", outcome.LogTail);
        Assert.Contains("pull access denied", DokployInfrastructure.DescribeOutcome(outcome, LongTimeout));
    }

    [Fact]
    public async Task UnreadableLog_DoesNotMaskTheFailure()
    {
        var waiter = Waiter(
            Polls([Row("d1", Title, "error")]),
            readLogs: (_, _, _) => throw new InvalidOperationException("log endpoint broken")
        );

        var outcome = await waiter.WaitAsync(Pending(), LongTimeout, Ct);

        Assert.Equal(DeploymentOutcomeKind.Failed, outcome.Kind);
        Assert.Null(outcome.LogTail);
    }

    [Fact]
    public async Task StillRunning_AtTimeout_IsTimedOut()
    {
        var waiter = Waiter(Polls([Row("d1", Title, "running")]));

        var outcome = await waiter.WaitAsync(Pending(), ShortTimeout, Ct);

        Assert.Equal(DeploymentOutcomeKind.TimedOut, outcome.Kind);
        Assert.Equal("running", outcome.Status);
        Assert.Contains("was not cancelled", DokployInfrastructure.DescribeOutcome(outcome, ShortTimeout));
    }

    [Fact]
    public async Task NeverAppearing_IsTimedOut_AndSaysSo()
    {
        var waiter = Waiter(Polls(Array.Empty<DeploymentListItem>()));

        var outcome = await waiter.WaitAsync(Pending(), ShortTimeout, Ct);

        Assert.Equal(DeploymentOutcomeKind.TimedOut, outcome.Kind);
        Assert.Null(outcome.DeploymentId);
        Assert.Contains("never appeared", DokployInfrastructure.DescribeOutcome(outcome, ShortTimeout));
    }

    [Fact]
    public async Task OtherTitles_AreIgnored_WhenTheTitleMatches()
    {
        // A concurrent deploy (webhook, UI click) finishing first must not be taken for ours.
        var waiter = Waiter(
            Polls(
                [Row("other", "Manual deployment", "done", "2026-01-02"), Row("d1", Title, "running")],
                [Row("other", "Manual deployment", "done", "2026-01-02"), Row("d1", Title, "error")]
            )
        );

        var outcome = await waiter.WaitAsync(Pending(), LongTimeout, Ct);

        Assert.Equal(DeploymentOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("d1", outcome.DeploymentId);
    }

    [Fact]
    public void TitleDropped_FallsBackToTheNewRowSinceTheTrigger()
    {
        var rows = new[]
        {
            Row("old", "Manual deployment", "done", "2026-01-01"),
            Row("new", "Manual deployment", "error", "2026-01-02"),
        };

        var found = DokployDeploymentWaiter.FindDeployment(rows, Pending(prior: ["old"]));

        Assert.Equal("new", found?.DeploymentId);
    }

    [Fact]
    public void TitleDropped_WithoutASnapshot_NeverMatchesAnOldRow()
    {
        // No snapshot means "everything is new" would match last run's success. Must match nothing.
        var rows = new[] { Row("old", "Manual deployment", "done") };

        var found = DokployDeploymentWaiter.FindDeployment(rows, Pending(prior: null));

        Assert.Null(found);
    }

    [Fact]
    public async Task TransientPollFailure_IsRetried()
    {
        var calls = 0;
        var waiter = Waiter((_, _) =>
            ++calls == 1
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult<IReadOnlyList<DeploymentListItem>>([Row("d1", Title, "done")])
        );

        var outcome = await waiter.WaitAsync(Pending(), LongTimeout, Ct);

        Assert.Equal(DeploymentOutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NonTransientPollFailure_Propagates()
    {
        var waiter = Waiter((_, _) => throw new InvalidOperationException("unexpected"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => waiter.WaitAsync(Pending(), LongTimeout, Ct)
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PendingDeployment Pending(string[]? prior = null) =>
        new("api", "app-1", Title, prior?.ToHashSet(StringComparer.Ordinal));

    private static DeploymentListItem Row(
        string id,
        string title,
        string status,
        string createdAt = "2026-01-01",
        string? error = null
    ) =>
        new()
        {
            DeploymentId = id,
            Title = title,
            Status = status,
            CreatedAt = createdAt,
            ErrorMessage = error,
        };

    /// <summary>Returns each snapshot in turn, then repeats the last one forever.</summary>
    private static Func<string, CancellationToken, Task<IReadOnlyList<DeploymentListItem>>> Polls(
        params DeploymentListItem[][] snapshots
    )
    {
        var i = 0;
        return (_, _) =>
            Task.FromResult<IReadOnlyList<DeploymentListItem>>(snapshots[Math.Min(i++, snapshots.Length - 1)]);
    }

    private static DokployDeploymentWaiter Waiter(
        Func<string, CancellationToken, Task<IReadOnlyList<DeploymentListItem>>> listDeployments,
        Func<string, int, CancellationToken, Task<string>>? readLogs = null
    ) =>
        new(
            listDeployments,
            readLogs ?? ((_, _, _) => Task.FromResult(string.Empty)),
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(1)
        );
}
