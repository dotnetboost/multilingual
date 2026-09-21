using DotNetBoost.MultiLingual.Core.Models;

namespace DotNetBoost.MultiLingual.Core.Interfaces;

/// <summary>
/// The language the current unit of work reads and writes content in. Scoped: in ASP.NET Core
/// the <c>DotNetBoost.MultiLingual.API</c> middleware resolves it from <c>Accept-Language</c>
/// once per request; anywhere else (background jobs, seeders) call <see cref="UseAsync"/>.
/// </summary>
public interface ILanguageContext
{
    /// <summary>False until a language has been applied to this scope.</summary>
    bool IsResolved { get; }

    /// <summary>The language content is read and authored in.</summary>
    /// <exception cref="NoLanguageConfiguredException">No language has been resolved.</exception>
    LanguageInfo Current { get; }

    /// <summary>The system default, used for the read-path fallback.</summary>
    /// <exception cref="NoLanguageConfiguredException">No language has been resolved.</exception>
    LanguageInfo DefaultLanguage { get; }

    /// <summary>False when the caller asked for a language that does not exist or is inactive.</summary>
    bool WasRequestedLanguageHonoured { get; }

    /// <summary>Applies an already-resolved language. Used by request middleware.</summary>
    void Apply(LanguageInfo current, LanguageInfo defaultLanguage, bool honoured = true);

    /// <summary>
    /// Resolves <paramref name="languageCode"/> against the active languages and applies it,
    /// falling back to the default when it is null, unknown or inactive.
    /// </summary>
    /// <returns>True when the requested language itself was applied.</returns>
    /// <exception cref="NoLanguageConfiguredException">No languages exist yet.</exception>
    Task<bool> UseAsync(string? languageCode, CancellationToken ct = default);
}

/// <summary>
/// In-memory cache of the language list. Every request resolves a language and every write
/// validates codes — hitting the store each time would be wasteful for a list that changes a
/// few times a year. Entries expire after <see cref="MultiLingualOptions.LanguageCacheDuration"/>
/// so several application instances converge after one of them changes the list.
/// </summary>
public interface ILanguageCatalog
{
    /// <summary>Every language, ordered by code.</summary>
    Task<IReadOnlyList<LanguageInfo>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Synchronous <see cref="GetAllAsync"/>, for a synchronous <c>SaveChanges</c>. The default
    /// blocks on the async method; the built-in catalog loads synchronously instead.
    /// </summary>
    IReadOnlyList<LanguageInfo> GetAll() => GetAllAsync().GetAwaiter().GetResult();

    /// <summary>The default language.</summary>
    /// <exception cref="NoLanguageConfiguredException">No default language exists.</exception>
    Task<LanguageInfo> GetDefaultAsync(CancellationToken ct = default);

    /// <summary>The language matching <paramref name="code"/> (case-insensitive), or null.</summary>
    Task<LanguageInfo?> FindByCodeAsync(string? code, CancellationToken ct = default);

    /// <summary>The ACTIVE language matching <paramref name="code"/> (case-insensitive), or null.</summary>
    Task<LanguageInfo?> FindActiveByCodeAsync(string? code, CancellationToken ct = default);

    /// <summary>Drops the cache. <see cref="ILanguageManager"/> calls it after every write.</summary>
    void Invalidate();
}

/// <summary>
/// Translations for application code: read them, stage and clear them, and — without a change
/// tracker — save and delete them.
/// <list type="bullet">
///   <item>With EF Core, the interceptors read and write for you; application code only stages,
///   clears, or reads everything stored for one entity.</item>
///   <item>Without change tracking (Dapper, MongoDB) call
///   <see cref="HydrateAsync(IEnumerable{object}, CancellationToken)"/> after a query and
///   <see cref="SaveAsync"/> / <see cref="DeleteAsync"/> alongside your own writes.</item>
/// </list>
/// Storage providers use <see cref="ITranslationWriter"/> as well, for the steps of a save.
/// </summary>
public interface ITranslationManager
{
    // ------------------------------------------------------------------ read

    /// <summary>
    /// Populates the translatable properties of <paramref name="entities"/> in the current
    /// language, falling back to the default language for values that are missing. Values that
    /// exist in neither are set to <c>null</c>.
    /// </summary>
    Task HydrateAsync(IEnumerable<object> entities, CancellationToken ct = default);

