using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using System.Linq.Expressions;
using System.Reflection;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>
/// Default <see cref="IMultiLingualManager"/>. A thin front over the engine services; it adds no
/// behaviour of its own beyond checking a language code up front, where the engine would only
/// have found out at save time.
/// </summary>
internal sealed class MultiLingualManager(
    ILanguageContext context,
    ILanguageCatalog catalog,
    ILanguageManager languageManager,
    ITranslationManager translations,
    MultiLingualOptions options) : IMultiLingualManager
{
    private ILanguageAccessor? _languages;

    public LanguageInfo CurrentLanguage => context.Current;

    public LanguageInfo DefaultLanguage => context.DefaultLanguage;

    public bool IsLanguageResolved => context.IsResolved;

    public bool WasRequestedLanguageHonoured => context.WasRequestedLanguageHonoured;

    public Task<bool> UseLanguageAsync(string? languageCode, CancellationToken ct = default) =>
        context.UseAsync(languageCode, ct);

    public ILanguageAccessor Languages => _languages ??= new LanguageAccessor(catalog, languageManager, options);

    public ITranslationAccessor<T> For<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!MultiLingualMetadata.IsMultiLingual(entity.GetType()))
        {
            throw new ArgumentException(
                $"'{entity.GetType().Name}' has no [MultiLingual] properties, so it has no translations.", nameof(entity));
        }

        return new TranslationAccessor<T>(entity, translations, catalog, options);
    }

    public ITranslationSubject For(string entityName, object entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);

        if (EntityKey.IsUnset(entityId))
        {
            throw new ArgumentException(
                $"'{entityId}' is the unset value for its key type, so it addresses no subject. " +
                "Save the row first, then write its translations.", nameof(entityId));
        }

        return new TranslationSubject(SubjectName(entityName), EntityKey.ToKeyString(entityId), translations, catalog);
    }

    public ITranslationSubjects ForMany(string entityName, IReadOnlyCollection<object> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);

        // Unset ids address nothing and duplicates would only widen the IN clause: both are
        // dropped rather than refused, because a page of rows is a normal thing to hand in.
        var keys = entityIds
            .Where(id => id is not null && !EntityKey.IsUnset(id))
            .Select(EntityKey.ToKeyString)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new TranslationSubjects(SubjectName(entityName), keys, translations);
    }

    /// <summary>
    /// A usable entity name: the length limit is checked here rather than left to the database,
    /// where the error would name a column instead of the call that made the mistake.
    /// </summary>
    private static string SubjectName(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);

        var trimmed = entityName.Trim();
        if (trimmed.Length > MultiLingualMetadata.MaxEntityNameLength)
        {
            throw new ArgumentException(
                $"Entity name '{trimmed[..32]}…' is {trimmed.Length} characters long; translation " +
                $"entity names hold at most {MultiLingualMetadata.MaxEntityNameLength}.", nameof(entityName));
        }

        return trimmed;
    }

    public Task LocalizeAsync(IEnumerable<object> entities, CancellationToken ct = default) =>
        translations.HydrateAsync(entities, ct);

    public Task LocalizeAsync(IEnumerable<object> entities, string languageCode, CancellationToken ct = default) =>
        translations.HydrateAsync(entities, languageCode, ct);

    public void Localize(IEnumerable<object> entities) => translations.Hydrate(entities);

    public void Localize(IEnumerable<object> entities, string languageCode) => translations.Hydrate(entities, languageCode);
}

