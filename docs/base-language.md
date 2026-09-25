# Base-language mode

By default every language — including the default one — lives in the translations table, and
translatable properties have no column at all. Set a **base language** and one language moves into
each entity's own table:

```csharp
builder.Services.AddMultiLingual(o => o.BaseLanguage = "en")
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
`Translations`.

| | Default mode | Base-language mode |
|---|---|---|
| Base language stored in | `Translations` | the entity's own column |
| Other languages stored in | `Translations` | `Translations` |
| Missing translation falls back to | default language, then `null` | default language, then the base column |
| Reading the base language costs | 2 queries | 1 query — nothing is fetched |
| `SELECT * FROM Products` | no names | names, in the base language |
| Filter/sort in SQL | automatic | automatic; `IgnoreTranslations()` to filter on the base column itself |
| Adding a translatable property | no migration | a migration, for the new column |

## Why it is not just "the default language"

The default language is a row in the language table and can be changed at runtime. The base
language is a property of the **schema** and is fixed for the life of the database. Keeping them
apart is the point: `POST /api/languages/{code}/set-default` stays a two-row flip, because no
stored content ever has to move. Changing `BaseLanguage` itself means migrating content between
columns and rows, so treat it as a schema change, not a setting.

## Three rules the mode enforces

1. **An entity may only be created in the base language.** The base value is the column every
   translation hangs off and the last fallback for every other language, so it has to exist first.
   Creating with any other `Accept-Language` is refused with `BaseLanguageRequiredException`
   (HTTP 409). Add other languages with a follow-up update, one language per request. Scopes with
   no language resolved — seeders, background jobs — are exempt.
2. **The base language cannot be deleted or deactivated**, even once it is no longer the default.
3. **The base language is never a translation row.** It has exactly one home, so the two cannot
   drift; `For(entity).Set(baseLanguage, …)` and `Clear(baseLanguage)` are refused with
   `BaseLanguageNotTranslatableException` (HTTP 409) for the same reason.

## Writing in a language other than the base one

Assign only what the caller actually supplied:

```csharp
if (request.Name is not null)
    product.Name = request.Name;
```

Two things make that the right shape here. Translatable properties are real columns, so assigning
`null` *erases* the stored base value instead of meaning "not supplied". And a property nobody
assigns keeps whatever the query read — the Arabic text, or English borrowed from the base column
when there is no Arabic. The engine recognises such a value as read, not authored, and never writes
it back: updating only a price under `Accept-Language: ar` can neither file the English name as the
Arabic one nor put the Arabic name into the English column. That holds in every scope, including a
background job reading in a default language other than the base one.

Everything else is unchanged: the value goes to `Translations`, and the base column is left out of
the `UPDATE` entirely.

Without a change tracker — Dapper, MongoDB — the engine cannot tell which properties the caller
touched, so declare the untouched ones yourself:

```csharp
multiLingual.For(product).MarkAsBaseLanguageValues(p => p.Description);   // only Name was edited
await multiLingual.For(product).SaveAsync();
```

See [Dapper](dapper.md#the-three-differences) and [MongoDB](mongodb.md#3-base-language-mode-puts-two-duties-on-your-code).

## The columns are ordinary columns

Configure them as you would any other:

```csharp
builder.Property(p => p.Name).HasMaxLength(256);
builder.Property(p => p.Description).HasMaxLength(2048);
```

In the default mode those same lines are accepted and then silently discarded, because
`ApplyMultiLingual()` removes the property from the model afterwards. Nothing warns you, so do not
read a configured length there as evidence that a column exists.

## The base language has to exist

The application checks at startup that the configured code names a language in the store and that
it is active, and refuses to start when it does not — a typo like `"en-US"` for `"en"` would
otherwise surface much later, as a failed write.

Two cases are deliberately tolerated with a warning instead: a store that cannot be read yet (an
un-migrated database) and an empty language list, because failing there would be a deadlock —
creating that first language is what `POST /api/languages` is for.

To run the check at a moment you choose — after your own migrations and seeding — turn the
automatic one off and call it yourself:

```csharp
builder.Services.AddMultiLingual(o => o.BaseLanguage = "en")
    .UseEntityFrameworkCore<AppDbContext>()
    .DisableStartupValidation()
    .Build();

// …
var app = builder.Build();
await app.Services.ValidateMultiLingualAsync();
```

## Adopting it on an existing database

This is the mode to use when the table already has the columns and the data: an existing
`Products.Name` full of English *is* the base language, so nothing has to be imported and no column
is dropped. Set `BaseLanguage` to whatever that content is already in, add `[MultiLingual]` to the
properties, and the only migration is the two new tables.

## If the mapping and the configuration disagree

`BaseLanguage` set but `ApplyMultiLingual()` called without the context, or the reverse — the first
save throws and says which call to fix. Silently guessing would write the authored language into
the base column, or drop the column in the next generated migration.

Two contexts of the same type configured with different `BaseLanguage` values in one process share
EF Core's cached model, so use one mode per context type.
