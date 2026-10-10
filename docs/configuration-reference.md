# Configuration reference

## `AddMultiLingual()` builder methods

Chain them after `services.AddMultiLingual()` and finish with `.Build()`, which validates the
configuration and every `[MultiLingual]` type in the loaded assemblies.

| Method | Package | Description |
|---|---|---|
| `.UseEntityFrameworkCore<TContext>()` | EntityFrameworkCore | Backing store. `TContext` is any `DbContext` that calls `ApplyMultiLingual()` in `OnModelCreating` |
| `.UseDapper(connectionFactory, migrateSchema, configure)` | Dapper | Backing store; optionally creates the two tables at startup |
| `.UseMongoDb(configure)` | MongoDb | Backing store, over the `IMongoDatabase` your application registers |
| `.UseMongoDb(connectionString, databaseName, configure)` | MongoDb | Backing store, over a client the provider owns |
| `.UseMongoDb(databaseFactory, configure)` | MongoDb | Backing store, one database per tenant |
| `.UseCustomStores<TTranslationStore, TLanguageStore>()` | Core | A backing store this library does not ship |
| `.UseCustomStores(translationStoreFactory, languageStoreFactory)` | Core | The same, from factories |
| `.WithBaseLanguage(code)` | Core | Base-language mode; same as `AddMultiLingual(o => o.BaseLanguage = code)` |
| `.DisableDefaultLanguageFallback()` | Core | A missing translation reads as `null` instead of the default language's value |
| `.WithLanguageCacheDuration(TimeSpan)` | Core | Override the 5-minute default |
| `.SynchronizeLanguagesThroughDistributedCache(checkInterval, key)` | Core | Every instance sees a language change within seconds, through a shared `IDistributedCache` |
| `.UseLanguageChangeSignal<TSignal>(checkInterval)` | Core | The same, over a backend of your own |
| `.UseLanguageChangeSignal(factory, checkInterval)` | Core | The same, from a factory |
| `.UseEntityKeyAccessor<TAccessor>()` | Core | Replace how entity keys are read |
| `.DisableStartupValidation()` | Core | Do not check the base language at startup |
| `.ExposeOverHttp(configure)` | API | Names the entities whose translations `MapTranslationEndpoints()` serves. Nothing is served until they are listed |
| `.AddJsonLocalization(configure)` | Localization | `IStringLocalizer<T>` over JSON message files |
| `.Build()` | Core | Validate the configuration and the model, return `IServiceCollection` |

Only one storage provider may be configured; a second one throws, naming the first.

## `MultiLingualOptions`

Set through `AddMultiLingual(o => …)` or the builder methods above. Registered as a singleton.

| Property | Default | Description |
|---|---|---|
| `BaseLanguage` | `null` | The language stored in each entity's own columns. See [base-language mode](base-language.md). Validated and canonicalised on assignment (`"EN"` → `"en"`) |
| `FallbackToDefaultLanguage` | `true` | Whether a missing translation is read from the default language |
| `LanguageCacheDuration` | 5 minutes | How long each instance caches the language list |
| `LanguageChangeCheckInterval` | 5 seconds | How often the cached list asks the change signal, when one is registered |
| `ValidateBaseLanguageAtStartup` | `true` | Whether the base language is checked at startup. A no-op without one |
| `HasBaseLanguage`, `IsBaseLanguage(code)` | — | Read-only; what provider code branches on |

## ASP.NET Core

| Call | Description |
|---|---|
| `app.UseMultiLingual()` | Resolve `Accept-Language` into the request's language |
| `app.UseMultiLingual(o => …)` | The same, with `MultiLingualRequestOptions` |
| `app.MapLanguageEndpoints(prefix)` | Language administration under `prefix` (default `/api/languages`). Returns the route group |
| `app.MapTranslationEndpoints(prefix)` | Translation reads and writes under `prefix` (default `/api/translations`), for the names given to `.ExposeOverHttp(...)`. Returns the route group |
| `services.AddExceptionHandler<MultiLingualExceptionHandler>()` | Library exceptions as problem details |
| `AddOpenApi(o => o.AddAcceptLanguageParameter())` | Document `Accept-Language` on every operation (`net10.0`) |
| `[LanguageAgnostic]` | Leave one operation or controller out of that |

`MultiLingualRequestOptions`:

| Property | Default | Description |
|---|---|---|
| `SetCurrentUICulture` | `true` | Set `CultureInfo.CurrentUICulture` to the resolved language for the rest of the pipeline |
| `SetCurrentCulture` | `false` | Also set `CultureInfo.CurrentCulture`. Off by default: it changes how numbers and dates are parsed |

## Entity Framework Core

| Call | Description |
|---|---|
| `modelBuilder.ApplyMultiLingual(configure)` | Map the two tables, and unmap translatable properties. Call **last** in `OnModelCreating` |
| `modelBuilder.ApplyMultiLingual(this, configure)` | The same, reading the engine's options from the context's application services. Use this whenever `BaseLanguage` is set |
| `modelBuilder.ApplyMultiLingual(engineOptions, configure)` | The same, with the options supplied directly — for design-time contexts |
| `modelBuilder.IgnoreMultiLingualProperties(…)` | Only the unmapping, if you map the two tables yourself |
| `options.UseMultiLingual(sp)` | Attach the four interceptors. Use the `(sp, options)` overload of `AddDbContext` |
| `query.InLanguage(code)` | Read one query in a specific language |
| `query.IgnoreTranslations()` | Read one query's stored columns, untranslated |
| `db.PurgeOrphanedTranslationsAsync(ct)` | Delete translation rows whose entity is gone |

`MultiLingualModelOptions` (the `configure` argument): `TranslationsTable` (`Translations`),
`LanguagesTable` (`Languages`), `Schema` (the model's default).

## Dapper

`DapperOptions`: `TranslationsTable` (`Translations`), `LanguagesTable` (`Languages`), `Schema`
(`null`), `Dialect` (inferred from the connection type — set it when the connection is a wrapper).

`DapperSchema.Script(dialect, options)` returns the DDL; `DapperSchema.EnsureCreatedAsync(connection)`
runs it. See [Dapper](dapper.md).

## MongoDB

`MongoDbOptions`: `TranslationsCollection` (`translations`), `LanguagesCollection` (`languages`),
`CreateIndexes` (`true`). See [MongoDB](mongodb.md).

## Localization

`JsonLocalizationOptions`: `ResourcesPath` (`Resources`), `FileProvider` (the content root),
`ReloadOnChange` (`true`), `ValidateFilesAtStartup` (`true`). See
[localized messages](localization.md).

## Other public helpers

| Member | Description |
|---|---|
| `services.ValidateMultiLingualAsync(ct)` | Run the base-language check yourself, after `DisableStartupValidation()` |
| `multiLingual.ApplyUiCulture(includeFormatting)` | Set the ambient culture to the current language for the calling method. Synchronous, so it survives the call returning |
| `LanguageCodes.Normalize(code)`, `TryNormalize`, `InferDirection(code)`, `MaxLength` | Code validation and canonicalisation |
| `MultiLingualMetadata.For(type)`, `EntityNameFor(type)`, `MaxEntityNameLength` | Which properties are translatable, and under what name they are stored |
| `EntityKey.MaxLength` | 64 — the longest key a translation row holds |
| `MultiLingualProblems.StatusCodeFor(ex)`, `ToProblemDetails(ex, httpContext)` | The exception-to-HTTP mapping, if you handle the exceptions yourself |
