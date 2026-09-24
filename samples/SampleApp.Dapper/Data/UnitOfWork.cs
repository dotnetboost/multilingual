using System.Data;

namespace SampleApp.Dapper.Data;

/// <summary>
/// One transaction per request, shared by the sample's own SQL and by the library's stores.
/// <para>
/// This is the piece that has no counterpart in <c>samples/SampleApp.MongoDb</c>, and it is the
/// reason this sample exists alongside it. <c>Program.cs</c> registers
/// <c>IDbTransaction</c> as <c>sp =&gt; sp.GetRequiredService&lt;UnitOfWork&gt;().Current</c>, and
/// <c>UseDapper(...)</c> resolves an <c>IDbTransaction</c> from the scope when it builds its
/// stores. So a product's INSERT and its translations' upserts run in the SAME transaction without
/// a single line in the controllers saying so — and either both land or neither does. The EF Core
/// provider gets this from <c>SaveChanges</c>; MongoDB would need a session, which the AppHost's
/// standalone <c>mongod</c> cannot give it.
/// </para>
/// <para>
/// Lazy on purpose: the transaction begins on the first call to <see cref="Current"/>, so a request
/// that touches no data — <c>/health</c>, the OpenAPI document — never opens one.
/// <see cref="CommitAsync"/> is called by <see cref="UnitOfWorkFilter"/> once the action has
/// succeeded; anything left uncommitted is rolled back when the scope disposes this object, which
/// is what makes a thrown exception undo the whole request.
/// </para>
/// </summary>
/// <param name="connection">The request's connection — the same instance the library's stores use.</param>
public sealed class UnitOfWork(IDbConnection connection) : IDisposable, IAsyncDisposable
{
    private IDbTransaction? _transaction;

    /// <summary>Whether a transaction has actually been started.</summary>
    public bool HasTransaction => _transaction is not null;

    /// <summary>
    /// The request's transaction, begun on first use. Pass it to every Dapper call the sample makes:
    /// once a connection has an open transaction, ADO.NET requires its commands to carry it.
    /// </summary>
    public IDbTransaction Current
    {
        get
        {
            if (_transaction is not null)
            {
                return _transaction;
            }

            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            return _transaction = connection.BeginTransaction();
        }
    }

    /// <summary>Commits the transaction if one was started. Safe to call when none was.</summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_transaction is null)
        {
            return;
        }

        if (_transaction is System.Data.Common.DbTransaction async)
        {
            await async.CommitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            _transaction.Commit();
        }

        _transaction.Dispose();
        _transaction = null;
    }

    /// <summary>
    /// Rolls back anything still open. A transaction that reaches here is one whose action threw,
    /// so the entity's own rows and its translations are undone together.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_transaction is null)
        {
            return;
        }

        try
        {
            if (_transaction is System.Data.Common.DbTransaction async)
            {
                await async.RollbackAsync().ConfigureAwait(false);
            }
            else
            {
                _transaction.Rollback();
            }
        }
        catch (InvalidOperationException)
        {
            // Already completed, or the connection is gone. Nothing left to undo.
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    /// <summary>
    /// The synchronous half of the same rollback.
    /// <para>
    /// Both interfaces on purpose, and it is not optional: a scoped service that implements ONLY
    /// <see cref="IAsyncDisposable"/> makes <c>IServiceScope.Dispose()</c> throw
    /// "type only implements IAsyncDisposable", and not every scope this object lands in is
    /// disposed asynchronously — the language catalog refreshes itself from a synchronous scope
    /// when a synchronous save finds it expired. A scoped service reached from a synchronous code
    /// path needs this.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_transaction is null)
        {
            return;
        }

        try
        {
            _transaction.Rollback();
        }
        catch (InvalidOperationException)
        {
            // Already completed, or the connection is gone. Nothing left to undo.
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }
}
