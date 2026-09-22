# Adding a provider

Dapper and MongoDB are planned. This is what either — or any other backend — needs.

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
| Writes join whatever unit of work the application is in (Dapper: the ambient `IDbTransaction`; MongoDB: the `IClientSessionHandle`). | An entity and its translations commit or roll back together. |

Suggested shapes:

- **Dapper** — `Translations` / `Languages` tables identical to the EF provider's (see
  `TranslationConfiguration` / `LanguageConfiguration`), so a database can move between providers.
  `MERGE` / `INSERT ... ON CONFLICT` for upserts.
- **MongoDB** — a `translations` collection with a unique compound index on the natural key
  (create it from a hosted service, like `DotNetBoost.Settings.MongoDb` does), and a `languages`
  collection keyed by code.

## 2. Register them

Add a `UseDapper(...)` / `UseMongoDb(...)` builder extension in the
`Microsoft.Extensions.DependencyInjection` namespace, mirroring `UseEntityFrameworkCore`:

```csharp
public static MultiLingualBuilder UseDapper(this MultiLingualBuilder builder, Func<IServiceProvider, IDbConnection> connection)
{
    MultiLingualBuilderGuard.EnsureProviderNotConfigured(builder, "Dapper");
    builder.Services.AddScoped<ITranslationStore>(sp => new DapperTranslationStore(connection(sp)));
    builder.Services.AddScoped<ILanguageStore>(sp => new DapperLanguageStore(connection(sp)));
    return builder;
}
```

If the backend knows entity keys better than the naming convention (`[Key]`, `Id`,
`{Type}Id`), replace `IEntityKeyAccessor` too — MongoDB could read `[BsonId]` / the class map.

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

- **MongoDB:** unmap them from the class map (`BsonClassMap` → `UnmapMember`) for every type
  `MultiLingualMetadata.For(type)` returns properties for.
- **Dapper:** nothing to do on read — there is no column, so they stay `null`. Document that
  INSERT/UPDATE statements must not reference them.

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

A provider is done when both are green. Then add the project to the pack step in
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
