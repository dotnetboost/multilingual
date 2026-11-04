# Composition gaps — DotNetBoost.MultiLingual

Working notes, not user documentation. They came from composing this library with
DotNetBoost.Auditing, Settings and Media in one `DbContext` in
[dotnetboost-sample](https://github.com/dotnetboost/dotnetboost-sample). Each gap lists what goes
wrong, why, and the steps to fix it here. Delete an entry once it ships.

---

## 1. Another interceptor's save inside ours wiped the outer save's state — fixed, unreleased

**Fixed** in `MultiLingualSaveChangesInterceptor` (see `CHANGELOG.md`, *Unreleased*). Delete this
entry once that ships.

**What went wrong.** `SaveState` is kept per context, and `Begin` restarted it for any save but
this library's own nested one. An interceptor registered *before* `UseMultiLingual` runs its
`SavedChanges` first, so a `SaveChanges` it issued there (Auditing's deferred pass does) dropped the
outer save's pending deletes and snapshot, and committed its transaction from the inside.
`SaveCompositionTests` confirmed both a lost delete and a failure that rolled nothing back.

**What shipped.**

- Saves are counted per context (`SaveState.Depth`). A save nested in an outer one adds its work
  to the outer state without resetting it, skipping entries already pending, and only the
  outermost completion writes, commits or rolls back.
- A nested save's failure is left to whoever issued it. The notes proposed marking the outer save
  failed instead; that was dropped, because when the outer save had no transaction of its own
  its changes are already committed, and failing it would report a loss that did not happen.
- **What EF does and does not report**, checked on EF 8 and 10. Auditing's depth tracking (its
  gap 2) needs the same care:

  | Event | Interceptor callback |
  |---|---|
  | An interceptor's `SavingChanges` throws | **none** |
  | A concurrency conflict | `ThrowingConcurrencyException` only |
  | A cancelled save | `SaveChangesCanceled` only |
  | An interceptor's `SavedChanges` throws | `SaveChangesFailed` |

  The last three are now handled, which also fixes a leak older than this work: a concurrency
  conflict or a cancellation left the owned transaction open. For the first, a save counts as
  nested only while the outer save's changed entries have all been accepted, i.e. EF has already
  saved them. A save EF never finished does not satisfy that, so the next save starts over
  instead of waiting forever for a completion that will not come.

**Known limits.**

- An outer save that changed only unmapped translatable properties gives the check nothing to go
  on, so a save nested inside it starts over as before. Interceptors like Auditing do no
  follow-up save after a save with no tracked changes, so this should not arise.
- With `SaveChanges(acceptAllChangesOnSuccess: false)` the outer entries are never accepted, so a
  nested save also starts over, as before.
- If an interceptor *after* this one throws from `SavingChanges` during a **nested** save, that
  save is never reported finished, and the outer save's completion or failure is counted as the
  nested one. The outer translations are then not written, and an owned transaction is neither
  committed nor rolled back; it ends when the context is disposed, which rolls it back. It takes
  three interceptors and a refusal at that exact point.

**Not reproduced: the empty product name.** In the sample, an Auditing follow-up save was seen to
write an empty product name over the stored one. `SaveCompositionTests` runs the base-language
flows in all four arrangements, both an update under `ar` and the sample's own
`TranslatedText.SaveAsync` sequence, and the stored column and translation come out right every time.
So the cause is most likely outside this library: Auditing's own state handling (that repo's
`COMPOSITION-GAPS.md`, gap 2) or the sample's `TranslatedText.Assign`, which turns a `null` base
value into `""`. The sample uses HiLo keys today, so nothing is deferred there now; the observation
dates from an identity-key setup.

## 2. The owned transaction is committed before later interceptors run — partly addressed

**What goes wrong.** When the caller has no transaction, `SavingChangesAsync` opens one and
`SavedChangesAsync` commits it (`CommitOwnedTransactionAsync`). EF runs interceptors in the order
they were added. Any interceptor registered after this one therefore does its post-save work
**after** the commit. Auditing's deferred Created entries are one example (the sample registers
`UseMultiLingual(sp).UseAuditing(sp)`). Nothing reports it.

