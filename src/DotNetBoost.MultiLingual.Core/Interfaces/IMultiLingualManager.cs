using DotNetBoost.MultiLingual.Core.Models;
using System.Linq.Expressions;

namespace DotNetBoost.MultiLingual.Core.Interfaces;

/// <summary>
/// The one service application code injects. Everything the library does for you is reachable
/// from here:
/// <list type="bullet">
///   <item><see cref="CurrentLanguage"/> — the language this request (or job) reads and writes in.</item>
///   <item><see cref="Languages"/> — find, list and administer languages.</item>
///   <item><see cref="For{T}(T)"/> — one entity's translations: read every language, set or clear one.</item>
///   <item><see cref="LocalizeAsync(IEnumerable{object}, CancellationToken)"/> — fill translated properties
///   yourself, for providers without a change tracker (Dapper, MongoDB).</item>
/// </list>
/// <code>
/// public class ProductService(AppDbContext db, IMultiLingualManager multiLingual)
/// {
///     public async Task ClearArabicAsync(int id)
///     {
///         var product = await db.Products.FirstAsync(p => p.Id == id);
///         var stored  = await multiLingual.For(product).GetAllAsync(); // every language
///         multiLingual.For(product).Clear("ar");                       // remove one language
///         await db.SaveChangesAsync();
///     }
/// }
/// </code>
/// Scoped: one instance per request or unit of work.
/// </summary>
public interface IMultiLingualManager
{
    // ------------------------------------------------------------------ current language

    /// <summary>
    /// The language content is read and authored in. In ASP.NET Core <c>UseMultiLingual()</c>
    /// resolves it from <c>Accept-Language</c>; anywhere else call <see cref="UseLanguageAsync"/>.
    /// </summary>
    /// <exception cref="NoLanguageConfiguredException">No language has been resolved.</exception>
    LanguageInfo CurrentLanguage { get; }

    /// <summary>The system default, used when a value is missing in <see cref="CurrentLanguage"/>.</summary>
    /// <exception cref="NoLanguageConfiguredException">No language has been resolved.</exception>
    LanguageInfo DefaultLanguage { get; }

    /// <summary>False until a language has been resolved for this scope.</summary>
    bool IsLanguageResolved { get; }

    /// <summary>False when the caller asked for a language that does not exist or is inactive.</summary>
    bool WasRequestedLanguageHonoured { get; }

    /// <summary>
    /// Switches this scope to <paramref name="languageCode"/> — for background jobs, seeders and
    /// tests. Falls back to the default language when the code is null, unknown or inactive.
    /// </summary>
    /// <returns>True when the requested language itself was applied.</returns>
    /// <exception cref="NoLanguageConfiguredException">No languages exist yet.</exception>
    Task<bool> UseLanguageAsync(string? languageCode, CancellationToken ct = default);

    // ------------------------------------------------------------------ languages

    /// <summary>Find, list and administer languages.</summary>
    ILanguageAccessor Languages { get; }

    // ------------------------------------------------------------------ translations

    /// <summary>
    /// The translations of one entity instance.
    /// <code>
    /// await multiLingual.For(product).GetAllAsync();
    /// multiLingual.For(product).Set("fr", p => p.Name, "Chaise");
    /// multiLingual.For(product).Clear("ar");
    /// </code>
    /// </summary>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> has no <c>[MultiLingual]</c> properties.</exception>
    ITranslationAccessor<T> For<T>(T entity) where T : class;

    /// <summary>
    /// Fills the translatable properties of <paramref name="entities"/> in the current language,
    /// falling back to the default language for missing values. Values that exist in neither are
    /// set to <c>null</c>.
    /// <para>
    /// With EF Core you rarely need this — every query fills the entities it returns. Call it after
    /// a Dapper or MongoDB query, or for entities EF did not return directly (a collection inside
    /// a projection, say) or that you built yourself.
    /// </para>
    /// </summary>
    Task LocalizeAsync(IEnumerable<object> entities, CancellationToken ct = default);

