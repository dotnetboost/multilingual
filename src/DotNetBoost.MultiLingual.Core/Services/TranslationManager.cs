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
    ILogger<TranslationManager> logger) : ITranslationManager, ITranslationWriter
{
    // A field, not the primary-constructor parameter: the net8.0 [LoggerMessage] generator (from
    // the 8.0 logging packages) only finds a logger held in a field, and writes invalid code for a
    // captured parameter. The parameter is used here, in the initializer, and nowhere else.
    private readonly ILogger<TranslationManager> _logger = logger;

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> NoValues =
        new Dictionary<string, IReadOnlyDictionary<string, string?>>();

    public ITranslationWriter WithStore(ITranslationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new TranslationManager(store, catalog, languageContext, keys, writeBuffer, tracker, options, _logger);
    }

    // ================================================================== read

    public async Task HydrateAsync(IEnumerable<object> entities, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var plan = languageContext.IsResolved
            ? Plan(languageContext.Current.Code, languageContext.DefaultLanguage.Code)
            : Plan(DefaultOf(await catalog.GetAllAsync(ct)));

        foreach (var batch in Batches(entities))
            Apply(batch, plan, plan.Codes.Length > 0 ? await store.GetAsync(batch.EntityName, batch.ByKey.Keys, plan.Codes, ct) : []);
    }

    public async Task HydrateAsync(IEnumerable<object> entities, string languageCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var plan = Plan(await catalog.GetAllAsync(ct), languageCode);

        foreach (var batch in Batches(entities))
            Apply(batch, plan, plan.Codes.Length > 0 ? await store.GetAsync(batch.EntityName, batch.ByKey.Keys, plan.Codes, ct) : []);
    }

    public void Hydrate(IEnumerable<object> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var plan = languageContext.IsResolved
            ? Plan(languageContext.Current.Code, languageContext.DefaultLanguage.Code)
            : Plan(DefaultOf(catalog.GetAll()));

        foreach (var batch in Batches(entities))
            Apply(batch, plan, plan.Codes.Length > 0 ? store.GetMany(batch.EntityName, batch.ByKey.Keys, plan.Codes) : []);
    }

    public void Hydrate(IEnumerable<object> entities, string languageCode)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var plan = Plan(catalog.GetAll(), languageCode);

        foreach (var batch in Batches(entities))
            Apply(batch, plan, plan.Codes.Length > 0 ? store.GetMany(batch.EntityName, batch.ByKey.Keys, plan.Codes) : []);
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

    // ------------------------------------------------------------------ hydration, shared by both paths

    /// <summary>Which language to serve, what it falls back to, and which of them to fetch.</summary>
    private sealed record HydrationPlan(string Language, string? Fallback, string[] Codes);

    /// <summary>The instances of one type to hydrate, grouped by key.</summary>
    private sealed record Batch(Type ClrType, string EntityName, Dictionary<string, List<object>> ByKey);

    private static LanguageInfo DefaultOf(IReadOnlyList<LanguageInfo> languages) =>
        languages.FirstOrDefault(l => l.IsDefault) ?? throw new NoLanguageConfiguredException();

    private HydrationPlan Plan(LanguageInfo defaultLanguage) => Plan(defaultLanguage.Code, defaultLanguage.Code);

    private HydrationPlan Plan(IReadOnlyList<LanguageInfo> languages, string languageCode)
    {
        var trimmed  = languageCode?.Trim();
        var language = languages.FirstOrDefault(l => string.Equals(l.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                       ?? throw new UnknownLanguageException(languageCode ?? "", "reading translations");

        return Plan(language.Code, DefaultOf(languages).Code);
    }

    private HydrationPlan Plan(string language, string fallback)
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

        return new HydrationPlan(language, useFallback ? fallback : null, codes);
    }

    /// <summary>
    /// The translatable entities, one batch per type — one query each — keyed so that a query
    /// returning the same row as several untracked objects hydrates them all. Entities without a
    /// key yet are skipped.
    /// </summary>
    private IEnumerable<Batch> Batches(IEnumerable<object> entities)
    {
        var byType = entities
            .Where(e => e is not null && MultiLingualMetadata.IsMultiLingual(e.GetType()))
            .GroupBy(e => e.GetType());

        foreach (var group in byType)
        {
            var byKey = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (var entity in group)
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

            if (byKey.Count > 0)
                yield return new Batch(group.Key, MultiLingualMetadata.EntityNameFor(group.Key), byKey);
        }
    }

    /// <summary>Applies the fetched <paramref name="rows"/> to <paramref name="batch"/> along the fallback chain.</summary>
    private void Apply(Batch batch, HydrationPlan plan, IReadOnlyList<Translation> rows)
    {
        var properties = MultiLingualMetadata.For(batch.ClrType);
        var language   = plan.Language;
        var fallback   = plan.Fallback;

        var requested = new Dictionary<(string EntityId, string PropertyName), string?>();
        var fallbacks = new Dictionary<(string EntityId, string PropertyName), string?>();

        foreach (var row in rows)
        {
            var rowKey = (row.EntityId, row.PropertyName);

            if (string.Equals(row.LanguageCode, language, StringComparison.OrdinalIgnoreCase))
                requested[rowKey] = row.Value;
            else if (fallback is not null && string.Equals(row.LanguageCode, fallback, StringComparison.OrdinalIgnoreCase))
                fallbacks[rowKey] = row.Value;
        }

        foreach (var (entityId, instances) in batch.ByKey)
        {
            foreach (var property in properties)
            {
                var key = (entityId, property.Name);
                requested.TryGetValue(key, out var own);
                fallbacks.TryGetValue(key, out var borrowed);

                foreach (var instance in instances)
                    Serve(instance, property, own, borrowed, language, fallback);
            }
        }
    }

    /// <summary>
    /// Serves one property of one instance along the chain — requested language, then default
    /// language, then the base language's column, then null — and records where the value came
    /// from, so that saving the instance does not write it back as a translation.
    /// </summary>
    private void Serve(object instance, TranslatableProperty property, string? own, string? borrowed, string language, string? fallback)
    {
        var baseLanguage = options.BaseLanguage;

        string? value = null;
        string? servedFrom = null;
        var isFallback = false;
        var keepColumnValue = false;

        if (own is not null)
        {
            value      = own;
            servedFrom = language;
        }
        else if (fallback is not null && borrowed is not null)
        {
            value      = borrowed;
            servedFrom = fallback;
            isFallback = true;
        }
        else if (baseLanguage is not null)
        {
            // The base language is the column the entity was loaded with: keep it as it is.
            // Writing it back is what the tracker below exists to prevent.
            keepColumnValue = true;
            servedFrom      = baseLanguage;
            isFallback      = !string.Equals(language, baseLanguage, StringComparison.OrdinalIgnoreCase);
        }

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

    // ------------------------------------------------------------------ values loaded by a query

    /// <summary>
    /// The languages a read serves right now: <paramref name="explicitLanguage"/> or the current
    /// one (the default when none is resolved), and the language it falls back to — null when the
    /// fallback is disabled, is the same language, or the requested language is the base one,
    /// whose column already ends the chain.
    /// </summary>
    /// <exception cref="UnknownLanguageException"><paramref name="explicitLanguage"/> does not exist.</exception>
    internal (string Language, string? Fallback) ReadLanguages(string? explicitLanguage = null)
    {
        var plan = explicitLanguage is not null
            ? Plan(catalog.GetAll(), explicitLanguage)
            : languageContext.IsResolved
                ? Plan(languageContext.Current.Code, languageContext.DefaultLanguage.Code)
                : Plan(DefaultOf(catalog.GetAll()));

        return (plan.Language, plan.Fallback);
    }

    /// <summary>
    /// Applies values a query loaded together with <paramref name="entity"/> — for each of
    /// <paramref name="properties"/>, the stored value in <paramref name="language"/> and in
    /// <paramref name="fallback"/> — exactly as hydration would.
    /// <para>
    /// A property is only served while it still holds what the query materialized
    /// (<paramref name="materialized"/>, when the entity is new to this query) or what was served
    /// to it last. Anything else was assigned by the caller since — an entity the query found
    /// already tracked — and is left alone, so a later read cannot overwrite an unsaved edit.
    /// </para>
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="properties">The translatable properties of the type the query declared, which the values follow.</param>
    /// <param name="language">The language the values were read in.</param>
    /// <param name="fallback">The fallback they were read in, or null.</param>
    /// <param name="values">Two per property: requested language, then fallback.</param>
    /// <param name="materialized">The property values as materialized, by name; null when not new to this query.</param>
    internal void ApplyLoaded(
        object entity,
        IReadOnlyList<TranslatableProperty> properties,
        string language,
        string? fallback,
        IReadOnlyList<string?> values,
        IReadOnlyDictionary<string, string?>? materialized)
    {
        for (var i = 0; i < properties.Count && (2 * i) + 1 < values.Count; i++)
        {
            var property = properties[i];
            var current  = property.GetValue(entity);

            var untouched = (materialized is not null
                             && materialized.TryGetValue(property.Name, out var loaded)
                             && string.Equals(current, loaded, StringComparison.Ordinal))
                            || tracker.IsServedValue(entity, property.Name, current);
            if (!untouched)
                continue;

            Serve(entity, property, values[2 * i], values[(2 * i) + 1], language, fallback);
        }
    }

    /// <summary>
    /// True when <paramref name="propertyName"/> still holds what a read served it from a language
    /// other than the base one — a value that must never be written to the base language's column.
    /// </summary>
    internal bool HoldsServedTranslation(object entity, string propertyName, string? currentValue) =>
        options.BaseLanguage is { } baseLanguage
        && tracker.IsServedFromAnotherLanguage(entity, propertyName, currentValue, baseLanguage);

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

    public void Clear(object entity, string languageCode, params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(propertyNames);

        var names = propertyNames.Length > 0
            ? propertyNames
            : MultiLingualMetadata.For(entity.GetType()).Select(p => p.Name).ToArray();

        // A staged null reaches the store as a null row, which the store contract turns into a
        // removal. Stage does the validation: translatable names, and not the base language.
        Stage(entity, languageCode, names.Distinct(StringComparer.Ordinal).ToDictionary(n => n, _ => (string?)null, StringComparer.Ordinal));
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

        var rows = ToRows(writes, await catalog.GetAllAsync(ct));
        if (rows.Count > 0)
            await store.UpsertManyAsync(rows, ct);

        CompleteWrites(writes);
    }

    public void Write(IReadOnlyCollection<TranslationWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return;

        var rows = ToRows(writes, catalog.GetAll());
        if (rows.Count > 0)
            store.UpsertMany(rows);

        CompleteWrites(writes);
    }

    private static List<Translation> ToRows(IReadOnlyCollection<TranslationWrite> writes, IReadOnlyList<LanguageInfo> languages)
    {
        var canonical = languages.ToDictionary(l => l.Code, l => l.Code, StringComparer.OrdinalIgnoreCase);

        var rows = new List<Translation>();
        foreach (var write in writes)
        {
            // Refused here rather than left to the database: SQLite does not enforce the column
            // length at all, and elsewhere the error would name a column, not the entity.
            if (write.EntityId.Length > EntityKey.MaxLength)
            {
                throw new InvalidOperationException(
                    $"'{write.EntityName}' key '{write.EntityId[..16]}…' is {write.EntityId.Length} characters long; " +
                    $"translation keys hold at most {EntityKey.MaxLength}. Use a shorter key for translatable entities.");
            }

            foreach (var (languageCode, values) in write.ValuesByLanguage)
            {
                // Loud, not silent: a value for a language nobody can read would be lost content.
                if (!canonical.TryGetValue(languageCode, out var code))
                    throw new UnknownLanguageException(languageCode, "the values being written");

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

        return rows;
    }

    public void CompleteWrites(IReadOnlyCollection<TranslationWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);

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

    public int DeleteEntities(string entityName, IReadOnlyCollection<string> entityIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(entityIds);

        if (entityIds.Count == 0)
            return 0;

        var removed = store.DeleteEntities(entityName, entityIds);
        LogTranslationsRemoved(removed, entityName, entityIds.Count);
        return removed;
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug,
        Message = "Removed {Count} translation rows for {EntityCount} {EntityName} instance(s).")]
    private partial void LogTranslationsRemoved(int count, string entityName, int entityCount);
}