/// <summary>Default <see cref="ILanguageAccessor"/>: the catalog for reads, the manager for writes.</summary>
internal sealed class LanguageAccessor(
    ILanguageCatalog catalog,
    ILanguageManager manager,
    MultiLingualOptions options) : ILanguageAccessor
{
    public Task<IReadOnlyList<LanguageInfo>> GetAllAsync(CancellationToken ct = default) => manager.GetAllAsync(ct);

    public Task<LanguageInfo> GetAsync(string code, CancellationToken ct = default) => manager.GetAsync(code, ct);

    public Task<LanguageInfo?> FindAsync(string? code, CancellationToken ct = default) => catalog.FindByCodeAsync(code, ct);

    public Task<LanguageInfo?> FindActiveAsync(string? code, CancellationToken ct = default) => catalog.FindActiveByCodeAsync(code, ct);

    public Task<LanguageInfo> GetDefaultAsync(CancellationToken ct = default) => catalog.GetDefaultAsync(ct);

    public string? BaseLanguage => options.BaseLanguage;

    public bool IsBaseLanguage(string? code) => options.IsBaseLanguage(code?.Trim());

    public void InvalidateCache() => catalog.Invalidate();

    public Task<LanguageInfo> CreateAsync(NewLanguage language, CancellationToken ct = default) => manager.CreateAsync(language, ct);

    public Task<LanguageInfo> UpdateAsync(string code, LanguageUpdate update, CancellationToken ct = default) =>
        manager.UpdateAsync(code, update, ct);

    public Task DeleteAsync(string code, CancellationToken ct = default) => manager.DeleteAsync(code, ct);

    public Task<LanguageInfo> SetDefaultAsync(string code, CancellationToken ct = default) => manager.SetDefaultAsync(code, ct);

    public Task<int> PurgeTranslationsAsync(string code, CancellationToken ct = default) => manager.PurgeTranslationsAsync(code, ct);
}