    /// <summary>As <see cref="LocalizeAsync(IEnumerable{object}, CancellationToken)"/>, in a specific language.</summary>
    /// <exception cref="UnknownLanguageException"><paramref name="languageCode"/> does not exist.</exception>
    Task LocalizeAsync(IEnumerable<object> entities, string languageCode, CancellationToken ct = default);

    /// <summary>Synchronous <see cref="LocalizeAsync(IEnumerable{object}, CancellationToken)"/>.</summary>
    void Localize(IEnumerable<object> entities);

    /// <summary>Synchronous <see cref="LocalizeAsync(IEnumerable{object}, string, CancellationToken)"/>.</summary>
    /// <exception cref="UnknownLanguageException"><paramref name="languageCode"/> does not exist.</exception>
    void Localize(IEnumerable<object> entities, string languageCode);
}

/// <summary>
/// Languages: cached lookups for everyday code, and administration with the rules that keep the
/// system consistent — codes are validated and normalised, the first language becomes the
/// default, there is always exactly one active default, and a language with content cannot be
/// deleted by accident.
/// </summary>
public interface ILanguageAccessor
{
    // ------------------------------------------------------------------ read

    /// <summary>Every language, default first, then by code — read from the store, never cached.</summary>
    Task<IReadOnlyList<LanguageInfo>> GetAllAsync(CancellationToken ct = default);

    /// <summary>One language, read from the store, never cached.</summary>
    /// <exception cref="LanguageNotFoundException">No language has that code.</exception>
    Task<LanguageInfo> GetAsync(string code, CancellationToken ct = default);

    // The lookups below are served from the in-memory language cache: cheap enough for every
    // request. Writes through this accessor refresh it.

    /// <summary>The language matching <paramref name="code"/> (case-insensitive), or null.</summary>
    Task<LanguageInfo?> FindAsync(string? code, CancellationToken ct = default);

    /// <summary>The ACTIVE language matching <paramref name="code"/> (case-insensitive), or null.</summary>
    Task<LanguageInfo?> FindActiveAsync(string? code, CancellationToken ct = default);

    /// <summary>The default language.</summary>
    /// <exception cref="NoLanguageConfiguredException">No default language exists.</exception>
    Task<LanguageInfo> GetDefaultAsync(CancellationToken ct = default);

    /// <summary>
    /// The configured base language (<see cref="MultiLingualOptions.BaseLanguage"/>), or null
    /// when every language is stored as a translation.
    /// </summary>
    string? BaseLanguage { get; }

    /// <summary>
    /// True when <paramref name="code"/> is the base language — whose values live in the
    /// entity's own columns and are therefore not a translation.
    /// </summary>
    bool IsBaseLanguage(string? code);

    /// <summary>
    /// Drops the cached language list, so the next read goes to the store. Writes through this
    /// accessor already do it; call it after changing the languages some other way.
    /// </summary>
    void InvalidateCache();

