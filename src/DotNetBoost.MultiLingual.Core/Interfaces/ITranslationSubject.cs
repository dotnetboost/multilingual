namespace DotNetBoost.MultiLingual.Core.Interfaces;

/// <summary>
/// The translations of a <b>subject</b> — something addressed by entity name and id rather than
/// by instance, from <see cref="IMultiLingualManager.For(string, object)"/>.
/// <para>
/// This is the API for per-language text about something whose type the application does not own:
/// the alt text of a <c>MediaFile</c> from another library, a row read with raw SQL, a record in
/// another service. Nothing is reflected over, so no property is validated — any name may be
/// written, which is the point.
/// </para>
/// <code>
/// await multiLingual.For("MediaFile", file.Id)
///     .Set("en", "AltText", "A red chair")
///     .Set("ar", "AltText", "كرسي أحمر")
///     .SaveAsync();
///
/// var text = await multiLingual.For("MediaFile", file.Id).GetAsync();   // current language
/// </code>
/// <para>
/// <b>A subject has no columns of its own</b>, so every language it is written in — including a
/// configured <see cref="MultiLingualOptions.BaseLanguage"/> — is stored as a translation row.
/// Do not address a <c>[MultiLingual]</c> entity type this way in base-language mode: its base
/// language belongs in the entity's own columns, and a row written here for that language would
/// be invisible to every query. Use <see cref="IMultiLingualManager.For{T}(T)"/> for your own
/// entities.
/// </para>
/// </summary>
public interface ITranslationSubject
{
    /// <summary>The entity name the rows are filed under.</summary>
    string EntityName { get; }

    /// <summary>The subject's id, as it is stored — formatted by <see cref="Metadata.EntityKey.ToKeyString"/>.</summary>
    string EntityId { get; }

    // ------------------------------------------------------------------ read

    /// <summary>
    /// The subject's values in the current language, falling back to the default language for
    /// values missing from it — the SAME rule the EF Core interceptors apply to entities, from
    /// the same code. propertyName → value. Empty when nothing is stored.
    /// </summary>
    /// <exception cref="NoLanguageConfiguredException">No languages exist yet.</exception>
    Task<IReadOnlyDictionary<string, string?>> GetAsync(CancellationToken ct = default);

    /// <summary>Every stored language, as languageCode → (propertyName → value).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(CancellationToken ct = default);

    // ------------------------------------------------------------------ write (queued)

    /// <summary>
    /// Queues one value. A <c>null</c> value removes the stored row, so this is also how a
    /// subject's translation is cleared. Nothing is written until <see cref="SaveAsync"/>.
    /// </summary>
    /// <exception cref="LanguageNotFoundException"><paramref name="languageCode"/> does not exist.</exception>
    ITranslationSubject Set(string languageCode, string propertyName, string? value);

    /// <summary>Queues several values in one language (propertyName → value).</summary>
    /// <exception cref="LanguageNotFoundException"><paramref name="languageCode"/> does not exist.</exception>
    ITranslationSubject Set(string languageCode, IReadOnlyDictionary<string, string?> values);

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Writes everything queued, in one store operation — atomically where the backend supports
    /// it. A no-op when nothing is queued.
    /// </summary>
    /// <exception cref="UnknownLanguageException">A queued language no longer exists.</exception>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Removes every stored translation of the subject. Returns the number of rows removed.</summary>
    Task<int> DeleteAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="ITranslationSubject"/> for many ids at once, from
/// <see cref="IMultiLingualManager.ForMany(string, IReadOnlyCollection{object})"/> — <b>one</b>
/// store round trip, not one per id, which is what a listing page needs:
/// <code>
/// var text = await multiLingual.ForMany("MediaFile", page.Select(f => (object)f.Id).ToList()).GetAsync();
/// var alt  = text.TryGetValue(file.Id.ToString("D"), out var values) ? values["AltText"] : null;
/// </code>
/// </summary>
public interface ITranslationSubjects
{
    /// <summary>The entity name the rows are filed under.</summary>
    string EntityName { get; }

    /// <summary>The subjects' ids, as they are stored.</summary>
    IReadOnlyCollection<string> EntityIds { get; }

    /// <summary>
    /// The subjects' values in the current language with the default-language fallback, as
    /// entityId → (propertyName → value), in ONE store read. Ids with nothing stored are absent.
    /// </summary>
    /// <exception cref="NoLanguageConfiguredException">No languages exist yet.</exception>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAsync(CancellationToken ct = default);

    /// <summary>Removes every stored translation of every subject. Returns the number of rows removed.</summary>
    Task<int> DeleteAsync(CancellationToken ct = default);
}
