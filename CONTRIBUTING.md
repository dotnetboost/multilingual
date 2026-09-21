# Contributing to DotNetBoost.MultiLingual

Thanks for considering a contribution! This guide covers everything you need to get started.

## Repository layout

```
dotnetboost-multilingual/
├── src/                                          # Library projects (each ships as its own NuGet package)
│   ├── DotNetBoost.MultiLingual.Core
│   ├── DotNetBoost.MultiLingual.EntityFrameworkCore
│   └── DotNetBoost.MultiLingual.API
├── tests/
│   ├── DotNetBoost.MultiLingual.UnitTests        # Core, over in-memory stores
│   ├── DotNetBoost.MultiLingual.ProviderTests    # store contract suites + EF Core (SQLite) end to end
│   ├── DotNetBoost.MultiLingual.ApiTests         # middleware and endpoints over TestServer
│   ├── DotNetBoost.MultiLingual.IntegrationTests # Testcontainers, needs Docker
│   └── DotNetBoost.MultiLingual.TestSupport      # in-memory stores shared by the suites
├── samples/
│   └── SampleApp                                 # Runnable ASP.NET Core demo (SQLite, Scalar)
└── docs/                                         # Additional documentation
```

## Prerequisites

- .NET 10 SDK
- .NET 8 runtime — the libraries and most test projects multi-target `net8.0`, so the net8.0
  test pass needs it.
- Docker — only for `IntegrationTests`, which start real PostgreSQL and SQL Server servers.

## Building and testing

```bash
dotnet build
dotnet test tests/DotNetBoost.MultiLingual.UnitTests
dotnet test tests/DotNetBoost.MultiLingual.ProviderTests
dotnet test tests/DotNetBoost.MultiLingual.ApiTests
dotnet test tests/DotNetBoost.MultiLingual.IntegrationTests   # Docker required
```

Warnings are errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`), and every
public member of a shipped package needs XML docs.

## Guidelines

- **Core stays provider-neutral.** No EF Core, ASP.NET Core or driver references in
  `DotNetBoost.MultiLingual.Core`. If a provider needs something from the engine, add it to
  `ITranslationManager` in provider-neutral terms.
- **Stores stay dumb.** Rules go in the managers, so every provider gets them for free.
- **A store change needs a contract test.** Add it to `TranslationStoreContractTests` /
  `LanguageStoreContractTests`, so every provider — including the in-memory doubles the other
  suites rely on — is held to it.
- New providers: see [docs/adding-a-provider.md](docs/adding-a-provider.md).