The retrying path only half avoids this. `SaveUnderStrategyAsync`
(`MultiLingualSaveChangesInterceptor.RetryingStrategy.cs`) suppresses the caller's save, runs it
again inside its own transaction, and commits once that inner `SaveChangesAsync` returns. An
interceptor registered after this one does its work inside that inner save, so inside the
transaction. One registered **before** it, and that waits for the outermost save to complete
(as Auditing's depth tracking in its gap 2 would), works in the outer save's `SavedChanges`,
after the strategy's commit.

Two more things `SaveCompositionTests` showed:

- This library opens a transaction only when the save has translation work. A save that changes
  only a price opens none, so another interceptor cannot rely on this one for its transaction.
  The retrying path is the exception: it opens one whenever a translatable entity is tracked.
- The only arrangement that stayed atomic in both orders is a transaction the **caller** opened
  (`CallerTransaction_KeepsEverythingAtomic`).

**Confirmed by `SaveCompositionTests`:** `Create_RunsTheFollowUpInsideTheTransaction` and
`FailureAfterTheFollowUp_RollsBackEverything` fail when the follow-up interceptor is registered
after (no strategy) and when it is registered before under a retrying strategy.

**Shipped so far** (unreleased, see `CHANGELOG.md`):

- **Option B:** `.OwnTransactions(false)`. The interceptor then never opens a transaction, and a
  save that writes translations with neither `Database.CurrentTransaction` nor
  `Transaction.Current` is refused in `SavingChanges`, before anything reaches the database. It
  also turns off the retrying path, which opens a transaction too.
- **The docs:** *Sharing a DbContext with other SaveChanges interceptors* in `docs/limitations.md`,
  linked from `docs/architecture.md` and the configuration reference. What each hook does, and
  what to do per setup, each line backed by a `SaveCompositionTests` case:
  - no retrying strategy: the application opens the transaction around every `SaveChanges`, which
    keeps every interceptor inside it in either order
    (`TheDocumentedAppTransaction_*`, `CallerTransaction_KeepsEverythingAtomic`);
  - a retrying strategy: keep `OwnTransactions` on and register `UseMultiLingual` first (the
    `After, true` rows).

**Corrected: the retrying-strategy variant these notes proposed does not work.** It used EF's usual
`SaveChangesAsync(acceptAllChangesOnSuccess: false)` plus `AcceptAllChanges()` after the commit.
Beside an interceptor that saves again from its `SavedChanges`, the outer save's changes are not
accepted yet when that second save runs, so the second save writes them again: the product was
inserted twice, and a delete failed as a concurrency conflict. The docs now warn against it, and
recommend this library's own retrying path instead.

**Still open: Option A.** Run every owned save the way the retrying path does: when there is work
and no caller transaction, `SavingChanges(Async)` opens the transaction, runs the save inside it,
commits, and returns `SuppressWithResult`. That covers every interceptor that does its work in the
inner save. It does **not** cover one registered first that waits for the outermost save, which is
exactly what Auditing's gap 2 fix proposes, and the `Before, true` rows show it failing today. The
two proposals conflict, so settle them together, probably by having neither library save again on
the caller's context (see *Related*). It also costs what the class remarks already state for the
retrying path: other interceptors see a `SavingChanges`/`SavedChanges` pair for the inner save as
well as the outer one, and `SaveChanges(false)` still accepts changes on success. The six skipped
rows in `SaveCompositionTests` are this gap.

**Related.** The translation store's nested `SaveChanges` is itself what trips Auditing (see that
repo's `COMPOSITION-GAPS.md`, gap 2). Only the upsert path does this: deletes already go through
`ExecuteDelete`. Consider writing translation rows the same way that gap proposes for audit
entries: through a library-owned context with no interceptors, on the caller's connection and
transaction. Then no other library ever sees this one's follow-up write.

## 3. No way to write or read "all languages, base included"

**What goes wrong.** In base-language mode, `ITranslationAccessor<T>.Set(baseCode, ...)` throws
`BaseLanguageNotTranslatableException` (`MultiLingualManager.cs`, `TranslationAccessor.Translatable`),
and `GetAllAsync` returns every language except the base one. An editor that sends every language
at once therefore has to:

- assign the base values to the properties itself,
- switch the scope to the base language with `UseLanguageAsync` for the save, so the interceptor
  writes the columns instead of a translation,
- switch back in a `finally`, because localized messages read `CurrentLanguage`,
- put the base values back into the read result itself.

That is the sample's `TranslatedText.cs`. It is global per-request state, and the most delicate
code in the application.

**Fix.**

1. Add `ITranslationAccessor<T>.SetAll(IReadOnlyDictionary<string, IReadOnlyDictionary<string,
   string?>> byLanguage)`:
   - Non-base languages are queued like `Set`.
   - Base-language values are assigned to the properties and recorded in the engine as
     **base-authored**, so the interceptor writes them to the columns whatever `CurrentLanguage` is.
   - The base-authored mark must be honoured in **two** places. In `ApplyBaseLanguageRules`, so the
     column is not excluded from the UPDATE. And in `CaptureValues`, which otherwise treats every
     in-memory property value as text in the current language: under `ar`, a base value with no
     Arabic value for the same property would also be stored as the Arabic translation.
   - Base-language values are checked against `MultiLingualMetadata` the same way `Set` checks
     names, so an unknown property is refused identically in both modes.
   - `null` for a base value becomes `string.Empty` (the columns are NOT NULL). Document it.
2. Let the create rule ("an entity can only be created in the base language") accept an entity
   whose base values were assigned through `SetAll`, instead of checking `CurrentLanguage` only.
3. Add `GetAllAsync(includeBase: true)` (or `GetAllWithBaseAsync`). Where the base value comes
   from needs care:
   - Not EF's original values. They hold the column after a read (tracking starts before
     `EntityFill` serves the translation), but `ApplyBaseLanguageRules` sets
     `OriginalValue = CurrentValue`, and a save under `ar` accepts the Arabic text as original. In
     both cases the "base" read back would be Arabic.
   - Instead, have the engine remember the base value per entity, in the tracker: the column value
     as materialized (`EntityFill.Record` already captures it, before the fill), updated when a save
     writes the base column. This also keeps `TranslationAccessor`, which lives in Core and serves
     Dapper and MongoDB too, free of EF types.
   - With nothing remembered (a detached entity), read the row with `IgnoreTranslations()` or the
     provider's equivalent.
   - Return a case-insensitive map.
