# SampleApp.MongoDb

The same library as `samples/SampleApp`, over the MongoDB provider instead of EF Core. One entity
(`Dish`) and seven endpoints, so the parts that differ stay in view — see
[docs/mongodb.md](../../docs/mongodb.md) for what those are.

English is the base language, as in the EF Core sample, so a dish's document holds the English text
and every other language is a document in the `translations` collection.

## Run it

Through the Aspire AppHost, which starts MongoDB, this API and the EF Core sample together:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

Or on its own, against any MongoDB:

```bash
docker run -d --name multilingual-mongo -p 27017:27017 mongo:8.0.4
```

```bash
ConnectionStrings__dishesdb="mongodb://localhost:27017/dishes" dotnet run --project samples/SampleApp.MongoDb
```

Three languages (en, ar, fr) and three dishes are seeded on first start, with translations
deliberately missing so the fallback shows immediately.

## Try it

```bash
curl -s localhost:5170/api/dishes -H 'Accept-Language: ar'
```

Arabic where it exists, English where it does not — Tabbouleh has no translations at all, and
Falafel has an Arabic name but no Arabic description.

```bash
curl -s -X POST localhost:5170/api/dishes -H 'Content-Type: application/json' -H 'Accept-Language: ar' -d '{"price":9.9,"name":"كنافة"}'
```

409, `Base language required`: in base-language mode a dish can only be created in English, because
every translation hangs off the base value. The EF Core provider's save interceptor enforces this;
here the endpoint does, and throws the same exception.

Create it in English, then add Arabic with a `PUT`:

```bash
curl -s -X PUT localhost:5170/api/dishes/{id} -H 'Content-Type: application/json' -H 'Accept-Language: ar' -d '{"name":"كنافة"}'
```

Then look at what that actually wrote:

```bash
curl -s localhost:5170/api/dishes/{id}/translations
```

Only `ar.Name`. The description was **not** filed as an Arabic translation even though the entity
held English text in it — that is `MarkAsBaseLanguageValues` doing its job in the `PUT`. And the
document itself still holds the English name: `db.dishes.findOne()` in Mongo Express or `mongosh`
shows it untouched. Those two things are the whole point of the base-language rules in
`Program.cs`.

## Endpoints

| | |
|---|---|
| `GET /api/dishes?search=` | List, localized. `search` runs in memory — see difference 4 in the docs. |
| `GET /api/dishes/{id}` | One dish, localized. |
| `GET /api/dishes/{id}/translations` | Every stored language for one dish. |
| `POST /api/dishes` | Create. Base language only. |
| `PUT /api/dishes/{id}` | Update in the request's language. A null field means "leave it alone". |
| `DELETE /api/dishes/{id}/translations/{code}` | Remove one language. English is refused with a 409. |
| `DELETE /api/dishes/{id}` | Delete the dish and its translations. |
| `GET /api/languages`, … | Language administration, from `MapLanguageEndpoints()`. |
| `GET /api/current-language` | Which language the request resolved to. |
