using AreTheyDeadOrNot.Core.Models;
using AreTheyDeadOrNot.Core.Repositories;
using AreTheyDeadOrNot.Infrastructure.Data;
using Dapper;
using Npgsql;

namespace AreTheyDeadOrNot.Infrastructure.Repositories;

public sealed class PostgresPersonRepository(ReadReplicaDataSource readReplica) : IPersonRepository
{
    private sealed class PersonRow
    {
        public string Qid { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public DateOnly? BirthDate { get; init; }
        public DateOnly? DeathDate { get; init; }
        public int Sitelinks { get; init; }
        public string[]? Occupations { get; init; }
    }

    public async Task<IReadOnlyList<PersonRecord>> SearchByNameAsync(
        string normalizedName,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return [];
        }

        const string exactSql = """
            WITH matched AS (
                SELECT qid, name, birth_date, death_date, sitelinks
                FROM people
                WHERE normalized_name = @NormalizedName
                ORDER BY sitelinks DESC
                LIMIT @Limit
            )
            SELECT
                m.qid AS Qid,
                m.name AS Name,
                m.birth_date AS BirthDate,
                m.death_date AS DeathDate,
                m.sitelinks AS Sitelinks,
                COALESCE(
                    ARRAY(
                        SELECT po.occupation_name
                        FROM person_occupations po
                        WHERE po.qid = m.qid
                        ORDER BY po.occupation_name
                    ),
                    ARRAY[]::text[]
                ) AS Occupations
            FROM matched m
            ORDER BY m.sitelinks DESC;
            """;

        await using var conn = await readReplica.OpenConnectionAsync(cancellationToken);

        var exactRows = (await conn.QueryAsync<PersonRow>(
            new CommandDefinition(
                exactSql,
                new { NormalizedName = normalizedName, Limit = limit },
                cancellationToken: cancellationToken))).ToList();

        if (exactRows.Count > 0 || normalizedName.Length < 3)
        {
            return exactRows.Select(MapRow).ToList();
        }

        // Fallback to trigram similarity when exact normalized name has no match.
        // Tighter threshold + 1.5s statement timeout prevent cache-miss bursts from saturating DB CPU.
        const string fuzzySql = """
            WITH matched AS (
                SELECT qid, name, birth_date, death_date, sitelinks,
                       similarity(normalized_name, @NormalizedName) AS sim
                FROM people
                WHERE normalized_name % @NormalizedName
                ORDER BY sim DESC, sitelinks DESC
                LIMIT @Limit
            )
            SELECT
                m.qid AS Qid,
                m.name AS Name,
                m.birth_date AS BirthDate,
                m.death_date AS DeathDate,
                m.sitelinks AS Sitelinks,
                COALESCE(
                    ARRAY(
                        SELECT po.occupation_name
                        FROM person_occupations po
                        WHERE po.qid = m.qid
                        ORDER BY po.occupation_name
                    ),
                    ARRAY[]::text[]
                ) AS Occupations
            FROM matched m
            ORDER BY m.sim DESC, m.sitelinks DESC;
            """;

        try
        {
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);
            await conn.ExecuteAsync(
                new CommandDefinition(
                    "SET LOCAL pg_trgm.similarity_threshold = 0.55; SET LOCAL statement_timeout = '1500ms';",
                    transaction: tx,
                    cancellationToken: cancellationToken));

            var fuzzyRows = (await conn.QueryAsync<PersonRow>(
                new CommandDefinition(
                    fuzzySql,
                    new { NormalizedName = normalizedName, Limit = limit },
                    transaction: tx,
                    commandTimeout: 2,
                    cancellationToken: cancellationToken))).ToList();

            await tx.CommitAsync(cancellationToken);

            return fuzzyRows.Select(MapRow).ToList();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            // Gracefully degrade on 1.5s fuzzy timeout rather than returning 500
            return [];
        }
    }

    public async Task<PersonRecord?> GetByQidAsync(
        string qid,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                p.qid AS Qid,
                p.name AS Name,
                p.birth_date AS BirthDate,
                p.death_date AS DeathDate,
                p.sitelinks AS Sitelinks,
                COALESCE(
                    ARRAY(
                        SELECT po.occupation_name
                        FROM person_occupations po
                        WHERE po.qid = p.qid
                        ORDER BY po.occupation_name
                    ),
                    ARRAY[]::text[]
                ) AS Occupations
            FROM people p
            WHERE p.qid = @Qid
            LIMIT 1;
            """;

        await using var conn = await readReplica.OpenConnectionAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<PersonRow>(
            new CommandDefinition(sql, new { Qid = qid }, cancellationToken: cancellationToken));

        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<PersonRecord>> GetRecentDeathsAsync(
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH recent AS (
                SELECT qid, name, birth_date, death_date, sitelinks
                FROM people
                WHERE death_date IS NOT NULL
                  AND death_date <= CURRENT_DATE
                ORDER BY death_date DESC, sitelinks DESC
                LIMIT @Limit
            )
            SELECT
                r.qid AS Qid,
                r.name AS Name,
                r.birth_date AS BirthDate,
                r.death_date AS DeathDate,
                r.sitelinks AS Sitelinks,
                COALESCE(
                    ARRAY(
                        SELECT po.occupation_name
                        FROM person_occupations po
                        WHERE po.qid = r.qid
                        ORDER BY po.occupation_name
                    ),
                    ARRAY[]::text[]
                ) AS Occupations
            FROM recent r
            ORDER BY r.death_date DESC, r.sitelinks DESC;
            """;

        await using var conn = await readReplica.OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<PersonRow>(
            new CommandDefinition(sql, new { Limit = limit }, cancellationToken: cancellationToken));

        return rows.Select(MapRow).ToList();
    }

    public async Task<IReadOnlyList<PersonRecord>> GetInMemoryAsync(
        IReadOnlyList<short> monthDayKeys,
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        if (monthDayKeys.Count == 0)
        {
            return [];
        }

        const string sql = """
            WITH memorial AS (
                SELECT qid, name, birth_date, death_date, sitelinks
                FROM people
                WHERE death_month_day = ANY(@MonthDays)
                  AND death_date IS NOT NULL
                ORDER BY sitelinks DESC
                LIMIT @Limit
            )
            SELECT
                m.qid AS Qid,
                m.name AS Name,
                m.birth_date AS BirthDate,
                m.death_date AS DeathDate,
                m.sitelinks AS Sitelinks,
                COALESCE(
                    ARRAY(
                        SELECT po.occupation_name
                        FROM person_occupations po
                        WHERE po.qid = m.qid
                        ORDER BY po.occupation_name
                    ),
                    ARRAY[]::text[]
                ) AS Occupations
            FROM memorial m
            ORDER BY m.sitelinks DESC;
            """;

        await using var conn = await readReplica.OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<PersonRow>(
            new CommandDefinition(
                sql,
                new { MonthDays = monthDayKeys.ToArray(), Limit = limit },
                cancellationToken: cancellationToken));

        return rows.Select(MapRow).ToList();
    }

    private static PersonRecord MapRow(PersonRow row) => new()
    {
        Qid = row.Qid,
        Name = row.Name,
        BirthDate = row.BirthDate,
        DeathDate = row.DeathDate,
        Sitelinks = row.Sitelinks,
        Occupations = row.Occupations ?? []
    };
}
