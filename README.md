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
| `DotNetBoost.MultiLingual.Core` | The provider-neutral engine: attributes, `IMultiLingualManager` — the one service applications inject — and, for storage providers, `ITranslationWriter` and the store contracts `ITranslationStore` / `ILanguageStore`. No EF Core, no ASP.NET Core. |
| `DotNetBoost.MultiLingual.EntityFrameworkCore` | EF Core provider. Maps translatable properties according to the configured mode and reads/writes them transparently through interceptors. SQL Server, PostgreSQL, SQLite. |
| `DotNetBoost.MultiLingual.API` | ASP.NET Core: `Accept-Language` middleware, language management endpoints, ProblemDetails mapping, and (net10.0) an OpenAPI transformer. |
| `DotNetBoost.MultiLingual.Localization` | `IStringLocalizer` for the application's own messages, read from JSON files in the same language as the content. See [localized messages](#localized-messages). |
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
app.UseMultiLingual();                             // Accept-Language -> the current language
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

Controllers then read and write `Product.Name` like any other property — with nothing but EF
Core's own methods:

```csharp
// Read: plain EF Core. Name comes back in the request's language (with fallback), filled in by the
// same SQL statement that loads the products.
var products = await db.Products.AsNoTracking().Where(p => p.Name!.Contains(term)).ToListAsync(ct);

// Write: assign only what the caller supplied, and Name is stored for the request's language.
// Other languages are untouched. Assigning null would mean "not supplied" in the default mode,
// but erases the stored value in base-language mode — so this shape is the one to use in both.
if (request.Name is not null)
    product.Name = request.Name;

await db.SaveChangesAsync(ct);
```

## Using it: `IMultiLingualManager`

Everything else goes through one service. Inject `IMultiLingualManager` anywhere — like
`ISettingManager` in DotNetBoost.Settings, it is the only thing application code needs:

```csharp
public sealed class ProductsController(
    AppDbContext db,
    IMultiLingualManager multiLingual,
    IStringLocalizer<SharedResource> localizer) : ControllerBase
{
    [HttpDelete("{id:int}/translations/{languageCode}")]
    public async Task<IActionResult> ClearTranslation(int id, string languageCode, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
            return NotFound();

        multiLingual.For(product).Clear(languageCode);   // unknown → 404, base language → 409
        await db.SaveChangesAsync(ct);
        return Ok(localizer["Products.TranslationCleared", product.Sku, languageCode]);
    }
}
```

| You want to… | Call |
|---|---|
| know the request's language | `multiLingual.CurrentLanguage`, `DefaultLanguage`, `WasRequestedLanguageHonoured` |
| set it outside a request (jobs, seeders) | `await multiLingual.UseLanguageAsync("ar")` |
| read every stored language of an entity | `await multiLingual.For(product).GetAllAsync()` |
| add or change a translation in another language | `multiLingual.For(product).Set("fr", p => p.Name, "Chaise")` |
| remove a translation | `multiLingual.For(product).Clear("fr")` — or `Clear("fr", p => p.Name)` |
| find or administer languages | `multiLingual.Languages.FindAsync("ar")`, `CreateAsync(…)`, `SetDefaultAsync(…)`, `IsBaseLanguage(…)`, … |
| fill translated properties yourself (Dapper, MongoDB) | `await multiLingual.LocalizeAsync(products)` |
| save / delete translations yourself (Dapper, MongoDB) | `await multiLingual.For(product).SaveAsync()` / `DeleteAsync()` |

`Set` and `Clear` only queue the change; it is written by the next `SaveChanges` (or
`For(…).SaveAsync()` without EF Core), in the same transaction as the entity. They check the
language straight away, so a mistake surfaces where it was made: an unknown code throws
`LanguageNotFoundException`, the base language `BaseLanguageNotTranslatableException`.
`MultiLingualExceptionHandler` turns both into problem details, so a controller needs no checks
of its own.

