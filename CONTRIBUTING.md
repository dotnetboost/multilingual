# Contributing to DotNetBoost.MultiLingual

Thanks for considering a contribution! This guide covers everything you need to get started.

## Repository layout

```
dotnetboost-multilingual/
├── src/                                          # Library projects (each ships as its own NuGet package)
│   ├── DotNetBoost.MultiLingual.Core
│   ├── DotNetBoost.MultiLingual.EntityFrameworkCore
│   ├── DotNetBoost.MultiLingual.Dapper
│   ├── DotNetBoost.MultiLingual.MongoDb
│   ├── DotNetBoost.MultiLingual.API
│   └── DotNetBoost.MultiLingual.Localization
├── tests/
│   ├── DotNetBoost.MultiLingual.UnitTests        # Core, over in-memory stores
│   ├── DotNetBoost.MultiLingual.ProviderTests    # Store contract suites + EF Core (SQLite) end to end
│   ├── DotNetBoost.MultiLingual.ApiTests         # Middleware and endpoints over TestServer
│   ├── DotNetBoost.MultiLingual.IntegrationTests # Testcontainers, needs Docker
│   └── DotNetBoost.MultiLingual.TestSupport      # In-memory stores shared by the suites
├── samples/
│   ├── SampleApp                                 # EF Core + PostgreSQL, the reference demo
│   ├── SampleApp.Dapper                          # The same API over the Dapper provider
│   └── SampleApp.MongoDb                         # The same API over the MongoDB provider
├── clients/
│   └── dashboard                                 # Nuxt 4 dashboard for languages and content (SPA client)
├── aspire/
│   ├── DotNetBoost.MultiLingual.AppHost          # Orchestrates the three APIs, the dashboard and the containers
│   └── DotNetBoost.MultiLingual.ServiceDefaults  # OTel, health checks, service discovery
└── docs/                                         # Additional documentation
```

[docs/architecture.md](docs/architecture.md) explains how the pieces fit together.

## Prerequisites

- .NET 10 SDK
- .NET 8 runtime — the libraries and most test projects multi-target `net8.0`, so the net8.0
  test pass needs it.
- Docker — for `IntegrationTests`, which start real PostgreSQL, SQL Server and MongoDB servers, and
  for running the samples through the Aspire AppHost.

## Building and testing

```bash
dotnet build
dotnet test tests/DotNetBoost.MultiLingual.UnitTests
dotnet test tests/DotNetBoost.MultiLingual.ProviderTests
dotnet test tests/DotNetBoost.MultiLingual.ApiTests
dotnet test tests/DotNetBoost.MultiLingual.IntegrationTests   # Docker required
```

To run the whole demo stack while you work on it:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

Warnings are errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`), and every
public member of a shipped package needs XML docs.

## Guidelines

- **Core stays provider-neutral.** No EF Core, ASP.NET Core or driver references in
  `DotNetBoost.MultiLingual.Core`. If a provider needs something from the engine, add it to
  `ITranslationWriter` in provider-neutral terms. `IMultiLingualManager` is the only service
  application code injects: keep it small, since every member added to it is one more thing users
  have to understand. The engine services behind it (`ILanguageContext`, `ILanguageCatalog`,
  `ITranslationManager`, `ILanguageManager`) are internal; the sibling packages and tests see them
  through `InternalsVisibleTo` in `DotNetBoost.MultiLingual.Core.csproj`, as the EF Core and MongoDB
  packages do.
- **Stores stay dumb.** Rules go in the managers, so every provider gets them for free.
- **A store change needs a contract test.** Add it to `TranslationStoreContractTests` /
  `LanguageStoreContractTests`, so every provider — including the in-memory doubles the other
  suites rely on — is held to it.
- **Behaviour the three providers share belongs in all three samples.** `samples/SampleApp`,
  `SampleApp.Dapper` and `SampleApp.MongoDb` serve the same routes and the same bodies from shared
  DTO files on purpose: that is what proves the provider is invisible from outside.
- New providers: see [docs/adding-a-provider.md](docs/adding-a-provider.md).

## Documentation

The root [README.md](README.md) is packed into every NuGet package, so its links must be **absolute**
GitHub URLs — a relative one breaks on nuget.org. Pages under `docs/` link to each other relatively.

Keep the README short: it is the 5-minute path plus a map. Anything longer belongs on a page under
`docs/`, listed in [docs/README.md](docs/README.md).
