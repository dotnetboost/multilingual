# Languages

A language is a code, a display name, a text direction and two flags. `multiLingual.Languages` —
and [the endpoints over it](rest-api.md) — enforce the rules that keep the set consistent:

- codes are validated and normalised (`pt-br` → `pt-BR`), and a code is immutable once created;
- the first language created becomes the default;
- there is always exactly one **active** default;
- a language that still has translations cannot be deleted — purge it first, or deactivate it;
- a configured [base language](base-language.md) can be neither deleted nor deactivated, even once
  it is no longer the default.

```csharp
await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "ar", Name = "العربية", Direction = TextDirection.Rtl });
await multiLingual.Languages.SetDefaultAsync("ar");
await multiLingual.Languages.PurgeTranslationsAsync("fr");
await multiLingual.Languages.DeleteAsync("fr");
```

## Reading

| Call | Reads from | Use it for |
|---|---|---|
| `GetAllAsync()`, `GetAsync(code)` | the store, every time | administration screens, where staleness would be confusing |
| `FindAsync(code)`, `FindActiveAsync(code)`, `GetDefaultAsync()` | the cached list | everyday code, on every request |
| `BaseLanguage`, `IsBaseLanguage(code)` | configuration | deciding whether a value is a translation or the base value |

`FindAsync` returns `null` for an unknown code rather than throwing, so a caller can decide what an
unrecognised code means. `GetAsync` throws `LanguageNotFoundException`.

## The cache

Each instance caches the language list for 5 minutes (`WithLanguageCacheDuration()`). A write
through `multiLingual.Languages` refreshes the instance that made it at once; if you change the
languages some other way, call `InvalidateCache()`.

**Several instances** only see each other's changes when their own cache expires. To have every
instance pick a change up within seconds, share a change signal through a distributed cache you
already run:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddMultiLingual()
    .UseEntityFrameworkCore<AppDbContext>()
    .SynchronizeLanguagesThroughDistributedCache()   // checks every 5 seconds; pass another interval if you like
    .Build();
```

Each instance then reads one small cache entry per interval, and reloads the list only when it
changed. If the cache is unreachable, instances keep serving their cached list and log a warning,
and the cache expiry still applies. For another backend, implement `ILanguageChangeSignal` and
register it with `UseLanguageChangeSignal<T>()`.

## The request's language

In ASP.NET Core, `app.UseMultiLingual()` resolves it from `Accept-Language` — see
[the REST API](rest-api.md#accept-language). Anywhere else, set it yourself:

```csharp
await multiLingual.UseLanguageAsync("ar");   // returns false if "ar" is unknown or inactive
```

`CurrentLanguage` and `DefaultLanguage` throw `NoLanguageConfiguredException` while no language has
been resolved; `IsLanguageResolved` says so without throwing.
`WasRequestedLanguageHonoured` is false when the caller asked for a language that does not exist or
is inactive, which is how an API can tell a client it is looking at a fallback.

## Bootstrapping

With no languages at all, the middleware leaves each request unresolved so that the language
endpoints still work; anything that needs a language answers 409. Creating the first language is
what `POST /api/languages` is for — and it becomes the default automatically.

## Errors

`MultiLingualExceptionHandler` answers the library's exceptions with problem details. One
distinction is worth knowing:

| Exception | Status | When |
|---|---|---|
| `LanguageNotFoundException` | 404 | the language the URL addresses does not exist (`GET /api/languages/xx`) |
| `UnknownLanguageException` | 400 | a request merely *uses* a language that does not exist — a `languageCode` passed to a query helper, say — since what it was about does exist |
| `InvalidLanguageException` | 400 | a malformed code, name or direction |
| `LanguageConflictException` | 409 | a rule above was broken |
| `NoLanguageConfiguredException` | 409 | no language has been resolved, or none exists |

The titles and details are translatable; see [localized messages](localization.md#the-librarys-own-problem-details).
