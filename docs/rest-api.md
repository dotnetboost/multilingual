# REST API

```bash
dotnet add package DotNetBoost.MultiLingual.API --prerelease
```

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();
builder.Services.AddOpenApi(o => o.AddAcceptLanguageParameter());   // optional, net10.0

var app = builder.Build();

app.UseExceptionHandler();
app.UseMultiLingual();                          // Accept-Language → the request's language
app.MapLanguageEndpoints().RequireAuthorization("admin");
app.MapTranslationEndpoints().RequireAuthorization("content-editor");
```

Four separate pieces, each usable on its own: the middleware, the language endpoints, the
translation endpoints, and the problem details mapping.

## `Accept-Language`

`app.UseMultiLingual()` resolves every request's language from the header — the best
quality-weighted match among **active** languages, exact (`en-US`) first, then the bare language
part (`en`), then the system default. `*` never selects a language, since "anything" is what the
default already is, but it does make falling back to the default count as honoured.

The result is what `IMultiLingualManager.CurrentLanguage`, the EF Core interceptors and the
localizer all read. Two response headers say what happened:

```
Content-Language: ar
Vary: Accept-Language
```

`Vary` is there so caches keep one copy per language. `WasRequestedLanguageHonoured` is false when
the caller named a language that does not exist or is inactive.

Place the middleware before the endpoints. With no languages configured yet it leaves the request
unresolved, so the language endpoints still work while you create the first one.

It also sets `CultureInfo.CurrentUICulture` for the rest of the pipeline, which is what framework
components that cannot hold a scope read:

```csharp
app.UseMultiLingual(o =>
{
    o.SetCurrentUICulture = false;   // default: true
    o.SetCurrentCulture   = true;    // default: false — it changes how numbers and dates parse
});
```

See [localized messages](localization.md#which-language) for when that matters.

## The language endpoints

`app.MapLanguageEndpoints()` maps language administration under `/api/languages` (pass another
prefix if you like) and returns the route group:

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/languages` | Every language, default first |
| `GET` | `/api/languages/{code}` | One language (`404` if unknown) |
| `POST` | `/api/languages` | Create. The first one becomes the default |
| `PUT` | `/api/languages/{code}` | Replace name, direction and active flag |
| `DELETE` | `/api/languages/{code}` | Delete (refused for the default, or while it still has content) |
| `POST` | `/api/languages/{code}/set-default` | Promote to default, demoting the previous one atomically |
| `DELETE` | `/api/languages/{code}/translations` | Purge every translation in that language |

```bash
curl -X POST http://localhost:5160/api/languages \
  -H "Content-Type: application/json" \
  -d '{"code":"ar","name":"العربية","direction":"rtl"}'
```

`direction` is `ltr` or `rtl`; omit it and it is derived from the code. The rules these endpoints
enforce are on the [Languages](languages.md) page.

### Securing the endpoints

**The generated endpoints are anonymous**, as every `Map…` in ASP.NET Core is. A library cannot
know which authorization policies an application has declared: requiring one here would either
invent a policy name every consumer has to define, or fail at startup for any application that has
not configured authentication. Protecting them is the application's decision — and the route group
is returned so that it takes one line:

```csharp
app.MapLanguageEndpoints().RequireAuthorization("admin");
```

> **Do this before you deploy.** These endpoints change what *every* client of the application
> sees — deactivating a language, or moving the default, changes the content everyone is served —
> and `DELETE /api/languages/{code}/translations` deletes content outright, with no undo.

The [`samples/`](../samples) apps map them without `[Authorize]` on purpose: they are showcases and
run without an identity provider. Do not copy that part into a real application.

## The translation endpoints

