using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

// The save under a retrying execution strategy. See the remarks on the class, in
// MultiLingualSaveChangesInterceptor.cs, for why and what it changes.
internal sealed partial class MultiLingualSaveChangesInterceptor
{
    /// <summary>
    /// True when this save would open its own transaction under a strategy that refuses one. Inside
    /// a strategy's own execution <c>RetriesOnFailure</c> is false, so a caller who already wrapped
    /// the save — and this interceptor's replayed save — take the ordinary path.
    /// </summary>
    private static bool NeedsRetryingSave(DbContext context) =>
        context.Database.IsRelational()
        && context.Database.CurrentTransaction is null
        && System.Transactions.Transaction.Current is null
        && context.Database.CreateExecutionStrategy().RetriesOnFailure
        && context.ChangeTracker.Entries().Any(e => MultiLingualMetadata.IsMultiLingual(e.Metadata.ClrType));

    private async Task<int> SaveUnderStrategyAsync(DbContext context, CancellationToken ct)
    {
        var (snapshot, state) = PrepareRetryingSave(context);
        try
        {
            var saved = await context.Database.CreateExecutionStrategy().ExecuteAsync(
                (Context: context, Snapshot: snapshot, State: state),
                static async (_, s, ct) =>
                {
                    s.State.Deferred.Clear();
                    await using var transaction = await s.Context.Database.BeginTransactionAsync(ct);
                    try
                    {
                        var saved = await s.Context.SaveChangesAsync(ct);
                        await transaction.CommitAsync(ct);
                        return saved;
                    }
                    catch
                    {
                        // Disposing the transaction rolls the database back; this puts the change
                        // tracker back, so the next attempt starts where this one did.
                        s.Snapshot.Restore(s.Context);
                        throw;
                    }
                },
                verifySucceeded: null,
                ct);

            Writer.CompleteWrites(state.Deferred);
            return saved;
        }
        finally
        {
            state.DeferCompletion = false;
            state.Deferred.Clear();
        }
    }

    private int SaveUnderStrategy(DbContext context)
    {
        var (snapshot, state) = PrepareRetryingSave(context);
        try
        {
            var saved = context.Database.CreateExecutionStrategy().Execute(
                (Context: context, Snapshot: snapshot, State: state),
                static (_, s) =>
                {
                    s.State.Deferred.Clear();
                    using var transaction = s.Context.Database.BeginTransaction();
                    try
                    {
                        var saved = s.Context.SaveChanges();
                        transaction.Commit();
                        return saved;
                    }
                    catch
                    {
                        s.Snapshot.Restore(s.Context);
                        throw;
                    }
                },
                verifySucceeded: null);

            Writer.CompleteWrites(state.Deferred);
            return saved;
        }
        finally
        {
            state.DeferCompletion = false;
            state.Deferred.Clear();
        }
    }

    /// <summary>
    /// Snapshots the tracker before the first attempt — before <see cref="Begin"/> adjusts any
    /// base-language property — and tells the replayed saves to leave completing their writes to
    /// the strategy, which alone sees the commit.
    /// </summary>
    private (TrackerSnapshot Snapshot, SaveState State) PrepareRetryingSave(DbContext context)
    {
        var state = _states.GetOrCreateValue(context);
        state.DeferCompletion = true;
        state.Deferred.Clear();
        return (TrackerSnapshot.Take(context.ChangeTracker.Entries().ToList()), state);
    }
}
