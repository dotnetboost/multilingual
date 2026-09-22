# DotNetBoost.MultiLingual — Dashboard

A [Nuxt 4](https://nuxt.com) single-page client for the language endpoints that
`DotNetBoost.MultiLingual.API` maps, and for the translatable content of the sample app. A sidebar
links to **Languages**, where languages are added, edited, activated, promoted to default and
purged, and to **Content**, where a top menu holds one entry per `[MultiLingual]` entity — each one
lists its items in a language of your choice, creates and deletes them, and edits them in every
language side by side.

Built on the [Nuxt UI dashboard template](https://github.com/nuxt-ui-templates/dashboard)
(collapsible sidebar, command palette, light/dark mode, theme picker).

## Setup

```bash
npm install
```

## Development

The easiest way is the Aspire AppHost, which starts PostgreSQL, the API and this dashboard with the
variables below already set. From the repository root:

```bash
dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost
```

To run the dashboard on its own against an API that is already up, in `clients/dashboard`:

```bash
npm run dev
```

It is then at `http://localhost:3000`.

## Configuration

Copy `.env.example` to `.env` to point the dashboard at a different API.

| Variable | Default | Purpose |
|---|---|---|
| `NUXT_MULTILINGUAL_API_URL` | `http://localhost:5160` | Base address of the API |
| `NUXT_PUBLIC_API_REFERENCE_URL` | `http://localhost:5160/scalar` | Target of the sidebar's "API reference" link |
| `NUXT_PUBLIC_BASE_LANGUAGE` | `en` | The API's `MultiLingualOptions.BaseLanguage`; empty when it has none |

The browser never calls the API directly: `server/api/[...path].ts` proxies `/api/**` through
Nitro to `NUXT_MULTILINGUAL_API_URL`, `Accept-Language` included. That keeps the API free of CORS
configuration and makes the target a deploy-time setting rather than a build-time one.

The base language has to be configured because the API does not report it, and it changes what
"stored" means: `GET {id}/translations` leaves the base language out — it is the entity's own row —
so the editor reads it from the item itself, and never offers to clear it.

## How it maps to the API

| File | Role |
|---|---|
| `app/composables/useLanguages.ts` | The shared language list and every `api/languages` call |
| `app/pages/languages.vue` | The language table: add, edit, activate, set default, purge, delete |
| `app/utils/content.ts` | The content-type registry — `route` must match the application's controller |
| `app/composables/useContentItem.ts` | One item in every active language: load, dirty-tracking, save, clear |
| `app/components/content/Browser.vue` | Lists one content type in the chosen `Accept-Language` |
| `app/components/content/Editor.vue` | Edits one item, one card per language |
| `app/components/content/CreateModal.vue` | Creates an item: `POST api/{route}` in one language |
| `app/pages/content.vue` | Top menu, one entry per content type |
| `server/api/[...path].ts` | Proxy to the .NET API |

**Languages** are the library's own endpoints, so that page works against any application that
calls `MapLanguageEndpoints()`. The rules are the API's: when it refuses — deleting a language
that still has translations, deactivating the default — the toast shows its problem details as
they came back, in the request's language.

**Content** endpoints belong to the application, not the library. The registry describes the
sample's: `GET api/{route}` and `GET api/{route}/{id}` read in `Accept-Language`,
`PUT api/{route}/{id}` writes the supplied fields in it, and `GET api/{route}/{id}/translations`
returns every stored language, `POST api/{route}` creates an item and `DELETE api/{route}/{id}`
deletes one. An application with the same
shape can list its own entities there.

**New product** / **New category** asks for the untranslated properties the registry lists under
`create` — SKU, price and category; slug and display order — and the translatable fields in one
language, sent as `Accept-Language`. With a base language that language is fixed to it, because the
API only creates in the base language. The new item then opens in the editor, where the other
languages are added.

The bin at the end of each row deletes the item, after a confirmation. Its translations go with it
in every language — the save interceptor removes them in the same transaction — and the registry's
`deleteNote` says what else the endpoint does: deleting a category keeps its products, with no
category.

Each language saves on its own, with that language in `Accept-Language`, and sends only the fields
that changed — a field left out means "leave it alone", so other fields in that language, and every
other language, are untouched. An empty field that has nothing stored shows, as its placeholder,
what reads in that language fall back to. **Clear translation** calls
`DELETE api/{route}/{id}/translations/{code}` where the registry says the API has it (`clearable`).

## Adding a content type

1. Add an entry to `contentTypes` in `app/utils/content.ts`, with `route` matching the controller
   `fields` listing its `[MultiLingual]` properties, and `create` its untranslated ones.
2. Add a page under `app/pages/content/` that renders `<ContentBrowser :type="…" />`.

The sidebar and the top menu both build from the registry, so no other file needs to change.

## Production

```bash
npm run build
node .output/server/index.mjs
```

`npm run lint` and `npm run typecheck` cover the project.
