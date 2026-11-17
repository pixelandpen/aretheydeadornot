namespace AreTheyDeadOrNot.Core.Repositories;

public sealed record AlivePersonCursorItem(string Qid, string NormalizedName, int Sitelinks);

public interface IDeathSyncRepository
{
    Task<IReadOnlyList<AlivePersonCursorItem>> GetNextAliveBatchAsync(
        int? lastSitelinks,
        string? lastQid,
        int batchSize,
        int? minSitelinks = null,
        int? maxSitelinksExclusive = null,
        CancellationToken cancellationToken = default);

    Task<bool> MarkDeceasedAsync(
        string qid,
        DateOnly deathDate,
        CancellationToken cancellationToken = default);
}
