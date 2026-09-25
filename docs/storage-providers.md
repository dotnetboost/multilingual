# Storage providers

Pick one provider per application. Each is a separate package on top of
`DotNetBoost.MultiLingual.Core`, and each is configured with one line of the `AddMultiLingual()`
chain. Everything above the store — the attribute, `IMultiLingualManager`, the language rules, the
fallback chain, base-language mode — is the same whichever you pick.

| Provider | Databases | Translated reads and writes | Creates its own schema? |
|---|---|---|---|
| [Entity Framework Core](#entity-framework-core) | SQL Server, PostgreSQL, SQLite | Automatic, through interceptors | No: through your EF migrations |
| [Dapper](dapper.md) | SQL Server, PostgreSQL, SQLite | Explicit, through `IMultiLingualManager` | Yes, with `migrateSchema: true` |
| [MongoDB](mongodb.md) | MongoDB | Explicit, through `IMultiLingualManager` | Yes (collections and indexes on startup) |

The one real difference between them is in the middle column, and it is not about the database
engine: EF Core has a change tracker and a LINQ pipeline to hook, so reads and writes are
invisible. The other two have neither, so an application calls `LocalizeAsync` after a query and
`For(entity).SaveAsync()` after a write — see [providers without a change
tracker](#providers-without-a-change-tracker).

## Entity Framework Core

```bash
dotnet add package DotNetBoost.MultiLingual.EntityFrameworkCore --prerelease
```

Add the two library tables to your `DbContext` and call `ApplyMultiLingual()` **last** in
`OnModelCreating`, after `ApplyConfigurationsFromAssembly`, so entity types discovered through
configurations and navigations are covered too:

```csharp
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IMultiLingualDbContext
{
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<Language>    Languages    => Set<Language>();
    public DbSet<Product>     Products     => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.ApplyMultiLingual();
    }
}
```

```csharp
builder.Services.AddMultiLingual()
    .UseEntityFrameworkCore<AppDbContext>()
    .Build();

builder.Services.AddDbContext<AppDbContext>((sp, o) => o
    .UseSqlServer(connectionString)
    .UseMultiLingual(sp));              // attaches the four interceptors
```

Use the `(sp, options)` overload of `AddDbContext`: the interceptors are resolved per request and
need the scope to read its language from. `AddDbContextPool` is not supported for the same reason —
pooled options are built once, from the root provider.

Then `dotnet ef migrations add Init && dotnet ef database update` creates `Translations` and
`Languages` alongside your own tables.

`ApplyMultiLingual()` also *removes* every translatable property from its owning entity's table
mapping, so `Products` has no `Name` column. In [base-language mode](base-language.md) it keeps the
column instead — pass the context so the mapping follows the configuration:

```csharp
modelBuilder.ApplyMultiLingual(this);
```

Table and schema names are configurable:

```csharp
modelBuilder.ApplyMultiLingual(this, o =>
{
    o.TranslationsTable = "ContentTranslations";
    o.LanguagesTable    = "ContentLanguages";
    o.Schema            = "i18n";
});
```

A design-time context built without the application's DI cannot reach the engine's options. Pass
them explicitly there — the values must match what `AddMultiLingual` configures, or the generated
migration will not match the mapping the engine expects:

```csharp
modelBuilder.ApplyMultiLingual(new MultiLingualOptions { BaseLanguage = "en" });
```

From here, queries need nothing else: see [translated queries](querying.md).

## Dapper

```csharp
builder.Services.AddMultiLingual(o => o.BaseLanguage = "en")
    .UseDapper(sp => new SqlConnection(connectionString), migrateSchema: true)
    .Build();
```

The connection factory's runtime type selects the dialect. The tables are the ones the EF Core
provider maps, so a database can move between the two providers, and `migrateSchema: true` creates
them at startup when nothing else owns your schema. Full page: [Dapper](dapper.md).

## MongoDB

```csharp
builder.Services.AddSingleton<IMongoClient>(new MongoClient(connectionString));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("shop"));

builder.Services.AddMultiLingual(o => o.BaseLanguage = "en")
    .UseMongoDb()                      // or UseMongoDb(connectionString, "shop")
    .Build();
```

Translations go in one collection keyed by the natural key, and `[MultiLingual]` properties are
unmapped from the class map so they are never stored on your own documents. Full page:
[MongoDB](mongodb.md).

## Providers without a change tracker

Dapper and MongoDB have no interceptors to hook, so their users call the same
`IMultiLingualManager` an EF Core application injects — just a few more of its members:

```csharp
// Read: fill the translatable properties after your own query.
var products = (await connection.QueryAsync<Product>(sql)).ToList();
await multiLingual.LocalizeAsync(products);

// Write: after the entity's own row exists, so its key does.
await connection.ExecuteAsync(insertSql, product);
await multiLingual.For(product).SaveAsync();

// Delete: after (or before) your own delete.
await connection.ExecuteAsync(deleteSql, product);
await multiLingual.For(product).DeleteAsync();
```

The rules are identical to EF Core's: `null` means "not supplied", a value that came from the read
path is never written back as a translation, and anything queued with `Set` / `Clear` is written by
the next `SaveAsync`.

Two things only EF Core can do, because both work by rewriting a LINQ query:
[filtering and sorting on translated text in the database](querying.md), and
`PurgeOrphanedTranslationsAsync()`.

In base-language mode such a provider has one extra duty, because it cannot ask a change tracker
which properties the caller touched. Before saving in a language other than the base one, declare
the ones that still hold the value the row was loaded with:

```csharp
multiLingual.For(product).MarkAsBaseLanguageValues(p => p.Description);   // only Name was edited
await multiLingual.For(product).SaveAsync();
```

Without it, the base-language text read from the row is written back as a translation in whatever
language is current. The EF Core provider does this for you, from `IsModified`.

## Something else

Implement `ITranslationStore` and `ILanguageStore` and register them:

```csharp
builder.Services.AddMultiLingual()
    .UseCustomStores<MyTranslationStore, MyLanguageStore>()
    .Build();
```

The stores are deliberately dumb — no validation, no fallback, no change detection — so a new
backend inherits every rule for free. [Adding a provider](adding-a-provider.md) walks through what
one has to get right, and the two contract suites that prove it.
