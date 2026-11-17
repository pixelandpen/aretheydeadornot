using AreTheyDeadOrNot.Core.Repositories;
using AreTheyDeadOrNot.Infrastructure.Data;
using Dapper;

namespace AreTheyDeadOrNot.Infrastructure.Repositories;

public sealed class PostgresDeathSyncRepository(
    ReadReplicaDataSource readReplica,
    PrimaryWriteDataSource primaryWrite) : IDeathSyncRepository
{
    private sealed class CursorRow
    {
        public string Qid { get; init; } = string.Empty;
        public string NormalizedName { get; init; } = string.Empty;
        public int Sitelinks { get; init; }
    }

    public async Task<IReadOnlyList<AlivePersonCursorItem>> GetNextAliveBatchAsync(
        int? lastSitelinks,
        string? lastQid,
        int batchSize,
        int? minSitelinks = null,
        int? maxSitelinksExclusive = null,
        CancellationToken cancellationToken = default)
    {
        const string initialSql = """
            SELECT qid AS Qid, normalized_name AS NormalizedName, sitelinks AS Sitelinks
            FROM people
            WHERE death_date IS NULL
              AND (@MinSitelinks IS NULL OR sitelinks >= @MinSitelinks)
              AND (@MaxSitelinksExclusive IS NULL OR sitelinks < @MaxSitelinksExclusive)
            ORDER BY sitelinks DESC, qid ASC
            LIMIT @BatchSize;
            """;

        const string keysetSql = """
            SELECT qid AS Qid, normalized_name AS NormalizedName, sitelinks AS Sitelinks
            FROM people
            WHERE death_date IS NULL
              AND (@MinSitelinks IS NULL OR sitelinks >= @MinSitelinks)
              AND (@MaxSitelinksExclusive IS NULL OR sitelinks < @MaxSitelinksExclusive)
              AND (sitelinks < @LastSitelinks OR (sitelinks = @LastSitelinks AND qid > @LastQid))
            ORDER BY sitelinks DESC, qid ASC
            LIMIT @BatchSize;
            """;

        await using var conn = await readReplica.OpenConnectionAsync(cancellationToken);
        var rows = lastSitelinks is null || string.IsNullOrEmpty(lastQid)
            ? await conn.QueryAsync<CursorRow>(new CommandDefinition(
                initialSql,
                new
                {
                    BatchSize = batchSize,
                    MinSitelinks = minSitelinks,
                    MaxSitelinksExclusive = maxSitelinksExclusive
                },
                cancellationToken: cancellationToken))
            : await conn.QueryAsync<CursorRow>(new CommandDefinition(
                keysetSql,
                new
                {
                    LastSitelinks = lastSitelinks.Value,
                    LastQid = lastQid,
                    BatchSize = batchSize,
                    MinSitelinks = minSitelinks,
                    MaxSitelinksExclusive = maxSitelinksExclusive
                },
                cancellationToken: cancellationToken));

        return rows
            .Select(r => new AlivePersonCursorItem(r.Qid, r.NormalizedName, r.Sitelinks))
            .ToList();
    }

    public async Task<bool> MarkDeceasedAsync(
        string qid,
        DateOnly deathDate,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE people
            SET death_date = @DeathDate,
                death_month_day = @DeathMonthDay,
                updated_at = NOW()
            WHERE qid = @Qid
              AND death_date IS NULL;
            """;

        var monthDay = (short)(deathDate.Month * 100 + deathDate.Day);

        await using var conn = await primaryWrite.OpenConnectionAsync(cancellationToken);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { Qid = qid, DeathDate = deathDate, DeathMonthDay = monthDay },
            cancellationToken: cancellationToken));

        return affected > 0;
    }
}
