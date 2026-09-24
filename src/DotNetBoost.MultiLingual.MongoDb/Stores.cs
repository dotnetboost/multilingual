using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using MongoDB.Driver;

namespace DotNetBoost.MultiLingual.MongoDb;

/// <summary>
/// MongoDB <see cref="ITranslationStore"/>. One document per property, per entity instance, per
/// language, identified by the natural key that
/// <see cref="MongoIndexInitializer"/> makes unique.
/// <para>
/// Every operation runs in <see cref="WithSession"/>'s session when it has one, so translations
/// join the transaction the application is already saving its entity in. Without a session each
/// operation stands alone — see <c>WithSession</c> for what that costs.
/// </para>
/// </summary>
public sealed class MongoTranslationStore : ITranslationStore
{
    private readonly IMongoCollection<Translation> _collection;
    private readonly IClientSessionHandle? _session;

    /// <summary>Creates a store over the translations collection named by <paramref name="options"/>.</summary>
    /// <param name="database">The database holding the collection.</param>
    /// <param name="options">Collection naming.</param>
    /// <param name="session">The session to enlist in, if the application has one.</param>
    public MongoTranslationStore(IMongoDatabase database, MongoDbOptions options, IClientSessionHandle? session = null)
        : this(Collection(database, options), session)
    {
    }

    private MongoTranslationStore(IMongoCollection<Translation> collection, IClientSessionHandle? session)
    {
        _collection = collection;
        _session    = session;
    }

    internal static IMongoCollection<Translation> Collection(IMongoDatabase database, MongoDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        return database.GetCollection<Translation>(options.TranslationsCollection);
    }

    /// <summary>
    /// The same store, writing through <paramref name="session"/>. Pass it to
    /// <see cref="ITranslationWriter.WithStore"/> to put an entity and its translations in one
    /// transaction:
    /// <code>
    /// using var session = await client.StartSessionAsync(ct);
    /// session.StartTransaction();
    /// await products.InsertOneAsync(session, product, cancellationToken: ct);
    /// await writer.WithStore(store.WithSession(session)).WriteAsync(writes, ct);
    /// await session.CommitTransactionAsync(ct);
    /// </code>
    /// A transaction needs a replica set or a sharded cluster; against a standalone <c>mongod</c>
    /// there is nothing to enlist in and each write stands on its own.
    /// </summary>
    /// <param name="session">The session to enlist in.</param>
    public MongoTranslationStore WithSession(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new MongoTranslationStore(_collection, session);
    }

    private static FilterDefinition<Translation> Rows(string entityName, IReadOnlyCollection<string> entityIds) =>
        Builders<Translation>.Filter.And(
            Builders<Translation>.Filter.Eq(t => t.EntityName, entityName),
            Builders<Translation>.Filter.In(t => t.EntityId, entityIds));

    private static FilterDefinition<Translation> Rows(
        string entityName, IReadOnlyCollection<string> entityIds, IReadOnlyCollection<string> languageCodes) =>
        Builders<Translation>.Filter.And(
            Rows(entityName, entityIds),
            Builders<Translation>.Filter.In(t => t.LanguageCode, languageCodes));

    /// <summary>The natural key of one row, which the unique index makes a single document.</summary>
    private static FilterDefinition<Translation> Row(Translation row) =>
        Builders<Translation>.Filter.And(
            Builders<Translation>.Filter.Eq(t => t.EntityName, row.EntityName),
            Builders<Translation>.Filter.Eq(t => t.EntityId, row.EntityId),
            Builders<Translation>.Filter.Eq(t => t.PropertyName, row.PropertyName),
            Builders<Translation>.Filter.Eq(t => t.LanguageCode, row.LanguageCode));

