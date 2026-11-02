using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace MusicFinder.Infrastructure.Persistence;

/// <summary>Single serialized writer: every mutation runs in its own short transaction on one connection.</summary>
public sealed class SqliteWriteQueue : IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Channel<Action<SqliteConnection>> _channel = Channel.CreateUnbounded<Action<SqliteConnection>>();
    private readonly Task _loop;

    public SqliteWriteQueue(SqliteConnectionFactory factory)
    {
        _factory = factory;
        _loop = Task.Run(RunAsync);
    }

    public Task<T> EnqueueAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = _channel.Writer.TryWrite(conn =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            using var tx = conn.BeginTransaction();
            try
            {
                var result = work(conn, tx);
                tx.Commit();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { /* connection may already be unusable */ }
                tcs.TrySetException(ex);
            }
        });
        if (!written) tcs.TrySetException(new ObjectDisposedException(nameof(SqliteWriteQueue)));
        return tcs.Task;
    }

    public Task EnqueueAsync(Action<SqliteConnection, SqliteTransaction> work, CancellationToken ct = default) =>
        EnqueueAsync<object?>((c, t) => { work(c, t); return null; }, ct);

    private async Task RunAsync()
    {
        using var conn = _factory.Open();
        await foreach (var job in _channel.Reader.ReadAllAsync())
            job(conn);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _loop;
    }
}
