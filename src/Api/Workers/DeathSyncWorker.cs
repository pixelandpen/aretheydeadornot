using AreTheyDeadOrNot.Api.Observability;
using AreTheyDeadOrNot.Core.Repositories;
using AreTheyDeadOrNot.Infrastructure.Services;
using Microsoft.Extensions.Caching.Hybrid;

namespace AreTheyDeadOrNot.Api.Workers;

/// <summary>
/// Tiered background sync against Wikidata P570:
/// - Priority Tier (sitelinks >= PriorityMinSitelinks): refreshed first every PriorityRefreshHours (e.g. daily).
/// - Long-Tail Tier (sitelinks < PriorityMinSitelinks): scanned continuously between priority cycles.
/// Uses keyset pagination on idx_people_alive_sitelinks and honors Wikidata Retry-After backoff.
/// </summary>
public sealed class DeathSyncWorker(
    IServiceScopeFactory scopeFactory,
    HybridCache cache,
    AppMetrics metrics,
    IConfiguration configuration,
    ILogger<DeathSyncWorker> logger) : BackgroundService
{
    private enum SyncTier { Priority, LongTail }

    private SyncTier _tier = SyncTier.Priority;
    private DateTimeOffset _lastPriorityStartUtc = DateTimeOffset.UtcNow;

    private int? _priorityLastSitelinks;
    private string? _priorityLastQid;

    private int? _longTailLastSitelinks;
    private string? _longTailLastQid;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("DeathSync:Enabled", false))
        {
            logger.LogInformation("DeathSyncWorker is disabled (set DeathSync:Enabled=true to enable).");
            return;
        }

        var batchSize = Math.Clamp(configuration.GetValue("DeathSync:BatchSize", 50), 1, 50);
        var intervalSeconds = Math.Clamp(configuration.GetValue("DeathSync:IntervalSeconds", 2), 1, 3600);
        var priorityMinSitelinks = Math.Max(configuration.GetValue("DeathSync:PriorityMinSitelinks", 10), 1);
        var priorityRefreshHours = Math.Clamp(configuration.GetValue("DeathSync:PriorityRefreshHours", 24), 1, 168);

        logger.LogInformation(
            "DeathSyncWorker started (batchSize={BatchSize}, intervalSeconds={IntervalSeconds}, priorityMinSitelinks={MinSitelinks}, priorityRefreshHours={RefreshHours}).",
            batchSize,
            intervalSeconds,
            priorityMinSitelinks,
            priorityRefreshHours);

        // Short startup delay so readiness probes pass immediately
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        do
        {
            try
            {
                if (_tier == SyncTier.LongTail &&
                    DateTimeOffset.UtcNow - _lastPriorityStartUtc >= TimeSpan.FromHours(priorityRefreshHours))
                {
                    logger.LogInformation("Starting daily Priority Tier sync (sitelinks >= {MinSitelinks}).", priorityMinSitelinks);
                    _tier = SyncTier.Priority;
                    _priorityLastSitelinks = null;
                    _priorityLastQid = null;
                    _lastPriorityStartUtc = DateTimeOffset.UtcNow;
                }

                await ProcessNextBatchAsync(batchSize, priorityMinSitelinks, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (WikidataRateLimitException ex)
            {
                logger.LogWarning(ex, "Wikidata rate limit hit; backing off for {Delay}s.", ex.RetryAfter.TotalSeconds);
                await Task.Delay(ex.RetryAfter, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DeathSyncWorker batch failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessNextBatchAsync(int batchSize, int priorityMinSitelinks, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeathSyncRepository>();
        var wikidata = scope.ServiceProvider.GetRequiredService<WikidataDeathClient>();

        IReadOnlyList<AlivePersonCursorItem> batch;
        if (_tier == SyncTier.Priority)
        {
            batch = await repo.GetNextAliveBatchAsync(
                _priorityLastSitelinks,
                _priorityLastQid,
                batchSize,
                minSitelinks: priorityMinSitelinks,
                cancellationToken: ct);

            if (batch.Count == 0)
            {
                logger.LogInformation("Priority Tier pass complete; resuming Long-Tail Tier.");
                _tier = SyncTier.LongTail;
                _priorityLastSitelinks = null;
                _priorityLastQid = null;
                return;
            }

            _priorityLastSitelinks = batch[^1].Sitelinks;
            _priorityLastQid = batch[^1].Qid;
        }
        else
        {
            batch = await repo.GetNextAliveBatchAsync(
                _longTailLastSitelinks,
                _longTailLastQid,
                batchSize,
                maxSitelinksExclusive: priorityMinSitelinks,
                cancellationToken: ct);

            if (batch.Count == 0)
            {
                logger.LogInformation("Long-Tail Tier pass complete; wrapping cursor.");
                _longTailLastSitelinks = null;
                _longTailLastQid = null;
                return;
            }

            _longTailLastSitelinks = batch[^1].Sitelinks;
            _longTailLastQid = batch[^1].Qid;
        }

        var deathDates = await wikidata.FetchDeathDatesAsync(batch.Select(b => b.Qid).ToList(), ct);
        var updatedCount = 0;

        foreach (var item in batch)
        {
            if (!deathDates.TryGetValue(item.Qid, out var deathDate))
            {
                continue;
            }

            if (await repo.MarkDeceasedAsync(item.Qid, deathDate, ct))
            {
                updatedCount++;
                logger.LogInformation(
                    "Marked {Qid} ({Name}) deceased as of {DeathDate}.",
                    item.Qid,
                    item.NormalizedName,
                    deathDate);

                await cache.RemoveAsync($"person:{item.Qid}", ct);
                if (!string.IsNullOrEmpty(item.NormalizedName))
                {
                    await cache.RemoveAsync($"search:{item.NormalizedName}", ct);
                }
            }
        }

        if (updatedCount > 0)
        {
            await cache.RemoveAsync("recent-deaths:8", ct);
            await cache.RemoveAsync("recent-deaths:12", ct);
        }

        metrics.RecordDeathSync(batch.Count, updatedCount);
    }
}
