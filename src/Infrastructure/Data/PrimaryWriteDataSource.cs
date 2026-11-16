using Npgsql;

namespace AreTheyDeadOrNot.Infrastructure.Data;

/// <summary>
/// Dedicated NpgsqlDataSource pointing to the PostgreSQL primary server for background sync writes.
/// Falls back to the configured read connection string when running against a single-instance DB.
/// </summary>
public sealed class PrimaryWriteDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        DataSource.OpenConnectionAsync(cancellationToken);

    public void Dispose() => DataSource.Dispose();
    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
