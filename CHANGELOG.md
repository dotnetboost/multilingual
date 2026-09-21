# Changelog

All notable changes to this project are documented here.
This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

Initial library, extracted from the MultilingualApi prototype.

### Added
- **Base-language mode.** `AddMultiLingual(o => o.BaseLanguage = "en")` stores one language in
  each entity's own table and every other language in `Translations`, with a missing translation
  falling back to that column. Independent of the default language, which stays changeable at
  runtime without moving stored content. An entity may only be created in the base language
  (`BaseLanguageRequiredException`, HTTP 409), and the base language cannot be deleted or
  deactivated. `modelBuilder.ApplyMultiLingual(this)` makes the mapping follow the configuration;
  a mapping that disagrees with it fails on the first save rather than corrupting a column.
  A startup check confirms the base language exists in the store and is active — tolerating an
  unreadable store and an empty language list, which are bootstrapping rather than mistakes.
  Turn it off with `DisableStartupValidation()` and run `services.ValidateMultiLingualAsync()`
  when it suits. This adds a `Microsoft.Extensions.Hosting.Abstractions` reference to
  `DotNetBoost.MultiLingual.Core`, for `IHostedService` only.
- `ITranslationManager.MarkAsBaseLanguageValues`, so a provider can declare which properties still
  hold the base column's value rather than something a caller assigned. The EF Core interceptor
  uses it for every property an update did not touch: a write endpoint loads without hydrating,
  which in base-language mode leaves the base value in the property, and writing it back would
  quietly file English text as the Arabic translation.
- The sample application runs in base-language mode (`BaseLanguage = "en"`), so `Products.Name`
  and `Categories.Name` are real columns and `SampleApp.http` walks the rules that come with it.
- `DotNetBoost.MultiLingual.Core`: `[MultiLingual]` / `[NotMultiLingual]`, `ITranslationManager`
  (hydrate with default-language fallback, save, delete, stage), `ILanguageManager` with the
  language rules, `ILanguageCatalog` with a configurable expiry, `ILanguageContext`, and the
  `ITranslationStore` / `ILanguageStore` / `IEntityKeyAccessor` extension points.
- `DotNetBoost.MultiLingual.EntityFrameworkCore`: `ApplyMultiLingual()`, save and
  materialization interceptors, `ITranslationHydrator` and the `ToLocalizedListAsync` family.
- `DotNetBoost.MultiLingual.API`: `UseMultiLingual()` middleware (adds `Content-Language` and
  `Vary: Accept-Language`), `MapLanguageEndpoints()`, `MultiLingualExceptionHandler`, and the
  `AddAcceptLanguageParameter()` OpenAPI transformer (net10.0).
- Multi-targets `net8.0` and `net10.0`.

### Changed (relative to the prototype)
- Translations reference languages by **code** rather than a numeric id, and use the natural
  composite key instead of a surrogate `Id`. Language endpoints are addressed by code.
- Language codes are immutable once created.
- Setting only a translatable property on a tracked entity is saved without marking the entity
  `Modified` by hand; unchanged real translations are not rewritten.

### Fixed (relative to the prototype)
- A materialization interceptor instance per request made EF Core build a new internal service
  provider per request, failing with `ManyServiceProvidersCreatedWarning` after twenty requests.
- A default language could be created inactive.
