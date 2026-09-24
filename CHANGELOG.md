# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/).

## [1.0.0-preview.1] - Unreleased

First public release: translatable content for any entity, driven by a `[MultiLingual]` attribute,
with Entity Framework Core, MongoDB and Dapper providers and ASP.NET Core integration. The API may
still change before 1.0.0.

| Package | Contents |
|---|---|
| `DotNetBoost.MultiLingual.Core` | Attributes, the engine and its contracts. No EF Core or ASP.NET Core dependency. |
| `DotNetBoost.MultiLingual.EntityFrameworkCore` | The EF Core provider: mapping, interceptors, translated queries. |
| `DotNetBoost.MultiLingual.MongoDb` | The MongoDB provider: two collections, class-map unmapping, session-scoped writes. |
| `DotNetBoost.MultiLingual.Dapper` | The Dapper provider: the EF Core provider's own schema, optional schema creation, transaction-scoped writes. |
| `DotNetBoost.MultiLingual.API` | `Accept-Language` middleware, language endpoints, problem details, OpenAPI. |
| `DotNetBoost.MultiLingual.Localization` | `IStringLocalizer` over JSON files, in the same language as the content. |

All six target `net8.0` and `net10.0`, and are tested on SQL Server, PostgreSQL, SQLite and
MongoDB 8.0. On `net8.0` they need only the 8.0 line of `Microsoft.Extensions` (and EF Core 8), so an
application keeps the versions its runtime ships.

### One service for application code
- `IMultiLingualManager` is the only service an application injects besides the `DbContext`, in the spirit of
  DotNetBoost.Settings' `ISettingManager`: `CurrentLanguage` / `UseLanguageAsync`, `Languages` (find
  and administer), `For(entity)` (read every language, `Set`, `Clear`, and — without EF Core —
  `SaveAsync` / `DeleteAsync`), and `LocalizeAsync` for providers without a change tracker.
- `For(entity).Set` / `Clear` check the language at once: an unknown one throws
  `LanguageNotFoundException` (404), the base language `BaseLanguageNotTranslatableException` (409).
- The engine services behind it are internal. Storage providers build on the public store contracts
  and `ITranslationWriter`.

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
- Every query is translated, with EF Core's own methods: entities come back with their
  translatable properties — and those of their included navigations — in the request's language,
  falling back to the default language (and in base-language mode to the column), loaded by the
  query's own SQL statement. `ToListAsync`, `First…` / `Single…`, `Find`, streaming, sync queries,
  explicit loading, split queries and every kind of `Include` are covered.
- A tracked entity a later query finds again is refreshed only where nobody has edited it since.
- Also covered: lazy-loading proxies, compiled queries, `Concat` / `Union`, queries composed on
  `FromSql`, `ExecuteUpdate`, automatic includes, and inheritance — a query over a base type fills
  the properties each derived type adds.
- `Translation` and `Language` are no longer sealed, so a model that uses EF Core's lazy-loading
  proxies can include them; a proxy is named and described as the entity class it stands for.

### Querying on translated text
- Plain LINQ: every read of a translatable property in `Where`, `OrderBy` and `Select` — through
  navigations and nested collections, and in `Count`, `Any` or `ExecuteDelete` — is the value a read
  would show, in the database. Ordinary column filters are untouched and keep their indexes.
- `InLanguage("fr")` reads one query in another language; `IgnoreTranslations()` reads the stored
  columns, and in the default mode refuses a query that reads a translatable property, instead of
  silently returning `null`.
- One compiled query serves every language: the SQL holds placeholders, filled in per execution
  by a command interceptor. Entity and property names are literals, so there are no per-value
  parameters.

### Writing
- Assign the property and call `SaveChanges`: the value is stored for the request's language, and
  other languages are untouched. A `null` means "not supplied"; values that came from a fallback are
  never written back as a translation.
- `multiLingual.For(entity).Set("fr", p => p.Name, …)` writes other languages in the same save;
  `multiLingual.For(entity).Clear("fr")` removes a stored translation.
- An entity and its translations are saved atomically, including under retrying execution strategies
  (`EnableRetryOnFailure`). A failed save leaves the change tracker as it was, so it can be retried.
- Synchronous `SaveChanges` is supported and never blocks on asynchronous work.

### Languages and HTTP
- `multiLingual.Languages` enforces the rules: validated, normalised and immutable codes; exactly one active
  default; a language with translations cannot be deleted until they are purged.
- `UseMultiLingual()` resolves `Accept-Language` (quality-weighted, with fallback to the default) and
  sets `Content-Language` and `Vary: Accept-Language`.
- `MapLanguageEndpoints()` for managing languages; `MultiLingualExceptionHandler` answers the
  library's exceptions with problem details (a missing language resource is a 404, a request that
  uses an unknown language a 400, a broken rule a 409).
- `AddAcceptLanguageParameter()` documents `Accept-Language` in OpenAPI (net10.0).
- `UseMultiLingual()` also sets `CultureInfo.CurrentUICulture` to the resolved language (and, opted in
  with `SetCurrentCulture`, `CurrentCulture`); `multiLingual.ApplyUiCulture()` does the same outside the middleware.

### Localized messages
- `AddJsonLocalization()` serves `IStringLocalizer<T>` from `Resources/{language}.json`: nested
  objects become dotted keys, and a message missing in the requested language falls back exactly as
  content does — parent language, default language, then the base language.
- The injected localizer reads the scope's current language, so it answers in the request's
  language in exception handlers and background jobs too; the factory's follows `CurrentUICulture`,
  for framework code such as validation messages.
