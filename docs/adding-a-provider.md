# Adding a provider

This is what a backend needs. `DotNetBoost.MultiLingual.MongoDb` and
`DotNetBoost.MultiLingual.Dapper` are the worked examples of every step below — providers with no
change tracker, unlike the EF Core one — so read one of them alongside this.

## 1. Implement the two stores

`ITranslationStore` and `ILanguageStore` in `DotNetBoost.MultiLingual.Core.Interfaces`. They are
deliberately dumb: no validation, no fallback, no change detection — that all lives in the
engine behind `IMultiLingualManager`. What a store does have to get right:

| Rule | Why |
|---|---|
| Upserts match on the natural key `(EntityName, EntityId, PropertyName, LanguageCode)`, later rows in a batch win. | There is no surrogate key. |
| A new default language clears the others **atomically** (`AddAsync` with `IsDefault`, `SetDefaultAsync`). | Exactly one default at all times. |
| Never keep references to the caller's `Translation` / `Language` instances. | Callers reuse them. |
| Override the synchronous members (`UpsertMany`, `DeleteEntities`, `ILanguageStore.GetAll`) when the backend has a synchronous API. | Their defaults block on the async members, which can deadlock a synchronous save on a UI thread. |
| Take entity names from `MultiLingualMetadata.EntityNameFor(type)`, never `type.Name`, and size the column for `MultiLingualMetadata.MaxEntityNameLength`. | Types can fix their stored name with `[MultiLingualEntity]`. |
| Writes join whatever unit of work the application is in (Dapper: the ambient `IDbTransaction`; MongoDB: the `IClientSessionHandle`), exposed as a `With…` method that returns a store bound to it. | An entity and its translations commit or roll back together. |

Two of those rules — writes joining the caller's unit of work, and the atomic default-language
flip — need real transactions. On MongoDB that means a session, and a session means a replica set
or a sharded cluster: against a standalone `mongod` both degrade into separate writes that can
leave a half-applied change behind. It is a deployment prerequisite, not an implementation detail,
so state it in the provider's README as well as here.

Shapes as shipped:

- **Dapper** — `Translations` / `Languages` tables identical to the EF provider's (see
  `TranslationConfiguration` / `LanguageConfiguration`), so a database can move between providers;
  `DapperSchema` generates that DDL for all three engines and `UseDapper(..., migrateSchema: true)`
  runs it at startup. `MERGE ... WITH (HOLDLOCK)` on SQL Server, `INSERT ... ON CONFLICT` elsewhere.
  Two traps a relational provider hits that MongoDB does not: identifiers must be **quoted** so
  PostgreSQL does not fold them to lower case and lose sight of the EF provider's tables, and a list
  filter has two spellings — Dapper expands a list into `IN (@p1, @p2)` on most providers but binds
  ONE array parameter on Npgsql, where only `= ANY(@p)` matches what was bound.
- **MongoDB** — as shipped: a `translations` collection whose documents carry the four key fields
  and no `_id` of their own, with a unique compound index on them created by a hosted service
  (`MongoIndexInitializer`), and a `languages` collection where the code IS `_id`. The natural key
  is deliberately NOT a composite `_id`: a query on the sub-fields of `_id` cannot use that index,
  and the key's leading fields are exactly what every read filters on.

## 2. Register them

Add a `UseDapper(...)` / `UseMongoDb(...)` builder extension in the
`Microsoft.Extensions.DependencyInjection` namespace, mirroring `UseEntityFrameworkCore`. As
shipped, minus its options and schema handling:

```csharp
public static MultiLingualBuilder UseDapper(
    this MultiLingualBuilder builder,
    Func<IServiceProvider, IDbConnection> connectionFactory,
    bool migrateSchema = false)
{
    MultiLingualBuilderGuard.EnsureProviderNotConfigured(builder, "Dapper");

    // Scoped and container-owned, so the connection is disposed with the scope and anything else
    // in it that asks for IDbConnection gets the very one the stores use.
    builder.Services.AddScoped(connectionFactory);

    // Scoped too, so a transaction the application puts in the scope is the one the stores enlist in.
    builder.Services.AddScoped<ITranslationStore>(sp => new DapperTranslationStore(
        sp.GetRequiredService<IDbConnection>(), transaction: sp.GetService<IDbTransaction>()));
    builder.Services.AddScoped<ILanguageStore>(sp => new DapperLanguageStore(
        sp.GetRequiredService<IDbConnection>(), transaction: sp.GetService<IDbTransaction>()));

    if (migrateSchema)
        builder.Services.AddSingleton<IHostedService>(sp => new DapperMigrationHostedService(sp, ...));

    return builder;
}
```

A provider whose users have no migrations to hang the schema off can create it itself, behind an
opt-in flag as `migrateSchema` is. Generate the DDL from the same Core constants the EF Core
configuration uses (`MultiLingualMetadata.MaxEntityNameLength`, `EntityKey.MaxLength`,
`LanguageCodes.MaxLength`) so the two cannot describe the same column differently, keep every
statement conditional so it is safe on every boot, and be explicit that it only ever CREATEs —
it can bring a database into existence, not migrate one whose shape has changed.

