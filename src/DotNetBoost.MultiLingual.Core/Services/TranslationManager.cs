using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>
/// Default <see cref="ITranslationManager"/>. Holds no state of its own: staged values and
/// hydration tracking live in scoped helpers, which is what lets <see cref="WithStore"/> hand
/// out a sibling bound to another store without either losing track of the other's work.
/// </summary>
internal sealed partial class TranslationManager(
    ITranslationStore store,
    ILanguageCatalog catalog,
    ILanguageContext languageContext,
    IEntityKeyAccessor keys,
    TranslationWriteBuffer writeBuffer,
    HydratedValueTracker tracker,
    MultiLingualOptions options,
    ILogger<TranslationManager> logger) : ITranslationManager
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> NoValues =
        new Dictionary<string, IReadOnlyDictionary<string, string?>>();

    public ITranslationManager WithStore(ITranslationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new TranslationManager(store, catalog, languageContext, keys, writeBuffer, tracker, options, logger);
    }

    // ================================================================== read

    public async Task HydrateAsync(IEnumerable<object> entities, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var (language, fallback) = languageContext.IsResolved
            ? (languageContext.Current.Code, languageContext.DefaultLanguage.Code)
            : await DefaultPairAsync(ct);

        await HydrateCoreAsync(entities, language, fallback, ct);
    }

    public async Task HydrateAsync(IEnumerable<object> entities, string languageCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var language = await catalog.FindByCodeAsync(languageCode, ct)
                       ?? throw new LanguageNotFoundException(languageCode);
        var fallback = await catalog.GetDefaultAsync(ct);

        await HydrateCoreAsync(entities, language.Code, fallback.Code, ct);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(
        object entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entityId = keys.GetKey(entity);
        if (entityId is null)
            return NoValues;

        var rows = await store.GetAllAsync(MultiLingualMetadata.EntityNameFor(entity.GetType()), entityId, ct);

        return rows
            .GroupBy(r => r.LanguageCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string?>)g.ToDictionary(r => r.PropertyName, r => r.Value, StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<(string Language, string Fallback)> DefaultPairAsync(CancellationToken ct)
    {
        var defaultLanguage = await catalog.GetDefaultAsync(ct);
        return (defaultLanguage.Code, defaultLanguage.Code);
    }

    private async Task HydrateCoreAsync(IEnumerable<object> entities, string language, string fallback, CancellationToken ct)
    {
        // Serving the base language itself ends the chain immediately: its value is the column the
        // entity was loaded with, and it is authoritative. Without this, asking for the base
        // language while some OTHER language is the default would fall through to that default
        // and answer an English request with Arabic.
        var servingBase = options.IsBaseLanguage(language);

        var useFallback = !servingBase
                          && options.FallbackToDefaultLanguage
                          && !string.Equals(language, fallback, StringComparison.OrdinalIgnoreCase);

        // In base-language mode the base value is already loaded — it is a column on the entity's
        // own table — so it is never fetched. Leaving the materialized value in place IS the last
        // step of the chain, which also means a request for the base language reads nothing at all.
        var codes = (useFallback ? new[] { language, fallback } : [language])
            .Where(code => !options.IsBaseLanguage(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var byType = entities
            .Where(e => e is not null && MultiLingualMetadata.IsMultiLingual(e.GetType()))
            .GroupBy(e => e.GetType());

        foreach (var group in byType)
            await HydrateTypeAsync(group.Key, group, language, useFallback ? fallback : null, codes, ct);
    }

    private async Task HydrateTypeAsync(
        Type clrType,
        IEnumerable<object> entities,
        string language,
        string? fallback,
        string[] codes,
        CancellationToken ct)
    {
        var properties = MultiLingualMetadata.For(clrType);
        var entityName = MultiLingualMetadata.EntityNameFor(clrType);

        // key -> instances (one query can return the same row as several untracked objects)
        var byKey = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var key = keys.GetKey(entity);
            if (key is null)
                continue;

            if (!byKey.TryGetValue(key, out var list))
            {
                list = [];
                byKey[key] = list;
            }

            list.Add(entity);
        }

        if (byKey.Count == 0)
            return;

        var requested = new Dictionary<(string EntityId, string PropertyName), string?>();
        var fallbacks = new Dictionary<(string EntityId, string PropertyName), string?>();

        // Only the requested language and the fallback are fetched — never every language. With
        // nothing left to fetch (base-language mode serving the base language) there is no query.
        if (codes.Length > 0)
        {
            var rows = await store.GetAsync(entityName, byKey.Keys, codes, ct);

            foreach (var row in rows)
            {
                var key = (row.EntityId, row.PropertyName);

                if (string.Equals(row.LanguageCode, language, StringComparison.OrdinalIgnoreCase))
                    requested[key] = row.Value;
                else if (fallback is not null && string.Equals(row.LanguageCode, fallback, StringComparison.OrdinalIgnoreCase))
                    fallbacks[key] = row.Value;
            }
        }

        var baseLanguage = options.BaseLanguage;

        foreach (var (entityId, instances) in byKey)
        {
            foreach (var property in properties)
            {
                var key = (entityId, property.Name);

                // Requested language, then default language, then the base language's column,
                // then null.
                string? value = null;
                string? servedFrom = null;
                var isFallback = false;
                var keepColumnValue = false;

                if (requested.TryGetValue(key, out var own) && own is not null)
                {
                    value      = own;
                    servedFrom = language;
                }
                else if (fallback is not null && fallbacks.TryGetValue(key, out var borrowed) && borrowed is not null)
                {
                    value      = borrowed;
                    servedFrom = fallback;
                    isFallback = true;
                }
                else if (baseLanguage is not null)
                {
                    // The base language is the column the entity was loaded with: keep it as it
                    // is. Writing it back is what the tracker below exists to prevent.
                    keepColumnValue = true;
                    servedFrom      = baseLanguage;
                    isFallback      = !string.Equals(language, baseLanguage, StringComparison.OrdinalIgnoreCase);
                }

                foreach (var instance in instances)
                {
                    string? effective;
                    if (keepColumnValue)
                    {
                        effective = property.GetValue(instance);
                    }
                    else
                    {
                        property.SetValue(instance, value);
                        effective = value;
                    }

                    if (servedFrom is null)
                        tracker.Forget(instance, property.Name);
                    else
                        tracker.Mark(instance, property.Name, effective, servedFrom, isFallback);
                }
            }
        }
    }

    // ================================================================== write

    public void Stage(object entity, string languageCode, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        ArgumentNullException.ThrowIfNull(values);

        if (options.IsBaseLanguage(languageCode))
        {
            throw new ArgumentException(
                $"'{languageCode}' is the base language: its values are stored in " +
                $"{entity.GetType().Name}'s own table, so they are written by assigning the " +
                "properties and saving the entity, not by staging them.", nameof(languageCode));
        }

        var translatable = MultiLingualMetadata.For(entity.GetType());
        foreach (var propertyName in values.Keys)
        {
            if (!translatable.Any(p => string.Equals(p.Name, propertyName, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"'{entity.GetType().Name}.{propertyName}' is not a translatable property.", nameof(values));
            }
        }

        writeBuffer.Stage(entity, languageCode.Trim(), values);
    }

    public void MarkAsBaseLanguageValues(object entity, IEnumerable<string> propertyNames)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(propertyNames);

        if (options.BaseLanguage is not { } baseLanguage)
            return;

        var translatable = MultiLingualMetadata.For(entity.GetType());

        foreach (var propertyName in propertyNames)
        {
            var property = translatable.FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.Ordinal));
            if (property is null)
                continue;

            // isFallback: the value is on loan from the base language, so writing it in any other
            // language is exactly what the tracker refuses.
            tracker.Mark(entity, property.Name, property.GetValue(entity), baseLanguage, isFallback: true);
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> CaptureValues(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var translatable = MultiLingualMetadata.For(entity.GetType());
        if (translatable.Count == 0)
            return NoValues;

        var byLanguage = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

        // The in-memory property values belong to the authoring language: the current one.
        // With no language resolved (a seeder, say) only explicitly staged values are written.
        //
        // In base-language mode the base language is NOT one of them: its values belong in the
        // entity's own columns, which the provider writes as part of the entity itself. Storing
        // them here as well would give the same text two homes, and they would drift.
        if (languageContext.IsResolved && !options.IsBaseLanguage(languageContext.Current.Code))
        {
            var authoring = languageContext.Current.Code;
            var authored  = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var property in translatable)
            {
                var value = property.GetValue(entity);

                // null means "not supplied" — leave whatever is stored alone.
                if (value is null || tracker.IsUnchanged(entity, property.Name, value, authoring))
                    continue;

                authored[property.Name] = value;
            }

            if (authored.Count > 0)
                byLanguage[authoring] = authored;
        }

        if (writeBuffer.Get(entity) is { } staged)
        {
            foreach (var (languageCode, values) in staged)
            {
                if (!byLanguage.TryGetValue(languageCode, out var target))
                {
                    target = new Dictionary<string, string?>(StringComparer.Ordinal);
                    byLanguage[languageCode] = target;
                }

                foreach (var (propertyName, value) in values)
                    target[propertyName] = value;
            }
        }

        return byLanguage.Count == 0
            ? NoValues
            : byLanguage.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, string?>)kv.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task WriteAsync(IReadOnlyCollection<TranslationWrite> writes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return;

        var languages = await catalog.GetAllAsync(ct);
        var canonical = languages.ToDictionary(l => l.Code, l => l.Code, StringComparer.OrdinalIgnoreCase);

        var rows = new List<Translation>();
        foreach (var write in writes)
        {
            foreach (var (languageCode, values) in write.ValuesByLanguage)
            {
                // Loud, not silent: a value for a language nobody can read would be lost content.
                if (!canonical.TryGetValue(languageCode, out var code))
                    throw new LanguageNotFoundException(languageCode);

                foreach (var (propertyName, value) in values)
                {
                    rows.Add(new Translation
                    {
                        EntityName   = write.EntityName,
                        EntityId     = write.EntityId,
                        PropertyName = propertyName,
                        LanguageCode = code,
                        Value        = value
                    });
                }
            }
        }

        if (rows.Count > 0)
            await store.UpsertManyAsync(rows, ct);

        foreach (var write in writes)
        {
            if (write.Source is null)
                continue;

            writeBuffer.Remove(write.Source);
            RememberPersisted(write);
        }
    }

    /// <summary>
    /// What was just written for the authoring language is now what the store holds, so a
    /// second save of the same untouched instance has nothing to write.
    /// </summary>
    private void RememberPersisted(TranslationWrite write)
    {
        if (!languageContext.IsResolved)
            return;

        var authoring = languageContext.Current.Code;
        if (!write.ValuesByLanguage.TryGetValue(authoring, out var values))
            return;

        foreach (var (propertyName, value) in values)
            tracker.Mark(write.Source!, propertyName, value, authoring, isFallback: false);
    }

    public async Task SaveAsync(object entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entityId = keys.GetKey(entity) ?? throw new InvalidOperationException(
            $"'{entity.GetType().Name}' has no key yet. Save the entity itself first, then its translations.");

        var values = CaptureValues(entity);
        if (values.Count == 0)
            return;

        await WriteAsync(
            [new TranslationWrite(MultiLingualMetadata.EntityNameFor(entity.GetType()), entityId, values) { Source = entity }],
            ct);
    }

    public async Task DeleteAsync(object entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entityId = keys.GetKey(entity);
        if (entityId is null)
            return;

        await DeleteEntitiesAsync(MultiLingualMetadata.EntityNameFor(entity.GetType()), [entityId], ct);
    }

    public async Task<int> DeleteEntitiesAsync(
        string entityName, IReadOnlyCollection<string> entityIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(entityIds);

        if (entityIds.Count == 0)
            return 0;

        var removed = await store.DeleteEntitiesAsync(entityName, entityIds, ct);
        LogTranslationsRemoved(removed, entityName, entityIds.Count);
        return removed;
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug,
        Message = "Removed {Count} translation rows for {EntityCount} {EntityName} instance(s).")]
    private partial void LogTranslationsRemoved(int count, string entityName, int entityCount);
}
