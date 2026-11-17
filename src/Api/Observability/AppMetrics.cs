using System.Diagnostics.Metrics;

namespace AreTheyDeadOrNot.Api.Observability;

public sealed class AppMetrics
{
    public const string MeterName = "AreTheyDeadOrNot.Api";

    private readonly Counter<long> _searchRequests;
    private readonly Histogram<double> _searchDurationMs;
    private readonly Counter<long> _deathSyncChecked;
    private long _deathSyncUpdatedTotal;

    public AppMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _searchRequests = meter.CreateCounter<long>(
            "atdon_search_requests",
            description: "Total search requests");
        _searchDurationMs = meter.CreateHistogram<double>(
            "atdon_search_duration_ms",
            unit: "ms",
            description: "Search endpoint duration in milliseconds");
        _deathSyncChecked = meter.CreateCounter<long>(
            "atdon_death_sync_checked",
            description: "Total alive records checked against Wikidata");
        meter.CreateObservableCounter(
            "atdon_death_sync_updated",
            () => Interlocked.Read(ref _deathSyncUpdatedTotal),
            description: "Total records updated with newly discovered death dates");
    }

    public void RecordSearch(bool found, double durationMs)
    {
        _searchRequests.Add(1, new KeyValuePair<string, object?>("found", found));
        _searchDurationMs.Record(durationMs, new KeyValuePair<string, object?>("found", found));
    }

    public void RecordDeathSync(int checkedCount, int updatedCount)
    {
        _deathSyncChecked.Add(checkedCount);
        if (updatedCount > 0)
        {
            Interlocked.Add(ref _deathSyncUpdatedTotal, updatedCount);
        }
    }
}
