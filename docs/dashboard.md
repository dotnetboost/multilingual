# Dashboard (SPA client)

[`clients/dashboard`](../clients/dashboard) is a [Nuxt 4](https://nuxt.com) single-page client for
the language endpoints and the sample's content, built on the
[Nuxt UI dashboard template](https://github.com/nuxt-ui-templates/dashboard).

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

The AppHost starts PostgreSQL, the API and the dashboard, and wires them together — the dashboard's
link is on the Aspire dashboard, the API reference at `http://localhost:5160/scalar`. See
[aspire.md](aspire.md).

Against an API you run yourself, start the dashboard on its own; it is then at
`http://localhost:3000`:

```bash
npm install --prefix clients/dashboard && npm run dev --prefix clients/dashboard
```

## Languages

A table over `MapLanguageEndpoints()`: add a language (its direction derived from the code unless
you pick one), rename it, switch it on or off, promote it to default, delete every translation in
it, or delete it. The dashboard does not second-guess the rules — `ILanguageManager` enforces them,
and a refusal comes back as problem details shown in a toast: deleting a language that still has
translations, for instance, is answered with how many there are and what to do instead. This page
works against any application that maps the endpoints.

## Content

One tab per `[MultiLingual]` entity of [`samples/SampleApp`](../samples/SampleApp) — Products
(property-level attribute) and Categories (class-level). The list is read with the language picked
in its toolbar as `Accept-Language`, so it is exactly what a client in that language receives,
fallbacks included; a badge per language shows which ones each item is actually translated into.

**New product** and **New category** create an item: its untranslated properties, and its
translatable fields in the base language — the only one the sample's API creates in, since every
other language hangs off the base value. The new item opens straight in the editor, ready for its
other languages. The bin at the end of a row deletes the item, and its translations with it in every
language; deleting a category keeps its products, with no category.

Selecting a row opens the item in every active language, one card each. A card saves on its own, as
`PUT api/products/{id}` with that language in `Accept-Language`, and sends only the fields you
changed. With the sample's base language, English is the `Products` row itself; every other
language is a row in `Translations`, and an empty field shows what reads in that language fall back
to. **Clear translation** removes one language's stored fields, so it falls back again.

These endpoints are the sample's, not the library's. `app/utils/content.ts` describes them, and an
application with the same shape can list its own entities there.

The browser never calls the API directly: Nitro proxies `/api/**` to `NUXT_MULTILINGUAL_API_URL`,
so the API needs no CORS configuration. See
[`clients/dashboard/README.md`](../clients/dashboard/README.md) for the full setup.
