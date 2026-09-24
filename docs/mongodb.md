# MongoDB

`DotNetBoost.MultiLingual.MongoDb` stores translations in one collection and keeps translatable
properties out of your own documents. The library calls are the same ones an EF Core application
makes — the engine is provider-neutral — so this page is only about what differs.

`samples/SampleApp.MongoDb` is a runnable version of everything below: the EF Core sample's own
Products and Categories API, over this provider, serving the same routes and the same bodies.

## Setup

```csharp
// The driver, as it expects: one client for the process.
builder.Services.AddSingleton<IMongoClient>(new MongoClient(connectionString));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("shop"));

builder.Services
    .AddMultiLingual(o => o.BaseLanguage = "en")
    .UseMongoDb()                 // the only line that differs from the EF Core setup
    .Build();
```

`UseMongoDb()` also takes a connection string and database name, or a factory
(`Func<IServiceProvider, IMongoDatabase>`) for one database per tenant. Collection names and index
creation are configurable through `MongoDbOptions`.

What it does for you:

| | |
|---|---|
| Two collections: `translations` and `languages` | A unique compound index on `(EntityName, EntityId, PropertyName, LanguageCode)`, created when the host starts. Its leading fields are what every read filters on, so reads need no index of their own. `languages` uses the code as `_id`. |
| Unmaps `[MultiLingual]` properties from the class map | So they are never stored on your own documents. In base-language mode they stay mapped, because that field is where the base language lives. |
| Replaces `IEntityKeyAccessor` | Keys come from the driver's own mapping — `[BsonId]`, your class map, or its conventions. |

## The four differences

### 1. Localize after a query

A driver query fills a document's own fields. The translatable ones arrive `null` until you ask:

```csharp
var products = await collection.Find(filter).ToListAsync(ct);
await multiLingual.LocalizeAsync(products, ct);       // Name and Description filled in
```

`LocalizeAsync` batches its reads per entity type, so this is one translations query for the whole
list, not one per row. With EF Core the query interceptor does it and you never write this line.

### 2. Save translations after the document

The document is written first, because translations need a key to hang off:

```csharp
await collection.InsertOneAsync(product, cancellationToken: ct);   // its key now exists
await multiLingual.For(product).SaveAsync(ct);
```

Calling `SaveAsync` first is refused: an `ObjectId` the driver has not assigned yet is
`ObjectId.Empty`, which the provider reads as "no key", so you get an `InvalidOperationException`
instead of translations filed under `000000000000000000000000`.

Deletes are the mirror image — `For(product).DeleteAsync()` where EF Core's save interceptor would
have done it.

### 3. Base-language mode puts two duties on your code

Only when you set `BaseLanguage`. EF Core infers both from `IsModified`; without a change tracker
they are yours, and both are easy to miss.

**A create must be in the base language.** Every translation hangs off the base value, so EF Core
refuses a create in any other language. Nothing intercepts a driver insert, so check it yourself —
throw the same exception and the library's `MultiLingualExceptionHandler` still answers 409:

```csharp
if (!multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
{
    throw new BaseLanguageRequiredException(
        nameof(Product), multiLingual.Languages.BaseLanguage!, multiLingual.CurrentLanguage.Code);
}
```

**An update must not write non-base text into the base field, and must declare what it did not
touch.** Two rules, one method:

```csharp
await multiLingual.LocalizeAsync([product], ct);   // first, so the engine knows what the READ served

if (request.Name is not null)
    product.Name = request.Name;
else
    // Still holds what the read served. Say so, or the next save files that English as a
    // translation in the request's language — quietly turning English into Arabic.
    multiLingual.For(product).MarkAsBaseLanguageValues(p => p.Name);

// The field belongs in the DOCUMENT only in the base language. In any other language it belongs in
// a translation document, so leave it out of the $set and let SaveAsync file it.
if (multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code) && request.Name is not null)
    updates.Add(Builders<Product>.Update.Set(p => p.Name, request.Name));

await collection.UpdateOneAsync(p => p.Id == product.Id, Builders<Product>.Update.Combine(updates), cancellationToken: ct);
await multiLingual.For(product).SaveAsync(ct);
```

Without a base language none of this applies: translatable properties have no field at all, and
every language including the default is a translation document.

### 4. No filtering or sorting on translated text in the database

With EF Core, `Where(p => p.Name.Contains(term))` and `OrderBy(p => p.Name)` compare the
*translated* value, in the request's language, inside the database. That works by rewriting the LINQ
query and has no driver equivalent.

A MongoDB application filters on what it stores, localizes, then sorts in memory:

```csharp
var products = await collection.Find(filter).ToListAsync(ct);
await multiLingual.LocalizeAsync(products, ct);
products = products.Where(p => p.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) == true).ToList();
```

Fine for a page of results. For a large collection, write a `$lookup` pipeline against the
translations collection, or denormalise a per-language field — both are yours to write.

`PurgeOrphanedTranslationsAsync` is EF Core only for the same reason: it joins `Translations`
against each entity's own table. Collect the keys **before** deleting the documents and pass them to
`ITranslationWriter.DeleteEntitiesAsync` — afterwards nothing knows what they were.

## Transactions

To commit a document and its translations together, hand the store the session:

```csharp
using var session = await client.StartSessionAsync(ct);
session.StartTransaction();

await collection.InsertOneAsync(session, product, cancellationToken: ct);
await writer.WithStore(store.WithSession(session)).WriteAsync(writes, ct);

await session.CommitTransactionAsync(ct);
```

This needs a replica set or a sharded cluster. Against a standalone `mongod` there is nothing to
enlist in: each write stands alone, and so do the two halves of a default-language change — the
promotion lands first, so a reader may briefly see two defaults rather than none.

A one-node replica set is enough:

```bash
docker run -d --name multilingual-mongo -p 27017:27017 mongo:8.0.4 --replSet rs0
```

then initiate it once:

```bash
docker exec multilingual-mongo mongosh --quiet --eval 'rs.initiate()'
```

Connect with `mongodb://localhost:27017/multilingual?directConnection=true`.

## What stays exactly the same

Everything else. `[MultiLingual]` and `[MultiLingualEntity]`, class-level or property-level;
`For(entity).Set / Clear / GetAllAsync`; the whole `Languages` accessor; `Accept-Language`
resolution and `MapLanguageEndpoints()` from `DotNetBoost.MultiLingual.API`; the JSON localizer;
fallback to the default language; and every exception and its HTTP mapping.

## Running the sample

`samples/SampleApp.MongoDb` — see its [README](../samples/SampleApp.MongoDb/README.md). The Aspire
AppHost starts it beside the EF Core sample, so both providers run at once, and one line there points
`clients/dashboard` at either.
