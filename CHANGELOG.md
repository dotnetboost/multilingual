# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/).

## [1.0.0-preview.1] - Unreleased

First public release: translatable content for any entity, driven by a `[MultiLingual]` attribute,
with an Entity Framework Core provider and ASP.NET Core integration. The API may still change
before 1.0.0.

| Package | Contents |
|---|---|
| `DotNetBoost.MultiLingual.Core` | Attributes, the engine and its contracts. No EF Core or ASP.NET Core dependency. |
| `DotNetBoost.MultiLingual.EntityFrameworkCore` | The EF Core provider: mapping, interceptors, translated queries. |
| `DotNetBoost.MultiLingual.API` | `Accept-Language` middleware, language endpoints, problem details, OpenAPI. |

All three target `net8.0` and `net10.0`, and are tested on SQL Server, PostgreSQL and SQLite. On
`net8.0` they need only the 8.0 line of `Microsoft.Extensions` (and EF Core 8), so an application
keeps the versions its runtime ships.

### Declaring translatable content
- `[MultiLingual]` on a class (every string property) or on individual properties, with
  `[NotMultiLingual]` to opt one out.
- `[MultiLingualEntity("name")]` or `[MultiLingual(EntityName = "name")]` fixes the name translations
  are stored under, so a class can be renamed without losing them.
- Mistakes fail at startup or on first use, not in stored data: invalid declarations, two entities
  sharing a name (internal types included), mappings that disagree with the configured mode, and
  keys longer than the 64 characters a translation key holds.

### Two storage modes
- **Default:** every language, including the default, lives in one `Translations` table; translatable
  properties have no column.
- **Base language** (`AddMultiLingual(o => o.BaseLanguage = "en")`): one language stays in each
  entity's own columns — readable in any SQL client, adoptable on a database that already has them —
  and every other language lives in `Translations`, falling back to the column.

### Reading
- `ToLocalizedListAsync` / `FirstLocalizedOrDefaultAsync` / `SingleLocalizedOrDefaultAsync` fill in
  every translatable property — navigations included — in one extra query per entity type, in the
  request's language, falling back to the default language (and in base-language mode to the column).
- Synchronous versions (`ToLocalizedList`, …) that never block on asynchronous work.

### Querying on translated text
- `SelectTranslated(db, p => new Dto { Name = p.Name, Category = p.Category!.Name })` projects in the
  request's language, through navigations and nested collections.
- `WhereTranslation` and `OrderByTranslation` / `ThenByTranslation` (and `…Descending`) filter and
  sort in the database, on the value a read would show.
- In the default mode, a plain query that reads a translatable property is refused with an error
  naming the property and these helpers, instead of silently returning `null`.

### Writing
- Assign the property and call `SaveChanges`: the value is stored for the request's language, and
  other languages are untouched. A `null` means "not supplied"; values that came from a fallback are
  never written back as a translation.
- `ITranslationManager.Stage` writes other languages in the same save; `ITranslationManager.Clear`
  removes a stored translation.
- An entity and its translations are saved atomically, including under retrying execution strategies
  (`EnableRetryOnFailure`). A failed save leaves the change tracker as it was, so it can be retried.
- Synchronous `SaveChanges` is supported and never blocks on asynchronous work.

### Languages and HTTP
- `ILanguageManager` enforces the rules: validated, normalised and immutable codes; exactly one active
  default; a language with translations cannot be deleted until they are purged.
- `UseMultiLingual()` resolves `Accept-Language` (quality-weighted, with fallback to the default) and
  sets `Content-Language` and `Vary: Accept-Language`.
- `MapLanguageEndpoints()` for managing languages; `MultiLingualExceptionHandler` answers the
  library's exceptions with problem details (a missing language resource is a 404, a request that
  uses an unknown language a 400, a broken rule a 409).
- `AddAcceptLanguageParameter()` documents `Accept-Language` in OpenAPI (net10.0).

### Running several instances
- `SynchronizeLanguagesThroughDistributedCache()` shares language changes between instances through
  any `IDistributedCache` (Redis, SQL Server, …), so every instance sees one within seconds.

### Maintenance
- `PurgeOrphanedTranslationsAsync()` removes translations left behind by deletes that bypass
  `SaveChanges` — `ExecuteDelete`, database cascades, raw SQL.

### Extending
- `ITranslationStore` and `ILanguageStore` for other backends, with contract test suites a provider
  must pass; `ITranslationWriter` for the steps of a save; `IEntityKeyAccessor` for key discovery;
  `ILanguageChangeSignal` for other ways to share language changes. See `docs/adding-a-provider.md`.

### Known limitations
Single-column keys only, no `AddDbContextPool`, and SQL Server compatibility level 130 or higher.
The README lists them all, with the reasons.

[1.0.0-preview.1]: https://github.com/dotnetboost/multilingual/releases/tag/v1.0.0-preview.1