The application's own messages come from the standard `IStringLocalizer<T>` — see
[localized messages](#localized-messages).

`samples/SampleApp` is a complete, runnable version of this — PostgreSQL, Scalar UI, seeded data,
and configured in base-language mode so `Products.Name` is a real column you can see in any SQL
client. With Docker running, one command starts it together with the database, a browser SQL
client and the dashboard:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost    # API on http://localhost:5160
```

See [docs/aspire.md](docs/aspire.md) for what comes up, switching to SQL Server or SQLite, and
running the API on its own. `samples/SampleApp/SampleApp.http` walks every behaviour below, edge
cases included. To click through it instead, [`clients/dashboard`](clients/dashboard) is a Nuxt web
UI for the language endpoints and the sample's content — see [docs/dashboard.md](docs/dashboard.md).

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
multiLingual.For(product).Clear("fr", p => p.Name);   // one property
multiLingual.For(product).Clear("fr");                // every translatable property
await db.SaveChangesAsync();
```

**Every query is translated.** On a context configured with `UseMultiLingual(sp)` there is nothing
to call: every read of a translatable property — in `Where`, `OrderBy`, `Select`, through
navigations, inside nested collections, in `Count` / `Any` / `ExecuteDelete` — is the value a read
shows (requested language, then the default, then the base column), and every entity a query
returns comes back with its translatable properties filled in, included navigations too. It all
runs in the database, in the query's own SQL statement:

```csharp
// Entities, filtered and sorted on translated text; Category.Name filled too.
var page = await db.Products
    .Where(p => p.CategoryId == categoryId && p.Name!.Contains(term))   // ordinary columns stay ordinary
    .OrderBy(p => p.Name)
    .Include(p => p.Category)
    .Skip(skip).Take(take)
    .ToListAsync(ct);

// Straight into a DTO — navigations and nested collections included.
var rows = await db.Products
    .Select(p => new ProductDto { Sku = p.Sku, Name = p.Name, Category = p.Category!.Name })
    .ToListAsync(ct);
```

That covers `ToListAsync`, `First…`, `Single…`, `Last…`, `ElementAt…`, `Find`, `foreach` /
`AsAsyncEnumerable`, synchronous queries, tracking and no-tracking queries, explicit loading,
split queries, and `Include` / `ThenInclude` — collection, reference, filtered, string-based and
automatic includes alike. A tracked entity that a later query finds again is refreshed only where
nobody has edited it since, so a read never overwrites an unsaved change.

Two switches change one query:

```csharp
db.Products.InLanguage("fr")          // read this query in French, whatever the request's language
db.Products.IgnoreTranslations()      // read the stored columns: in base-language mode, the base text
```

`IgnoreTranslations()` in the default mode, where a translatable property has no column, returns
entities with those properties `null` — and a query that reads one is refused with an error naming
the property, rather than EF quietly projecting `null`. `InLanguage` compiles one query per code,
like any constant: pass the application's languages, not free user input.

Types in an inheritance hierarchy should share one entity name (`[MultiLingualEntity]` on the base),
since a query over the base type looks rows up by its name.

*How.* EF compiles a query shape once and reuses it for every request, so the compiled SQL cannot
contain the request's language. Three interceptors split the work, all through EF Core's
documented interception API:

1. **When EF compiles the query**, the query-expression interceptor replaces every translatable
   property read with indexed lookups into the translations table, and gives a query that returns
   entities a final projection that loads their translated values alongside. Where the languages
   go it writes placeholders; entity and property names are literals.
2. **Before each execution**, a command interceptor puts the scope's languages in place of the
   placeholders — which is why `ToQueryString()` shows `'~ml:L~'` rather than `'fr'`.
3. **As EF materializes each entity**, the materialization interceptor records which context it
   belongs to, so the projection can serve the values through that request's engine — recording,
   as a hydration always has, which values came from a fallback, so saving never promotes them.

*Cost.* Each translated value is an indexed lookup per language in the chain, in the same statement.
Measured on PostgreSQL 17 (warm cache, Arabic with English fallback):

| Products | Page of 20 entities, translated | 1,000 entities, translated (untranslated) | Search on translated name | Sort by translated name, page 1 |
|---:|---:|---:|---:|---:|
| 1,000 | < 1 ms | 6 ms (2.5 ms) | 2 ms | 2 ms |
| 10,000 | < 1 ms | 6 ms (2.4 ms) | 26 ms | 25 ms |
| 100,000 | < 1 ms | 6 ms (1.5 ms) | 277 ms | 281 ms |
| 1,000,000 | < 1 ms | 7 ms (1.4 ms) | 1.8 s | 1.7 s |

Returning translated rows is cheap at any size. Searching or sorting *on* translated text reads it
for every candidate row, like any `LIKE '%…%'` on a column — so narrow with an ordinary column
where you can (the same 1M-row search within one category: ~95 ms), keep the base language as a
real, indexable column (base-language mode), and use a search engine for full-text search over
large catalogues.

**EF Core specifics.** `ApplyMultiLingual()` removes translatable properties from the model — or,
with a base language configured, leaves them mapped. The save interceptor captures values in
`SavingChanges` and writes them in `SavedChanges`, once generated keys exist, in the same
transaction — opening one if the caller has not. It also inspects `Unchanged` entries, because
assigning only an unmapped property does not make EF consider the entity modified.

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
| Filter/sort in SQL | automatic | automatic; `IgnoreTranslations()` to filter on the base column itself |
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
   drift; `For(entity).Set(baseLanguage, …)` and `Clear(baseLanguage)` are refused with
   `BaseLanguageNotTranslatableException` (HTTP 409) for the same reason.

**Writing in a language other than the base one.** Assign only what the caller actually supplied:

```csharp
if (request.Name is not null)
    product.Name = request.Name;
```

Two things make that the right shape here. Translatable properties are real columns, so assigning
`null` *erases* the stored base value instead of meaning "not supplied". And a property nobody
assigns keeps whatever the query read — the Arabic text, or English borrowed from the base column
when there is no Arabic. The engine recognises such a value as read, not authored, and never writes
it back: updating only a price under `Accept-Language: ar` can neither file the English name as the
Arabic one nor put the Arabic name into the English column. That holds in every scope — a
background job reading in a default language other than the base one included.

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

`multiLingual.Languages` (and the endpoints over it) enforce the rules: codes are validated and
normalised (`pt-br` → `pt-BR`), the first language becomes the default, there is always exactly
one active default, and a language that still has translations cannot be deleted — purge it
first, or deactivate it. A configured base language can be neither deleted nor deactivated, even
once it is no longer the default.

`MultiLingualExceptionHandler` answers the library's exceptions with problem details. A language
the URL addresses that does not exist (`GET /api/languages/xx`) is a 404; a request that merely
*uses* a language that does not exist — `languageCode` passed to a query helper, say — is a 400
(`UnknownLanguageException`), since what it was about does exist.

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
`WithLanguageCacheDuration()`). A change through `multiLingual.Languages` refreshes the instance that
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

