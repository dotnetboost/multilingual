# Localized messages

Translated content is data; the messages an API returns ("Product 42 was not found.") are part of
the code. `DotNetBoost.MultiLingual.Localization` serves those through the standard
`IStringLocalizer<T>`, from one JSON file per language, and answers in the same language as the
content with the same fallback.

```bash
dotnet add package DotNetBoost.MultiLingual.Localization --prerelease
```

```csharp
builder.Services.AddMultiLingual()
    .UseEntityFrameworkCore<AppDbContext>()
    .AddJsonLocalization()            // Resources/en.json, Resources/ar.json, …
    .Build();
```

```jsonc
// Resources/en.json — comments and trailing commas are allowed
{
  "Products": {
    "NotFound": "Product {0} was not found.",
    "SkuExists": "A product with SKU '{0}' already exists."
  }
}
```

```csharp
public sealed class ProductsController(IStringLocalizer<SharedResource> localizer) : ControllerBase
{
    // …
    return NotFound(localizer["Products.NotFound", id].Value);
}
```

## The rules

- **Files** are named after the language code (`pt-BR.json`, matched case-insensitively) and all
  localizers share them; the `T` in `IStringLocalizer<T>` only names the consumer. Nested objects
  join into dotted keys, so `Products.NotFound` above. Arguments fill `{0}`, `{1}`, … as in
  `string.Format`.
- **Fallback** follows the content: the requested language, its parent (`ar-EG` → `ar`), the
  default language (unless `DisableDefaultLanguageFallback()`), then the base language. A key found
  nowhere comes back as itself with `ResourceNotFound = true`, like every `IStringLocalizer`. A
  language with no file at all simply reads the default's messages.
- **Where they are read from:** `ResourcesPath` (default `Resources`) under the content root. Web
  projects publish `*.json` automatically; elsewhere add
  `<None Update="Resources/*.json" CopyToOutputDirectory="PreserveNewest" />`, or pass an
  `EmbeddedFileProvider` as `FileProvider` to ship them inside an assembly.
- **Checked and reloaded:** every file is parsed at startup, so a malformed one fails the start
  with the file and key in the message (`ValidateFilesAtStartup`). An edited file is picked up
  without a restart (`ReloadOnChange`).
- `AddJsonLocalization()` replaces the `.resx` localizer from `AddLocalization()`, whichever is
  called first.

```csharp
.AddJsonLocalization(o =>
{
    o.ResourcesPath          = "Messages";
    o.FileProvider           = new EmbeddedFileProvider(typeof(Program).Assembly);
    o.ReloadOnChange         = false;
    o.ValidateFilesAtStartup = true;
})
```

## Which language

The `IStringLocalizer<T>` you inject reads the scope's current language
(`IMultiLingualManager.CurrentLanguage`), exactly like the EF Core interceptors. That matters in two
places where .NET's ambient `CultureInfo.CurrentUICulture` would be wrong.

**Exception handlers** run outside `UseMultiLingual()`, after the culture it set is gone. They are
singletons, so take the localizer from the request rather than the constructor — the root provider
has no language, and with scope validation on (the Development default) resolving it there throws:

```csharp
var localizer = httpContext.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
```

**Background jobs** set a language with `UseLanguageAsync`, and localizers resolved from that scope
follow it:

```csharp
await using var scope = services.CreateAsyncScope();
await scope.ServiceProvider.GetRequiredService<IMultiLingualManager>().UseLanguageAsync("ar");
var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();
```

Localizers the framework creates itself through `IStringLocalizerFactory` — DataAnnotations
validation messages, for instance — are cached for the application's lifetime and cannot hold a
scope, so they read `CurrentUICulture`. `UseMultiLingual()` sets it to the resolved language for
the rest of the pipeline; `app.UseMultiLingual(o => o.SetCurrentUICulture = false)` turns that off,
and `o.SetCurrentCulture = true` sets the formatting culture as well (off by default: it changes
how numbers and dates are parsed). Anywhere else, `multiLingual.ApplyUiCulture()` sets it for the
calling method — it is synchronous so that the culture survives it returning.

## The library's own problem details

They are looked up in the same files, under `MultiLingual.Problems.{Kind}.Title` and `.Detail`,
where `Kind` is the exception name without "Exception":

`UnknownLanguage`, `LanguageNotFound`, `InvalidLanguage`, `LanguageConflict`,
`NoLanguageConfigured`, `BaseLanguageRequired`, `BaseLanguageNotTranslatable`.

Anything left out keeps its English text. Details take the exception's data as arguments — see
`MultiLingualProblems.ToProblemDetails`. `InvalidLanguage` and `LanguageConflict` each cover many
rules, so only their titles are translatable.
[`samples/SampleApp/Resources/ar.json`](../samples/SampleApp/Resources/ar.json) translates them all.
