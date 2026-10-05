using System.Diagnostics;
using CosmicChimps.Aspire.Hosting.Dokploy.Models;
using Flurl.Http;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// An application deploy that has been triggered and not yet seen to finish.
/// </summary>
/// <param name="Title">The unique title sent with <c>application.deploy</c>.</param>
/// <param name="PriorDeploymentIds">
/// Deployment IDs that existed before the trigger, or <c>null</c> if they could not be listed.
/// Only used as a fallback for a Dokploy that drops the title; with <c>null</c> there is no safe
/// fallback, because any pre-existing <c>done</c> row would be mistaken for this deploy.
/// </param>
internal sealed record PendingDeployment(
    string ServiceName,
    string ApplicationId,
    string Title,
    IReadOnlySet<string>? PriorDeploymentIds
);

internal enum DeploymentOutcomeKind
{
    Succeeded,
    Failed,
    TimedOut,
}

internal sealed record DeploymentOutcome(
    PendingDeployment Pending,
    DeploymentOutcomeKind Kind,
    string? DeploymentId,
    string? Status,
    string? ErrorMessage,
    string? LogTail
);

/// <summary>
/// Polls Dokploy until a triggered application deploy reaches a terminal status.
/// </summary>
/// <remarks>
/// <para>
/// <c>application.deploy</c> only enqueues a job and returns, so without this a deploy whose image
/// cannot be pulled, or whose Swarm update fails, is reported as deployed. The deploy step would go
/// green while the service is down.
/// </para>
/// <para>
/// The API calls are injected so the polling rules can be tested without a server.
/// </para>
/// </remarks>
internal sealed class DokployDeploymentWaiter(
    Func<string, CancellationToken, Task<IReadOnlyList<DeploymentListItem>>> listDeployments,
    Func<string, int, CancellationToken, Task<string>> readLogs,
    ILogger logger,
    TimeSpan pollInterval
)
{
    internal const int LogTailLines = 50;

    internal async Task<DeploymentOutcome> WaitAsync(
        PendingDeployment pending,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var stopwatch = Stopwatch.StartNew();
        DeploymentListItem? last = null;

        while (true)
        {
            try
            {
                var deployments = await listDeployments(pending.ApplicationId, ct);
                last = FindDeployment(deployments, pending) ?? last;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                // One failed poll is not a failed deploy. A persistent fault still ends at the timeout.
                logger.LogWarning(
                    "Polling deployment status for '{Service}' failed ({Error}) — retrying",
                    pending.ServiceName,
                    ex.Message
                );
            }

            switch (last?.Status?.ToLowerInvariant())
            {
                case "done":
                    return Outcome(pending, DeploymentOutcomeKind.Succeeded, last, logTail: null);
                case "error":
                case "cancelled":
                    return Outcome(
                        pending,
                        DeploymentOutcomeKind.Failed,
                        last,
                        await TryReadLogTailAsync(pending, last.DeploymentId, ct)
                    );
            }

            if (stopwatch.Elapsed >= timeout)
                return Outcome(
                    pending,
                    DeploymentOutcomeKind.TimedOut,
                    last,
                    last is null ? null : await TryReadLogTailAsync(pending, last.DeploymentId, ct)
                );

            await Task.Delay(pollInterval, ct);
        }
    }

    /// <summary>
    /// The row for THIS deploy: by title first, else the newest row created since the trigger.
    /// </summary>
    internal static DeploymentListItem? FindDeployment(
        IEnumerable<DeploymentListItem> deployments,
        PendingDeployment pending
    )
    {
        var list = deployments.ToList();

        var byTitle = list
            .Where(d => string.Equals(d.Title, pending.Title, StringComparison.Ordinal))
            .MaxBy(d => d.CreatedAt, StringComparer.Ordinal);
        if (byTitle is not null || pending.PriorDeploymentIds is null)
            return byTitle;

        return list
            .Where(d => d.DeploymentId is not null && !pending.PriorDeploymentIds.Contains(d.DeploymentId))
            .MaxBy(d => d.CreatedAt, StringComparer.Ordinal);
    }

    private async Task<string?> TryReadLogTailAsync(
        PendingDeployment pending,
        string? deploymentId,
        CancellationToken ct
    )
    {
        if (deploymentId is null)
            return null;

        // Best effort: the log is the diagnosis, but failing to fetch it must not mask the failure.
        try
        {
            var log = await readLogs(deploymentId, LogTailLines, ct);
            return string.IsNullOrWhiteSpace(log) ? null : log.TrimEnd();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(
                ex,
                "Could not read deployment log {DeploymentId} for '{Service}'",
                deploymentId,
                pending.ServiceName
            );
            return null;
        }
    }

    private static DeploymentOutcome Outcome(
        PendingDeployment pending,
        DeploymentOutcomeKind kind,
        DeploymentListItem? deployment,
        string? logTail
    ) =>
        new(
            pending,
            kind,
            deployment?.DeploymentId,
            deployment?.Status,
            string.IsNullOrWhiteSpace(deployment?.ErrorMessage) ? null : deployment.ErrorMessage,
            logTail
        );

    /// <summary>Transport failures and 5xx are retried; a 4xx (bad token, gone app) is not.</summary>
    internal static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex switch
        {
            FlurlHttpException f => f.StatusCode is null or >= 500,
            HttpRequestException => true,
            OperationCanceledException => !ct.IsCancellationRequested,
            _ => false,
        };
}