Outside a request (jobs, seeders), set the language yourself — or set values per language:

```csharp
await multiLingual.UseLanguageAsync("ar");

multiLingual.For(product).Set("fr", p => p.Name, "Portable");
await db.SaveChangesAsync();   // writes the French alongside everything else
```

In base-language mode a seeder assigns the base language to the properties and sets only the
others — setting the base language is refused, because the properties are already its one home.
Seeding needs no resolved language: with none, the "create in the base language" rule does not
apply, since there is no `Accept-Language` to be wrong about.

```csharp
var product = new Product { Sku = "LAPTOP-14", Name = "14-inch Laptop" };   // en, the column
multiLingual.For(product).Set("ar", p => p.Name, "حاسوب محمول");
db.Products.Add(product);
await db.SaveChangesAsync();
```

## Localized messages

Translated content is data; the messages an API returns ("Product 42 was not found.") are part of
the code. `DotNetBoost.MultiLingual.Localization` serves those through the standard
`IStringLocalizer<T>`, from one JSON file per language, and answers in the same language as the
content with the same fallback.

```csharp
builder.Services.AddMultiLingual()
    .UseEntityFrameworkCore<AppDbContext>()
    .AddJsonLocalization()            // Resources/en.json, Resources/ar.json, …
    .Build();
```

```jsonc
// Resources/en.json — comments and trailing commas are allowed
{
  "Products": {
    "NotFound": "Product {0} was not found.",
    "SkuExists": "A product with SKU '{0}' already exists."
  }
}
```

```csharp
public sealed class ProductsController(IStringLocalizer<SharedResource> localizer) : ControllerBase
{
    // …
    return NotFound(localizer["Products.NotFound", id].Value);
}
```

- **Files** are named after the language code (`pt-BR.json`, matched case-insensitively) and all
  localizers share them; the `T` in `IStringLocalizer<T>` only names the consumer. Nested objects
  join into dotted keys, so `Products.NotFound` above. Arguments fill `{0}`, `{1}`, … as in
  `string.Format`.
- **Fallback** follows the content: the requested language, its parent (`ar-EG` → `ar`), the default
  language (unless `DisableDefaultLanguageFallback()`), then the base language. A key found nowhere
  comes back as itself with `ResourceNotFound = true`, like every `IStringLocalizer`. A language with
  no file at all simply reads the default's messages.
- **Where** they are read from: `ResourcesPath` (default `Resources`) under the content root. Web
  projects publish `*.json` files automatically; elsewhere add
  `<None Update="Resources/*.json" CopyToOutputDirectory="PreserveNewest" />`, or pass an
  `EmbeddedFileProvider` as `FileProvider` to ship them inside an assembly.
