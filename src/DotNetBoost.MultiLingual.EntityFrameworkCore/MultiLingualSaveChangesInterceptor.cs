using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// The write path. Nothing in a repository or controller needs to know about translations:
/// decorating an entity with <c>[MultiLingual]</c> is enough.
///
/// <para><b>Why the work happens after the save, not before.</b> An <c>Added</c> entity with a
/// database-generated key has no id until the INSERT has run, and <c>Translation.EntityId</c>
/// needs that id. So <c>SavingChanges</c> only captures what to write, and
/// <c>SavedChanges</c> — where every key is populated — does the writing.</para>
///
/// <para><b>Unchanged entities count too.</b> Translatable properties are not mapped, so
/// setting only <c>Name</c> leaves the entry <c>Unchanged</c> as far as EF knows. Those entries
/// are inspected as well; values the read path put there and nobody touched are skipped.</para>
///
/// <para><b>Atomicity.</b> If the caller has no transaction open, this interceptor opens one
/// before the primary save and commits it only once the translation rows are in. A failure
/// anywhere rolls the entity and its translations back together. When another
/// <c>SaveChanges</c> interceptor is registered after this one, the commit waits until that one's
/// post-save work is done as well; see <see cref="DeferCommit"/>.</para>
///
/// <para><b>Retrying execution strategies.</b> A strategy such as <c>EnableRetryOnFailure</c>
/// refuses a transaction it did not start, because it could not replay it. So when one is
/// configured and the caller has no transaction, the save is run inside the strategy instead:
/// transaction, entity, translations and commit together, replayed as a unit on a transient
/// failure, with the change tracker restored between attempts. The caller's <c>SaveChanges</c>
/// then returns that save's result. Two consequences: other interceptors see a
/// <c>SavingChanges</c>/<c>SavedChanges</c> pair for the replayed save as well as the outer
/// one, and changes are always accepted on success, even if <c>SaveChanges(false)</c> was
/// called. A caller who opens a transaction inside
/// <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c> keeps full control and avoids
/// both.</para>
///
/// <para><b>A failed save can be retried.</b> EF accepts the primary save's changes before
/// <c>SavedChanges</c> runs, so a translation write that fails afterwards would otherwise leave
/// every entity <c>Unchanged</c> — carrying keys the rollback just un-issued — and a retry would
/// write nothing. On a relational provider the change tracker is therefore put back exactly as
/// it was before the save: added entities added again with temporary keys, modified ones
/// modified with their original values, deleted ones deleted. This matches what EF does when
/// its own save fails, whether the transaction was this interceptor's or the caller's.</para>
///
/// <para><b>Scope.</b> State is kept per <see cref="DbContext"/> instance, so several contexts
/// can share one scope. Context pooling (<c>AddDbContextPool</c>) is not supported: pooled
/// options are built once, from the root provider, and cannot carry per-request services.</para>
/// </summary>
/// <param name="services">The scope's provider; the engine is resolved lazily, on first save.</param>
internal sealed partial class MultiLingualSaveChangesInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, SaveState> _states = new();

    // Resolved on first use, not injected: the engine depends on the stores, the stores on the
    // DbContext, and this interceptor is created while that DbContext is still being built.
    private ITranslationWriter? _writer;
    private ITranslationWriter Writer => _writer ??= services.GetRequiredService<ITranslationWriter>();

    private ITranslationManager? _manager;
    private ITranslationManager Manager => _manager ??= services.GetRequiredService<ITranslationManager>();

    private TranslationManager? _engine;
    private TranslationManager Engine => _engine ??= services.GetRequiredService<TranslationManager>();

    private MultiLingualOptions? _options;
    private MultiLingualOptions Options => _options ??= services.GetRequiredService<MultiLingualOptions>();

    private EfCoreSaveOptions? _saveOptions;
    private EfCoreSaveOptions SaveOptions => _saveOptions ??= services.GetService<EfCoreSaveOptions>() ?? new EfCoreSaveOptions();

    // Scoped, and read fresh: the language is resolved after this interceptor is constructed.
    private ILanguageContext LanguageContext => services.GetRequiredService<ILanguageContext>();

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context)
            return result;

        if (!result.HasResult && SaveOptions.OwnTransactions && !IsNested(context) && NeedsRetryingSave(context))
            return InterceptionResult<int>.SuppressWithResult(await SaveUnderStrategyAsync(context, cancellationToken));

        if (Begin(context) is not { } state)
            return result;

        try
        {
            if (NeedsOwnTransaction(context, state))
                state.OwnedTransaction = await BeginOwnedTransactionAsync(context, cancellationToken);
        }
        catch
        {
            Abandon(context, state);
            throw;
        }

        return result;
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context)
            return result;

        if (!result.HasResult && SaveOptions.OwnTransactions && !IsNested(context) && NeedsRetryingSave(context))
            return InterceptionResult<int>.SuppressWithResult(SaveUnderStrategy(context));

        if (Begin(context) is not { } state)
            return result;

        try
        {
            if (NeedsOwnTransaction(context, state))
                state.OwnedTransaction = BeginOwnedTransaction(context);
        }
        catch
        {
            Abandon(context, state);
            throw;
        }

        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context || !_states.TryGetValue(context, out var state) || state.IsApplying)
            return result;

        if (!Leave(state))
        {
            NoteNestedSaveCompleted(state);
            return result;
        }

        if (!state.HasWork)
        {
            await CommitOwnedTransactionAsync(state, cancellationToken);
            return result;
        }

        state.IsApplying = true;
        var deferred = false;
        try
        {
            var (writer, deletes, writes) = PrepareApply(context, state);

            foreach (var (entityName, entityIds) in deletes)
                await writer.DeleteEntitiesAsync(entityName, entityIds, cancellationToken);

            // Written without their sources, and completed only once they are durable: completing
            // before a commit that then fails would make the retry believe they were stored.
            await writer.WriteAsync(WithoutSources(writes), cancellationToken);

            if (DeferCommit(context, state, writes))
            {
                deferred = true;
            }
            else
            {
                await CommitOwnedTransactionAsync(state, cancellationToken);
                Complete(state, writes);
            }
        }
        catch
        {
            await RollbackOwnedTransactionAsync(state);
            state.Snapshot?.Restore(context);
            throw;
        }
        finally
        {
            state.IsApplying = false;
            if (deferred)
                state.ClearPending();
            else
                state.Reset();
        }

        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Synchronous all the way down — the same steps as <see cref="SavedChangesAsync"/> through the
    /// synchronous members of the manager, catalog and stores — so a synchronous
    /// <c>SaveChanges</c> never blocks on a task, and cannot deadlock under a UI thread's
    /// synchronization context.
    /// </remarks>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context || !_states.TryGetValue(context, out var state) || state.IsApplying)
            return result;

        if (!Leave(state))
        {
            NoteNestedSaveCompleted(state);
            return result;
        }

        if (!state.HasWork)
        {
            CommitOwnedTransaction(state);
            return result;
        }

        state.IsApplying = true;
        var deferred = false;
        try
        {
            var (writer, deletes, writes) = PrepareApply(context, state);

            foreach (var (entityName, entityIds) in deletes)
                writer.DeleteEntities(entityName, entityIds);

            writer.Write(WithoutSources(writes));

            if (DeferCommit(context, state, writes))
            {
                deferred = true;
            }
            else
            {
                CommitOwnedTransaction(state);
                Complete(state, writes);
            }
        }
        catch
        {
            RollbackOwnedTransaction(state);
            state.Snapshot?.Restore(context);
            throw;
        }
        finally
        {
            state.IsApplying = false;
            if (deferred)
                state.ClearPending();
            else
                state.Reset();
        }

        return result;
    }

    /// <summary>What <c>SavedChanges</c> has to do, now that every key exists.</summary>
    private (ITranslationWriter Writer, List<(string EntityName, string[] EntityIds)> Deletes, List<TranslationWrite> Writes)
        PrepareApply(DbContext context, SaveState state)
    {
        // Through THIS context, so the rows join its connection and transaction — even when it is
        // not the context type the provider was registered with.
        var writer = Writer.WithStore(new EfCoreTranslationStore(context));

        var deletes = state.Deletes
            .GroupBy(d => d.EntityName, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Select(d => d.EntityId).Distinct(StringComparer.Ordinal).ToArray()))
            .ToList();

        var writes = new List<TranslationWrite>();
        foreach (var pending in state.Writes)
        {
            var entityId = EntityKeys.TryGetKey(pending.Entry)
                ?? throw new InvalidOperationException(
                    $"'{pending.EntityName}' still has no key after SaveChanges, so its translations cannot be stored.");

            writes.Add(new TranslationWrite(pending.EntityName, entityId, pending.Values) { Source = pending.Entry.Entity });
        }

        return (writer, deletes, writes);
    }

    private static TranslationWrite[] WithoutSources(List<TranslationWrite> writes) =>
        writes.Select(w => w with { Source = null }).ToArray();

    /// <summary>
    /// Records <paramref name="writes"/> as stored, once that is true. With this interceptor's own
    /// transaction it has just committed; under a retrying strategy, the strategy completes them
    /// after its commit; in the caller's transaction the commit is out of sight, so now is the best
    /// moment there is.
    /// </summary>
    private void Complete(SaveState state, List<TranslationWrite> writes)
    {
        if (state.DeferCompletion)
            state.Deferred.AddRange(writes);
        else
            Writer.CompleteWrites(writes);
    }

    /// <inheritdoc/>
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        return FailedAsync(eventData.Context);
    }

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Failed(eventData.Context);
    }

    /// <inheritdoc/>
    /// <remarks>EF raises this instead of <c>SaveChangesFailed</c> for a cancelled save.</remarks>
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        return FailedAsync(eventData.Context);
    }

    /// <inheritdoc/>
    /// <remarks>EF raises this instead of <c>SaveChangesFailed</c> for a cancelled save.</remarks>
    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Failed(eventData.Context);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// EF raises this instead of <c>SaveChangesFailed</c> for a concurrency conflict, just before
    /// it throws — unless an interceptor suppresses the exception, and then the save goes on.
    /// </remarks>
    public override async ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (!result.IsSuppressed)
            await FailedAsync(eventData.Context);

        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// EF raises this instead of <c>SaveChangesFailed</c> for a concurrency conflict, just before
    /// it throws — unless an interceptor suppresses the exception, and then the save goes on.
    /// </remarks>
    public override InterceptionResult ThrowingConcurrencyException(ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (!result.IsSuppressed)
            Failed(eventData.Context);

        return result;
    }

    private async Task FailedAsync(DbContext? context)
    {
        if (context is null || !_states.TryGetValue(context, out var state) || state.IsApplying || !LeaveFailed(state))
            return;

        await RollbackOwnedTransactionAsync(state);

        // EF leaves entity states alone when its own save fails, but Begin may already have
        // adjusted base-language properties; without this a retry would lose the authored text.
        state.Snapshot?.Restore(context);
        state.Reset();
    }

    private void Failed(DbContext? context)
    {
        if (context is null || !_states.TryGetValue(context, out var state) || state.IsApplying || !LeaveFailed(state))
            return;

        RollbackOwnedTransaction(state);
        state.Snapshot?.Restore(context);
        state.Reset();
    }

    // ------------------------------------------------------------------ collect

    /// <summary>
    /// Captures the pending work. Returns null when this save is our own nested one, or when it is
    /// nested inside a save of ours still in flight — another interceptor saving from its
    /// <c>SavedChanges</c> — whose work then joins the outer save's.
    /// <para>
    /// <b>Why nesting is counted.</b> An interceptor registered before this one runs its
    /// <c>SavedChanges</c> before ours. A save it issues there would otherwise start over: forget
    /// the outer save's pending deletes (their entries are detached by now), drop its snapshot, and
    /// commit its transaction from the inside. Counted, the nested save only adds work, and only the
    /// outermost completion writes, commits or rolls back.
    /// </para>
    /// </summary>
    private SaveState? Begin(DbContext context)
    {
        var state = _states.GetOrCreateValue(context);

        // Guard against the nested SaveChanges the translation store issues.
        if (state.IsApplying)
            return null;

        if (IsNested(state))
        {
            state.Depth++;
            try
            {
                Capture(context, state, context.ChangeTracker.Entries().ToList());
            }
            catch
            {
                state.Depth--;
                throw;
            }

            return null;
        }

        state.Reset();
        state.Depth = 1;

        try
        {
            var entries = context.ChangeTracker.Entries().ToList();
            foreach (var entry in entries)
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    state.Changed.Add((entry, entry.State));
            }

            if (!entries.Exists(e => MultiLingualMetadata.IsMultiLingual(e.Metadata.ClrType)))
                return state;

            // Before ApplyBaseLanguageRules touches anything. Only relational saves run inside a
            // transaction, so only there does a failure mean the database never saw the changes.
            if (context.Database.IsRelational())
                state.Snapshot = TrackerSnapshot.Take(entries);

            Capture(context, state, entries);

            if (!state.HasWork)
                state.Snapshot = null;

            return state;
        }
        catch
        {
            Abandon(context, state);
            throw;
        }
    }

    /// <summary>Adds the translation work in <paramref name="entries"/> that is not pending already.</summary>
    private void Capture(DbContext context, SaveState state, List<EntityEntry> entries)
    {
        foreach (var entry in entries)
        {
            var clrType = entry.Metadata.ClrType;
            if (!MultiLingualMetadata.IsMultiLingual(clrType))
                continue;

            var entityName = MultiLingualMetadata.EntityNameFor(clrType);

            MultiLingualModelCheck.Verify(context, clrType, Options);

            switch (entry.State)
            {
                case EntityState.Deleted:
                    // The key is still readable here; after SaveChanges the entry is detached.
                    if (EntityKeys.TryGetKey(entry) is { } key && !state.Deletes.Contains(new PendingDelete(entityName, key)))
                        state.Deletes.Add(new PendingDelete(entityName, key));
                    break;

                case EntityState.Added:
                case EntityState.Modified:
                case EntityState.Unchanged:
                    // A nested save sees the outer save's entries again, accepted by now — pending,
                    // or already written while a deferred commit waits.
                    if (state.Writes.Exists(w => ReferenceEquals(w.Entry.Entity, entry.Entity))
                        || state.Applied.Exists(w => ReferenceEquals(w.Source, entry.Entity)))
                    {
                        break;
                    }

                    if (entry.State == EntityState.Added)
                        RequireBaseLanguageOnCreate(entityName);
                    else
                        ApplyBaseLanguageRules(entry);

                    // After ApplyBaseLanguageRules, which is careful to leave the in-memory values
                    // exactly as the caller assigned them.
                    var values = Writer.CaptureValues(entry.Entity);
                    if (values.Count > 0)
                        state.Writes.Add(new PendingWrite(entry, entityName, values));
                    break;
            }
        }
    }

    private bool IsNested(DbContext context) => _states.TryGetValue(context, out var state) && IsNested(state);

    /// <summary>
    /// True when a save starting now is nested inside an outer save of ours: one is in flight, and
    /// EF has already saved and accepted its changes — the only moment another interceptor can
    /// save from inside it. Anything else is an outer save that never completed (EF raises no
    /// <c>SaveChangesFailed</c> when an interceptor's <c>SavingChanges</c> throws), and the new save
    /// starts over, as it would have without the count.
    /// <para>
    /// An outer save that changed no entry EF tracks — only unmapped translatable properties — gives
    /// nothing to tell the two apart by, so it is treated as completed too.
    /// </para>
    /// </summary>
    private static bool IsNested(SaveState state) =>
        state.CommitPending
        || state.Depth > 0
        && state.Changed.Count > 0
        && state.Changed.TrueForAll(c => c.Entry.State == (c.State == EntityState.Deleted ? EntityState.Detached : EntityState.Unchanged));

    /// <summary>
    /// Ends one save that completed; true when it was the outermost, which does the work. A save at
    /// depth 0 is one whose <c>SavingChanges</c> this interceptor suppressed, having already run it
    /// under the retrying strategy.
    /// </summary>
    private static bool Leave(SaveState state) => state.Depth > 0 && --state.Depth == 0;

    /// <summary>
    /// Ends one save that failed; true when it was the outermost, which rolls back. A nested save's
    /// failure is reported to whoever issued it, and the outer save goes on as that caller decides.
    /// </summary>
    private static bool LeaveFailed(SaveState state)
    {
        if (state.Depth > 1)
        {
            state.Depth--;
            return false;
        }

        state.Depth = 0;
        return true;
    }

    /// <summary>
    /// Ends a save refused in <c>SavingChanges</c>. EF raises no <c>SaveChangesFailed</c> for it, so
    /// nothing else would put back what <c>ApplyBaseLanguageRules</c> adjusted, or end this save.
    /// </summary>
    private static void Abandon(DbContext context, SaveState state)
    {
        state.Snapshot?.Restore(context);
        state.Depth = 0;
        state.Reset();
    }

    /// <summary>
    /// In base-language mode an entity may only be CREATED in the base language: the base value is
    /// the column every translation hangs off and the last fallback for every other language, so
    /// an entity created in, say, Arabic would have a permanently empty base column and read as
    /// null in every language but Arabic.
    /// <para>
    /// A scope with no language resolved — a seeder, a background job — is exempt: it has no
    /// Accept-Language to be wrong about, and its property values go straight to the columns.
    /// </para>
    /// </summary>
    private void RequireBaseLanguageOnCreate(string entityName)
    {
        if (!Options.HasBaseLanguage)
            return;

        var language = LanguageContext;
        if (!language.IsResolved || Options.IsBaseLanguage(language.Current.Code))
            return;

        throw new BaseLanguageRequiredException(entityName, Options.BaseLanguage!, language.Current.Code);
    }

    /// <summary>
    /// In base-language mode a translatable property has a real column, and that column holds the
    /// BASE language. When something else is being authored, each translatable property is handled
    /// according to whether the caller actually touched it:
    /// <list type="bullet">
    ///   <item><b>Assigned</b> — the column is excluded from the UPDATE and the value goes to the
    ///   translations table instead.</item>
    ///   <item><b>Untouched</b> — whatever is in the property is what a read left there. It is
    ///   declared as a borrowed base value, so it is not written back as a translation. Without
    ///   this, updating only a product's price under <c>Accept-Language: ar</c> would file its
    ///   English name as the Arabic one.</item>
    /// </list>
    /// <para>
    /// Before either, in any scope: a value a read served from another language and nobody changed
    /// — every query fills tracked entities — is kept out of the base column, even where no
    /// language is resolved or the base language is being authored.
    /// </para>
    /// <para>
    /// <b>Why the original value is moved rather than the flag just cleared.</b> Clearing
    /// <c>IsModified</c> on its own makes EF reject the change and restore the property from its
    /// original value — which would throw away the text being authored before it is ever captured.
    /// Assigning the original first means there is no longer a change to reject: EF writes no
    /// column for the property, and the in-memory value stays exactly what the caller assigned, so
    /// a response can still show it.
    /// </para>
    /// <para>
    /// Doing it this way is also what makes a detached entity passed to <c>Update()</c> correct,
    /// where every property is marked modified and the stored base value is not known.
    /// </para>
    /// </summary>
    private void ApplyBaseLanguageRules(EntityEntry entry)
    {
        if (!Options.HasBaseLanguage)
            return;

        // In any scope: a value a read served from another language — every query fills tracked
        // entities in the request's language — and that nobody has changed since, is not the base
        // text, and must never be written to the base column. A background job with no language
        // resolved, reading in a default language other than the base one, would otherwise file
        // that language's text as the base text on its next save.
        var served = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in MultiLingualMetadata.For(entry.Metadata.ClrType))
        {
            if (entry.Metadata.FindProperty(property.Name) is null)
                continue;

            var member = entry.Property(property.Name);
            if (!Engine.HoldsServedTranslation(entry.Entity, property.Name, member.CurrentValue as string))
                continue;

            if (member.IsModified)
            {
                member.OriginalValue = member.CurrentValue;
                member.IsModified    = false;
            }

            served.Add(property.Name);
        }

        var language = LanguageContext;
        if (!language.IsResolved || Options.IsBaseLanguage(language.Current.Code))
            return;

        // ChangeTracker.Entries() has already run DetectChanges, so IsModified is current.
        var untouched = new List<string>();

        foreach (var property in MultiLingualMetadata.For(entry.Metadata.ClrType))
        {
            if (entry.Metadata.FindProperty(property.Name) is null || served.Contains(property.Name))
                continue;

            var member = entry.Property(property.Name);

            if (member.IsModified)
            {
                member.OriginalValue = member.CurrentValue;
                member.IsModified    = false;
            }
            else
            {
                untouched.Add(property.Name);
            }
        }

        if (untouched.Count > 0)
            Manager.MarkAsBaseLanguageValues(entry.Entity, untouched);
    }

    private Task<IDbContextTransaction> BeginOwnedTransactionAsync(DbContext context, CancellationToken ct)
    {
        EnsureMayOwnTransaction();
        return context.Database.BeginTransactionAsync(ct);
    }

    private IDbContextTransaction BeginOwnedTransaction(DbContext context)
    {
        EnsureMayOwnTransaction();
        return context.Database.BeginTransaction();
    }

    /// <summary>
    /// With <c>OwnTransactions(false)</c>, a save that writes translations without a transaction of
    /// the caller's is refused before anything reaches the database, rather than written in pieces.
    /// </summary>
    private void EnsureMayOwnTransaction()
    {
        if (SaveOptions.OwnTransactions)
            return;

        throw new InvalidOperationException(
            "This save writes translations and no transaction is open, and OwnTransactions(false) " +
            "leaves opening one to the application. Open one around SaveChanges, for instance in a " +
            "SaveChangesAsync override; under a retrying execution strategy, keep OwnTransactions on " +
            "and register UseMultiLingual before the other interceptors instead. See 'Sharing a " +
            "DbContext with other SaveChanges interceptors' in docs/limitations.md.");
    }

    private static bool NeedsOwnTransaction(DbContext context, SaveState state) =>
        state.HasWork
        && context.Database.IsRelational()
        && context.Database.CurrentTransaction is null
        && System.Transactions.Transaction.Current is null;

    // ------------------------------------------------------------------ plumbing

    private static async Task CommitOwnedTransactionAsync(SaveState state, CancellationToken ct)
    {
        if (state.OwnedTransaction is not { } transaction)
            return;

        try
        {
            await transaction.CommitAsync(ct);
        }
        finally
        {
            await transaction.DisposeAsync();
            state.OwnedTransaction = null;
        }
    }

    private static void CommitOwnedTransaction(SaveState state)
    {
        if (state.OwnedTransaction is not { } transaction)
            return;

        try
        {
            transaction.Commit();
        }
        finally
        {
            transaction.Dispose();
            state.OwnedTransaction = null;
        }
    }

    private static void RollbackOwnedTransaction(SaveState state)
    {
        if (state.OwnedTransaction is not { } transaction)
            return;

        try
        {
            transaction.Rollback();
        }
        finally
        {
            transaction.Dispose();
            state.OwnedTransaction = null;
        }
    }

    private static async Task RollbackOwnedTransactionAsync(SaveState state)
    {
        if (state.OwnedTransaction is not { } transaction)
            return;

        try
        {
            // Not cancellable: abandoning a rollback half way helps nobody.
            await transaction.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            await transaction.DisposeAsync();
            state.OwnedTransaction = null;
        }
    }

    private sealed class SaveState
    {
        public List<PendingWrite> Writes { get; } = [];
        public List<PendingDelete> Deletes { get; } = [];
        public IDbContextTransaction? OwnedTransaction { get; set; }
        public TrackerSnapshot? Snapshot { get; set; }
        public bool IsApplying { get; set; }

        /// <summary>
        /// How many saves of this context are in flight, the translation store's own excluded;
        /// survives <see cref="Reset"/>.
        /// </summary>
        public int Depth { get; set; }

        /// <summary>Every entry the outermost save changed, and how: what <see cref="IsNested(SaveState)"/> checks.</summary>
        public List<(EntityEntry Entry, EntityState State)> Changed { get; } = [];

        /// <summary>
        /// Set when the owned transaction waits for EF's <c>SavedChanges</c> event, because another
        /// interceptor still has its <c>SavedChanges</c> to run. See <see cref="DeferCommit"/>.
        /// </summary>
        public bool CommitPending { get; set; }

        /// <summary>Saves nested in the pending window that completed, whose own event comes first.</summary>
        public int NestedSavesAwaitingEvent { get; set; }

        /// <summary>Writes stored, to complete once the deferred commit succeeds.</summary>
        public List<TranslationWrite> Applied { get; } = [];

        /// <summary>Whether this interceptor listens to the context's <c>SavedChanges</c> event.</summary>
        public bool ListensToSavedChanges { get; set; }

        /// <summary>Set while a retrying strategy owns the transaction; survives <see cref="Reset"/>.</summary>
        public bool DeferCompletion { get; set; }

        /// <summary>Writes stored by the current attempt, completed once the strategy's commit succeeds.</summary>
        public List<TranslationWrite> Deferred { get; } = [];
        public bool HasWork => Writes.Count > 0 || Deletes.Count > 0;

        /// <summary>Forgets the work already applied, and keeps everything a deferred commit still needs.</summary>
        public void ClearPending()
        {
            Writes.Clear();
            Deletes.Clear();
        }

        public void Reset()
        {
            Writes.Clear();
            Deletes.Clear();
            Snapshot = null;
            Changed.Clear();
            CommitPending = false;
            NestedSavesAwaitingEvent = 0;
            Applied.Clear();
        }
    }

    private sealed record PendingWrite(
        EntityEntry Entry,
        string EntityName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Values);

    private sealed record PendingDelete(string EntityName, string EntityId);
}
