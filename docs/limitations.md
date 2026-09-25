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
  yourself inside `Database.CreateExecutionStrategy().ExecuteAsync(…)`.
- **Two contexts of the same type** configured with different `BaseLanguage` values in one process
  share EF Core's cached model. Use one mode per context type.

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
