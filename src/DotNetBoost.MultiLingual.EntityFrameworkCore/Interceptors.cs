using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Collects every <c>[MultiLingual]</c> entity EF materializes during the scope, so the
/// hydrator can fill them all in — including ones reached through <c>Include</c> — with one
/// query per entity type.
///
/// <para><b>Edited entities are left alone.</b> Each entity's translatable values are
/// remembered as they were materialized. One that no longer matches by the time it is drained
/// was assigned by the caller — typically an entity loaded for update, which a write endpoint
/// never hydrates, left queued until some later read in the same scope drains the registry.
/// Hydrating it would overwrite the caller's edit with the stored value, and the save that
/// follows would then see nothing to write.</para>
/// </summary>
public sealed class MaterializedEntityRegistry
{
    private readonly List<Pending> _pending = [];
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Safety valve. If a caller streams a huge result set and never hydrates, stop growing
    /// rather than pinning every row for the life of the scope.
    /// </summary>
    public const int Capacity = 20_000;

    /// <summary>Queues <paramref name="entity"/> for the next hydration.</summary>
    public void Register(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (_pending.Count >= Capacity || !_seen.Add(entity))
            return;

        _pending.Add(new Pending(entity, Snapshot(entity)));
    }

    /// <summary>
    /// Takes everything queued so far that is still as it was materialized, and empties the queue.
    /// </summary>
    public IReadOnlyList<object> Drain()
    {
        if (_pending.Count == 0)
            return [];

        var pending = _pending.ToArray();
        _pending.Clear();
        _seen.Clear();

        var drained = new List<object>(pending.Length);
        foreach (var item in pending)
        {
            if (IsUntouched(item))
                drained.Add(item.Entity);
        }

        return drained;
    }

    /// <summary>Number of entities waiting for hydration.</summary>
    public int PendingCount => _pending.Count;

    private static string?[] Snapshot(object entity)
    {
        var properties = MultiLingualMetadata.For(entity.GetType());
        var values = new string?[properties.Count];

        for (var i = 0; i < values.Length; i++)
            values[i] = properties[i].GetValue(entity);

        return values;
    }

