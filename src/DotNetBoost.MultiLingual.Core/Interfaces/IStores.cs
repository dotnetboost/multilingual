using DotNetBoost.MultiLingual.Core.Models;

namespace DotNetBoost.MultiLingual.Core.Interfaces;

/// <summary>
/// Persistence contract for translation rows. Implement this (with <see cref="ILanguageStore"/>)
/// to add a backend. Implementations are deliberately dumb: validation, fallback and
/// change detection all live in <see cref="ITranslationManager"/>.
/// <para>
/// Language codes passed in are canonical (as stored on <see cref="Language.Code"/>), so
/// implementations may compare them ordinally.
/// </para>
/// <para>
/// <b>Synchronous members.</b> <see cref="UpsertMany"/> and <see cref="DeleteEntities"/> serve a
/// synchronous <c>SaveChanges</c>. Their default implementations block on the async ones, which
/// can deadlock under a single-threaded synchronization context (WPF, WinForms); a store whose
/// backend has a synchronous API should override them, as the built-in stores do.
/// </para>
/// </summary>
public interface ITranslationStore
{
    /// <summary>
    /// Returns every row for the given entities whose language is one of
    /// <paramref name="languageCodes"/>. Row order is not significant.
    /// </summary>
    Task<IReadOnlyList<Translation>> GetAsync(
        string entityName,
        IReadOnlyCollection<string> entityIds,
        IReadOnlyCollection<string> languageCodes,
        CancellationToken ct = default);

    /// <summary>Returns every row, in every language, for one entity instance.</summary>
    Task<IReadOnlyList<Translation>> GetAllAsync(string entityName, string entityId, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates rows matched on their natural key
    /// (entity name, entity id, property name, language code). When the batch repeats a key,
    /// the later row wins. Atomic where the backend supports it, and enlisted in whatever
    /// unit of work the provider is currently part of.
    /// </summary>
    Task UpsertManyAsync(IReadOnlyCollection<Translation> translations, CancellationToken ct = default);

    /// <summary>Removes every row belonging to the given entities. Returns the number removed.</summary>
    Task<int> DeleteEntitiesAsync(string entityName, IReadOnlyCollection<string> entityIds, CancellationToken ct = default);

    /// <summary>Synchronous <see cref="UpsertManyAsync"/>. See the type's remarks before relying on the default.</summary>
    void UpsertMany(IReadOnlyCollection<Translation> translations) =>
        UpsertManyAsync(translations).GetAwaiter().GetResult();

    /// <summary>Synchronous <see cref="DeleteEntitiesAsync"/>. See the type's remarks before relying on the default.</summary>
    int DeleteEntities(string entityName, IReadOnlyCollection<string> entityIds) =>
        DeleteEntitiesAsync(entityName, entityIds).GetAwaiter().GetResult();

    /// <summary>Number of rows written in <paramref name="languageCode"/>, across every entity.</summary>
    Task<int> CountByLanguageAsync(string languageCode, CancellationToken ct = default);

    /// <summary>Removes every row written in <paramref name="languageCode"/>. Returns the number removed.</summary>
    Task<int> DeleteByLanguageAsync(string languageCode, CancellationToken ct = default);
}

/// <summary>
/// Persistence contract for the language list. Business rules (the first language becomes the
/// default, the default cannot be deleted, …) live in <see cref="ILanguageManager"/>; the store
/// only has to keep the "exactly one default" invariant atomic where it spans several rows.
/// </summary>
public interface ILanguageStore
{
    /// <summary>Every stored language.</summary>
    Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Synchronous <see cref="GetAllAsync"/>, for a synchronous <c>SaveChanges</c> that finds the
    /// language cache expired. The default blocks on the async method, which can deadlock under a
    /// single-threaded synchronization context; override it where the backend allows, as the
    /// built-in stores do.
    /// </summary>
    IReadOnlyList<Language> GetAll() => GetAllAsync().GetAwaiter().GetResult();

    /// <summary>The language with exactly this canonical code, or <c>null</c>.</summary>
    Task<Language?> FindAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Stores a new language. When <see cref="Language.IsDefault"/> is true, every other
    /// language must lose its default flag in the same atomic operation.
    /// </summary>
    Task AddAsync(Language language, CancellationToken ct = default);

    /// <summary>
    /// Updates <see cref="Language.Name"/>, <see cref="Language.Direction"/> and
    /// <see cref="Language.IsActive"/> of the language matched on its code. Never changes which
    /// language is the default — that is <see cref="SetDefaultAsync"/>'s job.
    /// </summary>
    /// <exception cref="LanguageNotFoundException">No language has that code.</exception>
    Task UpdateAsync(Language language, CancellationToken ct = default);

    /// <summary>Removes a language. Succeeds silently when it does not exist.</summary>
    Task DeleteAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Makes <paramref name="code"/> the only default language: set it and clear every other
    /// one, atomically — both or neither.
    /// </summary>
    /// <exception cref="LanguageNotFoundException">No language has that code.</exception>
    Task SetDefaultAsync(string code, CancellationToken ct = default);
}

/// <summary>
/// Reads an entity's primary key as the canonical string stored in
/// <see cref="Translation.EntityId"/>. The default implementation discovers keys by convention
/// (<c>[Key]</c>, <c>Id</c>, <c>{TypeName}Id</c>); providers that know better — EF Core reads
/// the model — replace it.
/// </summary>
public interface IEntityKeyAccessor
{
    /// <summary>
    /// The key of <paramref name="entity"/>, or <c>null</c> when it has not been assigned yet
    /// (a store-generated key before the entity is first saved).
    /// </summary>
    /// <exception cref="InvalidOperationException">The type has no discoverable single key.</exception>
    string? GetKey(object entity);
}
