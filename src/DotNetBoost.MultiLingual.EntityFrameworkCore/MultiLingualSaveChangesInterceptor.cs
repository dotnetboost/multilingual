using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
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
/// anywhere rolls the entity and its translations back together.</para>
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
public sealed partial class MultiLingualSaveChangesInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, SaveState> _states = new();

    // Resolved on first use, not injected: the engine depends on the stores, the stores on the
    // DbContext, and this interceptor is created while that DbContext is still being built.
    private ITranslationManager? _manager;
    private ITranslationManager Manager => _manager ??= services.GetRequiredService<ITranslationManager>();

    private MultiLingualOptions? _options;
    private MultiLingualOptions Options => _options ??= services.GetRequiredService<MultiLingualOptions>();

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

        if (!result.HasResult && NeedsRetryingSave(context))
            return InterceptionResult<int>.SuppressWithResult(await SaveUnderStrategyAsync(context, cancellationToken));

        if (Begin(context) is not { } state)
            return result;

        if (NeedsOwnTransaction(context, state))
            state.OwnedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

        return result;
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context)
            return result;

        if (!result.HasResult && NeedsRetryingSave(context))
            return InterceptionResult<int>.SuppressWithResult(SaveUnderStrategy(context));

        if (Begin(context) is not { } state)
            return result;

        if (NeedsOwnTransaction(context, state))
            state.OwnedTransaction = context.Database.BeginTransaction();

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

        if (!state.HasWork)
        {
            await CommitOwnedTransactionAsync(state, cancellationToken);
            return result;
        }

        state.IsApplying = true;
        try
        {
            var (writer, deletes, writes) = PrepareApply(context, state);

            foreach (var (entityName, entityIds) in deletes)
                await writer.DeleteEntitiesAsync(entityName, entityIds, cancellationToken);

            // Written without their sources, and completed only once they are durable: completing
            // before a commit that then fails would make the retry believe they were stored.
            await writer.WriteAsync(WithoutSources(writes), cancellationToken);

            await CommitOwnedTransactionAsync(state, cancellationToken);
            Complete(state, writes);
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

        if (!state.HasWork)
        {
            CommitOwnedTransaction(state);
            return result;
        }

        state.IsApplying = true;
        try
        {
            var (writer, deletes, writes) = PrepareApply(context, state);

            foreach (var (entityName, entityIds) in deletes)
                writer.DeleteEntities(entityName, entityIds);

            writer.Write(WithoutSources(writes));

            CommitOwnedTransaction(state);
            Complete(state, writes);
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
            state.Reset();
        }

        return result;
    }

    /// <summary>What <c>SavedChanges</c> has to do, now that every key exists.</summary>
    private (ITranslationManager Writer, List<(string EntityName, string[] EntityIds)> Deletes, List<TranslationWrite> Writes)
        PrepareApply(DbContext context, SaveState state)
    {
        // Through THIS context, so the rows join its connection and transaction — even when it is
        // not the context type the provider was registered with.
        var writer = Manager.WithStore(new EfCoreTranslationStore(context));

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
            Manager.CompleteWrites(writes);
    }

    /// <inheritdoc/>
    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context || !_states.TryGetValue(context, out var state) || state.IsApplying)
            return;

        await RollbackOwnedTransactionAsync(state);

        // EF leaves entity states alone when its own save fails, but Begin may already have
        // adjusted base-language properties; without this a retry would lose the authored text.
        state.Snapshot?.Restore(context);
        state.Reset();
    }

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context || !_states.TryGetValue(context, out var state) || state.IsApplying)
            return;

        RollbackOwnedTransaction(state);
        state.Snapshot?.Restore(context);
        state.Reset();
    }

    // ------------------------------------------------------------------ collect

    /// <summary>Captures the pending work, or returns null when this save is our own nested one.</summary>
    private SaveState? Begin(DbContext context)
    {
        var state = _states.GetOrCreateValue(context);

        // Guard against the nested SaveChanges the translation store issues.
        if (state.IsApplying)
            return null;

        state.Reset();

        var entries = context.ChangeTracker.Entries().ToList();
        if (!entries.Exists(e => MultiLingualMetadata.IsMultiLingual(e.Metadata.ClrType)))
            return state;

        // Before ApplyBaseLanguageRules touches anything. Only relational saves run inside a
        // transaction, so only there does a failure mean the database never saw the changes.
        if (context.Database.IsRelational())
            state.Snapshot = TrackerSnapshot.Take(entries);

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
                    if (EntityKeys.TryGetKey(entry) is { } key)
                        state.Deletes.Add(new PendingDelete(entityName, key));
                    break;

                case EntityState.Added:
                case EntityState.Modified:
                case EntityState.Unchanged:
                    if (entry.State == EntityState.Added)
                        RequireBaseLanguageOnCreate(entityName);
                    else
                        ApplyBaseLanguageRules(entry);

                    // After ApplyBaseLanguageRules, which is careful to leave the in-memory values
                    // exactly as the caller assigned them.
                    var values = Manager.CaptureValues(entry.Entity);
                    if (values.Count > 0)
                        state.Writes.Add(new PendingWrite(entry, entityName, values));
                    break;
            }
        }

        if (!state.HasWork)
            state.Snapshot = null;

        return state;
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
    ///   <item><b>Untouched</b> — whatever is in the property came from the base column, because a
    ///   write endpoint loads without hydrating. It is declared as a borrowed base value, so it is
    ///   not written back as a translation. Without this, updating only a product's price under
    ///   <c>Accept-Language: ar</c> would file its English name as the Arabic one.</item>
    /// </list>
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

        var language = LanguageContext;
        if (!language.IsResolved || Options.IsBaseLanguage(language.Current.Code))
            return;

        // ChangeTracker.Entries() has already run DetectChanges, so IsModified is current.
        var untouched = new List<string>();

        foreach (var property in MultiLingualMetadata.For(entry.Metadata.ClrType))
        {
            if (entry.Metadata.FindProperty(property.Name) is null)
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

        /// <summary>Set while a retrying strategy owns the transaction; survives <see cref="Reset"/>.</summary>
        public bool DeferCompletion { get; set; }

        /// <summary>Writes stored by the current attempt, completed once the strategy's commit succeeds.</summary>
        public List<TranslationWrite> Deferred { get; } = [];
        public bool HasWork => Writes.Count > 0 || Deletes.Count > 0;

        public void Reset()
        {
            Writes.Clear();
            Deletes.Clear();
            Snapshot = null;
        }
    }

    private sealed record PendingWrite(
        EntityEntry Entry,
        string EntityName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Values);

    private sealed record PendingDelete(string EntityName, string EntityId);
}
