# Declaring translatable content

One attribute makes a property translatable. There is no extra table to design, no repository code
and no service code — the value simply stops living on the entity's own row and starts living in
the shared `Translations` table.

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

## The attribute, at two levels

| Where | What becomes translatable |
|---|---|
| **On a property** | That property only. It must be a `string`; anything else fails when the type is first inspected. |
| **On the class** | Every public `string` property with a getter *and* a setter, except conventional keys (`Id`, `{TypeName}Id`) and anything marked `[NotMultiLingual]`. |

```csharp
[MultiLingual]                                    // class level: Name and Description, not Slug
public class Category
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }

    [NotMultiLingual] public string Slug { get; set; } = "";
}
```

Non-string members — numbers, booleans, dates, navigations, collections — are never translated,
even under a class-level declaration.

**If both levels appear on one type, the class-level declaration wins** and every eligible string
property is translatable. The property-level annotations are then redundant: they neither narrow
the set nor raise an error. This is deliberate — class level is the broader, more explicit intent,
and narrowing to the annotated subset would quietly make the class-level attribute a no-op.

`[MultiLingual]` on a non-string, `init`-only or privately-set property is a mistake, and
`Build()` says so at startup rather than letting it surface on a request.

## Entity names are part of your database

Translations are filed under `Translation.EntityName`, which is the class name unless you fix it.
Renaming the class would therefore orphan every row stored under the old name. Fix the name and the
class can be renamed freely:

```csharp
[MultiLingual(EntityName = "product")]     // class-level declarations
public class CatalogItem { … }

[MultiLingualEntity("article")]            // property-level declarations
public class BlogPost { [MultiLingual] public string? Title { get; set; } … }
```

To rename a class that has no fixed name yet, either fix it to the *old* name
(`EntityName = "Product"`) or move the rows:

```sql
UPDATE Translations SET EntityName = 'CatalogItem' WHERE EntityName = 'Product';
```

Two unrelated types may not share a name — compared case-insensitively, because a case-insensitive
database collation would mix their rows. `Build()` refuses `Sales.Item` and `Library.Item`, or
`"Shared"` and `"shared"`, and lists the offenders. It can only see public types, so
`ApplyMultiLingual()` runs the same check over every entity in the EF Core model, internal ones
included, when the model is first built.

A derived type inherits its base's name and shares its rows, which is what EF Core's TPH, TPT and
TPC mappings all want. Put `[MultiLingualEntity]` on the base of a hierarchy, since a query over the
base type looks rows up by its name.

## What a key has to look like

Two requirements come from the translations table itself, not from configuration, so check them
before building on this:

- **A single-column key.** `Translation.EntityId` holds one value, so composite keys are not
  supported — give the entity a surrogate key first.
- **A key of at most 64 characters** written as text (`EntityKey.MaxLength`). Every numeric key and
  a `Guid` fit comfortably; a longer `string` key is refused when its translations are written.

Keys of any single-column type (`int`, `long`, `Guid`, `string`) are stored as invariant strings.
How a key is found is the provider's business: EF Core asks the model, MongoDB asks the driver's
`[BsonId]` / class map, and everything else falls back to the convention `[Key]`, then `Id`, then
`{TypeName}Id`. Replace it with `UseEntityKeyAccessor<T>()` if your entities do something else.

## Where the values live

One `Translations` table serves every entity, keyed naturally on
`(EntityName, EntityId, PropertyName, LanguageCode)`. There is no surrogate key: the leading
columns are exactly what every read filters on, so the primary key index is also the read-path
index.

By default every language — the default one included — is a row there, and a translatable property
has no column at all. Set a base language and one language moves into the entity's own column
instead; see [base-language mode](base-language.md), which is what makes this adoptable on a
database that already has `Name` and `Description` full of data.

## Validation

`Build()` checks every public decorated type in the loaded assemblies and fails the start with a
list of everything wrong. The EF Core model check (`ApplyMultiLingual()`) repeats it over the
model, which is the only place internal entity types are visible. A mistake in a declaration is
therefore a failed boot, never a surprise in stored data.