`app.MapTranslationEndpoints()` maps reads and writes of translation rows under `/api/translations`
(pass another prefix if you like) and returns the route group. They are the CRUD every consumer
would otherwise hand-write, built on `IMultiLingualManager.For(entityName, entityId)`:

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/translations/{entityName}/{entityId}` | Every stored language |
| `GET` | `/api/translations/{entityName}/{entityId}/resolved` | The request's language, with the default-language fallback applied |
| `PUT` | `/api/translations/{entityName}/{entityId}` | Upsert every supplied language, as one batch |
| `POST` | `/api/translations/{entityName}/resolve` | Many ids at once, in the request's language — one query |
| `DELETE` | `/api/translations/{entityName}/{entityId}` | Every language of that subject |

### Nothing is exposed until you say so

An open `{entityName}` route would be a write-anything-anywhere surface: any caller could write
translation rows for any entity in the system, including ones you never meant to expose. So the
names are listed at registration, and **nothing is served until they are**:

```csharp
builder.Services
    .AddMultiLingual()
    .UseEntityFrameworkCore<AppDbContext>()
    .ExposeOverHttp(e => e
        .Allow("MediaFile")        // by name, for a type your application does not own
        .Allow<Product>()          // by type; the name is resolved through [MultiLingualEntity]
        .Allow<Category>())
    .Build();
```

An unlisted name answers `404` on every verb — not `403`, which would confirm which entity names
exist. With nothing allowed at all, `MapTranslationEndpoints()` maps nothing and logs a warning at
startup, rather than serving a surface on which every request 404s. Calling `ExposeOverHttp` more
than once adds to the same list.

### Writing

`PUT` takes every language in one body and writes them in a single store operation, so a three-
language edit is one upsert rather than three:

```bash
curl -X PUT http://localhost:5160/api/translations/MediaFile/8b1f…/ \
  -H "Content-Type: application/json" \
  -d '{"translations":{
        "en":{"AltText":"A red chair","Caption":"Our bestseller"},
        "ar":{"AltText":"كرسي أحمر"}
      }}'
```

A `null` value removes that stored row; a language or property the body omits is left alone. An
unknown language code is a `404` problem (`LanguageNotFound`), reported at the request, not after
half the batch has been written.

### Reading a page at once

`POST .../resolve` is the one to use for a listing: it resolves every id in one query, with the
same current-language-then-default-language fallback the EF Core interceptors apply.

```bash
curl -X POST http://localhost:5160/api/translations/MediaFile/resolve \
  -H "Content-Type: application/json" -H "Accept-Language: ar" \
  -d '{"entityIds":["8b1f…","9c2a…"]}'
```

Ids with nothing stored are simply absent from the response.

### Securing them

As with the language endpoints, they are **anonymous** until you say otherwise — and these *write
content*:

```csharp
app.MapTranslationEndpoints().RequireAuthorization("content-editor");
```

> The exposure list decides which entities are reachable. It is not an authorization check: it
> cannot tell one caller from another. Compose a policy on the group as well.

Subjects addressed this way have no columns of their own, so in
[base-language mode](base-language.md) every language they carry — the base language included — is
a translation row. Do not address one of your own `[MultiLingual]` entities this way in that mode:
its base-language values belong in its own columns, and a row written here for that language would
be invisible to every query. Expose such entities by type (`Allow<Product>()`) and write their base
language through the entity itself.

## Problem details

`MultiLingualExceptionHandler` turns the library's exceptions into `ProblemDetails`, so a
controller needs no checks of its own — `For(product).Clear(languageCode)` with a bad code is a 404
response without an `if` in sight.

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();
// …your own handlers after it
app.UseExceptionHandler();
```

| Exception | Status |
|---|---|
| `LanguageNotFoundException` | 404 |
| `UnknownLanguageException` | 400 |
| `InvalidLanguageException` | 400 |
| `LanguageConflictException` | 409 |
| `NoLanguageConfiguredException` | 409 |
| `BaseLanguageRequiredException` | 409 |
| `BaseLanguageNotTranslatableException` | 409 |

Titles and details are looked up in the JSON message files when
[the localizer](localization.md#the-librarys-own-problem-details) is configured, so the error text
arrives in the caller's language too.

## OpenAPI

The language is resolved by middleware rather than bound to a handler parameter, so nothing
advertises it — which would leave Scalar or Swagger with no way to exercise it. One line puts it
back in the document, with the live list of active codes as the enum, so the UI offers a dropdown
of exactly the languages this instance serves:

```csharp
builder.Services.AddOpenApi(o => o.AddAcceptLanguageParameter());
```

Mark an operation (or a whole controller) `[LanguageAgnostic]` to leave it off. The transformer
needs the built-in OpenAPI support from ASP.NET Core 9, so it ships for `net10.0` only; everything
else in the package targets `net8.0` as well.
