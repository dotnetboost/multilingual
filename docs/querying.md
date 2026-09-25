# Translated queries

*Entity Framework Core only.* On a context configured with `UseMultiLingual(sp)` there is nothing
to call. Every read of a translatable property — in `Where`, `OrderBy`, `Select`, through
navigations, inside nested collections, in `Count` / `Any` / `ExecuteDelete` — is the value a read
shows (the requested language, then the default, then the base column), and every entity a query
returns comes back with its translatable properties filled in, included navigations too. It all
runs in the database, in the query's own SQL statement.

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

## What is covered

`ToListAsync`, `First…`, `Single…`, `Last…`, `ElementAt…`, `Find`, `foreach` /
`AsAsyncEnumerable`, synchronous queries, tracking and no-tracking queries, explicit and lazy
loading (lazy-loading proxies included), split queries, compiled queries (`EF.CompileAsyncQuery`),
`Concat` / `Union`, queries composed on `FromSql`, `ExecuteUpdate` / `ExecuteDelete`, inheritance
(a query over a base type fills each derived type's own properties), and `Include` / `ThenInclude`
— collection, reference, filtered, string-based and automatic includes alike.

A tracked entity that a later query finds again is refreshed only where nobody has edited it
since, so a read never overwrites an unsaved change.

## What a query does not fill

Entities a query returns directly, inside a final `Select` (`new { p, p.Category }`), or through
`Include` are filled. These are not:

- a collection placed inside a projection (`Select(c => new { c.Products })`),
- `GroupBy` results,
- entities loaded outside EF Core, or built by hand.

Pass them to `multiLingual.LocalizeAsync(entities)`.

## Two per-query switches

```csharp
db.Products.InLanguage("fr")          // read this query in French, whatever the request's language
db.Products.IgnoreTranslations()      // read the stored columns: in base-language mode, the base text
```

`InLanguage` applies to filters, sorting, projections and the entities the query returns, with the
usual fallback. Each distinct code compiles as its own query, like any other constant in a query —
so pass one of the application's languages, not free user input. An unknown code throws
`UnknownLanguageException` when the query runs.

`IgnoreTranslations()` is most useful in [base-language mode](base-language.md), where it reads the
base column itself. In the default mode a translatable property has no column at all: entities come
back with those properties `null`, and a query that *reads* one is refused with an error naming the
property, rather than EF quietly projecting `null`.

## How it works

EF Core compiles a query shape once and reuses it for every request, so the compiled SQL cannot
contain the request's language. Four interceptors split the work, all through EF Core's documented
interception API:

1. **When EF compiles the query**, the query-expression interceptor replaces every translatable
   property read with indexed lookups into the translations table, and gives a query that returns
   entities a final projection loading their translated values alongside. Where the languages go it
   writes placeholders; entity and property names are literals.
2. **Before each execution**, a command interceptor puts the scope's languages in place of the
   placeholders — which is why `ToQueryString()` shows `'~ml:L~'` rather than `'fr'`.
3. **As EF materializes each entity**, the materialization interceptor records which context it
   belongs to, so the projection can serve the values through that request's engine — recording, as
   a hydration always has, which values came from a fallback, so saving never promotes them.
4. **On save**, the save interceptor captures values in `SavingChanges` and writes them in
   `SavedChanges`, once generated keys exist, in the same transaction — opening one if the caller
   has not.

## Cost

Each translated value is an indexed lookup per language in the chain, in the same statement.
Measured on PostgreSQL 17 (warm cache, Arabic with English fallback):

| Products | Page of 20 entities, translated | 1,000 entities, translated (untranslated) | Search on translated name | Sort by translated name, page 1 |
|---:|---:|---:|---:|---:|
| 1,000 | < 1 ms | 6 ms (2.5 ms) | 2 ms | 2 ms |
| 10,000 | < 1 ms | 6 ms (2.4 ms) | 26 ms | 25 ms |
| 100,000 | < 1 ms | 6 ms (1.5 ms) | 277 ms | 281 ms |
| 1,000,000 | < 1 ms | 7 ms (1.4 ms) | 1.8 s | 1.7 s |

Returning translated rows is cheap at any size. Searching or sorting *on* translated text reads it
for every candidate row, like any `LIKE '%…%'` on a column. Three ways to keep that in hand:

- narrow with an ordinary column where you can — the same 1M-row search within one category is
  about 95 ms;
- keep the base language as a real, indexable column ([base-language mode](base-language.md));
- use a search engine for full-text search over large catalogues.

## Not available elsewhere

Filtering and sorting on translated values in the database is EF Core only: it works by rewriting
the LINQ query, and there is no equivalent for a Dapper or MongoDB driver query. Those applications
filter on what they store, then localize what they read — see [Dapper](dapper.md) and
[MongoDB](mongodb.md).
