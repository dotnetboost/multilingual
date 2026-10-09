# DotNetBoost.MultiLingual

[![NuGet](https://img.shields.io/nuget/vpre/DotNetBoost.MultiLingual.Core?label=nuget)](https://www.nuget.org/packages/DotNetBoost.MultiLingual.Core)
[![CI](https://github.com/dotnetboost/multilingual/actions/workflows/ci.yml/badge.svg)](https://github.com/dotnetboost/multilingual/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/dotnetboost/multilingual/blob/main/LICENSE)

**Serve your content in every language, from the entity classes you already have.**

You add one attribute to a property. DotNetBoost.MultiLingual stores each language's value for you,
serves the right one for every request, and falls back when a translation is missing — with no
extra table to design, no repository code and no service code.

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
GET /api/products/1   Accept-Language: en  ->  { "name": "14-inch Laptop", … }
GET /api/products/1   Accept-Language: ar  ->  { "name": "حاسوب محمول 14 بوصة", … }
GET /api/products/1   Accept-Language: fr  ->  English, when there is no French yet
```

Reading and writing that product is still plain EF Core — `Where(p => p.Name!.Contains(term))`
filters on the *translated* text, in the database.

Works with **.NET 8 and .NET 10**, on **SQL Server, PostgreSQL, SQLite or MongoDB**.

> **Status:** preview. The packages are on NuGet as `1.0.0-preview.1`, so installing them needs
> `--prerelease` — every command below has it. The API may still change before 1.0.0.

---

## Get started in 5 minutes

This builds a minimal web app that stores its content in a local SQLite file. Nothing else to install.

**1. Create a project and add the packages**

```bash
dotnet new web -n MyShop
cd MyShop
dotnet add package DotNetBoost.MultiLingual.Core --prerelease
dotnet add package DotNetBoost.MultiLingual.EntityFrameworkCore --prerelease
dotnet add package DotNetBoost.MultiLingual.API --prerelease
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
```

**2. Replace `Program.cs` with this**

```csharp
using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Store translations in SQLite through EF Core.
builder.Services.AddMultiLingual()
    .UseEntityFrameworkCore<AppDbContext>()
    .Build();

builder.Services.AddDbContext<AppDbContext>((sp, o) => o
    .UseSqlite("Data Source=shop.db")
    .UseMultiLingual(sp));              // attaches the interceptors

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();

var app = builder.Build();

// Create the database on first run. In a real app, use EF Core migrations instead.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();
app.UseMultiLingual();        // Accept-Language -> the language this request reads and writes in
app.MapLanguageEndpoints();   // adds GET/POST/… /api/languages

app.MapGet("/products", (AppDbContext db) => db.Products.ToListAsync());

app.MapPost("/products", async (Product product, AppDbContext db) =>
{
    db.Products.Add(product);
    await db.SaveChangesAsync();        // Name is stored for the request's language
    return product;
});

app.MapPut("/products/{id:int}/name", async (int id, NameUpdate body, AppDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null)
        return Results.NotFound();

    product.Name = body.Name;           // this language only; the others are untouched
    await db.SaveChangesAsync();
    return Results.Ok(product);
});

app.Run();

// Your entity: one attribute per translatable property.
public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    [MultiLingual] public string? Name { get; set; }
}

public record NameUpdate(string Name);

// Your EF Core context: nothing to implement, just call ApplyMultiLingual() LAST.
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyMultiLingual();   // adds the Translations and Languages tables
}
```

**3. Run it and add a language**

```bash
dotnet run --urls http://localhost:5080
```

In a second terminal. The first language created becomes the default:

```bash
curl -X POST http://localhost:5080/api/languages -H "Content-Type: application/json" -d '{"code":"en","name":"English"}'
curl -X POST http://localhost:5080/api/languages -H "Content-Type: application/json" -d '{"code":"ar","name":"العربية","direction":"rtl"}'
curl -X POST http://localhost:5080/api/languages -H "Content-Type: application/json" -d '{"code":"fr","name":"Français"}'
```

**4. Write one product in two languages**

```bash
curl -X POST http://localhost:5080/products -H "Content-Type: application/json" -H "Accept-Language: en" -d '{"sku":"LAPTOP-14","name":"14-inch Laptop"}'
curl -X PUT http://localhost:5080/products/1/name -H "Content-Type: application/json" -H "Accept-Language: ar" -d '{"name":"حاسوب محمول 14 بوصة"}'
```

**5. Read it back in each one**

```bash
curl http://localhost:5080/products -H "Accept-Language: ar"
```

```json
[{"id":1,"sku":"LAPTOP-14","name":"حاسوب محمول 14 بوصة"}]
```

```bash
curl http://localhost:5080/products -H "Accept-Language: fr"
```

```json
[{"id":1,"sku":"LAPTOP-14","name":"14-inch Laptop"}]
```

There is no French yet, so the read falls back to the default language. Nothing in the two product
endpoints mentions a language: the values were filled in by the same SQL statement that loaded the
row.

> ⚠️ **The language endpoints allow anonymous access by default.** They change what every client of
> your application sees, and one of them deletes content. Protect them before you deploy:
> `app.MapLanguageEndpoints().RequireAuthorization("admin")`. See
> [Securing the endpoints](https://github.com/dotnetboost/multilingual/blob/main/docs/rest-api.md#securing-the-endpoints).

---

## Using it in your own code

Reading and writing translated properties needs no library call at all — it is the EF Core you
already write. For everything else, inject `IMultiLingualManager`:

```csharp
public class ProductService(AppDbContext db, IMultiLingualManager multiLingual)
{
    public async Task TranslateAsync(int id)
    {
        var product = await db.Products.FirstAsync(p => p.Id == id);

        multiLingual.For(product).Set("fr", p => p.Name, "Portable");    // add a language
        multiLingual.For(product).Clear("ar");                           // remove one

        await db.SaveChangesAsync();   // written with the entity, in the same transaction
    }
}
```

| You want to… | Call |
|---|---|
| know the request's language | `multiLingual.CurrentLanguage`, `DefaultLanguage`, `WasRequestedLanguageHonoured` |
| set it outside a request (jobs, seeders) | `await multiLingual.UseLanguageAsync("ar")` |
| read every stored language of an entity | `await multiLingual.For(product).GetAllAsync()` |
| add, change or remove a translation | `.Set("fr", p => p.Name, "Chaise")`, `.Clear("fr")` |
| find or administer languages | `multiLingual.Languages.FindAsync("ar")`, `CreateAsync(…)`, `SetDefaultAsync(…)`, … |
| fill translated properties yourself (Dapper, MongoDB) | `await multiLingual.LocalizeAsync(products)` |
| translate something whose class you don't own | `multiLingual.For("MediaFile", id).Set("ar", "AltText", "…")` |
| read text for a whole page of those, in one query | `await multiLingual.ForMany("MediaFile", ids).GetAsync()` |

`Set` and `Clear` check the language straight away, so a mistake surfaces where it was made — and
`MultiLingualExceptionHandler` turns it into problem details, so a controller needs no checks of its
own. Full tour: [reading and writing](https://github.com/dotnetboost/multilingual/blob/main/docs/reading-and-writing.md).

---

## Using your existing database

The quick start uses EF Core with SQLite because that needs no setup. In a real app, use the
database you already have:

| You use… | Package | Register with |
|---|---|---|
| Entity Framework Core | `DotNetBoost.MultiLingual.EntityFrameworkCore` | `.UseEntityFrameworkCore<AppDbContext>()` |
| Dapper / plain ADO.NET | `DotNetBoost.MultiLingual.Dapper` | `.UseDapper(sp => new SqlConnection(cs), migrateSchema: true)` |
| MongoDB | `DotNetBoost.MultiLingual.MongoDb` | `.UseMongoDb("mongodb://localhost:27017", "shop")` |

EF Core needs one call in `OnModelCreating` and nothing else on your context. Dapper and MongoDB
have no change tracker to hook, so their users localize after a query and save translations
explicitly — a few extra lines, the same rules. See the [storage providers
guide](https://github.com/dotnetboost/multilingual/blob/main/docs/storage-providers.md).

**Already have a `Products.Name` column full of English?** Then that content *is* a language. Set
`BaseLanguage = "en"` and it stays exactly where it is, with every other language in the
translations table falling back to it — nothing to import, no column dropped. See
[base-language mode](https://github.com/dotnetboost/multilingual/blob/main/docs/base-language.md).

---

## What else it can do

Each feature is optional and switched on with one line on `AddMultiLingual()`:

```csharp
builder.Services.AddMultiLingual(o => o.BaseLanguage = "en")    // keep English in its own columns
    .UseEntityFrameworkCore<AppDbContext>()
    .AddJsonLocalization()                            // your own messages, in the same language
    .SynchronizeLanguagesThroughDistributedCache()    // several instances, one language list
    .WithLanguageCacheDuration(TimeSpan.FromMinutes(5))
    .Build();
```

| I want to… | Guide |
|---|---|
| Mark content translatable, and rename a class safely | [Declaring translatable content](https://github.com/dotnetboost/multilingual/blob/main/docs/translatable-content.md) |
| Use SQL Server, PostgreSQL, Dapper or MongoDB | [Storage providers](https://github.com/dotnetboost/multilingual/blob/main/docs/storage-providers.md) |
| Filter, sort and project on translated text, in SQL | [Translated queries](https://github.com/dotnetboost/multilingual/blob/main/docs/querying.md) |
| Keep one language in the entity's own columns | [Base-language mode](https://github.com/dotnetboost/multilingual/blob/main/docs/base-language.md) |
| Add, deactivate or promote a language at runtime | [Languages](https://github.com/dotnetboost/multilingual/blob/main/docs/languages.md) |
| Expose and secure the language and translation endpoints | [REST API](https://github.com/dotnetboost/multilingual/blob/main/docs/rest-api.md) |
| Join translation rows with another library's (`DotNetBoost.Media`) | [Entity addressing](#entity-addressing) |
| Return my own error messages in the caller's language | [Localized messages](https://github.com/dotnetboost/multilingual/blob/main/docs/localization.md) |
| Run on several servers (Redis) | [Languages → the cache](https://github.com/dotnetboost/multilingual/blob/main/docs/languages.md#the-cache) |
| Give editors a web UI | [Dashboard](https://github.com/dotnetboost/multilingual/blob/main/docs/dashboard.md) |
| See every builder option | [Configuration reference](https://github.com/dotnetboost/multilingual/blob/main/docs/configuration-reference.md) |
| Know what this does *not* do | [Known limitations](https://github.com/dotnetboost/multilingual/blob/main/docs/limitations.md) |
| Run the full demo stack (three APIs, dashboard, PostgreSQL, MongoDB) | [.NET Aspire](https://github.com/dotnetboost/multilingual/blob/main/docs/aspire.md) |
| Understand how it works inside, or contribute | [Architecture](https://github.com/dotnetboost/multilingual/blob/main/docs/architecture.md) |

---

## Entity addressing

Every translation row is filed against a pair: an **entity name** and an **entity id**. That pair is
all this library needs to know about your entity, which is why one table serves every
`[MultiLingual]` class in the system — and why `For("MediaFile", id)` can carry text for a class
this library has never seen.

Other DotNetBoost libraries file rows against the same pair. `DotNetBoost.Media` stores a file's
owner that way, so an application can join its media rows with its translation rows. That only
works while both libraries format the pair identically, so the rules are a versioned spec rather
than a coincidence.

### DotNetBoost Entity Addressing v1

| | Rule |
|---|---|
| **Entity name** | The name fixed by `[MultiLingualEntity("…")]` or `[MultiLingual(EntityName = "…")]`; otherwise the CLR type's `Name` (not its full name, so a namespace move changes nothing). A class generated at runtime — an EF Core lazy-loading proxy, any dynamic-assembly subclass — is addressed as the class it derives from. |
| **Name length** | At most 128 characters. Stored and compared as written: resolution yields one casing for a given type, so case only matters for a name passed by hand, where whether `"mediafile"` reaches `"MediaFile"`'s rows is the database collation's business. Pass the resolved name. |
| **Key discovery** | The single property marked `[Key]`, else one named `Id`, else one named `{TypeName}Id` — public, instance, readable, and not an indexer. More than one `[Key]` is a composite key and is **refused**: one column holds one value, and reflection does not order properties, so picking one would key the entity on whichever this runtime happened to return. A provider with a model to read replaces this — both EF Core providers do — and is authoritative where the two disagree. |
| **Key format** | Invariant culture. `Guid` → `"D"` (lower case, hyphenated, no braces). `int`, `long`, `short`, `byte` → their invariant decimal form. `string` → itself, case preserved. Any other `IFormattable` → invariant `ToString`. |
| **Key length** | At most 64 characters — a `Guid` in `"D"` form is 36, and every numeric key fits with room to spare. A longer key is refused where it is written, not silently truncated by the database. |
| **Unset keys** | `null`, `0` (of any integer width), `Guid.Empty` and `""` mean "not assigned yet" and address nothing. A provider whose keys are a custom struct must return `null` from its `IEntityKeyAccessor` for its own unassigned value — Core cannot recognise a type it may not reference. |

Because an id is formatted by the library, `For("X", 42)` and `For("X", "42")` reach the same rows.

### Asserting that two libraries agree

The version is a public constant, so a consumer can state the dependency in one line instead of
writing its own theory table over key formats:

```csharp
using DotNetBoost.MultiLingual.Core.Metadata;

[Fact]
public void MediaAndMultiLingualAddressSubjectsTheSameWay() =>
    Assert.Equal(
        DotNetBoost.Media.Core.Metadata.EntityKey.AddressingVersion,
        EntityKey.AddressingVersion);
```

`EntityKey.AddressingVersion` is bumped for **any** change to name resolution, key formatting or
either limit — so such a change fails loudly in every consumer instead of quietly stranding rows
that were written under the old rules. That one equality is the whole of the dependency: there is
nothing further to install and nothing to keep in step by hand.

`EntityKey.MaxLength` and `MultiLingualMetadata.MaxEntityNameLength` are public as well, for a
consumer that wants to compare the limits it stores keys and names under rather than trust the
version alone.

---

## Try the full demo

[`samples/SampleApp`](https://github.com/dotnetboost/multilingual/tree/main/samples/SampleApp) is a
complete Products and Categories API in base-language mode, so `Products.Name` is a real column you
can read in any SQL client. Two more samples serve *the same routes and the same bodies* over the
Dapper and MongoDB providers. If you have Docker running, one command starts all of it — plus
PostgreSQL, MongoDB, a browser SQL client and the web dashboard:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

The API is then on `http://localhost:5160`, with its reference at `/scalar`. See
[Running everything with .NET Aspire](https://github.com/dotnetboost/multilingual/blob/main/docs/aspire.md).

---

## Packages

| Package | What it's for |
|---|---|
| `DotNetBoost.MultiLingual.Core` | Required. Attributes, the engine, `IMultiLingualManager`, the store contracts |
| `DotNetBoost.MultiLingual.EntityFrameworkCore` | EF Core provider: mapping and interceptors, so queries and saves translate themselves |
| `DotNetBoost.MultiLingual.Dapper` | Dapper provider, over the EF Core provider's own schema |
| `DotNetBoost.MultiLingual.MongoDb` | MongoDB provider |
| `DotNetBoost.MultiLingual.API` | ASP.NET Core: `Accept-Language`, language endpoints, problem details, OpenAPI |
| `DotNetBoost.MultiLingual.Localization` | `IStringLocalizer` over JSON files, in the content's language |

## Contributing

Contributions are welcome. See
[CONTRIBUTING.md](https://github.com/dotnetboost/multilingual/blob/main/CONTRIBUTING.md) and the
[architecture overview](https://github.com/dotnetboost/multilingual/blob/main/docs/architecture.md).

## License

MIT. See [LICENSE](https://github.com/dotnetboost/multilingual/blob/main/LICENSE).
