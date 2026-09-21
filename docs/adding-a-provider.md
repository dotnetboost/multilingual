# Adding a provider

Dapper and MongoDB are planned. This is what either — or any other backend — needs.

## 1. Implement the two stores

`ITranslationStore` and `ILanguageStore` in `DotNetBoost.MultiLingual.Core.Interfaces`. They are
deliberately dumb: no validation, no fallback, no change detection — that all lives in
`ITranslationManager` and `ILanguageManager`. What a store does have to get right:

| Rule | Why |
|---|---|
| Upserts match on the natural key `(EntityName, EntityId, PropertyName, LanguageCode)`, later rows in a batch win. | There is no surrogate key. |
| A new default language clears the others **atomically** (`AddAsync` with `IsDefault`, `SetDefaultAsync`). | Exactly one default at all times. |
| Never keep references to the caller's `Translation` / `Language` instances. | Callers reuse them. |
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

## 3. Keep translated properties out of the entity's own storage

- **MongoDB:** unmap them from the class map (`BsonClassMap` → `UnmapMember`) for every type
  `MultiLingualMetadata.For(type)` returns properties for.
- **Dapper:** nothing to do on read — there is no column, so they stay `null`. Document that
  INSERT/UPDATE statements must not reference them.

## 4. Pass the contract suites

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

There are no interceptors, so application code calls the engine explicitly:

```csharp
// Read
var products = (await connection.QueryAsync<Product>(sql)).ToList();
await translations.HydrateAsync(products);

// Write — after the entity's own row exists, so its key does
await connection.ExecuteAsync(insertSql, product, transaction);
await translations.SaveAsync(product);

// Delete
await translations.DeleteAsync(product);
```

The same rules apply as with EF Core: `null` means "not supplied", untouched fallbacks are never
promoted, and staged values (`translations.Stage(...)`) are written by the next `SaveAsync`.