4. Make both methods inert outside base-language mode: `includeBase` adds nothing, and `SetAll` is
   `Set` per language.
5. Document both in `docs/base-language.md`, with an editor example that never calls
   `UseLanguageAsync`. Note that after `SetAll` under `ar` the in-memory properties hold the base
   text, so a response built from the entity shows the base language.

**Workaround today (sample side).** The read half can drop the language switch now with
`query.InLanguage(baseCode)`. Only the write still needs it.

## 4. Text stored about another library's entity is never cleaned up

**What goes wrong.** The sample stores per-language alt text under the subject `"MediaFile"` with
`For(subject, id)`. Cleanup depends on the application remembering to register Media's
`OnFilesDeleted<MediaTextCleanup>()`. If it forgets, deleted files leave translation rows behind
forever. `PurgeOrphanedTranslationsAsync` cannot catch them either, because it only checks entity
names of `[MultiLingual]` types in the model, and `"MediaFile"` is a subject, not a translatable
type.

**Fix.**

1. Add a builder method that declares a subject and the mapped type it describes:

   ```csharp
   .TrackSubject<MediaFile>("MediaFile")   // EF: a mapped type of this context; key from the model
   ```

   The key comes from the model alone, with no separate selector that could disagree with it, so
   the purge's `DELETE … WHERE NOT EXISTS` and `EntityKeys.KeyAsString` work for it unchanged. The
   type must be mapped in **this** context's model; refuse the registration otherwise.
2. Extend `PurgeOrphanedTranslationsAsync` to include tracked subjects. **Guard against mass
   deletion:** a row counts as orphaned when its `EntityId` matches no key, so an application that
   passes `For(subject, id)` anything other than the mapped key, in the format
   `EntityKey.ToKeyString` produces, would have every live row for that subject purged. Make
   `For(subject, id)` refuse an id whose type is not the tracked type's key type.
3. Add an optional hosted sweep: `.PurgeOrphansEvery(TimeSpan)`, logging how many rows it removed.
   Forgotten cleanup then becomes slow cleanup instead of none.
4. Add an opt-in startup or health check (`.WarnOnUnknownSubjects()`). It lists entity names
   present in `Translations` that are neither a `[MultiLingual]` type nor a tracked subject, and
   logs one warning per name with the fix. Rows owned by another context sharing the table are
   legitimate (see the purge's own remarks), so let the check take a list of names to ignore.
5. ~~Add a checklist, *Storing text about another library's entity*~~ — done (unreleased), in
   `docs/reading-and-writing.md` beside the subject API rather than in `translatable-content.md`,
   which covers `[MultiLingual]` types. It says to use a `const` subject name, delete through the
   owning library's hook (with the `ForMany(...).DeleteAsync()` example), and that the purge does
   not cover subjects; the orphan section says so too. Once steps 1–2 ship, add "track the
   subject" to it.

## 5. Tests beside another post-save interceptor (partly done)

**Done.** `tests/DotNetBoost.MultiLingual.ProviderTests/EfCore/SaveCompositionTests.cs`, on
SQLite, without referencing Auditing. `FollowUpSaveInterceptor` behaves like Auditing's deferred
pass: once the outermost save that changed a product completes, it adds a row of its own, saves
again on the same context, and relies on whatever transaction is open. Each case runs with it
registered before and after `UseMultiLingual`, with and without a retrying strategy, over a
product with an identity key, in both modes. Rows that fail today are skipped with the gap they
belong to, so each fix un-skips its own. The gap 1 rows pass now; the six still skipped are gap 2.
The same file covers the failures EF never reports (gap 1, *What shipped*), a nested save that
itself fails, `OwnTransactions(false)` (gap 2), and the documented application-owned transaction.
`SyncSaveCompositionTests` runs the same cases through the synchronous `SaveChanges()`.

**Still to do.**

1. One PostgreSQL smoke case in `tests/DotNetBoost.MultiLingual.IntegrationTests`.
2. Tests for `SetAll` / `GetAllAsync(includeBase: true)` once gap 3 ships. Under
   `Accept-Language: ar`, they must write the base columns, store no base text as an Arabic
   translation, return the base text after a save under `ar`, and leave `CurrentLanguage`
   unchanged.
