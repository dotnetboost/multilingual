# Running everything with .NET Aspire

[`aspire/DotNetBoost.MultiLingual.AppHost`](../aspire/DotNetBoost.MultiLingual.AppHost) orchestrates
the whole demo stack — all three sample APIs, the dashboard, PostgreSQL and MongoDB — from one
command:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

That needs nothing installed beyond the .NET 10 SDK and a running container engine (Docker or
Podman) — the AppHost pulls the orchestrator and dashboard from NuGet via `dnx aspire.cli` on first
run. Watch the console for the Aspire dashboard link; it carries a one-time login token.

The Aspire CLI is optional and shortens the command to `aspire run`, which works from anywhere in
the repo — [`aspire.config.json`](../aspire.config.json) points it at the AppHost:

```bash
dotnet tool install -g Aspire.Cli
```

What comes up:

| Resource | What it is |
|---|---|
| `postgres` | PostgreSQL container, named volume, persistent across runs |
| `multilingualdb` | The EF Core sample's database |
| `multilingualdapperdb` | The Dapper sample's database, on the same server — so the two schemas can be compared in pgweb. They are identical, which is the claim that provider makes |
| `pgweb` | Browser SQL client, reaching both: `Products.Name` holds English, `Translations` every other language |
| `mongo` / `multilingualmongodb` | MongoDB container and database, named volume |
| `mongo-express` | The pgweb counterpart for MongoDB |
| `api` | `samples/SampleApp` on `http://localhost:5160` — EF Core provider |
| `mongo-api` | `samples/SampleApp.MongoDb` on `http://localhost:5170` — MongoDB provider |
| `dapper-api` | `samples/SampleApp.Dapper` on `http://localhost:5180` — Dapper provider |
| `dashboard` | `clients/dashboard` — `npm install` then `npm run dev`, wired to one of the APIs automatically |

Each API serves the same routes and the same request and response bodies — the DTO files are shared
— so the dashboard can be pointed at any of them by changing one line in `AppHost.cs`. All three
expose the Scalar reference at `/scalar`.

Each API waits for its database, and the dashboard waits for the API's `/health`, which includes a
store check — so the dashboard starts only once the database actually answers. The AppHost's own
ports (`15160`/`17160` and up) differ from DotNetBoost.Settings', so both stacks can run side by side.

## What the AppHost injects

| Into | Variable | Value |
|---|---|---|
| `api` | `ConnectionStrings__multilingualdb` | The Postgres container's database |
| `dapper-api` | `ConnectionStrings__multilingualdapperdb` | Its own database on the same Postgres container |
| `mongo-api` | `ConnectionStrings__multilingualmongodb` | The MongoDB container's database |
| `dashboard` | `NUXT_MULTILINGUAL_API_URL` | The API's http endpoint, for the `/api/**` proxy |
| `dashboard` | `NUXT_PUBLIC_API_REFERENCE_URL` | `{api}/scalar` |
| `dashboard` | `NUXT_PUBLIC_BASE_LANGUAGE` | `en`, matching `BaseLanguage` in `Program.cs` |

`aspire/DotNetBoost.MultiLingual.ServiceDefaults` adds OpenTelemetry (Npgsql's traces included, so
each request's SQL — the `Translations` joins too — shows on the Aspire dashboard), `/health` and
`/alive`, service discovery and resilient `HttpClient` defaults to the API.

## Resetting the data

The database is seeded only when `Languages` is empty, and the volume keeps it between runs. To start
over, stop the AppHost and remove the container and its volume:

```bash
docker rm -f $(docker ps -aq --filter name=postgres-) && docker volume rm dotnetboost-multilingual-pgdata
docker rm -f $(docker ps -aq --filter name=mongo-) && docker volume rm dotnetboost-multilingual-mongodata
```

Both containers use `ContainerLifetime.Persistent`, so they outlive the AppHost and have to be
removed explicitly.

## Another database

`AppHost.cs`, `samples/SampleApp/Program.cs` and `samples/SampleApp/SampleApp.csproj` each write out
SQL Server and SQLite in full next to the active PostgreSQL block. Switch all three together: comment
the active block, uncomment the one you want, and uncomment its package. SQLite needs no container —
the API then runs on its own with `dotnet run --project samples/SampleApp`. On either, the products
search goes back to `EF.Functions.Like` (`ILike` is PostgreSQL-only).

## Running the API without the AppHost

The sample needs `ConnectionStrings:multilingualdb` and says so at startup if it is missing. Point it
at any PostgreSQL database — the schema is created on first start:

```bash
ConnectionStrings__multilingualdb="Host=localhost;Database=multilingual;Username=postgres;Password=postgres" dotnet run --project samples/SampleApp
```
