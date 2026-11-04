using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

// The commit of the owned transaction, when it has to wait for interceptors registered after this
// one. See DeferCommit.
internal sealed partial class MultiLingualSaveChangesInterceptor
{
    /// <summary>
    /// True when the owned transaction is left open past this interceptor's <c>SavedChanges</c>, to
    /// be committed by EF's <c>DbContext.SavedChanges</c> event instead.
    /// <para>
    /// <b>Why.</b> EF runs interceptors in the order they were added. An interceptor registered
    /// after this one — an audit trail writing entries that needed the generated keys — does its
    /// post-save work in its own <c>SavedChanges</c>, after ours. Committed here, that work would
    /// land outside the transaction, and a failure in it could no longer roll the entity back.
    /// EF raises the <c>SavedChanges</c> event once every interceptor's <c>SavedChanges</c> has
    /// returned, and not at all when one of them throws: every interceptor then sees
    /// <c>SaveChangesFailed</c>, and this one rolls back the transaction it still holds.
    /// </para>
    /// <para>
    /// <b>What it costs.</b> The event is synchronous, so the commit is too, even under
    /// <c>SaveChangesAsync</c>: one blocking round trip. So it is deferred only when it has to be —
    /// this interceptor owns the transaction, and another <c>SaveChanges</c> interceptor follows it.
    /// </para>
    /// <para>
    /// Until the event, the save is still in flight: a <c>SaveChanges</c> a later interceptor issues
    /// in between is nested in it (<see cref="IsNested(SaveState)"/>), its work joins this save's,
    /// and its own completion — event included — commits nothing.
    /// </para>
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="state">The save's state; its pending writes and deletes are applied already.</param>
    /// <param name="applied">The writes just stored, completed once the commit succeeds.</param>
    private bool DeferCommit(DbContext context, SaveState state, List<TranslationWrite> applied)
    {
        if (state.OwnedTransaction is null || !HasSaveInterceptorAfterThis(context))
            return false;

        state.Applied.AddRange(applied);
        state.CommitPending = true;

        // Still in flight: what Leave released, the event ends.
        state.Depth = 1;

        if (!state.ListensToSavedChanges)
        {
            context.SavedChanges += OnSavedChanges;
            state.ListensToSavedChanges = true;
        }

        return true;
    }

    /// <summary>Whether a <c>SaveChanges</c> interceptor was added to the context after this one.</summary>
    private bool HasSaveInterceptorAfterThis(DbContext context)
    {
        var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors is null)
            return false;

        var seenThis = false;
        foreach (var interceptor in interceptors)
        {
            if (ReferenceEquals(interceptor, this))
                seenThis = true;
            else if (seenThis && interceptor is ISaveChangesInterceptor)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A save nested in the pending window completed. EF raises the <c>SavedChanges</c> event for it
    /// too, before the outer save's; that one must not commit.
    /// </summary>
    private static void NoteNestedSaveCompleted(SaveState state)
    {
        if (state.CommitPending)
            state.NestedSavesAwaitingEvent++;
    }

    /// <summary>EF's <c>SavedChanges</c> event: every interceptor is done, so commit.</summary>
    private void OnSavedChanges(object? sender, SavedChangesEventArgs e)
    {
        if (sender is not DbContext context || !_states.TryGetValue(context, out var state) || state.IsApplying || !state.CommitPending)
            return;

        if (state.NestedSavesAwaitingEvent > 0)
        {
            state.NestedSavesAwaitingEvent--;
            return;
        }

        try
        {
            // Work a nested save added after this save applied its own. Synchronous, as the event is.
            if (state.HasWork)
            {
                state.IsApplying = true;
                try
                {
                    var (writer, deletes, writes) = PrepareApply(context, state);

                    foreach (var (entityName, entityIds) in deletes)
                        writer.DeleteEntities(entityName, entityIds);

                    writer.Write(WithoutSources(writes));
                    state.Applied.AddRange(writes);
                }
                finally
                {
                    state.IsApplying = false;
                }
            }

            CommitOwnedTransaction(state);
            Complete(state, [.. state.Applied]);
        }
        catch
        {
            RollbackOwnedTransaction(state);
            state.Snapshot?.Restore(context);
            throw;
        }
        finally
        {
            state.Depth = 0;
            state.Reset();
        }
    }
}
