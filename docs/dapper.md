# Dapper

`DotNetBoost.MultiLingual.Dapper` stores translations in one table, in the same schema the EF Core
provider maps. The library calls are the same ones an EF Core application makes — the engine is
provider-neutral — so this page is only about what differs.

`samples/SampleApp.Dapper` is a runnable version of everything below: the EF Core sample's own
Products and Categories API, over this provider, serving the same routes and the same bodies.

## Setup

```csharp
builder.Services
    .AddMultiLingual(o => o.BaseLanguage = "en")
    .UseDapper(sp => new SqlConnection(connectionString), migrateSchema: true)
    .Build();
```

The first argument creates the connection for each scope; its runtime type selects the dialect —
SQL Server, PostgreSQL or SQLite. The connection is registered as `IDbConnection` in the scope, so
the container disposes it and your own code can inject the very same one (which is what makes the
shared transaction below work). Table and schema names, and an explicit dialect for a wrapped
connection, are configurable through `DapperOptions`.

## `migrateSchema`

`migrateSchema: true` registers a hosted service that creates the two tables at startup if they are
absent:

| | |
|---|---|
| `Languages` | `Code` (PK), `Name`, `Direction`, `IsDefault`, `IsActive`, plus `IX_Languages_IsDefault`. |
| `Translations` | Composite PK `(EntityName, EntityId, PropertyName, LanguageCode)`, a nullable `Value`, a restricting foreign key to `Languages`, plus `IX_Translations_LanguageCode`. |

The shape — names, lengths, keys, constraint names — is what the EF Core provider's
`TranslationConfiguration` / `LanguageConfiguration` produce, so a database created here is one that
provider will read and write, and vice versa. Column lengths come from the same Core constants both
providers use, so the two cannot drift.

Every statement is conditional, so it is safe on every boot. **It only ever CREATEs.** It will not
alter or drop a table that is already there, so it can bring a database into existence but cannot
migrate one whose shape has changed. Leave it off — the default — when migrations or a deployment
step own your schema. If the role lacks CREATE rights the host fails to start, deliberately: every
read and write needs these tables, so the alternative is failing later, on a request, less legibly.

To review the DDL, or check it into a migrations folder instead of running it at startup:

```csharp
Console.WriteLine(DapperSchema.Script(SqlDialect.PostgreSql));
// or run it yourself, from a deployment step or a test:
await DapperSchema.EnsureCreatedAsync(connection);
```

## The three differences

### 1. Localize after a query

A Dapper query fills an object's own columns. Translatable properties have no column, so they
arrive `null` until you ask:

```csharp
var products = (await connection.QueryAsync<Product>("SELECT * FROM Products")).ToList();
await multiLingual.LocalizeAsync(products);
```

Your `INSERT` and `UPDATE` statements must not reference translatable properties either — there is
no column to write them to.

### 2. Save and delete translations yourself

There is no change tracker, so the writes are explicit — and they come *after* the entity's own row
exists, so its key does:

```csharp
await connection.ExecuteAsync(insertSql, product);
await multiLingual.For(product).SaveAsync();

await connection.ExecuteAsync(deleteSql, product);
await multiLingual.For(product).DeleteAsync();
```

The rules are the same as with EF Core: `null` means "not supplied", untouched fallbacks are never
promoted, and values queued with `For(product).Set(...)` / `Clear(...)` are written by the next
`SaveAsync`.

### 3. One transaction for the entity and its translations

Pass your transaction to the store, and hand that store to the writer:

```csharp
using var transaction = connection.BeginTransaction();

await connection.ExecuteAsync(insertSql, product, transaction);

var store = new DapperTranslationStore(connection).WithTransaction(transaction);
await writer.WithStore(store).WriteAsync(writes, ct);

transaction.Commit();
```

An application that registers its transaction as a scoped `IDbTransaction` gets this without
writing it out: the stores resolve one from the scope and enlist automatically. That is what
`samples/SampleApp.Dapper` does — see its `Data/UnitOfWork.cs` — so its controllers read exactly
like the MongoDB sample's while every request is transactional.

Without a transaction, a multi-row write takes one of its own for the duration, so a batch of
upserts and deletes is still all-or-nothing.

Two things to know before holding a transaction open across a whole request:

- The commit has to cover every endpoint that shares it. An MVC action filter misses
  `MapLanguageEndpoints()`, which are minimal APIs, and a language written there would roll back
  silently. Middleware covers both.
- The language catalog refreshes in a scope of its own, on its own connection, so it cannot see
  rows still inside your transaction. Creating a language and then reading it back through
  `IMultiLingualManager.Languages` in the same uncommitted transaction will not find it — commit
  first. Language administration and content writes are better kept in separate requests anyway.

## Startup order

`migrateSchema: true` runs from a hosted service, and hosted services do not start until
`app.Run()`. Anything of your own that touches `Languages` or `Translations` at startup — seeding,
a warm-up, a data migration — therefore has to be a hosted service registered **after** the
`AddMultiLingual(...)` chain, not a call before `app.Run()`. `samples/SampleApp.Dapper` does this in
`Data/DatabaseSeeder.cs`.

## Upserts, per engine

| Engine | Upsert | List filters |
|---|---|---|
| PostgreSQL | `INSERT … ON CONFLICT DO UPDATE` | `= ANY(@ids)` — Dapper binds a list as one array parameter on Npgsql. |
| SQLite | `INSERT … ON CONFLICT DO UPDATE` | `IN (@ids1, @ids2, …)` — Dapper expands the list. |
| SQL Server | `MERGE … WITH (HOLDLOCK)` | `IN (@ids1, @ids2, …)`. `HOLDLOCK` is what keeps two sessions merging the same key from both deciding to INSERT. |

Identifiers are quoted in their declared casing on every engine, PascalCase included. On PostgreSQL
an unquoted name would be folded to lower case and would look for a table the EF Core provider
never created.

## Entity keys

`IEntityKeyAccessor` is left alone: Dapper maps plain objects, so Core's convention (`[Key]`, `Id`,
`{Type}Id`) is already the best available answer. An entity whose key is still unset raises "save
the entity first" rather than writing translations under a placeholder.
