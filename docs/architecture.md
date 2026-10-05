# Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Your application                                                        │
│  entity.Name — plain EF Core          IMultiLingualManager               │
└──────────────┬──────────────────────────────────┬───────────────────────┘
               │                                  │
   EF Core interceptors                   ITranslationManager
   (query, command, materialize, save)    ILanguageManager  ILanguageContext
               │                                  │
               └──────────────┬───────────────────┘
                              │
                ITranslationStore   ILanguageStore
                (EF Core / Dapper / MongoDB / your own)
                              │
                 Translations          Languages
      (EntityName, EntityId,        (Code, Name, Direction,
       PropertyName, LanguageCode)   IsDefault, IsActive)
```

`IMultiLingualManager` is the one service application code injects. Behind it, the engine splits
into a language side and a translation side, both scoped to one unit of work — an HTTP request, a
job run — because the current language and the pending writes belong to that scope. Underneath,
two deliberately dumb store contracts: no validation, no fallback, no change detection, so a new
backend inherits every rule for free.

`MultiLingualMetadata` works out once per type which properties are translatable and under what
entity name their rows are filed. `ILanguageCatalog` is the singleton cache of the language list.

The fallback rule — current language, then the default language — lives in exactly one method,
`TranslationManager.Plan`. The EF Core interceptors, the untracked hydration path and the subject
reads (`For(entityName, entityId)`) all plan their reads through it, so none of them can resolve
the same rows differently from the others. A subject passes `columnBacked: false`, which is the one
difference: it has no table of its own, so a configured base language is an ordinary stored
language for it rather than the unfetched end of the chain.

The EF Core provider adds four interceptors on top, which is what makes reads and writes invisible
there; [translated queries](querying.md#how-it-works) explains how each one contributes. Providers
without a change tracker have no equivalent, so their users drive the same engine explicitly
through `IMultiLingualManager`.

## Packages

| Package | Depends on | Contents |
|---|---|---|
| `…MultiLingual.Core` | `Microsoft.Extensions.*` only | Attributes, the engine, the store contracts. No EF Core, no ASP.NET Core |
| `…MultiLingual.EntityFrameworkCore` | Core, EF Core | Mapping, four interceptors, translated queries, orphan purge |
| `…MultiLingual.Dapper` | Core, Dapper | The EF Core provider's own schema, optional schema creation, transaction-scoped writes |
| `…MultiLingual.MongoDb` | Core, the driver | Two collections, class-map unmapping, session-scoped writes |
| `…MultiLingual.API` | Core, ASP.NET Core | `Accept-Language` middleware, language and translation endpoints, problem details, OpenAPI (`net10.0`) |
| `…MultiLingual.Localization` | Core | `IStringLocalizer` over JSON files |

All six target `net8.0` and `net10.0`. On `net8.0` they need only the 8.0 line of
`Microsoft.Extensions` (and EF Core 8), so an application keeps the versions its runtime ships.

The engine services behind `IMultiLingualManager` are internal. Everything a storage provider needs
is public, so a provider does not have to live in this repository — see
[adding a provider](adding-a-provider.md).

## Repository layout

```
dotnetboost-multilingual/
├── src/                                          # Library projects (each = 1 NuGet package)
│   ├── DotNetBoost.MultiLingual.Core/
│   ├── DotNetBoost.MultiLingual.EntityFrameworkCore/
│   ├── DotNetBoost.MultiLingual.Dapper/
│   ├── DotNetBoost.MultiLingual.MongoDb/
│   ├── DotNetBoost.MultiLingual.API/
│   └── DotNetBoost.MultiLingual.Localization/
├── tests/
│   ├── DotNetBoost.MultiLingual.UnitTests/        # Core logic, over in-memory stores
│   ├── DotNetBoost.MultiLingual.ProviderTests/    # Store contracts + EF Core end to end, on SQLite
│   ├── DotNetBoost.MultiLingual.ApiTests/         # Middleware and endpoints over TestServer
│   ├── DotNetBoost.MultiLingual.IntegrationTests/ # The same contracts on real engines (needs Docker)
│   └── DotNetBoost.MultiLingual.TestSupport/      # In-memory stores shared by the suites
├── samples/
│   ├── SampleApp/                                 # EF Core + PostgreSQL, the reference demo
│   ├── SampleApp.Dapper/                          # The same API over the Dapper provider
│   └── SampleApp.MongoDb/                         # The same API over the MongoDB provider
├── clients/
│   └── dashboard/                                 # Nuxt 4 client for languages and content
├── aspire/
│   ├── DotNetBoost.MultiLingual.AppHost/          # Orchestrates the three APIs + dashboard + containers
│   └── DotNetBoost.MultiLingual.ServiceDefaults/  # OTel, health checks, service discovery
├── docs/
├── .github/
│   ├── workflows/ci.yml
│   └── ISSUE_TEMPLATE/
├── Directory.Build.props                          # Shared MSBuild settings (net8.0;net10.0, analyzers)
├── Directory.Packages.props                       # Central package version management
├── DotNetBoost.MultiLingual.sln
├── aspire.config.json                             # Points `aspire run` at the AppHost
├── CHANGELOG.md
├── CONTRIBUTING.md
└── README.md
```

The three samples serve the same routes and the same request and response bodies — the DTOs and the
error handling are literally the same files, compiled into all three — so the dashboard can be
pointed at any of them by changing one line in `AppHost.cs`. That is the clearest evidence that the
provider is invisible from outside.

## Testing

```bash
dotnet test                                                  # everything (integration tests need Docker)
dotnet test tests/DotNetBoost.MultiLingual.UnitTests         # engine logic, in-memory stores
dotnet test tests/DotNetBoost.MultiLingual.ProviderTests     # store contracts + EF Core on SQLite
dotnet test tests/DotNetBoost.MultiLingual.ApiTests          # middleware and endpoints
dotnet test tests/DotNetBoost.MultiLingual.IntegrationTests  # PostgreSQL, SQL Server, MongoDB — needs Docker
dotnet test --collect:"XPlat Code Coverage"                  # with coverage
```

Adding a store provider? Inherit `TranslationStoreContractTests` and `LanguageStoreContractTests`
and implement their factory methods — you get the whole behavioural contract (upserts on the
natural key, atomic default-language flips, batch deletes, purges) for free, guaranteeing your
provider behaves identically to the built-in ones. A provider is done when both suites are green
against every engine it claims.

Warnings are errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`), and every public
member of a shipped package needs XML docs.