    /// <summary>As <see cref="HydrateAsync(IEnumerable{object}, CancellationToken)"/>, in a specific language.</summary>
    /// <exception cref="UnknownLanguageException"><paramref name="languageCode"/> does not exist.</exception>
    Task HydrateAsync(IEnumerable<object> entities, string languageCode, CancellationToken ct = default);

    /// <summary>
    /// Synchronous <see cref="HydrateAsync(IEnumerable{object}, CancellationToken)"/>. Blocks on
    /// nothing asynchronous as long as the stores and catalog implement their synchronous members.
    /// </summary>
    void Hydrate(IEnumerable<object> entities);

    /// <summary>Synchronous <see cref="HydrateAsync(IEnumerable{object}, string, CancellationToken)"/>.</summary>
    /// <exception cref="UnknownLanguageException"><paramref name="languageCode"/> does not exist.</exception>
    void Hydrate(IEnumerable<object> entities, string languageCode);

    /// <summary>
    /// Every stored translation for one entity instance, as languageCode → (propertyName → value).
    /// Empty when the entity has no key yet.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(
        object entity, CancellationToken ct = default);

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Persists the translatable values of an entity that already has its key: the in-memory
    /// property values for the current language, plus anything <see cref="Stage"/>d for it.
    /// A <c>null</c> property means "not supplied" and leaves the stored value alone; a value
    /// that is still exactly what the read path put there is not written back.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entity has no key yet — save it first.</exception>
    Task SaveAsync(object entity, CancellationToken ct = default);

    /// <summary>Removes every translation belonging to <paramref name="entity"/>.</summary>
    Task DeleteAsync(object entity, CancellationToken ct = default);

    /// <summary>
    /// Queues values for <paramref name="entity"/> in a language other than (or as well as) the
    /// current one. They are written by the next save of that instance and then discarded.
    /// Repeated calls merge; later values win.
    /// </summary>
    /// <exception cref="ArgumentException">A property name is not translatable on this type.</exception>
    void Stage(object entity, string languageCode, IReadOnlyDictionary<string, string?> values);

    /// <summary>
    /// Queues the removal of <paramref name="entity"/>'s stored translation in
    /// <paramref name="languageCode"/> for the named properties — every translatable property
    /// when none are named. Applied by the next save of that instance; reads then fall back as
    /// for any missing translation.
    /// <para>
    /// This is the way to clear a value: assigning <c>null</c> to a property means "not supplied"
    /// and leaves what is stored alone. In base-language mode the base language is not a
    /// translation, so it cannot be cleared here; assign <c>null</c> to the property instead.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A property name is not translatable on this type, or <paramref name="languageCode"/> is the base language.
    /// </exception>
    void Clear(object entity, string languageCode, params string[] propertyNames);

    /// <summary>
    /// Declares that the named properties of <paramref name="entity"/> currently hold the BASE
    /// language's values — read from the entity's own table rather than assigned by a caller — so
    /// the next save does not write them back as a translation in some other language.
    ///
    /// <para>
    /// Only meaningful in base-language mode, and a no-op without one. Call it before
    /// <see cref="SaveAsync"/> for the properties a caller did not touch, when saving without a
    /// change tracker (Dapper, say — the EF Core provider does it for you): a write endpoint
    /// deliberately loads without hydrating, which in this mode still leaves the base column's
    /// value in the property, and writing that back would quietly turn English text into an Arabic
    /// translation.
    /// </para>
    /// </summary>
    void MarkAsBaseLanguageValues(object entity, IEnumerable<string> propertyNames);
}

/// <summary>
/// The write side of the engine, as the steps a storage provider performs around a save —
/// capture before the entity is saved, write once its key exists, complete once the commit has
/// succeeded. The EF Core provider's save interceptor is built on it; a Dapper or MongoDB provider
/// would be too. Application code uses <see cref="ITranslationManager"/> instead.
/// <para>
/// Resolved from the same scope as <see cref="ITranslationManager"/>, and backed by the same
/// instance, so both see the same staged values and hydration tracking.
/// </para>
/// </summary>
public interface ITranslationWriter
{
    /// <summary>
    /// Takes the translatable values of <paramref name="entity"/> that need writing, as
    /// languageCode → (propertyName → value) — without touching the store. Providers call this
    /// before the entity itself is saved, when a store-generated key may not exist yet.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> CaptureValues(object entity);

