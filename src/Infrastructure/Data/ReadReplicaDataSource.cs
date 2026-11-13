using Npgsql;

namespace AreTheyDeadOrNot.Infrastructure.Data;

/// <summary>
/// Dedicated NpgsqlDataSource pointing to the PostgreSQL read replica(s)
/// so all read-only API queries are isolated from primary write workloads.
/// </summary>
public sealed class ReadReplicaDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        DataSource.OpenConnectionAsync(cancellationToken);

    public void Dispose() => DataSource.Dispose();
    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