    // ------------------------------------------------------------------ write

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

/// <summary>
/// The translations of one entity instance, from <see cref="IMultiLingualManager.For{T}(T)"/>.
/// <para>
/// <see cref="Set(string, Expression{Func{T, string}}, string)"/> and <see cref="Clear(string)"/>
/// only queue a change: it is written by the next save of the entity — <c>SaveChanges</c> with
/// EF Core, <see cref="SaveAsync"/> without a change tracker.
/// </para>
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public interface ITranslationAccessor<T> where T : class
{
    /// <summary>The entity this accessor works on.</summary>
    T Entity { get; }

    // ------------------------------------------------------------------ read

    /// <summary>
    /// Every stored translation of the entity, as languageCode → (propertyName → value).
    /// Empty when the entity has no key yet. In base-language mode the base language is not
    /// included: it is the entity's own columns.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Fills the entity's translatable properties in the current language, with fallback.</summary>
    Task LocalizeAsync(CancellationToken ct = default);

    /// <summary>Fills the entity's translatable properties in <paramref name="languageCode"/>, with fallback.</summary>
    /// <exception cref="UnknownLanguageException"><paramref name="languageCode"/> does not exist.</exception>
    Task LocalizeAsync(string languageCode, CancellationToken ct = default);

    // ------------------------------------------------------------------ write (queued)

    /// <summary>
    /// Queues <paramref name="value"/> as the translation of <paramref name="property"/> in
    /// <paramref name="languageCode"/> — a language other than (or as well as) the current one,
    /// whose values are simply assigned to the properties. A <c>null</c> value removes the
    /// stored translation, like <see cref="Clear(string, Expression{Func{T, string}}[])"/>.
    /// </summary>
    /// <exception cref="LanguageNotFoundException"><paramref name="languageCode"/> does not exist.</exception>
    /// <exception cref="BaseLanguageNotTranslatableException"><paramref name="languageCode"/> is the base language.</exception>
    /// <exception cref="ArgumentException"><paramref name="property"/> is not translatable.</exception>
    ITranslationAccessor<T> Set(string languageCode, Expression<Func<T, string?>> property, string? value);

    /// <summary>
    /// Queues several values in <paramref name="languageCode"/>, by property name
    /// (propertyName → value). Useful when the names come from a request rather than from code.
    /// </summary>
    /// <exception cref="LanguageNotFoundException"><paramref name="languageCode"/> does not exist.</exception>
    /// <exception cref="BaseLanguageNotTranslatableException"><paramref name="languageCode"/> is the base language.</exception>
    /// <exception cref="ArgumentException">A property name is not translatable.</exception>
    ITranslationAccessor<T> Set(string languageCode, IReadOnlyDictionary<string, string?> values);

    /// <summary>
    /// Queues the removal of every stored translation of the entity in
    /// <paramref name="languageCode"/>. Reads then fall back as for any missing translation.
    /// <para>
    /// This is the way to clear a value: assigning <c>null</c> to a property means "not supplied"
    /// and leaves what is stored alone. The base language is not a translation, so it cannot be
    /// cleared; assign the properties instead.
    /// </para>
    /// </summary>
    /// <exception cref="LanguageNotFoundException"><paramref name="languageCode"/> does not exist.</exception>
    /// <exception cref="BaseLanguageNotTranslatableException"><paramref name="languageCode"/> is the base language.</exception>
    ITranslationAccessor<T> Clear(string languageCode);

    /// <summary>As <see cref="Clear(string)"/>, for the given properties only.</summary>
    /// <exception cref="LanguageNotFoundException"><paramref name="languageCode"/> does not exist.</exception>
    /// <exception cref="BaseLanguageNotTranslatableException"><paramref name="languageCode"/> is the base language.</exception>
    /// <exception cref="ArgumentException">A property is not translatable.</exception>
    ITranslationAccessor<T> Clear(string languageCode, params Expression<Func<T, string?>>[] properties);

    /// <summary>As <see cref="Clear(string)"/>, for the named properties only.</summary>
    /// <exception cref="LanguageNotFoundException"><paramref name="languageCode"/> does not exist.</exception>
    /// <exception cref="BaseLanguageNotTranslatableException"><paramref name="languageCode"/> is the base language.</exception>
    /// <exception cref="ArgumentException">A property name is not translatable.</exception>
    ITranslationAccessor<T> Clear(string languageCode, IEnumerable<string> propertyNames);

    // ------------------------------------------------------------------ save (no change tracker)

    /// <summary>
    /// Without a change tracker (Dapper, MongoDB): writes the entity's translatable values for
    /// the current language, plus anything queued with <c>Set</c> / <c>Clear</c>. Call it after
    /// the entity's own row is saved, so it has its key. A <c>null</c> property means "not
    /// supplied" and leaves the stored value alone. EF Core does this inside <c>SaveChanges</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entity has no key yet — save it first.</exception>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// Without a change tracker: removes every translation of the entity. EF Core does this
    /// inside <c>SaveChanges</c> when the entity is deleted.
    /// </summary>
    Task DeleteAsync(CancellationToken ct = default);

    /// <summary>
    /// Without a change tracker, in base-language mode: declares that the given properties hold
    /// the BASE language's values — loaded from the entity's own columns, not assigned by the
    /// caller — so <see cref="SaveAsync"/> does not write them back as a translation in the
    /// current language. The EF Core provider does this for you.
    /// </summary>
    ITranslationAccessor<T> MarkAsBaseLanguageValues(params Expression<Func<T, string?>>[] properties);
}
