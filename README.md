# DotNetBoost.MultiLingual

Translatable content for any entity, driven by a single `[MultiLingual]` attribute. Adding
translation support to an entity needs **no repository code, no service code and no extra
table** — only the attribute.

```csharp
public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";     // ordinary column
    public decimal Price { get; set; }        // ordinary column

    [MultiLingual] public string? Name { get; set; }         // translated
    [MultiLingual] public string? Description { get; set; }  // translated
}
```

```
GET /api/products/1   Accept-Language: en  ->  { "name": "14-inch Laptop", ... }
GET /api/products/1   Accept-Language: ar  ->  { "name": "حاسوب محمول 14 بوصة", ... }
GET /api/products/1   Accept-Language: fr  ->  English, when there is no French yet
```

Translated values live in one shared `Translations` table. One language can optionally keep its
own column instead — see [base-language mode](#base-language-mode), which is what makes this
adoptable on a database that already has `Name` and `Description` full of data.

> **Status:** pre-release, not yet published. The API may still change.

## Packages

| Package | What it is |
|---|---|
| `DotNetBoost.MultiLingual.Core` | The provider-neutral engine: attributes, `ITranslationManager` (for applications) and `ITranslationWriter` (for storage providers), `ILanguageManager`, the language catalog, and the store contracts `ITranslationStore` / `ILanguageStore`. No EF Core, no ASP.NET Core. |
| `DotNetBoost.MultiLingual.EntityFrameworkCore` | EF Core provider. Maps translatable properties according to the configured mode and reads/writes them transparently through interceptors. SQL Server, PostgreSQL, SQLite. |
| `DotNetBoost.MultiLingual.API` | ASP.NET Core: `Accept-Language` middleware, language management endpoints, ProblemDetails mapping, and (net10.0) an OpenAPI transformer. |
| `DotNetBoost.MultiLingual.Dapper` | *Planned.* |
| `DotNetBoost.MultiLingual.MongoDb` | *Planned.* |

All packages target `net8.0` and `net10.0`.

## Quick start (EF Core + ASP.NET Core)

```csharp
// 1. Your DbContext holds the two library tables and calls ApplyMultiLingual() LAST.
public class AppDbContext(DbContextOptions<AppDbContext> o) : DbContext(o), IMultiLingualDbContext
{
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        b.ApplyMultiLingual();
    }
}

// 2. Program.cs
builder.Services.AddMultiLingual()
    .UseEntityFrameworkCore<AppDbContext>()
    .Build();

builder.Services.AddDbContext<AppDbContext>((sp, o) => o
    .UseSqlServer(connectionString)
    .UseMultiLingual(sp));                         // attaches the interceptors

builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();
builder.Services.AddOpenApi(o => o.AddAcceptLanguageParameter());   // optional, net10.0

var app = builder.Build();
app.UseExceptionHandler();
app.UseMultiLingual();                             // Accept-Language -> ILanguageContext
app.MapLanguageEndpoints().RequireAuthorization("admin");
```

To keep one language in the entity's own table instead, name it in `AddMultiLingual` and hand
`ApplyMultiLingual` the context so the mapping follows. Two edits to the above, covered in
[base-language mode](#base-language-mode):

```csharp
// In OnModelCreating
b.ApplyMultiLingual(this);

// In Program.cs
builder.Services.AddMultiLingual(o => o.BaseLanguage = "en")
    .UseEntityFrameworkCore<AppDbContext>()
    .Build();
```

Controllers then read and write `Product.Name` like any other property:

```csharp
// Read: one query for the products, one for all their translations.
var products = await db.Products.AsNoTracking().ToLocalizedListAsync(hydrator, ct);

// Write: assign only what the caller supplied, and Name is stored for the request's language.
// Other languages are untouched. Assigning null would mean "not supplied" in the default mode,
// but erases the stored value in base-language mode — so this shape is the one to use in both.
if (request.Name is not null)
    product.Name = request.Name;

await db.SaveChangesAsync(ct);
```

`samples/SampleApp` is a complete, runnable version of this — SQLite, Scalar UI, seeded data, and
configured in base-language mode so `Products.Name` is a real column you can see in any SQL
client. `samples/SampleApp/SampleApp.http` walks every behaviour below, edge cases included:

```bash
dotnet run --project samples/SampleApp    # http://localhost:5160
```

## How it works

**Metadata.** `MultiLingualMetadata` works out, once per type, which properties are translatable.
At property level only the decorated ones; at class level every public read/write `string`
except the key and anything marked `[NotMultiLingual]`. A `[MultiLingual]` on a non-string,
`init`-only or privately-set property fails at startup (`Build()` validates every public
decorated type).

**Storage.** One `Translations` table serves every entity, keyed naturally on
`(EntityName, EntityId, PropertyName, LanguageCode)`. Keys of any single-column type (`int`,
`long`, `Guid`, `string`) are stored as invariant strings. Languages are identified by their
code, which is immutable once created.

**Entity names are part of your database.** `EntityName` is the class name unless you fix it,
so renaming a class would orphan every translation stored under the old name. Fix the name and
the class can be renamed freely:

```csharp
[MultiLingual(EntityName = "product")]          // class-level declarations
public class CatalogItem { ... }

[MultiLingualEntity("article")]                 // property-level declarations
public class BlogPost { [MultiLingual] public string? Title { get; set; } ... }
```

To rename a class that has no fixed name yet, either fix it to the old name
(`EntityName = "Product"`) or move the rows:
`UPDATE Translations SET EntityName = 'CatalogItem' WHERE EntityName = 'Product'`.
`Build()` refuses two unrelated types that would share a name — `Sales.Item` and
`Library.Item`, say, or `"Shared"` and `"shared"` — and lists them. It can only see public types,
so `ApplyMultiLingual()` runs the same check over every entity in the EF model, internal ones
included, when the model is first built. A derived type inherits its base's name and shares its
rows, which is right for EF Core's inheritance mappings.

**Read path.** Values come from the requested language, then the default language, then `null`
(or, in base-language mode, the base language's column). Only the languages that are actually
stored as rows are fetched, in one query per entity type.

**Write path.** A `null` property means "not supplied" and leaves the stored value alone. A value
that is still exactly what the read path put there is not written back — so reading a product in
French (falling back to English) and saving a price change never turns the English text into a
French translation. The same protection covers a value that arrived from a base-language column
rather than from the read path.

To remove a stored translation, say so explicitly — the row is deleted on the next save, and
reads fall back as for any missing value:

```csharp
translations.Clear(product, "fr", nameof(Product.Name));   // one property
translations.Clear(product, "fr");                         // every translatable property
await db.SaveChangesAsync();
```

**Projecting, filtering and sorting.** A plain LINQ query cannot see translations. In the
default mode a translatable property has no column at all; in base-language mode its column
only ever holds the base language. So neither of these gives the request's language:

```csharp
db.Products.Select(p => new ProductDto { Name = p.Name })   // default mode: refused (see below)
                                                            // base-language mode: always the base language
db.Products.Where(p => p.Name.Contains(term))               // default mode: refused
```

These do, building the lookup over the translations table in SQL and reading the value a read
would show — requested language, then the default, then the base column:

```csharp
// Project straight into a DTO — navigations and nested collections included.
var rows = await db.Products
    .SelectTranslated(db, p => new ProductDto { Sku = p.Sku, Name = p.Name, Category = p.Category!.Name })
    .ToListAsync(ct);

// Filter and sort.
var page = await db.Products
    .WhereTranslation(db, p => p.Name, name => name!.Contains(term))
    .OrderByTranslation(db, p => p.Name)          // also …Descending, ThenBy…
    .Skip(skip).Take(take)
    .ToLocalizedListAsync(hydrator, ct);
```

Pass the same `db` the query runs on; the language is the current one unless you pass
`languageCode`. Each translated value is a correlated subquery per language, served by the
translations key — fine for a page of results, not a substitute for a search engine on a large
catalogue. Types in an inheritance hierarchy should share one entity name
(`[MultiLingualEntity]` on the base), since a query over the base type looks rows up by its name.

In the default mode, a query that reads a translatable property directly is refused with an
error naming the property and these helpers — rather than EF quietly projecting `null`. In
base-language mode a plain query is valid and reads the base column, which is sometimes what you
want, so it is not refused.

**EF Core specifics.** `ApplyMultiLingual()` removes translatable properties from the model — or,
with a base language configured, leaves them mapped. The save interceptor captures values in
`SavingChanges` and writes them in `SavedChanges`, once generated keys exist, in the same
transaction — opening one if the caller has not. It also inspects `Unchanged` entries, because
assigning only an unmapped property does not make EF consider the entity modified. The
materialization interceptor records loaded entities (including ones reached through `Include`) so
`ITranslationHydrator` can fill them all in one round trip. Synchronous code has the same helpers
without the suffix — `ToLocalizedList`, `FirstLocalizedOrDefault`, `SingleLocalizedOrDefault` —
and, like a synchronous `SaveChanges`, they never block on asynchronous work.

## Base-language mode

By default every language — including the default one — lives in the translations table, and
translatable properties have no column at all. Set a **base language** and one language moves
into each entity's own table:

```csharp
services.AddMultiLingual(o => o.BaseLanguage = "en")
        .UseEntityFrameworkCore<AppDbContext>()
        .Build();
```

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    modelBuilder.ApplyMultiLingual(this);   // `this`, so the mapping follows the configuration
}
```

`Products` then has a real `Name` column holding English, and Arabic and French are rows in
`Translations`. What changes:

| | Default mode | Base-language mode |
|---|---|---|
| Base language stored in | `Translations` | the entity's own column |
| Other languages stored in | `Translations` | `Translations` |
| Missing translation falls back to | default language, then `null` | default language, then the base column |
| Reading the base language costs | 2 queries | 1 query — nothing is fetched |
| `SELECT * FROM Products` | no names | names, in the base language |
| Filter/sort in SQL | `WhereTranslation` / `OrderByTranslation` | directly in the base language; those helpers for the others |
| Adding a translatable property | no migration | a migration, for the new column |

**Why it is not just "the default language".** The default language is a row in the language
table and can be changed at runtime; the base language is a property of the *schema* and is fixed
for the life of the database. Keeping them apart is the point — `POST /api/languages/{code}/set-default`
stays a two-row flip, because no stored content ever has to move. Changing `BaseLanguage` itself
means migrating content between columns and rows, so treat it as a schema change, not a setting.

**Three rules the mode enforces.**

1. **An entity may only be created in the base language.** The base value is the column every
   translation hangs off and the last fallback for every other language, so it has to exist first.
   Creating with any other `Accept-Language` is refused with `BaseLanguageRequiredException`
   (HTTP 409). Add other languages with a follow-up update, one language per request. Scopes with
   no language resolved — seeders, background jobs — are exempt.
2. **The base language cannot be deleted or deactivated.**
3. **The base language is never a translation row.** It has exactly one home, so the two cannot
   drift; `Stage(entity, baseLanguage, …)` is refused for the same reason.

**Writing in a language other than the base one.** Assign only what the caller actually supplied:

```csharp
if (request.Name is not null)
    product.Name = request.Name;
```

Two things make that the right shape here. Translatable properties are real columns, so assigning
`null` *erases* the stored base value instead of meaning "not supplied". And a property nobody
assigns keeps whatever the column was loaded with — write endpoints deliberately load without
hydrating, which in this mode leaves English sitting in `product.Name`. The engine recognises that
value as borrowed from the base column and never writes it back, so updating only a price under
`Accept-Language: ar` cannot file the English name as the Arabic one.

Everything else is unchanged: the value goes to `Translations`, and the base column is left out of
the `UPDATE` entirely.

**The columns are ordinary columns.** Configure them as you would any other:

```csharp
builder.Property(p => p.Name).HasMaxLength(256);
builder.Property(p => p.Description).HasMaxLength(2048);
```

In the default mode those same lines are accepted and then silently discarded, because
`ApplyMultiLingual()` removes the property from the model afterwards. Nothing warns you, so do not
read a configured length there as evidence that a column exists.

**The base language has to exist.** The application checks at startup that the configured code
names a language in the store and that it is active, and refuses to start when it does not —
a typo like `"en-US"` for `"en"` would otherwise surface much later, as a failed write. Two cases
are deliberately tolerated with a warning instead: a store that cannot be read yet (an un-migrated
database) and an empty language list, because failing there would be a deadlock — creating that
first language is what `POST /api/languages` is for.

To run the check at a moment you choose — after your own migrations and seeding — turn the
automatic one off and call it yourself:

```csharp
services.AddMultiLingual(o => o.BaseLanguage = "en")
        .UseEntityFrameworkCore<AppDbContext>()
        .DisableStartupValidation()
        .Build();

// ...
var app = builder.Build();
await app.Services.ValidateMultiLingualAsync();
```

**Adopting it on an existing database.** This is the mode to use when the table already has the
columns and the data: an existing `Products.Name` full of English *is* the base language, so
nothing has to be imported and no column is dropped. Set `BaseLanguage` to whatever that content
is already in.

**If the mapping and the configuration disagree** — `BaseLanguage` set but `ApplyMultiLingual()`
called without the context, or the reverse — the first save throws and says which call to fix.
Silently guessing would write the authored language into the base column, or drop the column in
the next generated migration.

## Languages

`ILanguageManager` (and the endpoints over it) enforce the rules: codes are validated and
normalised (`pt-br` → `pt-BR`), the first language becomes the default, there is always exactly
one active default, and a language that still has translations cannot be deleted — purge it
first, or deactivate it. A configured base language can be neither deleted nor deactivated, even
once it is no longer the default.

`MultiLingualExceptionHandler` answers the library's exceptions with problem details. A language
the URL addresses that does not exist (`GET /api/languages/xx`) is a 404; a request that merely
*uses* a language that does not exist — values staged in it, or `languageCode` passed to a query
helper — is a 400 (`UnknownLanguageException`), since what it was about does exist.

```
GET    /api/languages                      list, default first
GET    /api/languages/{code}
POST   /api/languages                      { "code": "ar", "name": "العربية", "direction": "rtl" }
PUT    /api/languages/{code}               { "name": "...", "direction": "ltr", "isActive": true }
DELETE /api/languages/{code}
POST   /api/languages/{code}/set-default
DELETE /api/languages/{code}/translations  purge
```

**Several instances.** Each instance caches the language list (5 minutes by default,
`WithLanguageCacheDuration()`). A change through `ILanguageManager` refreshes the instance that
made it at once, but the others only see it when their cache expires. To have every instance
pick it up within seconds, share a change signal through a distributed cache you already run:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddMultiLingual()
    .UseEntityFrameworkCore<AppDbContext>()
    .SynchronizeLanguagesThroughDistributedCache()      // checks every 5 seconds; pass another interval if you like
    .Build();
```

Each instance then reads one small cache entry per interval, and reloads the list only when it
changed. If the cache is unreachable, instances keep serving their cached list and log a warning,
and the cache expiry still applies. For another backend, implement `ILanguageChangeSignal` and
register it with `UseLanguageChangeSignal<T>()`.

Outside a request (jobs, seeders), set the language yourself — or stage values per language:

```csharp
await languageContext.UseAsync("ar");

translations.Stage(product, "fr", new Dictionary<string, string?> { ["Name"] = "Portable" });
await db.SaveChangesAsync();   // writes the staged French alongside everything else
```

In base-language mode a seeder assigns the base language to the properties and stages only the
others — staging the base language is refused, because the properties are already its one home.
Seeding needs no resolved language: with none, the "create in the base language" rule does not
apply, since there is no `Accept-Language` to be wrong about.

```csharp
var product = new Product { Sku = "LAPTOP-14", Name = "14-inch Laptop" };   // en, the column
translations.Stage(product, "ar", new Dictionary<string, string?> { ["Name"] = "حاسوب محمول" });
db.Products.Add(product);
await db.SaveChangesAsync();
```

## Providers without change tracking

Dapper and MongoDB have no interceptors to hook, so their users call `ITranslationManager`
directly — reading, saving and deleting translations alongside their own queries:

```csharp
var products = (await connection.QueryAsync<Product>(sql)).ToList();
await translations.HydrateAsync(products);

await connection.ExecuteAsync(insertSql, product);
await translations.SaveAsync(product);
```

In base-language mode such a provider has one extra duty, because it has no change tracker to ask
which properties a caller touched. Before saving in a language other than the base one, declare
the ones that still hold the value the row was loaded with:

```csharp
translations.MarkAsBaseLanguageValues(product, ["Description"]);   // only Name was edited
await translations.SaveAsync(product);
```

Without it, the base-language text read from the row is written back as a translation in whatever
language is current. The EF Core provider does this for you, from `IsModified`.

See [docs/adding-a-provider.md](docs/adding-a-provider.md) for what a provider has to implement.

## Known limitations

- **Deletes outside `SaveChanges` leave orphaned translations.** An entity's translations are
  removed when `SaveChanges` deletes it. `ExecuteDelete`, a database cascade over rows EF never
  loaded, and raw SQL bypass that, and leave its translation rows behind. They are never read,
  but an entity created again under the same key would inherit them. Clean up with
  `await db.PurgeOrphanedTranslationsAsync()` — after a bulk delete, or on a schedule. It deletes,
  in the database, only rows whose entity is gone, and leaves rows under entity names this
  context does not know.
- **Composite keys** are not supported: `Translation.EntityId` holds a single value.
- **Keys up to 64 characters.** Every numeric key and a `Guid` fit; a longer `string` key is
  refused when its translations are written (`EntityKey.MaxLength`).
- **`AddDbContextPool`** is not supported: pooled options are built once, from the root provider,
  and cannot carry per-request services.
- **Retrying execution strategies** (`EnableRetryOnFailure`) work with a plain `SaveChanges`:
  the save interceptor runs the entity, its translations and the commit inside the strategy,
  and replays them together on a transient failure. Two side effects: other interceptors see an
  extra `SavingChanges`/`SavedChanges` pair for the replayed save, and changes are accepted on
  success even under `SaveChanges(acceptAllChangesOnSuccess: false)`. To avoid both, open the
  transaction yourself inside `Database.CreateExecutionStrategy().ExecuteAsync(...)`.
- **Adding a translatable property** needs no migration in the default mode, and a migration for
  the new column in base-language mode.
- **Filtering and sorting** on translated values is not translated into SQL — they are not
  columns. Query `Translations` directly when you need to. In base-language mode the base
  language *is* a column, so filtering and sorting in that one language works normally.
- **Changing `BaseLanguage`** after content exists is a data migration, not a configuration
  change: the column and the translation rows would have to swap.
- **Two contexts of the same type** configured with different `BaseLanguage` values in one
  process share EF's cached model. Use one mode per context type.

## License

MIT