    private IFindFluent<Translation, Translation> Find(FilterDefinition<Translation> filter) =>
        _session is null ? _collection.Find(filter) : _collection.Find(_session, filter);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Translation>> GetAsync(
        string entityName,
        IReadOnlyCollection<string> entityIds,
        IReadOnlyCollection<string> languageCodes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(languageCodes);

        if (entityIds.Count == 0 || languageCodes.Count == 0)
            return [];

        return await Find(Rows(entityName, entityIds, languageCodes)).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IReadOnlyList<Translation> GetMany(
        string entityName,
        IReadOnlyCollection<string> entityIds,
        IReadOnlyCollection<string> languageCodes)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(languageCodes);

        if (entityIds.Count == 0 || languageCodes.Count == 0)
            return [];

        return Find(Rows(entityName, entityIds, languageCodes)).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Translation>> GetAllAsync(string entityName, string entityId, CancellationToken ct = default) =>
        await Find(Rows(entityName, [entityId])).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task UpsertManyAsync(IReadOnlyCollection<Translation> translations, CancellationToken ct = default)
    {
        var writes = WriteModels(translations);
        if (writes.Count == 0)
            return;

        if (_session is null)
            await _collection.BulkWriteAsync(writes, cancellationToken: ct).ConfigureAwait(false);
        else
            await _collection.BulkWriteAsync(_session, writes, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void UpsertMany(IReadOnlyCollection<Translation> translations)
    {
        var writes = WriteModels(translations);
        if (writes.Count == 0)
            return;

        if (_session is null)
            _collection.BulkWrite(writes);
        else
            _collection.BulkWrite(_session, writes);
    }

    /// <summary>
    /// One write per natural key: a replace-with-upsert for a value, a delete for <c>null</c>
    /// ("no translation" is a missing document, never a stored null). The batch is folded first so
    /// a key it repeats becomes a single write with the LAST row's outcome — two writes for one
    /// key in one bulk operation would otherwise race each other.
    /// </summary>
    private static List<WriteModel<Translation>> WriteModels(IReadOnlyCollection<Translation> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);

        var folded = new Dictionary<(string, string, string, string), Translation>();
        foreach (var row in translations)
            folded[(row.EntityName, row.EntityId, row.PropertyName, row.LanguageCode)] = row;

        var writes = new List<WriteModel<Translation>>(folded.Count);
        foreach (var row in folded.Values)
        {
            writes.Add(row.Value is null
                ? new DeleteOneModel<Translation>(Row(row))
                // A copy: the caller reuses its instances, and the driver serializes on write.
                : new ReplaceOneModel<Translation>(Row(row), Copy(row)) { IsUpsert = true });
        }

        return writes;
    }

    /// <inheritdoc/>
    public async Task<int> DeleteEntitiesAsync(
        string entityName, IReadOnlyCollection<string> entityIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0)
            return 0;

        var result = _session is null
            ? await _collection.DeleteManyAsync(Rows(entityName, entityIds), ct).ConfigureAwait(false)
            : await _collection.DeleteManyAsync(_session, Rows(entityName, entityIds), cancellationToken: ct).ConfigureAwait(false);

        return (int)result.DeletedCount;
    }

    /// <inheritdoc/>
    public int DeleteEntities(string entityName, IReadOnlyCollection<string> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0)
            return 0;

        var result = _session is null
            ? _collection.DeleteMany(Rows(entityName, entityIds))
            : _collection.DeleteMany(_session, Rows(entityName, entityIds));

        return (int)result.DeletedCount;
    }

    /// <inheritdoc/>
    public async Task<int> CountByLanguageAsync(string languageCode, CancellationToken ct = default)
    {
        var filter = Builders<Translation>.Filter.Eq(t => t.LanguageCode, languageCode);

        var count = _session is null
            ? await _collection.CountDocumentsAsync(filter, cancellationToken: ct).ConfigureAwait(false)
            : await _collection.CountDocumentsAsync(_session, filter, cancellationToken: ct).ConfigureAwait(false);

        return (int)count;
    }

    /// <inheritdoc/>
    public async Task<int> DeleteByLanguageAsync(string languageCode, CancellationToken ct = default)
    {
        var filter = Builders<Translation>.Filter.Eq(t => t.LanguageCode, languageCode);

        var result = _session is null
            ? await _collection.DeleteManyAsync(filter, ct).ConfigureAwait(false)
            : await _collection.DeleteManyAsync(_session, filter, cancellationToken: ct).ConfigureAwait(false);

        return (int)result.DeletedCount;
    }

    private static Translation Copy(Translation row) => new()
    {
        EntityName   = row.EntityName,
        EntityId     = row.EntityId,
        PropertyName = row.PropertyName,
        LanguageCode = row.LanguageCode,
        Value        = row.Value
    };
}

/// <summary>
/// MongoDB <see cref="ILanguageStore"/>. <see cref="Language.Code"/> is the document's <c>_id</c>,
/// so the list is unique and indexed by code without an index of its own.
/// </summary>
public sealed class MongoLanguageStore : ILanguageStore
{
    private readonly IMongoCollection<Language> _collection;
    private readonly IClientSessionHandle? _session;

    /// <summary>Creates a store over the languages collection named by <paramref name="options"/>.</summary>
    /// <param name="database">The database holding the collection.</param>
    /// <param name="options">Collection naming.</param>
    /// <param name="session">The session to enlist in, if the application has one.</param>
    public MongoLanguageStore(IMongoDatabase database, MongoDbOptions options, IClientSessionHandle? session = null)
        : this(Collection(database, options), session)
    {
    }

