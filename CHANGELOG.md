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
- `[MultiLingual(EntityName = "...")]` and `[MultiLingualEntity("...")]` fix the name stored in
  `Translation.EntityName`, so a class can be renamed without orphaning its translations.
  Without either, the class name is used, as before. `Build()` now fails when two unrelated
  types would share a name (compared case-insensitively), instead of letting them read and
  overwrite each other's rows; a derived type inheriting its base's name is allowed.

- Retrying execution strategies (`EnableRetryOnFailure`) work with a plain `SaveChanges`. The
  interceptor's transaction used to make every save of a translatable entity throw under one;
  the save now runs inside the strategy — transaction, entity, translations and commit replayed
  together on a transient failure, with the change tracker restored between attempts.
- `ITranslationManager.CompleteWrites`, split out of `WriteAsync`, so a provider that commits
  after writing can record the values as stored only once the commit succeeds.

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
- A value assigned to a translatable property after loading could be overwritten by a later
  localized query in the same scope: the entity stayed queued for hydration, the query drained
  the queue, and the save that followed found nothing to write. Entities whose translatable
  values changed since materialization are now left out of hydration.
- A translation write that failed after the primary save left the change tracker claiming
  everything was saved — entities `Unchanged`, carrying keys the rollback had un-issued — so a
  retry wrote nothing, or wrote translations for rows that did not exist. On a relational
  provider the change tracker is now restored to its pre-save state, as EF does when its own save
  fails. The same restore keeps base-language values the caller assigned when the primary save
  itself fails.
- A failed commit after a successful translation write lost the translations on retry: the
  write had already cleared the staged values and recorded the authored ones as stored, so the
  retry found nothing to write. The EF Core interceptor now completes writes only after the
  commit succeeds.
- `MaterializedEntityRegistry` stopped recording entities past its 20,000 capacity without a
  word, so they were served with `null` translations. It now logs a warning when it fills up,
  and the next hydration throws rather than returning a partial set.