/// <summary>Default <see cref="ITranslationAccessor{T}"/>, over <see cref="ITranslationManager"/>.</summary>
internal sealed class TranslationAccessor<T>(
    T entity,
    ITranslationManager translations,
    ILanguageCatalog catalog,
    MultiLingualOptions options) : ITranslationAccessor<T>
    where T : class
{
    public T Entity => entity;

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(CancellationToken ct = default) =>
        translations.GetAllAsync(entity, ct);

    public Task LocalizeAsync(CancellationToken ct = default) => translations.HydrateAsync([entity], ct);

    public Task LocalizeAsync(string languageCode, CancellationToken ct = default) =>
        translations.HydrateAsync([entity], languageCode, ct);

    public ITranslationAccessor<T> Set(string languageCode, Expression<Func<T, string?>> property, string? value) =>
        Set(languageCode, new Dictionary<string, string?>(StringComparer.Ordinal) { [PropertyName(property)] = value });

    public ITranslationAccessor<T> Set(string languageCode, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        translations.Stage(entity, Translatable(languageCode), values);
        return this;
    }

    public ITranslationAccessor<T> Clear(string languageCode)
    {
        translations.Clear(entity, Translatable(languageCode));
        return this;
    }

    public ITranslationAccessor<T> Clear(string languageCode, params Expression<Func<T, string?>>[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return Clear(languageCode, properties.Select(PropertyName));
    }

    public ITranslationAccessor<T> Clear(string languageCode, IEnumerable<string> propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);

        var names = propertyNames.ToArray();

        // An empty list would mean "every property" to the engine; here it means "none".
        if (names.Length == 0)
            return this;

        translations.Clear(entity, Translatable(languageCode), names);
        return this;
    }

    public Task SaveAsync(CancellationToken ct = default) => translations.SaveAsync(entity, ct);

    public Task DeleteAsync(CancellationToken ct = default) => translations.DeleteAsync(entity, ct);

    public ITranslationAccessor<T> MarkAsBaseLanguageValues(params Expression<Func<T, string?>>[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        translations.MarkAsBaseLanguageValues(entity, properties.Select(PropertyName).ToArray());
        return this;
    }

    /// <summary>
    /// The canonical code of an existing, non-base language — checked now, so the caller hears
    /// about a mistake where it made it rather than from a later <c>SaveChanges</c>.
    /// </summary>
    private string Translatable(string languageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        var trimmed  = languageCode.Trim();
        var language = catalog.GetAll().FirstOrDefault(l => string.Equals(l.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                       ?? throw new LanguageNotFoundException(trimmed);

        if (options.IsBaseLanguage(language.Code))
            throw new BaseLanguageNotTranslatableException(MultiLingualMetadata.EntityNameFor(entity.GetType()), language.Code);

        return language.Code;
    }

    private static string PropertyName(Expression<Func<T, string?>> property)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (property.Body is not MemberExpression { Member: PropertyInfo member } access
            || access.Expression != property.Parameters[0])
        {
            throw new ArgumentException(
                "Pass a translatable property of the entity itself, such as p => p.Name.", nameof(property));
        }

        return member.Name;
    }
}

/// <summary>
/// Default <see cref="ITranslationSubject"/>. Queues values in its own buffer rather than the
/// scope's write buffer: that one is keyed by entity instance, and a subject has none.
/// </summary>
internal sealed class TranslationSubject(
    string entityName,
    string entityId,
    ITranslationManager translations,
    ILanguageCatalog catalog) : ITranslationSubject
{
    private readonly Dictionary<string, Dictionary<string, string?>> _queued = new(StringComparer.OrdinalIgnoreCase);

    public string EntityName => entityName;

    public string EntityId => entityId;

    public async Task<IReadOnlyDictionary<string, string?>> GetAsync(CancellationToken ct = default)
    {
        var resolved = await translations.ResolveSubjectsAsync(entityName, [entityId], ct);
        return resolved.TryGetValue(entityId, out var values) ? values : NoValues;
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(CancellationToken ct = default) =>
        translations.GetAllAsync(entityName, entityId, ct);

    public ITranslationSubject Set(string languageCode, string propertyName, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return Set(languageCode, new Dictionary<string, string?>(StringComparer.Ordinal) { [propertyName] = value });
    }

    public ITranslationSubject Set(string languageCode, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var code = Existing(languageCode);

        if (!_queued.TryGetValue(code, out var queued))
        {
            queued = new Dictionary<string, string?>(StringComparer.Ordinal);
            _queued[code] = queued;
        }

        foreach (var (propertyName, value) in values)
            queued[propertyName] = value;

        return this;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_queued.Count == 0)
            return;

        var batch = _queued.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(kv.Value, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

        await translations.WriteSubjectAsync(entityName, entityId, batch, ct);

        // Only once the write succeeded: a failed save leaves the values queued, so the caller
        // can retry without reassembling them.
        _queued.Clear();
    }

    public Task<int> DeleteAsync(CancellationToken ct = default)
    {
        _queued.Clear();
        return translations.DeleteEntitiesAsync(entityName, [entityId], ct);
    }

    private static readonly IReadOnlyDictionary<string, string?> NoValues = new Dictionary<string, string?>();

    /// <summary>
    /// The canonical code of an existing language — checked now, so the caller hears about a
    /// mistake where it made it rather than from a later <see cref="SaveAsync"/>. Unlike
    /// <see cref="ITranslationAccessor{T}"/>, the base language is allowed: a subject has no
    /// columns, so every language it carries is a translation row.
    /// </summary>
    private string Existing(string languageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        var trimmed = languageCode.Trim();
        return catalog.GetAll().FirstOrDefault(l => string.Equals(l.Code, trimmed, StringComparison.OrdinalIgnoreCase))?.Code
               ?? throw new LanguageNotFoundException(trimmed);
    }
}

/// <summary>Default <see cref="ITranslationSubjects"/>: one read, one delete, over many ids.</summary>
internal sealed class TranslationSubjects(
    string entityName,
    IReadOnlyCollection<string> entityIds,
    ITranslationManager translations) : ITranslationSubjects
{
    public string EntityName => entityName;

    public IReadOnlyCollection<string> EntityIds => entityIds;

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAsync(CancellationToken ct = default) =>
        translations.ResolveSubjectsAsync(entityName, entityIds, ct);

    public Task<int> DeleteAsync(CancellationToken ct = default) =>
        translations.DeleteEntitiesAsync(entityName, entityIds, ct);
}
