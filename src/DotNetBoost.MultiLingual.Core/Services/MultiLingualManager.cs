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
