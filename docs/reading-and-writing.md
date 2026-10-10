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
| translate something whose class you don't own | `multiLingual.For("MediaFile", id)` — see [subjects](#subjects-translating-something-you-dont-own-the-class-of) |
| read text for a whole page of those, in one query | `await multiLingual.ForMany("MediaFile", ids).GetAsync()` |

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

## Subjects: translating something you don't own the class of

`For(entity)` needs a `[MultiLingual]` class. Sometimes there isn't one — you want alt text and
captions for a `MediaFile` that belongs to another library, or for a row you read with raw SQL, or
for a record that lives in another service. Address it by **entity name and id** instead:

```csharp
await multiLingual.For("MediaFile", file.Id)
    .Set("en", "AltText", "A red chair")
    .Set("ar", "AltText", "كرسي أحمر")
    .SaveAsync();                                      // one store write, every language

var text = await multiLingual.For("MediaFile", file.Id).GetAsync();
var alt  = text["AltText"];                            // current language, then the default
```

| Call | Returns |
|---|---|
| `.GetAsync()` | `propertyName → value` in the current language, with the default-language fallback — **the same rule** the EF Core interceptors apply, from the same code |
| `.GetAllAsync()` | `languageCode → (propertyName → value)`, everything stored |
| `.Set(code, name, value)` / `.Set(code, values)` | Queues a value. `null` removes the stored row |
| `.SaveAsync()` | Writes everything queued, as one batch |
| `.DeleteAsync()` | Removes every language, returning the row count |

For a listing page, resolve the whole page at once — `ForMany` is **one** query, where a loop over
`For` would be one per id:

```csharp
var ids  = page.Select(f => (object)f.Id).ToList();
var text = await multiLingual.ForMany("MediaFile", ids).GetAsync();   // entityId → (property → value)

var alt = text.TryGetValue(file.Id.ToString("D"), out var values) ? values["AltText"] : null;
```

The id may be an `int`, `long`, `Guid` or `string`: the library formats it, so `For("X", 42)` and
`For("X", "42")` reach the same rows. See [entity addressing](../README.md#entity-addressing) for
the rules, which are a versioned spec.

Two differences from `For(entity)`:

- **No property is validated.** There is no type to validate against, so any property name is
  accepted. That is the point of the API, not an oversight.
- **A subject has no columns of its own**, so in [base-language mode](base-language.md) every
  language it carries — the base language included — is a translation row. Do not address one of
  your own `[MultiLingual]` entities this way in that mode: its base-language values belong in its
  own columns, and a row written here for that language would be invisible to every query.

To let clients do this over HTTP without writing the CRUD yourself, see
[the translation endpoints](rest-api.md#the-translation-endpoints).

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