- Files are checked at startup and reloaded when they change.
- The library's own problem details can be translated in the same files, under
  `MultiLingual.Problems.*`.

### Running several instances
- `SynchronizeLanguagesThroughDistributedCache()` shares language changes between instances through
  any `IDistributedCache` (Redis, SQL Server, …), so every instance sees one within seconds.

### Maintenance
- `PurgeOrphanedTranslationsAsync()` removes translations left behind by deletes that bypass
  `SaveChanges` — `ExecuteDelete`, database cascades, raw SQL.

### MongoDB
- `services.AddMultiLingual().UseMongoDb().Build()` — over an `IMongoDatabase` the application
  registers, a connection string and database name, or a factory for one database per tenant.
- Translations live in one collection, identified by a unique compound index on
  `(EntityName, EntityId, PropertyName, LanguageCode)` that a hosted service creates at startup;
  its leading fields are what every read filters on, so reads need no index of their own. Languages
  live in a collection where the code IS `_id`.
- `[MultiLingual]` properties are unmapped from the class map, so they are never stored on the
  entity's own document — except in base-language mode, where that field is the base language's
  value.
- Ids come from the driver's own mapping (`[BsonId]`, the class map, its conventions). An `ObjectId`
  the driver has not assigned yet counts as no key, so saving an un-inserted document is refused
  rather than storing its translations under `000000000000000000000000`.
- `store.WithSession(session)`, with `ITranslationWriter.WithStore`, puts a document and its
  translations in one transaction. That needs a replica set or a sharded cluster; against a
  standalone `mongod` each write stands alone.
- No change tracker to hook, so application code localizes after its own queries and saves through
  `IMultiLingualManager` — and filtering or sorting on translated values in the database stays an
  EF Core feature.

### Dapper
- `services.AddMultiLingual().UseDapper(sp => new SqlConnection(cs), migrateSchema: true).Build()`.
  The connection factory's runtime type selects the dialect — SQL Server, PostgreSQL or SQLite —
  and `DapperOptions` configures table and schema names, or an explicit dialect for a wrapped
  connection.
- The tables are the ones the EF Core provider maps — same names, lengths, composite primary key,
  restricting foreign key and index names — so one database serves either provider. Column lengths
  come from the same Core constants both providers use, so the two cannot drift.
- `migrateSchema: true` adds a hosted service that creates those tables at startup if they are
  absent; `DapperSchema.Script(dialect)` returns the same DDL for a migrations folder, and
  `DapperSchema.EnsureCreatedAsync(connection)` runs it from a deployment step. Every statement is
  conditional, so it is safe on every boot — but it only ever CREATEs, and will not migrate a table
  whose shape has changed. Off by default.
- Upserts are `INSERT ... ON CONFLICT` on PostgreSQL and SQLite, `MERGE ... WITH (HOLDLOCK)` on SQL
  Server; a batch is folded to one outcome per natural key first, so the later row wins and a
  `null` value removes the row rather than storing a null.
- `store.WithTransaction(transaction)`, with `ITranslationWriter.WithStore`, puts an entity and its
  translations in one transaction; an application that registers a scoped `IDbTransaction` gets that
  automatically. Without one, a multi-row write takes a transaction of its own for the duration.
- Identifiers are quoted in their declared casing on every engine, so PostgreSQL does not fold them
  to lower case and lose sight of the EF Core provider's tables.
- Keys come from Core's convention (`[Key]`, `Id`, `{Type}Id`) — Dapper maps plain objects, so there
  is nothing better to read.
- No change tracker to hook, so application code localizes after its own queries and saves through
  `IMultiLingualManager` — and filtering or sorting on translated values in the database stays an
  EF Core feature.
- `samples/SampleApp.Dapper` is the EF Core sample's API over this provider, endpoint for endpoint,
  on its own database beside it. It is also where the unit of work is shown: one transaction per
  request, so a row and its translations commit or roll back together.

### Extending
- `ITranslationStore` and `ILanguageStore` for other backends, with contract test suites a provider
  must pass; `ITranslationWriter` for the steps of a save; `IEntityKeyAccessor` for key discovery;
  `ILanguageChangeSignal` for other ways to share language changes. See `docs/adding-a-provider.md`.

### Dashboard
- A Nuxt 4 single-page client in `clients/dashboard`, built on the Nuxt UI dashboard template: a
  Languages page over `MapLanguageEndpoints()`, and a Content page that lists the sample's entities
  in any language, creates and deletes them, and edits every field of each, one language at a time.
  Nitro proxies the API, so no CORS setup is needed.

### Aspire
- `aspire/DotNetBoost.MultiLingual.AppHost` starts PostgreSQL (with pgweb), MongoDB (with Mongo
  Express), all three sample APIs and the dashboard from one `dotnet run` (or `aspire run`),
  injecting each connection string and every dashboard URL. The Dapper sample takes a second
  database on the same PostgreSQL server, so its schema and the EF Core one can be compared in
  pgweb. SQL Server and SQLite are written out in full next to it, commented.
- `aspire/DotNetBoost.MultiLingual.ServiceDefaults`: OpenTelemetry over OTLP (Npgsql's traces and
  metrics included), `/health` with a `DbContext` check and `/alive`, service discovery and
  resilient `HttpClient` defaults.
- The sample runs on PostgreSQL instead of SQLite, with a bounded retry-on-failure strategy and a
  case-insensitive (`ILIKE`) product search.

### Known limitations
Single-column keys only, no `AddDbContextPool`, and SQL Server compatibility level 130 or higher.
The README lists them all, with the reasons.

[1.0.0-preview.1]: https://github.com/dotnetboost/multilingual/releases/tag/v1.0.0-preview.1
