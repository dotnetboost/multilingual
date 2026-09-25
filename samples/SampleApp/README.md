# SampleApp

The reference demo: a Products and Categories API using every part of the library, on PostgreSQL
through the EF Core provider. `samples/SampleApp.Dapper` and `samples/SampleApp.MongoDb` serve the
same routes and the same request and response bodies over the other two providers — the DTOs and
the error handling are *the same files*, compiled into all three.

English is the [base language](../../docs/base-language.md), so `Products.Name` is a real column
you can read in any SQL client, and Arabic and French are rows in `Translations`.

## Running it

With Docker, the Aspire AppHost starts PostgreSQL, this API, the two other samples, a browser SQL
client and the dashboard:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

The API is then on `http://localhost:5160`, with its Scalar reference at `/scalar`. To run it
against a PostgreSQL database of your own instead — the schema is created on first start:

```bash
ConnectionStrings__multilingualdb="Host=localhost;Database=multilingual;Username=postgres;Password=postgres" \
  dotnet run --project samples/SampleApp
```

`Program.cs` and `SampleApp.csproj` write out the SQL Server and SQLite alternatives in full,
commented, next to the active PostgreSQL block. SQLite needs no container at all. See
[docs/aspire.md](../../docs/aspire.md).

The database is seeded only when `Languages` is empty: English (the default and the base language),
Arabic and French, with three products and two categories translated into all three.

## What each part demonstrates

| Where | What |
|---|---|
| `Domain/Product.cs` | **Property-level** `[MultiLingual]` — `Name` and `Description` translated, `Sku` and `Price` ordinary columns |
| `Domain/Category.cs` | **Class-level** `[MultiLingual]` — every string property, with `[NotMultiLingual]` opting `Slug` back out and `DisplayOrder` skipped for not being a string |
| `ProductsController.GetAll` | Filtering and sorting on **translated** text, in the database, in one statement |
| `ProductsController.GetSummaries` | A projection into a DTO, with the category's translated name alongside |
| `ProductsController.Create` | The base-language rule: a product can only be created under `Accept-Language: en` |
| `ProductsController.Update` | The partial-update shape — assign only what the caller supplied, so other languages are untouched |
| `ProductsController.GetTranslations` | `For(product).GetAllAsync()` — every stored language, unmerged |
| `ProductsController.ClearTranslation` | `For(product).Clear(languageCode)`, with the library answering 404/409 by itself |
| `ProductsController.DeleteByCategory` | A bulk delete followed by `PurgeOrphanedTranslationsAsync()` |
| `Data/AppDbContext.cs` | The two `DbSet`s and `ApplyMultiLingual(this)` |
| `Resources/en.json`, `ar.json` | The app's own messages, and the library's problem details, in both languages |
| `Program.cs` | The whole setup, commented — including the Redis language-sync block, left out because this is one process |

## Trying it by hand

[`SampleApp.http`](SampleApp.http) walks every behaviour above, edge cases included — send the
requests from your editor. To click through it instead, [`clients/dashboard`](../../clients/dashboard)
is a Nuxt web UI for the language endpoints and this sample's content; see
[docs/dashboard.md](../../docs/dashboard.md).

A quick tour with `curl`, against the AppHost's port:

```bash
curl "http://localhost:5160/api/products?sort=name" -H "Accept-Language: ar"
curl "http://localhost:5160/api/products?search=laptop" -H "Accept-Language: en"
curl "http://localhost:5160/api/products/1/translations"
curl "http://localhost:5160/api/current-language" -H "Accept-Language: de"
```

The last one reports what the API resolved, which is how you can see a fallback happening:
`de` is not one of the three languages, so it answers with the default and
`requestedLanguageHonoured: false`.

## Not production-ready in one respect

`app.MapLanguageEndpoints()` is mapped without `.RequireAuthorization(…)`, because the sample runs
without an identity provider. Do not copy that into a real application — see
[securing the endpoints](../../docs/rest-api.md#securing-the-endpoints).