    /// <summary>
    /// Validates the language codes in <paramref name="writes"/> and upserts every value, then
    /// calls <see cref="CompleteWrites"/> for them.
    /// <para>
    /// A provider that commits the rows itself, after this returns, should pass writes without a
    /// <see cref="TranslationWrite.Source"/> and call <see cref="CompleteWrites"/> once the commit
    /// has succeeded. Completing earlier would make a retry after a failed commit think the
    /// values were already stored, and skip them.
    /// </para>
    /// </summary>
    /// <exception cref="LanguageNotFoundException">A write names a language that does not exist.</exception>
    Task WriteAsync(IReadOnlyCollection<TranslationWrite> writes, CancellationToken ct = default);

    /// <summary>
    /// Synchronous <see cref="WriteAsync"/>, for a synchronous <c>SaveChanges</c>. Blocks on
    /// nothing asynchronous as long as the stores and catalog implement their synchronous members.
    /// </summary>
    /// <exception cref="LanguageNotFoundException">A write names a language that does not exist.</exception>
    void Write(IReadOnlyCollection<TranslationWrite> writes);

    /// <summary>
    /// Records that <paramref name="writes"/> are now durably stored: for each one with a
    /// <see cref="TranslationWrite.Source"/>, discards what was <see cref="ITranslationManager.Stage"/>d for that
    /// instance and remembers what was written, so saving the same untouched instance again
    /// writes nothing. Writes without a source are ignored.
    /// </summary>
    void CompleteWrites(IReadOnlyCollection<TranslationWrite> writes);

    /// <summary>Removes every translation belonging to the given entities.</summary>
    Task<int> DeleteEntitiesAsync(string entityName, IReadOnlyCollection<string> entityIds, CancellationToken ct = default);

    /// <summary>Synchronous <see cref="DeleteEntitiesAsync"/>, for a synchronous <c>SaveChanges</c>.</summary>
    int DeleteEntities(string entityName, IReadOnlyCollection<string> entityIds);

    /// <summary>
    /// A writer that shares this one's scope state (staged values, hydration tracking, current
    /// language) but writes through <paramref name="store"/>. Providers use it to keep translation
    /// writes inside the unit of work that is saving the entity.
    /// </summary>
    ITranslationWriter WithStore(ITranslationStore store);
}

/// <summary>
/// Language administration with the rules that keep the system consistent: codes are validated
/// and normalised, the first language becomes the default, there is always exactly one active
/// default, and a language with content cannot be deleted by accident.
/// </summary>
public interface ILanguageManager
{
    /// <summary>Every language, default first, then by code.</summary>
    Task<IReadOnlyList<LanguageInfo>> GetAllAsync(CancellationToken ct = default);

    /// <summary>One language.</summary>
    /// <exception cref="LanguageNotFoundException">No language has that code.</exception>
    Task<LanguageInfo> GetAsync(string code, CancellationToken ct = default);

    /// <summary>Creates a language.</summary>
    /// <exception cref="InvalidLanguageException">The code or name is not acceptable.</exception>
    /// <exception cref="LanguageConflictException">The code exists, or the request breaks a rule.</exception>
    Task<LanguageInfo> CreateAsync(NewLanguage language, CancellationToken ct = default);

    /// <summary>Replaces a language's name, direction and active flag.</summary>
    /// <exception cref="LanguageNotFoundException">No language has that code.</exception>
    /// <exception cref="LanguageConflictException">The update would deactivate the default.</exception>
    Task<LanguageInfo> UpdateAsync(string code, LanguageUpdate update, CancellationToken ct = default);

    /// <summary>
    /// Deletes a language. Refused for the default language, and while translations in the
    /// language still exist — purge them first with <see cref="PurgeTranslationsAsync"/>.
    /// </summary>
    /// <exception cref="LanguageNotFoundException">No language has that code.</exception>
    /// <exception cref="LanguageConflictException">The language is the default or still has content.</exception>
    Task DeleteAsync(string code, CancellationToken ct = default);

    /// <summary>Promotes a language to default and demotes the previous one, atomically.</summary>
    /// <exception cref="LanguageNotFoundException">No language has that code.</exception>
    /// <exception cref="LanguageConflictException">The language is inactive.</exception>
    Task<LanguageInfo> SetDefaultAsync(string code, CancellationToken ct = default);

    /// <summary>Deletes every translation written in a language, across all entities.</summary>
    /// <exception cref="LanguageNotFoundException">No language has that code.</exception>
    Task<int> PurgeTranslationsAsync(string code, CancellationToken ct = default);
}