    private static bool IsUntouched(Pending item)
    {
        var properties = MultiLingualMetadata.For(item.Entity.GetType());

        for (var i = 0; i < properties.Count; i++)
        {
            if (!string.Equals(properties[i].GetValue(item.Entity), item.Materialized[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private sealed record Pending(object Entity, string?[] Materialized);
}

/// <summary>
/// The read path, first half. Translatable properties have no column, so a freshly
/// materialized entity has them all <c>null</c>. This interceptor does NOT query for their
/// values inline — a query per row from inside materialization would both re-enter the
/// context mid-query and be a textbook N+1. It only records the instance;
/// <see cref="ITranslationHydrator"/> resolves them all in one round trip afterwards.
///
/// <para><b>One shared instance.</b> EF Core treats materialization interceptors as
/// singleton interceptors: the instance is part of the key EF caches its internal service
/// provider under, so a new instance per request would build a new internal provider per
/// request (and fail after twenty). This one is stateless and shared; it finds the request's
/// <see cref="MaterializedEntityRegistry"/> through the context's application service provider
/// — the scoped provider <c>AddDbContext</c> builds the options from.</para>
/// </summary>
public sealed class MultiLingualMaterializationInterceptor : IMaterializationInterceptor
{
    /// <summary>The shared instance <see cref="MultiLingualDbContextOptionsExtensions.UseMultiLingual"/> attaches.</summary>
    public static MultiLingualMaterializationInterceptor Instance { get; } = new();

    // A context belongs to exactly one scope for its whole life, so its registry is looked up once.
    private readonly ConditionalWeakTable<DbContext, MaterializedEntityRegistry> _registries = new();

    private MultiLingualMaterializationInterceptor() { }

    /// <inheritdoc/>
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (MultiLingualMetadata.IsMultiLingual(entity.GetType()))
            _registries.GetValue(materializationData.Context, ResolveRegistry).Register(entity);

        return entity;
    }

    private static MaterializedEntityRegistry ResolveRegistry(DbContext context)
    {
        var applicationServices = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;

        return applicationServices?.GetService<MaterializedEntityRegistry>()
               ?? throw new InvalidOperationException(
                   $"'{context.GetType().Name}' materialized a [MultiLingual] entity but has no scoped " +
                   "MultiLingual services. Register the context with AddDbContext((sp, options) => " +
                   "options.UseMultiLingual(sp)) and a scoped options lifetime (the default).");
    }
}

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
/// anywhere rolls the entity and its translations back together. (That explicit transaction is
/// incompatible with a retrying execution strategy — with <c>EnableRetryOnFailure</c>, wrap the
/// save in <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c> yourself.)</para>
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
public sealed class MultiLingualSaveChangesInterceptor(IServiceProvider services) : SaveChangesInterceptor
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
        if (eventData.Context is not { } context || Begin(context) is not { } state)
            return result;

        if (NeedsOwnTransaction(context, state))
            state.OwnedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

        return result;
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context || Begin(context) is not { } state)
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
            // Through THIS context, so the rows join its connection and transaction — even when
            // it is not the context type the provider was registered with.
            var writer = Manager.WithStore(new EfCoreTranslationStore(context));

            foreach (var group in state.Deletes.GroupBy(d => d.EntityName, StringComparer.Ordinal))
            {
                await writer.DeleteEntitiesAsync(
                    group.Key, group.Select(d => d.EntityId).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
            }

            var writes = new List<TranslationWrite>();
            foreach (var pending in state.Writes)
            {
                var entityId = EntityKeys.TryGetKey(pending.Entry)
                    ?? throw new InvalidOperationException(
                        $"'{pending.EntityName}' still has no key after SaveChanges, so its translations cannot be stored.");

                writes.Add(new TranslationWrite(pending.EntityName, entityId, pending.Values) { Source = pending.Entry.Entity });
            }

            await writer.WriteAsync(writes, cancellationToken);
            await CommitOwnedTransactionAsync(state, cancellationToken);
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
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) =>
        // Synchronous SaveChanges is supported so nothing breaks if it is used; the translation
        // writes themselves are async all the way down.
        SavedChangesAsync(eventData, result, CancellationToken.None).AsTask().GetAwaiter().GetResult();

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
    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        SaveChangesFailedAsync(eventData, CancellationToken.None).GetAwaiter().GetResult();

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
        public bool HasWork => Writes.Count > 0 || Deletes.Count > 0;

        public void Reset()
        {
            Writes.Clear();
            Deletes.Clear();
            Snapshot = null;
        }
    }

    /// <summary>
    /// The change tracker as it stood before a save: every entry the save will change, and every
    /// tracked translation row. <see cref="Restore"/> puts it back after a rolled-back save.
    /// </summary>
    private sealed class TrackerSnapshot
    {
        private readonly List<EntrySnapshot> _entries;
        private readonly HashSet<object> _translations;

        private TrackerSnapshot(List<EntrySnapshot> entries, HashSet<object> translations)
        {
            _entries      = entries;
            _translations = translations;
        }

        public static TrackerSnapshot Take(IEnumerable<EntityEntry> entries)
        {
            var changed      = new List<EntrySnapshot>();
            var translations = new HashSet<object>(ReferenceEqualityComparer.Instance);

            foreach (var entry in entries)
            {
                if (entry.Entity is Translation)
                    translations.Add(entry.Entity);

                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                    continue;

                changed.Add(new EntrySnapshot(
                    entry,
                    entry.State,
                    entry.CurrentValues.Clone(),
                    entry.State == EntityState.Added ? null : entry.OriginalValues.Clone(),
                    entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToHashSet(StringComparer.Ordinal),
                    entry.Properties.Where(p => p.IsTemporary).Select(p => p.Metadata.Name).ToArray()));
            }

            return new TrackerSnapshot(changed, translations);
        }

        public void Restore(DbContext context)
        {
            // Translation rows the failed write started tracking would collide with the retry's.
            foreach (var entry in context.ChangeTracker.Entries<Translation>().ToList())
            {
                if (!_translations.Contains(entry.Entity))
                    entry.State = EntityState.Detached;
            }

            // States first: deleted entries are detached by now, and must be tracked again before
            // their values can be set.
            foreach (var saved in _entries)
            {
                if (saved.Entry.State != saved.State)
                    saved.Entry.State = saved.State;
            }

            // Then the temporary keys of added entries, principals and dependents alike, before any
            // other value. Fix-up carries a principal's key into its dependents' foreign keys, so
            // restoring keys first keeps every relationship intact.
            foreach (var saved in _entries.Where(e => e.State == EntityState.Added))
            {
                foreach (var name in saved.Temporary)
                {
                    var property = saved.Entry.Property(name);
                    if (property.Metadata.IsPrimaryKey())
                        property.CurrentValue = saved.Current[name];
                }
            }

            foreach (var saved in _entries)
            {
                foreach (var property in saved.Entry.Properties)
                {
                    var metadata = property.Metadata;

                    // Keys of tracked entries cannot change, and added ones were restored above.
                    if (metadata.IsPrimaryKey())
                        continue;

                    var current = saved.Current[metadata];
                    if (!Equals(property.CurrentValue, current))
                        property.CurrentValue = current;

                    if (saved.Original is { } original)
                    {
                        var value = original[metadata];
                        if (!Equals(property.OriginalValue, value))
                            property.OriginalValue = value;
                    }

                    if (saved.State == EntityState.Modified)
                        property.IsModified = saved.Modified.Contains(metadata.Name);
                }

                if (saved.State == EntityState.Added)
                {
                    foreach (var name in saved.Temporary)
                        saved.Entry.Property(name).IsTemporary = true;
                }
            }
        }
    }

    private sealed record EntrySnapshot(
        EntityEntry Entry,
        EntityState State,
        PropertyValues Current,
        PropertyValues? Original,
        HashSet<string> Modified,
        string[] Temporary);

    private sealed record PendingWrite(
        EntityEntry Entry,
        string EntityName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Values);

    private sealed record PendingDelete(string EntityName, string EntityId);
}
