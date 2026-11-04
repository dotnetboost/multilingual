# Known limitations

What this library does not do, and what to do instead.

## Requirements you cannot configure away

- **Composite keys are not supported.** `Translation.EntityId` holds a single value. Give the
  entity a surrogate key before marking it `[MultiLingual]`.
- **Keys up to 64 characters** (`EntityKey.MaxLength`), written as text. Every numeric key and a
  `Guid` fit; a longer `string` key is refused when its translations are written.
- **SQL Server compatibility level 130 or higher** (SQL Server 2016+). EF Core 8 and later send a
  list of keys — which reads and writes of translations use — as one JSON parameter that SQL Server
  unpacks with `OPENJSON`. On a database at an older level, say so and EF sends the keys inline
  instead: `UseSqlServer(connectionString, o => o.UseCompatibilityLevel(120))`.

## Entity Framework Core

- **`AddDbContextPool` is not supported.** Pooled options are built once, from the root provider,
  and cannot carry the per-request services the interceptors need. Use `AddDbContext` with the
  `(sp, options)` overload.
- **Relational providers only.** The languages reach a query through command interception, which
  EF Core offers for relational databases — SQL Server, PostgreSQL, SQLite.
- **Deletes outside `SaveChanges` leave orphaned translations.** `ExecuteDelete`, a database
  cascade over rows EF never loaded, and raw SQL all bypass the save interceptor. The rows are
  never read, but an entity created again under the same key would inherit them. Clean up with
  `await db.PurgeOrphanedTranslationsAsync()` — after a bulk delete, or on a schedule. See
  [deleting entities](reading-and-writing.md#deleting-entities-and-orphaned-rows).
- **What a query does not fill.** Entities returned directly, inside a final `Select`
  (`new { p, p.Category }`), or through `Include` are filled. Entities in a collection placed
  inside a projection (`Select(c => new { c.Products })`), in `GroupBy` results, or loaded outside
  EF Core are not; pass them to `multiLingual.LocalizeAsync(entities)`.
- **`ToQueryString()` and EF's SQL logs show placeholders** (`'~ml:L~'`) where the languages go:
  they print the compiled SQL, before the command interceptor fills in the request's languages.
- **Retrying execution strategies** (`EnableRetryOnFailure`) work with a plain `SaveChanges`: the
  save interceptor runs the entity, its translations and the commit inside the strategy, and
  replays them together on a transient failure. Two side effects — other interceptors see an extra
  `SavingChanges`/`SavedChanges` pair for the replayed save, and changes are accepted on success
  even under `SaveChanges(acceptAllChangesOnSuccess: false)`. To avoid both, open the transaction
  yourself inside `Database.CreateExecutionStrategy().ExecuteAsync(…)` — unless another
  interceptor on the context saves again after a save; see the next section.
- **Two contexts of the same type** configured with different `BaseLanguage` values in one process
  share EF Core's cached model. Use one mode per context type.

## Sharing a DbContext with other SaveChanges interceptors

Another library's interceptor on the same context (an audit trail, say) changes one thing: **who
owns the transaction**. What the save interceptor does at each step:

| Step | What it does |
|---|---|
| `SavingChanges` | Captures the translation work. When there is some and no transaction is open (neither `Database.CurrentTransaction` nor an ambient `TransactionScope`), opens one |
| `SavedChanges` | Writes the translation rows through the same context. That is a nested `SaveChanges`, which other interceptors see too. Then commits the transaction it opened, unless another `SaveChanges` interceptor is registered after it (next row) |
| EF's `DbContext.SavedChanges` event | Commits the transaction it opened, when another `SaveChanges` interceptor follows it. EF raises the event once every interceptor's `SavedChanges` has returned, so their post-save work is inside the transaction too |
| `SaveChangesFailed`, `SaveChangesCanceled`, `ThrowingConcurrencyException` | Rolls back the transaction it opened, and puts the change tracker back so the save can be retried, detaching any row the failed save started tracking |

EF Core runs interceptors in the order they were added, and each one's `SavedChanges` may do more
work: an audit trail writes the entries that needed the generated keys, often with a `SaveChanges`
of its own. Without a retrying strategy, that work is inside this library's transaction in either
order:

- registered **before** `UseMultiLingual`, its `SavedChanges` runs first, and a `SaveChanges` it
  issues there is recognised as nested inside the save in progress: its work joins that save, which
  alone writes, commits or rolls back;
- registered **after**, its `SavedChanges` runs before the commit, which waits for the event. That
  commit is synchronous, even under `SaveChangesAsync`, because the event is: one blocking round
  trip, and only on a context where another `SaveChanges` interceptor follows this one.

Two things stay true. The transaction exists only when the save writes translations: a save that
changes only a price opens none, so another interceptor cannot rely on it. And under a retrying
strategy the commit happens inside the strategy (below), before an interceptor registered first has
finished.

What to do:

| Your setup | Do this |
|---|---|
| No retrying execution strategy | Nothing, for saves that write translations: every interceptor's work is inside this library's transaction, in either order. For every save to be atomic, those that write no translations too, open the transaction yourself around `SaveChanges` (below), and add `.OwnTransactions(false)` so that a save which would otherwise have opened its own (one that bypasses the override, say) is refused instead |
| A retrying strategy (`EnableRetryOnFailure`) | Keep `OwnTransactions` on and register `UseMultiLingual(sp)` **before** the other interceptors. The save interceptor then runs the whole save, every interceptor's work included, inside one transaction under the strategy, and replays it as a unit. This one opens a transaction whenever a translatable entity is tracked |

The application-owned transaction, in the context. Override the synchronous `SaveChanges(bool)`
the same way if you call it:

```csharp
public override async Task<int> SaveChangesAsync(bool accept, CancellationToken ct = default)
{
    if (Database.CurrentTransaction is not null || Transaction.Current is not null)
        return await base.SaveChangesAsync(accept, ct);

    await using var tx = await Database.BeginTransactionAsync(ct);
    var result = await base.SaveChangesAsync(accept, ct);
    await tx.CommitAsync(ct);
    return result;
}
```

Do not combine that with a retrying strategy through EF's usual
`ExecuteAsync(… SaveChangesAsync(acceptAllChangesOnSuccess: false) … AcceptAllChanges())` pattern
when another interceptor saves again from its `SavedChanges`. Its changes are not accepted yet when
that second save runs, so the second save writes them again: an added row is inserted twice, and a
deleted one fails as a concurrency conflict.

## Providers without a change tracker

Dapper and MongoDB have no LINQ pipeline to rewrite, so two features are EF Core only:

- **Filtering and sorting on translated text in the database.** Filter on what you store, localize,
  then sort in memory — or write a `$lookup` pipeline / a join of your own for a large collection.
- **`PurgeOrphanedTranslationsAsync`**, which joins `Translations` against each entity's own table.
  Collect the keys *before* deleting, and pass them to `ITranslationWriter.DeleteEntitiesAsync`.

Reads and writes are explicit there rather than automatic; see
[providers without a change tracker](storage-providers.md#providers-without-a-change-tracker).

## Base-language mode

- **Adding a translatable property** needs no migration in the default mode, and a migration for
  the new column in base-language mode.
- **Changing `BaseLanguage`** after content exists is a data migration, not a configuration change:
  the column and the translation rows would have to swap.
- **An entity can only be created in the base language.** Add other languages with a follow-up
  update, one language per request. See [base-language mode](base-language.md#three-rules-the-mode-enforces).

## MongoDB

- **Transactions need a replica set or a sharded cluster.** Against a standalone `mongod` each
  write stands alone, and so do the two halves of a default-language change — the promotion lands
  before the demotion, so a reader may briefly see two defaults rather than none. See
  [MongoDB](mongodb.md#transactions).