    private MongoLanguageStore(IMongoCollection<Language> collection, IClientSessionHandle? session)
    {
        _collection = collection;
        _session    = session;
    }

    internal static IMongoCollection<Language> Collection(IMongoDatabase database, MongoDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        return database.GetCollection<Language>(options.LanguagesCollection);
    }

    /// <summary>The same store, writing through <paramref name="session"/>.</summary>
    /// <param name="session">The session to enlist in.</param>
    public MongoLanguageStore WithSession(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new MongoLanguageStore(_collection, session);
    }

    private static FilterDefinition<Language> ByCode(string code) =>
        Builders<Language>.Filter.Eq(l => l.Code, code);

    private IFindFluent<Language, Language> Find(FilterDefinition<Language> filter) =>
        _session is null ? _collection.Find(filter) : _collection.Find(_session, filter);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default) =>
        await Find(Builders<Language>.Filter.Empty).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public IReadOnlyList<Language> GetAll() => Find(Builders<Language>.Filter.Empty).ToList();

    /// <inheritdoc/>
    public async Task<Language?> FindAsync(string code, CancellationToken ct = default) =>
        await Find(ByCode(code)).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task AddAsync(Language language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(language);

        // Insert FIRST, then demote. A duplicate code fails before anything else is touched, and
        // the window between the two statements holds two defaults rather than none — the engine
        // reads a list with two defaults, but a list with none throws NoLanguageConfiguredException.
        // In a transaction (see WithSession) there is no window at all.
        if (_session is null)
            await _collection.InsertOneAsync(Copy(language), cancellationToken: ct).ConfigureAwait(false);
        else
            await _collection.InsertOneAsync(_session, Copy(language), cancellationToken: ct).ConfigureAwait(false);

        if (language.IsDefault)
            await DemoteOthersAsync(language.Code, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(Language language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(language);

        // IsDefault is deliberately absent: moving the default is SetDefaultAsync's job.
        var update = Builders<Language>.Update
            .Set(l => l.Name, language.Name)
            .Set(l => l.Direction, language.Direction)
            .Set(l => l.IsActive, language.IsActive);

        var result = _session is null
            ? await _collection.UpdateOneAsync(ByCode(language.Code), update, cancellationToken: ct).ConfigureAwait(false)
            : await _collection.UpdateOneAsync(_session, ByCode(language.Code), update, cancellationToken: ct).ConfigureAwait(false);

        if (result.MatchedCount == 0)
            throw new LanguageNotFoundException(language.Code);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string code, CancellationToken ct = default)
    {
        if (_session is null)
            await _collection.DeleteOneAsync(ByCode(code), ct).ConfigureAwait(false);
        else
            await _collection.DeleteOneAsync(_session, ByCode(code), cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetDefaultAsync(string code, CancellationToken ct = default)
    {
        var promote = Builders<Language>.Update.Set(l => l.IsDefault, true);

        var result = _session is null
            ? await _collection.UpdateOneAsync(ByCode(code), promote, cancellationToken: ct).ConfigureAwait(false)
            : await _collection.UpdateOneAsync(_session, ByCode(code), promote, cancellationToken: ct).ConfigureAwait(false);

        // Nothing has been demoted yet, so an unknown code leaves the list exactly as it was.
        if (result.MatchedCount == 0)
            throw new LanguageNotFoundException(code);

        await DemoteOthersAsync(code, ct).ConfigureAwait(false);
    }

    /// <summary>Clears the default flag on every language except <paramref name="code"/>.</summary>
    private async Task DemoteOthersAsync(string code, CancellationToken ct)
    {
        var others = Builders<Language>.Filter.And(
            Builders<Language>.Filter.Eq(l => l.IsDefault, true),
            Builders<Language>.Filter.Ne(l => l.Code, code));

        var demote = Builders<Language>.Update.Set(l => l.IsDefault, false);

        if (_session is null)
            await _collection.UpdateManyAsync(others, demote, cancellationToken: ct).ConfigureAwait(false);
        else
            await _collection.UpdateManyAsync(_session, others, demote, cancellationToken: ct).ConfigureAwait(false);
    }

    private static Language Copy(Language language) => new()
    {
        Code      = language.Code,
        Name      = language.Name,
        Direction = language.Direction,
        IsDefault = language.IsDefault,
        IsActive  = language.IsActive
    };
}
