using AreTheyDeadOrNot.Api.Observability;
using AreTheyDeadOrNot.Core.Repositories;
using AreTheyDeadOrNot.Infrastructure.Services;
using Microsoft.Extensions.Caching.Hybrid;

namespace AreTheyDeadOrNot.Api.Workers;

/// <summary>
/// Periodically checks batches of living people (ordered by sitelinks DESC) against Wikidata P570
/// using keyset pagination so it never scans the full table or blocks read-replica queries.
/// </summary>
public sealed class DeathSyncWorker(
    IServiceScopeFactory scopeFactory,
    HybridCache cache,
    AppMetrics metrics,
    IConfiguration configuration,
    ILogger<DeathSyncWorker> logger) : BackgroundService
{
    private int? _lastSitelinks;
    private string? _lastQid;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("DeathSync:Enabled", false))
        {
            logger.LogInformation("DeathSyncWorker is disabled (set DeathSync:Enabled=true to enable).");
            return;
        }

        var batchSize = Math.Clamp(configuration.GetValue("DeathSync:BatchSize", 50), 1, 50);
        var intervalSeconds = Math.Clamp(configuration.GetValue("DeathSync:IntervalSeconds", 30), 5, 3600);

        logger.LogInformation(
            "DeathSyncWorker started (batchSize={BatchSize}, intervalSeconds={IntervalSeconds}).",
            batchSize,
            intervalSeconds);

        // Short startup delay so API readiness probes pass immediately
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        do
        {
            try
            {
                await ProcessNextBatchAsync(batchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DeathSyncWorker batch failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessNextBatchAsync(int batchSize, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeathSyncRepository>();
        var wikidata = scope.ServiceProvider.GetRequiredService<WikidataDeathClient>();

        var batch = await repo.GetNextAliveBatchAsync(_lastSitelinks, _lastQid, batchSize, ct);
        if (batch.Count == 0)
        {
            // Reached end of table; wrap cursor back to highest-sitelink people
            _lastSitelinks = null;
            _lastQid = null;
            return;
        }

        var last = batch[^1];
        _lastSitelinks = last.Sitelinks;
        _lastQid = last.Qid;

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

                // Targeted invalidation only for the changed person & recent-deaths list
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
