# SampleApp.MongoDb

The EF Core sample (`samples/SampleApp`) over the MongoDB provider. Same `Product` and `Category`,
same routes, same request and response bodies — the DTOs and the error handling are *the same files*,
compiled into both projects — so `clients/dashboard` can be pointed at either API without a change.

English is the base language, as in the EF Core sample: a document holds the English text in its own
fields and every other language is a document in the `translations` collection.

## What differs, and where to look

| | |
|---|---|
| Localize after a query | Every read in both controllers: `await multiLingual.LocalizeAsync(found, ct)`. EF Core's query interceptor does it for you. |
| Save translations after the document | `ProductsController.Create` — the insert first, so the key exists, then `For(product).SaveAsync()`. |
| The base-language rules | `ProductsController.Create` (create in the base language only) and `ProductsController.Update` (the two marked comments). EF Core infers both from `IsModified`. |
| No translated filter or sort in the database | `ProductsController.GetAll` — `search` and `sort` run in memory. |
| No orphan purge | `ProductsController.DeleteByCategory` — keys are collected before the delete. |

[docs/mongodb.md](../../docs/mongodb.md) explains all five in one page.

Two smaller differences, neither about this library: there are no navigations on the documents (a
mapped one would serialize the whole category into every product), and `int` keys come from
`Data/IdSequence` because MongoDB does not generate them — kept as `int` so the routes, the DTOs and
the dashboard are unchanged.

## Run it

Through the Aspire AppHost, which starts MongoDB, this API, and the EF Core sample together:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

Or on its own, against any MongoDB:

```bash
docker run -d --name multilingual-mongo -p 27017:27017 mongo:8.0.4
```

```bash
ConnectionStrings__multilingualmongodb="mongodb://localhost:27017/multilingual" dotnet run --project samples/SampleApp.MongoDb
```

It listens on <http://localhost:5170> and opens Scalar at `/scalar`. Three languages (en, ar, fr),
two categories and three products are seeded on first start, with translations deliberately missing
so the fallback shows immediately.

## Try it

```bash
curl -s localhost:5170/api/products -H 'Accept-Language: ar'
```

Arabic where it exists, English where it does not — `NOTE-A5` has no translations at all, and
`MOUSE-01` has no French.

```bash
curl -s -X POST localhost:5170/api/products -H 'Content-Type: application/json' -H 'Accept-Language: ar' \
  -d '{"sku":"KNAFEH-1","price":9.9,"name":"كنافة"}'
```

409, `Base language required`: in base-language mode a product can only be created in English,
because every translation hangs off the base value. The EF Core provider's save interceptor enforces
this; here `ProductsController.Create` does, and throws the same exception.

Create it in English, then add Arabic with a `PUT` that supplies only the name:

```bash
curl -s -X PUT localhost:5170/api/products/4 -H 'Content-Type: application/json' -H 'Accept-Language: ar' \
  -d '{"name":"كنافة"}'
```

Then look at what that actually wrote:

```bash
curl -s localhost:5170/api/products/4/translations
```

Only `ar.Name`. The description was **not** filed as an Arabic translation, even though the entity
held English text in it — that is `MarkAsBaseLanguageValues` in the `PUT`. And the document itself
still holds the English name, which `db.products.findOne()` in Mongo Express or `mongosh` confirms.
Those two facts are the whole point of the base-language rules in that method.

## With the dashboard

In `aspire/DotNetBoost.MultiLingual.AppHost/AppHost.cs`, change the dashboard's
`NUXT_MULTILINGUAL_API_URL` from `api` to `mongoApi`. Nothing in `clients/dashboard` changes: it sees
the same routes and the same bodies.

## Endpoints

Identical to the EF Core sample's.

| | |
|---|---|
| `GET /api/products?categoryId=&search=&sort=` | List, localized. `search` and `sort` run in memory. |
| `GET /api/products/summaries` | Each product with its category's name, both localized. |
| `GET /api/products/{id}` | One product, localized. |
| `GET /api/products/{id}/translations` | Every stored language for one product. |
| `POST /api/products` | Create. Base language only. |
| `PUT /api/products/{id}` | Update in the request's language. A null field means "leave it alone". |
| `DELETE /api/products/{id}/translations/{code}` | Remove one language. English is refused with a 409. |
| `DELETE /api/products?categoryId=` | Bulk delete, and the translations it leaves behind. |
| `DELETE /api/products/{id}` | Delete a product and its translations. |
| `GET/POST/PUT/DELETE /api/categories…` | The same set, minus the bulk delete and the summaries. |
| `GET /api/languages`, … | Language administration, from `MapLanguageEndpoints()`. |
| `GET /api/current-language` | Which language the request resolved to. |
