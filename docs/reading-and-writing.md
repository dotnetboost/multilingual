# Reading and writing

With EF Core, reading and writing translated content is plain EF Core — no library call at all:

```csharp
// Read: Name comes back in the request's language, with fallback, filled in by the same SQL
// statement that loads the products.
var products = await db.Products.AsNoTracking().Where(p => p.Name!.Contains(term)).ToListAsync(ct);

// Write: Name is stored for the request's language. Other languages are untouched.
if (request.Name is not null)
    product.Name = request.Name;

await db.SaveChangesAsync(ct);
```

Everything beyond that goes through one service.

## `IMultiLingualManager`

Inject it anywhere. It is the only service application code needs besides the `DbContext` — like
`ISettingManager` in DotNetBoost.Settings.

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
| know the request's language | `multiLingual.CurrentLanguage`, `DefaultLanguage`, `IsLanguageResolved`, `WasRequestedLanguageHonoured` |
| set it outside a request (jobs, seeders) | `await multiLingual.UseLanguageAsync("ar")` |
| read every stored language of an entity | `await multiLingual.For(product).GetAllAsync()` |
| add or change a translation in another language | `multiLingual.For(product).Set("fr", p => p.Name, "Chaise")` |
| remove a translation | `multiLingual.For(product).Clear("fr")` — or `Clear("fr", p => p.Name)` |
| find or administer languages | `multiLingual.Languages.FindAsync("ar")`, `CreateAsync(…)`, `SetDefaultAsync(…)`, `IsBaseLanguage(…)`, … |
| fill translated properties yourself (Dapper, MongoDB) | `await multiLingual.LocalizeAsync(products)` |
| save / delete translations yourself (Dapper, MongoDB) | `await multiLingual.For(product).SaveAsync()` / `DeleteAsync()` |

`Set` and `Clear` only **queue** the change; it is written by the next `SaveChanges` (or
`For(…).SaveAsync()` without EF Core), in the same transaction as the entity. They check the
language straight away, so a mistake surfaces where it was made: an unknown code throws
`LanguageNotFoundException`, the base language `BaseLanguageNotTranslatableException`.
`MultiLingualExceptionHandler` turns both into problem details, so a controller needs no checks of
its own — see [the REST API](rest-api.md).

`multiLingual.Languages` is covered on its own page: [Languages](languages.md).

## The read path

A value is served from, in order:

1. the requested language,
2. the default language (unless `DisableDefaultLanguageFallback()`),
3. `null` — or, in [base-language mode](base-language.md), the base language's column, which is
   always the last resort.

Only the languages that are actually stored as rows are fetched, in one query per entity type.

`GetAllAsync()` returns every stored translation of one entity as
`languageCode → (propertyName → value)`, with no fallback and no merging — it is what is in the
table. In base-language mode the base language is not among them: it is the entity's own columns.

## The write path

**`null` means "not supplied"** and leaves the stored value alone. That is what makes a partial
update safe:

```csharp
if (request.Name is not null)
    product.Name = request.Name;
```

**A value that is still exactly what the read path put there is not written back.** Reading a
product in French — which fell back to English — and then saving a price change never turns the
English text into a French translation. The same protection covers a value that arrived from a
base-language column rather than from a translation row, and it holds in every scope, background
jobs included.

Those two rules are why the shape above is the one to use everywhere. In base-language mode there
is a third reason: translatable properties are real columns there, so assigning `null` would
*erase* the stored base value rather than mean "not supplied".

Without a change tracker, the second rule needs your help — see
[providers without a change tracker](storage-providers.md#providers-without-a-change-tracker).

## Removing a translation

Say so explicitly. The row is deleted on the next save, and reads fall back as for any missing
value:

```csharp
multiLingual.For(product).Clear("fr", p => p.Name);   // one property
multiLingual.For(product).Clear("fr");                // every translatable property
await db.SaveChangesAsync();
```

`Set(language, property, null)` does the same for one property. The base language is not a
translation and cannot be cleared; assign the properties instead.

## Setting other languages

`Set` writes a language other than — or as well as — the current one, whose values are simply
assigned to the properties:

```csharp
multiLingual.For(product)
    .Set("fr", p => p.Name, "Portable")
    .Set("fr", p => p.Description, "Un ordinateur portable de 14 pouces");

await db.SaveChangesAsync();
```

There is an overload taking `propertyName → value`, for when the names come from a request rather
than from code:

```csharp
multiLingual.For(product).Set("fr", request.Fields);   // IReadOnlyDictionary<string, string?>
```

## Outside a request

Background jobs, seeders and tests have no `Accept-Language`, so set the language yourself:

```csharp
await using var scope = services.CreateAsyncScope();
var multiLingual = scope.ServiceProvider.GetRequiredService<IMultiLingualManager>();

await multiLingual.UseLanguageAsync("ar");   // false if "ar" is unknown or inactive: the default applies
```

A seeder can skip that entirely and set each language explicitly:

```csharp
var product = new Product { Sku = "LAPTOP-14", Name = "14-inch Laptop" };
multiLingual.For(product).Set("ar", p => p.Name, "حاسوب محمول");
db.Products.Add(product);
await db.SaveChangesAsync();
```

In base-language mode the properties themselves *are* the base language, so a seeder assigns them
and sets only the others; setting the base language through `Set` is refused. Seeding needs no
resolved language: with none, the "create in the base language" rule does not apply, because there
is no `Accept-Language` to be wrong about.

## Deleting entities, and orphaned rows

`SaveChanges` removes an entity's translations when it deletes the entity. Deletes that never pass
through `SaveChanges` do not: `ExecuteDelete`, a database cascade over rows EF never loaded, raw
SQL, and other applications writing to the same database.

An orphaned row is never read, so mostly it just costs space — until an entity is created again
under the same key (a `string` or `Guid` key can come back) and silently inherits the old one's
translations. Clean up after a bulk delete, or on a schedule:

```csharp
await db.Products.Where(p => p.Discontinued).ExecuteDeleteAsync(ct);
await db.PurgeOrphanedTranslationsAsync(ct);
```

It runs one set-based `DELETE … WHERE NOT EXISTS` per translatable entity type, in the database,
and only for entity names this context's model knows — a row under any other name may belong to
another context sharing the table, or to a class renamed without fixing its entity name. Run it on
a context with no pending translation changes, such as one from a fresh scope.

It is EF Core only: it joins `Translations` against each entity's own table. On MongoDB, collect
the keys **before** deleting the documents and pass them to
`ITranslationWriter.DeleteEntitiesAsync` — afterwards nothing knows what they were.
