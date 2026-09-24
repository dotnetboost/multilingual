# SampleApp.Dapper

The EF Core sample (`samples/SampleApp`) over the Dapper provider. Same `Product` and `Category`,
same routes, same request and response bodies — the DTOs and the error handling are *the same files*,
compiled into both projects — so `clients/dashboard` can be pointed at any of the three APIs without
a change.

English is the base language, as in the EF Core sample: a row holds the English text in its own
columns and every other language is a row in `Translations`.

## What differs, and where to look

| | |
|---|---|
| Localize after a query | Every read in both controllers: `await multiLingual.LocalizeAsync(found, ct)`. EF Core's query interceptor does it for you. |
| Save translations after the row | `ProductsController.Create` — the `INSERT … RETURNING "Id"` first, so the key exists, then `For(product).SaveAsync()`. |
| The base-language rules | `ProductsController.Create` (create in the base language only) and `ProductsController.Update` (the two marked comments). EF Core infers both from `IsModified`. |
| No translated filter or sort in the database | `ProductsController.GetAll` — `search` and `sort` run in memory. |
| No orphan purge | `ProductsController.DeleteByCategory` — keys are collected before the delete. |

[docs/dapper.md](../../docs/dapper.md) explains them in one page. Those five are the same five the
MongoDB sample has: they come from having no change tracker, not from the storage engine.

Two things are specific to this sample.

**One transaction per request.** `Data/UnitOfWork.cs` puts an `IDbTransaction` in the request scope,
and `UseDapper(...)` resolves one when it builds its stores — so a row's `INSERT` and its
translations' upserts land in the same transaction with nothing in the controllers saying so, and
either both commit or neither does. This is the seam the MongoDB sample cannot demonstrate: the
AppHost runs a standalone `mongod`, and transactions there need a replica set. The only trace of it
in the controllers is that each Dapper call carries `unitOfWork.Current`, which ADO.NET requires
once a connection has an open transaction.

`Data/UnitOfWorkMiddleware.cs` commits it, and it is middleware rather than an MVC action filter on
purpose: a filter would cover the controllers and miss `MapLanguageEndpoints()`, which are minimal
APIs — a `POST /api/languages` would then write into a transaction nobody commits and the language
would vanish on the way out.

**The library creates its own tables.** `UseDapper(..., migrateSchema: true)` in `Program.cs`
creates `Languages` and `Translations` at startup; `Data/DbInitializer.cs` creates only the sample's
own `Products` and `Categories`. That split is the point of the flag — your schema stays yours.

Two consequences worth knowing:

- Anything that touches the library's tables at startup must be ordered *after* it. That is why the
  seeding is a hosted service (`Data/DatabaseSeeder.cs`) rather than a call before `app.Run()`, as
  in the other two samples: hosted services start in registration order, and `migrateSchema` uses
  one too.
- The language catalog refreshes in a scope of its own, on its own connection, so it cannot see rows
  still sitting in another scope's transaction. `DbInitializer` therefore commits the three
  languages before reading them back — the comment there explains it.

## Run it

Through the Aspire AppHost, which starts PostgreSQL, this API, and the other two samples together:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

It takes its own database (`multilingualdapperdb`) on the same PostgreSQL container the EF Core
sample uses, so pgweb reaches both and the two schemas can be compared side by side. They are
identical — that is the claim the provider makes.

Or on its own, against any PostgreSQL:

```bash
docker run -d --name multilingual-pg -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17-alpine
```

```bash
ConnectionStrings__multilingualdapperdb="Host=localhost;Database=postgres;Username=postgres;Password=postgres" dotnet run --project samples/SampleApp.Dapper
```

An empty database is enough: `migrateSchema: true` creates the library's tables and the seeder
creates the sample's. It listens on <http://localhost:5180> and opens Scalar at `/scalar`. Three
languages (en, ar, fr), two categories and three products are seeded on first start, with
translations deliberately missing so the fallback shows immediately.

To run it on SQL Server or SQLite instead, swap the driver package in `SampleApp.Dapper.csproj` and
the factory in `Program.cs`. The provider detects the dialect from the connection type; the DDL in
`Data/DbInitializer.cs` is the only PostgreSQL-specific code in the sample, and the library's own
DDL already covers all three.

## Try it

```bash
curl -s localhost:5180/api/products -H 'Accept-Language: ar'
```

Arabic where it exists, English where it does not — `NOTE-A5` has no translations at all, and
`MOUSE-01` has no French.

```bash
curl -s -X POST localhost:5180/api/products -H 'Content-Type: application/json' -H 'Accept-Language: ar' \
  -d '{"sku":"KNAFEH-1","price":9.9,"name":"كنافة"}'
```

409, `Base language required`: in base-language mode a product can only be created in English,
because every translation hangs off the base value. The EF Core provider's save interceptor enforces
this; here `ProductsController.Create` does, and throws the same exception.

Create it in English, then add Arabic with a `PUT` that supplies only the name:

```bash
curl -s -X PUT localhost:5180/api/products/4 -H 'Content-Type: application/json' -H 'Accept-Language: ar' \
  -d '{"name":"كنافة"}'
```

Then look at what that actually wrote:

```bash
curl -s localhost:5180/api/products/4/translations
```

Only `ar.Name`. The description was **not** filed as an Arabic translation, even though the entity
held English text in it — that is `MarkAsBaseLanguageValues` in the `PUT`. And the row itself still
holds the English name, which `SELECT * FROM "Products"` in pgweb confirms. Those two facts are the
whole point of the base-language rules in that method.

To see the schema the provider created for itself, and that it matches the EF Core sample's:

```bash
curl -s localhost:5180/api/products/1/translations
```

…or open pgweb from the Aspire dashboard and compare `multilingualdapperdb` with `multilingualdb`.

## With the dashboard

In `aspire/DotNetBoost.MultiLingual.AppHost/AppHost.cs`, change the dashboard's
`NUXT_MULTILINGUAL_API_URL` from `api` to `dapperApi`. Nothing in `clients/dashboard` changes: it
sees the same routes and the same bodies.

## Endpoints

Identical to the EF Core sample's.

| | |
|---|---|
| `GET /api/products?categoryId=&search=&sort=` | List, localized. `search` and `sort` run in memory. |
| `GET /api/products/summaries` | Each product with its category's name, both localized. |
| `GET /api/products/{id}` | One product, localized. |
| `GET /api/products/{id}/translations` | Every stored language for one product. |
| `POST /api/products` | Create. Base language only. |
| `PUT /api/products/{id}` | Update in the request's language. A null field means "leave it alone". |
| `DELETE /api/products/{id}/translations/{code}` | Remove one language. English is refused with a 409. |
| `DELETE /api/products?categoryId=` | Bulk delete, and the translations it leaves behind. |
| `DELETE /api/products/{id}` | Delete a product and its translations. |
| `GET/POST/PUT/DELETE /api/categories…` | The same set, minus the bulk delete and the summaries. |
| `GET /api/languages`, … | Language administration, from `MapLanguageEndpoints()`. |
| `GET /api/current-language` | Which language the request resolved to. |
