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
/// </summary>
public sealed class MaterializedEntityRegistry
{
    private readonly List<object> _pending = [];
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Safety valve. If a caller streams a huge result set and never hydrates, stop growing
    /// rather than pinning every row for the life of the scope.
    /// </summary>
    public const int Capacity = 20_000;

    /// <summary>Queues <paramref name="entity"/> for the next hydration.</summary>
    public void Register(object entity)
    {
        if (_pending.Count >= Capacity || !_seen.Add(entity))
            return;

        _pending.Add(entity);
    }

    /// <summary>Takes everything queued so far and empties the queue.</summary>
    public IReadOnlyList<object> Drain()
    {
        if (_pending.Count == 0)
            return [];

        var drained = _pending.ToArray();
        _pending.Clear();
        _seen.Clear();
        return drained;
    }

    /// <summary>Number of entities waiting for hydration.</summary>
    public int PendingCount => _pending.Count;
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

        foreach (var entry in context.ChangeTracker.Entries().ToList())
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
        public bool IsApplying { get; set; }
        public bool HasWork => Writes.Count > 0 || Deletes.Count > 0;

        public void Reset()
        {
            Writes.Clear();
            Deletes.Clear();
        }
    }

    private sealed record PendingWrite(
        EntityEntry Entry,
        string EntityName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Values);

    private sealed record PendingDelete(string EntityName, string EntityId);
}
