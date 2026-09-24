using System.Data;
using System.Data.Common;

namespace DotNetBoost.MultiLingual.Dapper;

/// <summary>
/// Runs a group of statements as one unit. When the application is already in a transaction the
/// group joins it and the application commits; otherwise the group gets a transaction of its own,
/// so a multi-statement operation — folding a batch of upserts and deletes, or promoting a
/// language while demoting the previous default — is all-or-nothing either way.
/// </summary>
internal static class UnitOfWork
{
    public static async Task<T> RunAsync<T>(
        IDbConnection connection,
        IDbTransaction? ambient,
        Func<IDbTransaction?, Task<T>> body,
        CancellationToken ct)
    {
        // The application owns the transaction, and its commit is the one that counts.
        if (ambient is not null)
            return await body(ambient).ConfigureAwait(false);

        var opened = await OpenIfClosedAsync(connection, ct).ConfigureAwait(false);
        try
        {
            var transaction = connection is DbConnection db
                ? await db.BeginTransactionAsync(ct).ConfigureAwait(false)
                : connection.BeginTransaction();

            try
            {
                var result = await body(transaction).ConfigureAwait(false);

                if (transaction is DbTransaction async)
                    await async.CommitAsync(ct).ConfigureAwait(false);
                else
                    transaction.Commit();

                return result;
            }
            catch
            {
                Rollback(transaction);
                throw;
            }
            finally
            {
                transaction.Dispose();
            }
        }
        finally
        {
            if (opened) connection.Close();
        }
    }

    public static T Run<T>(IDbConnection connection, IDbTransaction? ambient, Func<IDbTransaction?, T> body)
    {
        if (ambient is not null)
            return body(ambient);

        var opened = OpenIfClosed(connection);
        try
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                var result = body(transaction);
                transaction.Commit();
                return result;
            }
            catch
            {
                Rollback(transaction);
                throw;
            }
        }
        finally
        {
            if (opened) connection.Close();
        }
    }

    /// <summary>
    /// Opens <paramref name="connection"/> if it is closed, reporting whether it did — the caller
    /// closes only what it opened, and never a connection the application handed over open.
    /// </summary>
    public static async Task<bool> OpenIfClosedAsync(IDbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Closed)
            return false;

        if (connection is DbConnection db)
            await db.OpenAsync(ct).ConfigureAwait(false);
        else
            connection.Open();

        return true;
    }

    public static bool OpenIfClosed(IDbConnection connection)
    {
        if (connection.State != ConnectionState.Closed)
            return false;

        connection.Open();
        return true;
    }

    /// <summary>
    /// Rolls back on the way out of a failed unit. A rollback can itself throw — the connection
    /// may already be gone, which is often why the body failed — and that exception would replace
    /// the real one, so it is swallowed deliberately.
    /// </summary>
    private static void Rollback(IDbTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
#pragma warning disable CA1031 // See the summary: the original failure is the one worth reporting.
        catch
        {
            // Intentionally ignored.
        }
#pragma warning restore CA1031
    }
}