If the backend knows entity keys better than the naming convention (`[Key]`, `Id`,
`{Type}Id`), replace `IEntityKeyAccessor` too, as the EF Core provider does. **On MongoDB you
must**: read the id from `[BsonId]` / the class map, and return `null` when it is `ObjectId.Empty`.
The driver assigns the id during the insert, so an un-inserted document still holds the empty one —
and `EntityKey.IsUnset` cannot recognise it, because Core does not reference the driver. Left to
the convention, an empty `ObjectId` reads as a perfectly good key and the document's translations
are written under `000000000000000000000000` instead of raising "save the entity first". Compose
the public `ConventionEntityKeyAccessor` for the types you do not recognise yourself.

## 3. Save through `ITranslationWriter`

A provider that saves entities for its users — as the EF Core interceptor does — drives the write
side of the engine in three steps, resolved from the same scope as the application's
`IMultiLingualManager`:

| Step | Call | Why then |
|---|---|---|
| Before the entity is saved | `CaptureValues(entity)` | The in-memory values are the caller's; a store-generated key does not exist yet. |
| Once every key exists | `WithStore(store).WriteAsync(writes)` (and `DeleteEntitiesAsync`) | Inside the same unit of work as the entity, through a store bound to it. |
| Once the commit succeeds | `CompleteWrites(writes)` | Clears staged values and records what was stored. Earlier, a failed commit's retry would skip them. |

Pass writes to `WriteAsync` without a `Source` when you complete them yourself after the commit;
with a `Source`, `WriteAsync` completes them at once. Each step has a synchronous twin for a
synchronous save.

## 4. Keep translated properties out of the entity's own storage

- **MongoDB:** unmap them from the class map for every type `MultiLingualMetadata.For(type)`
  returns properties for — but **only when `MultiLingualOptions.HasBaseLanguage` is false**. In
  base-language mode a translatable property keeps its field, and that field holds the base
  language's value every missing translation falls back to, so unmapping it would throw the base
  text away. Check `HasBaseLanguage` before unmapping anything, and `IsBaseLanguage(code)` wherever
  the provider decides whether a value is a translation or the base value.

  The shipped provider does it with an `IClassMapConvention` (`UnmapTranslatablePropertiesConvention`)
  rather than a pass over the loaded assemblies, because the driver builds a class map lazily, on
  first serialization. That is after the whole builder chain has run, so the convention reads a
  final `HasBaseLanguage` however the chain was ordered, and no type loaded later is missed.
- **Dapper:** nothing to do on read — there is no column, so they stay `null`. The provider
  documents that INSERT/UPDATE statements must not reference them; see `docs/dapper.md`.

## 5. Pass the contract suites

Add the provider to `tests/DotNetBoost.MultiLingual.ProviderTests` (or `IntegrationTests` when it
needs a real server) by inheriting the two suites:

```csharp
public sealed class DapperTranslationStoreTests : TranslationStoreContractTests
{
    protected override Task<(ITranslationStore, ILanguageStore)> CreateStoresAsync() => ...;
}

public sealed class DapperLanguageStoreTests : LanguageStoreContractTests
{
    protected override Task<ILanguageStore> CreateStoreAsync() => ...;
}
```

A provider is done when both are green. Run them against every engine the provider claims: the
Dapper stores pass the suites on SQLite (in `ProviderTests`, where no container is needed) and on
PostgreSQL and SQL Server (in `IntegrationTests`) — and the PostgreSQL list-parameter trap above is
exactly the kind of bug only a real engine finds. Then add the project to the pack step in
`.github/workflows/ci.yml` and uncomment its package versions in `Directory.Packages.props`.

## What users of an untracked provider write

There are no interceptors, so application code calls the same `IMultiLingualManager` an EF Core
application injects — just a few more of its members:

```csharp
// Read
var products = (await connection.QueryAsync<Product>(sql)).ToList();
await multiLingual.LocalizeAsync(products);

// Write — after the entity's own row exists, so its key does
await connection.ExecuteAsync(insertSql, product, transaction);
await multiLingual.For(product).SaveAsync();

// Delete
await multiLingual.For(product).DeleteAsync();
```

The same rules apply as with EF Core: `null` means "not supplied", untouched fallbacks are never
promoted, and values queued with `For(product).Set(...)` / `Clear(...)` are written by the next
`SaveAsync`. `tests/DotNetBoost.MultiLingual.UnitTests/MultiLingualManagerTests.cs` runs exactly
this flow over the in-memory stores.

## What a provider package may use

Everything a provider needs is public, so a provider does not have to live in this repository:
`ITranslationStore`, `ILanguageStore`, `IEntityKeyAccessor`, `ITranslationWriter`,
`MultiLingualBuilder` / `MultiLingualBuilderGuard`, `MultiLingualMetadata` and the `Translation` /
`Language` models. The engine services behind `IMultiLingualManager` are internal; a first-party
provider in this repository that wants them can be added to the `InternalsVisibleTo` list in
`DotNetBoost.MultiLingual.Core.csproj`, as the EF Core package is.
