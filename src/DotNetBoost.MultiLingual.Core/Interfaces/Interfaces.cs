using DotNetBoost.MultiLingual.Core.Models;

namespace DotNetBoost.MultiLingual.Core.Interfaces;

/// <summary>
/// The write side of the engine, as the steps a storage provider performs around a save —
/// capture before the entity is saved, write once its key exists, complete once the commit has
/// succeeded. The EF Core provider's save interceptor is built on it; a Dapper or MongoDB provider
/// would be too. Application code uses <see cref="IMultiLingualManager"/> instead.
/// <para>
/// Resolved from the same scope as <see cref="IMultiLingualManager"/>, and backed by the same
/// engine, so both see the same queued values and hydration tracking.
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
    /// <see cref="TranslationWrite.Source"/>, discards what was queued (<see cref="ITranslationAccessor{T}"/>'s <c>Set</c> / <c>Clear</c>) for that
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