- **Checked and reloaded:** every file is parsed at startup, so a malformed one fails the start with
  the file and key in the message (`ValidateFilesAtStartup`). An edited file is picked up without a
  restart (`ReloadOnChange`).
- `AddJsonLocalization()` replaces the `.resx` localizer from `AddLocalization()`, whichever is called
  first.

**Which language.** The `IStringLocalizer<T>` you inject reads the scope's current language
(`IMultiLingualManager.CurrentLanguage`), exactly like the interceptors. That matters in two places where .NET's ambient
`CultureInfo.CurrentUICulture` would be wrong:

- **Exception handlers** run outside `UseMultiLingual()`, after the culture it set is gone. They are
  singletons, so take the localizer from the request rather than the constructor — the root
  provider has no language, and with scope validation on (the Development default) resolving it
  there throws:

  ```csharp
  var localizer = httpContext.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
  ```

- **Background jobs** set a language with `UseLanguageAsync`, and localizers resolved from that scope follow it:

  ```csharp
  await using var scope = services.CreateAsyncScope();
  await scope.ServiceProvider.GetRequiredService<IMultiLingualManager>().UseLanguageAsync("ar");
  var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();
  ```

Localizers the framework creates itself through `IStringLocalizerFactory` — DataAnnotations
validation messages, for instance — are cached for the application's lifetime and cannot hold a
scope, so they read `CurrentUICulture`. `UseMultiLingual()` sets it to the resolved language for the
rest of the pipeline; `app.UseMultiLingual(o => o.SetCurrentUICulture = false)` turns that off, and
`o.SetCurrentCulture = true` sets the formatting culture as well (off by default: it changes how
numbers and dates are parsed). Anywhere else, `multiLingual.ApplyUiCulture()` sets it for the
calling method — it is synchronous so that the culture survives it returning.

**The library's own problem details** are looked up in the same files, under
`MultiLingual.Problems.{Kind}.Title` and `.Detail`, where Kind is the exception name without
"Exception" (`UnknownLanguage`, `LanguageNotFound`, `InvalidLanguage`, `LanguageConflict`,
`NoLanguageConfigured`, `BaseLanguageRequired`, `BaseLanguageNotTranslatable`). Anything left out
keeps its English text. Details
take the exception's data as arguments — see `MultiLingualProblems.ToProblemDetails`;
`InvalidLanguage` and `LanguageConflict` cover many rules each, so only their titles are
translatable. `samples/SampleApp/Resources/ar.json` translates them all.

## Providers without change tracking

Dapper and MongoDB have no interceptors to hook, so their users call the same
`IMultiLingualManager` explicitly — reading, saving and deleting translations alongside their own
queries:

```csharp
var products = (await connection.QueryAsync<Product>(sql)).ToList();
await multiLingual.LocalizeAsync(products);

await connection.ExecuteAsync(insertSql, product);
await multiLingual.For(product).SaveAsync();

await connection.ExecuteAsync(deleteSql, product);
await multiLingual.For(product).DeleteAsync();
```

In base-language mode such a provider has one extra duty, because it has no change tracker to ask
which properties a caller touched. Before saving in a language other than the base one, declare
the ones that still hold the value the row was loaded with:

```csharp
multiLingual.For(product).MarkAsBaseLanguageValues(p => p.Description);   // only Name was edited
await multiLingual.For(product).SaveAsync();
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
- **SQL Server compatibility level 130 or higher** (SQL Server 2016+). EF Core 8 and later send a
  list of keys — which reads and writes of translations use — as one JSON parameter that SQL
  Server unpacks with `OPENJSON`. On a database at an older level, say so and EF sends the keys
  inline instead: `UseSqlServer(connectionString, o => o.UseCompatibilityLevel(120))`.
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
- **Relational providers only.** The languages reach a query through command interception, which EF
  Core offers for relational databases — SQL Server, PostgreSQL, SQLite.
- **What a query does not fill.** Entities it returns directly, inside a final `Select` (`new { p,
  p.Category }`), or through `Include` are filled. Entities in a collection placed inside a
  projection (`Select(c => new { c.Products })`), in `GroupBy` results, or loaded outside EF are
  not; pass them to `multiLingual.LocalizeAsync(entities)`.
- **`ToQueryString()` and EF's SQL logs show placeholders** (`'~ml:L~'`) where the languages go:
  they print the compiled SQL, before the command interceptor fills in the request's languages.
- **Changing `BaseLanguage`** after content exists is a data migration, not a configuration
  change: the column and the translation rows would have to swap.
- **Two contexts of the same type** configured with different `BaseLanguage` values in one
  process share EF's cached model. Use one mode per context type.

## License

MIT
