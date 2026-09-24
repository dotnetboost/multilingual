using Dapper;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using System.Data;

namespace DotNetBoost.MultiLingual.Dapper;

/// <summary>Parameters of one translation row, and the copy that keeps the caller's instance out of the store.</summary>
internal sealed record TranslationParameters(
    string EntityName, string EntityId, string PropertyName, string LanguageCode, string? Value);

/// <summary>The four fields of a row's natural key, for statements that do not carry a value.</summary>
internal sealed record TranslationKey(string EntityName, string EntityId, string PropertyName, string LanguageCode);

/// <summary>
/// Dapper <see cref="ITranslationStore"/> for SQL Server, PostgreSQL and SQLite. One row per
/// property, per entity instance, per language, identified by the composite primary key
/// (<c>EntityName</c>, <c>EntityId</c>, <c>PropertyName</c>, <c>LanguageCode</c>) — the same
/// schema the EF Core provider maps, so one database serves either provider.
/// <para>
/// Every statement runs on <see cref="WithTransaction"/>'s transaction when it has one, so
/// translations commit or roll back with the entity the application is saving. Without one, a
/// multi-row write takes a transaction of its own for the duration.
/// </para>
/// </summary>
public sealed class DapperTranslationStore : ITranslationStore
{
    private readonly IDbConnection _connection;
    private readonly IDbTransaction? _transaction;
    private readonly DapperStatements _sql;

    /// <summary>Creates a store over <paramref name="connection"/>.</summary>
    /// <param name="connection">
    /// The connection to issue statements on. Its runtime type selects the SQL dialect, unless
    /// <see cref="DapperOptions.Dialect"/> says otherwise.
    /// </param>
    /// <param name="options">Table naming. Defaults to <see cref="DapperOptions"/>'s own defaults.</param>
    /// <param name="transaction">The transaction to enlist in, if the application has one.</param>
    public DapperTranslationStore(IDbConnection connection, DapperOptions? options = null, IDbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection  = transaction?.Connection ?? connection;
        _transaction = transaction;
        _sql         = DapperStatements.For(_connection, options ?? new DapperOptions());
    }

    private DapperTranslationStore(IDbConnection connection, DapperStatements sql, IDbTransaction? transaction)
    {
        _connection  = connection;
        _transaction = transaction;
        _sql         = sql;
    }

    /// <summary>
    /// The same store, writing through <paramref name="transaction"/>. Pass it to
    /// <see cref="ITranslationWriter.WithStore"/> to put an entity and its translations in one
    /// transaction:
    /// <code>
    /// using var transaction = connection.BeginTransaction();
    /// await connection.ExecuteAsync(insertSql, product, transaction);
    /// await writer.WithStore(store.WithTransaction(transaction)).WriteAsync(writes, ct);
    /// transaction.Commit();
    /// </code>
    /// </summary>
    /// <param name="transaction">The transaction to enlist in.</param>
    public DapperTranslationStore WithTransaction(IDbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new DapperTranslationStore(transaction.Connection ?? _connection, _sql, transaction);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Translation>> GetAsync(
        string entityName,
        IReadOnlyCollection<string> entityIds,
        IReadOnlyCollection<string> languageCodes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(languageCodes);

        // Not just an optimisation: Dapper expands an empty list to "IN ()", which is a syntax
        // error on every engine here.
        if (entityIds.Count == 0 || languageCodes.Count == 0)
            return [];

        var rows = await _connection.QueryAsync<Translation>(
            Command(_sql.SelectTranslations, Filter(entityName, entityIds, languageCodes), ct: ct))
            .ConfigureAwait(false);

        return rows.AsList();
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

        return _connection.Query<Translation>(
            Command(_sql.SelectTranslations, Filter(entityName, entityIds, languageCodes)))
            .AsList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Translation>> GetAllAsync(string entityName, string entityId, CancellationToken ct = default)
    {
        var rows = await _connection.QueryAsync<Translation>(
            Command(_sql.SelectTranslationsForEntity, new { EntityName = entityName, EntityId = entityId }, ct: ct))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task UpsertManyAsync(IReadOnlyCollection<Translation> translations, CancellationToken ct = default)
    {
        var (upserts, deletes) = Fold(translations);
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        await UnitOfWork.RunAsync(_connection, _transaction, async transaction =>
        {
            if (deletes.Count > 0)
                await _connection.ExecuteAsync(Command(_sql.DeleteTranslation, deletes, transaction, ct)).ConfigureAwait(false);

            if (upserts.Count > 0)
                await _connection.ExecuteAsync(Command(_sql.UpsertTranslation, upserts, transaction, ct)).ConfigureAwait(false);

            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void UpsertMany(IReadOnlyCollection<Translation> translations)
    {
        var (upserts, deletes) = Fold(translations);
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        UnitOfWork.Run(_connection, _transaction, transaction =>
        {
            if (deletes.Count > 0)
                _connection.Execute(Command(_sql.DeleteTranslation, deletes, transaction: transaction));

            if (upserts.Count > 0)
                _connection.Execute(Command(_sql.UpsertTranslation, upserts, transaction: transaction));

            return 0;
        });
    }

    /// <summary>
    /// Splits a batch into the rows to write and the rows to remove, one outcome per natural key.
    /// The fold is what makes "the later row wins" true — two statements for one key in one batch
    /// would otherwise depend on execution order — and the parameter records are copies, so a
    /// caller that reuses its <see cref="Translation"/> instances cannot change what gets stored.
    /// A <c>null</c> value is a removal: "no translation" is a missing row, never a stored null.
    /// </summary>
    private static (List<TranslationParameters> Upserts, List<TranslationKey> Deletes) Fold(
        IReadOnlyCollection<Translation> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);

        var folded = new Dictionary<(string, string, string, string), Translation>();
        foreach (var row in translations)
            folded[(row.EntityName, row.EntityId, row.PropertyName, row.LanguageCode)] = row;

        var upserts = new List<TranslationParameters>();
        var deletes = new List<TranslationKey>();

        foreach (var row in folded.Values)
        {
            if (row.Value is null)
                deletes.Add(new TranslationKey(row.EntityName, row.EntityId, row.PropertyName, row.LanguageCode));
            else
                upserts.Add(new TranslationParameters(row.EntityName, row.EntityId, row.PropertyName, row.LanguageCode, row.Value));
        }

        return (upserts, deletes);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteEntitiesAsync(string entityName, IReadOnlyCollection<string> entityIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0)
            return 0;

        return await _connection.ExecuteAsync(
            Command(_sql.DeleteEntities, new { EntityName = entityName, EntityIds = entityIds.ToArray() }, ct: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public int DeleteEntities(string entityName, IReadOnlyCollection<string> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0)
            return 0;

        return _connection.Execute(Command(_sql.DeleteEntities, new { EntityName = entityName, EntityIds = entityIds.ToArray() }));
    }

    /// <inheritdoc/>
    public Task<int> CountByLanguageAsync(string languageCode, CancellationToken ct = default) =>
        _connection.ExecuteScalarAsync<int>(Command(_sql.CountByLanguage, new { LanguageCode = languageCode }, ct: ct));

    /// <inheritdoc/>
    public Task<int> DeleteByLanguageAsync(string languageCode, CancellationToken ct = default) =>
        _connection.ExecuteAsync(Command(_sql.DeleteByLanguage, new { LanguageCode = languageCode }, ct: ct));

    /// <summary>
    /// The filter every read shares. The two collections are copied into arrays: Npgsql infers an
    /// array parameter's element type from its runtime type, and a copy keeps the caller's own
    /// collection out of the command.
    /// </summary>
    private static object Filter(string entityName, IReadOnlyCollection<string> entityIds, IReadOnlyCollection<string> languageCodes) =>
        new { EntityName = entityName, EntityIds = entityIds.ToArray(), LanguageCodes = languageCodes.ToArray() };

    private CommandDefinition Command(
        string sql, object? parameters = null, IDbTransaction? transaction = null, CancellationToken ct = default) =>
        new(sql, parameters, transaction ?? _transaction, cancellationToken: ct);
}

/// <summary>Row shape of the Languages table, with <c>Direction</c> as the name it is stored under.</summary>
internal sealed class LanguageRow
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Direction { get; set; } = nameof(TextDirection.Ltr);
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }

    public Language ToLanguage() => new()
    {
        Code      = Code,
        Name      = Name,
        Direction = ParseDirection(),
        IsDefault = IsDefault,
        IsActive  = IsActive
    };

    private TextDirection ParseDirection() =>
        Enum.TryParse<TextDirection>(Direction, ignoreCase: true, out var direction)
            ? direction
            : throw new InvalidOperationException(
                $"Language '{Code}' has direction '{Direction}', which is not a {nameof(TextDirection)}. " +
                $"The column stores the name — '{nameof(TextDirection.Ltr)}' or '{nameof(TextDirection.Rtl)}'.");
}

/// <summary>
/// Dapper <see cref="ILanguageStore"/>. <see cref="Language.Code"/> is the primary key, and the
/// "exactly one default" invariant is kept inside a transaction: promoting one language and
/// demoting the previous default are two statements that must not be separable.
/// </summary>
public sealed class DapperLanguageStore : ILanguageStore
{
    private readonly IDbConnection _connection;
    private readonly IDbTransaction? _transaction;
    private readonly DapperStatements _sql;

    /// <summary>Creates a store over <paramref name="connection"/>.</summary>
    /// <param name="connection">The connection to issue statements on.</param>
    /// <param name="options">Table naming. Defaults to <see cref="DapperOptions"/>'s own defaults.</param>
    /// <param name="transaction">The transaction to enlist in, if the application has one.</param>
    public DapperLanguageStore(IDbConnection connection, DapperOptions? options = null, IDbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection  = transaction?.Connection ?? connection;
        _transaction = transaction;
        _sql         = DapperStatements.For(_connection, options ?? new DapperOptions());
    }

    private DapperLanguageStore(IDbConnection connection, DapperStatements sql, IDbTransaction? transaction)
    {
        _connection  = connection;
        _transaction = transaction;
        _sql         = sql;
    }

    /// <summary>The same store, writing through <paramref name="transaction"/>.</summary>
    /// <param name="transaction">The transaction to enlist in.</param>
    public DapperLanguageStore WithTransaction(IDbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new DapperLanguageStore(transaction.Connection ?? _connection, _sql, transaction);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _connection.QueryAsync<LanguageRow>(Command(_sql.SelectLanguages, ct: ct)).ConfigureAwait(false);
        return rows.Select(static r => r.ToLanguage()).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<Language> GetAll() =>
        _connection.Query<LanguageRow>(Command(_sql.SelectLanguages)).Select(static r => r.ToLanguage()).ToList();

    /// <inheritdoc/>
    public async Task<Language?> FindAsync(string code, CancellationToken ct = default)
    {
        var row = await _connection.QueryFirstOrDefaultAsync<LanguageRow>(
            Command(_sql.SelectLanguage, new { Code = code }, ct: ct)).ConfigureAwait(false);

        return row?.ToLanguage();
    }

    /// <inheritdoc/>
    public async Task AddAsync(Language language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(language);

        var parameters = new
        {
            language.Code,
            language.Name,
            // The name, not the ordinal, so reordering TextDirection cannot reinterpret stored rows.
            Direction = language.Direction.ToString(),
            language.IsDefault,
            language.IsActive
        };

        await UnitOfWork.RunAsync(_connection, _transaction, async transaction =>
        {
            await _connection.ExecuteAsync(Command(_sql.InsertLanguage, parameters, transaction, ct)).ConfigureAwait(false);

            // Inside the transaction, so the list is never seen with two defaults — or with none.
            if (language.IsDefault)
                await DemoteOthersAsync(language.Code, transaction, ct).ConfigureAwait(false);

            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(Language language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(language);

        var updated = await _connection.ExecuteAsync(Command(
            _sql.UpdateLanguage,
            new { language.Code, language.Name, Direction = language.Direction.ToString(), language.IsActive },
            ct: ct)).ConfigureAwait(false);

        if (updated == 0)
            throw new LanguageNotFoundException(language.Code);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string code, CancellationToken ct = default) =>
        _connection.ExecuteAsync(Command(_sql.DeleteLanguage, new { Code = code }, ct: ct));

    /// <inheritdoc/>
    public async Task SetDefaultAsync(string code, CancellationToken ct = default)
    {
        await UnitOfWork.RunAsync(_connection, _transaction, async transaction =>
        {
            var promoted = await _connection.ExecuteAsync(
                Command(_sql.PromoteLanguage, new { Code = code, True = true }, transaction, ct)).ConfigureAwait(false);

            // Nothing has been demoted yet, and the throw rolls the unit back, so an unknown code
            // leaves the list exactly as it was.
            if (promoted == 0)
                throw new LanguageNotFoundException(code);

            return await DemoteOthersAsync(code, transaction, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Clears the default flag on every language except <paramref name="code"/>.</summary>
    private Task<int> DemoteOthersAsync(string code, IDbTransaction? transaction, CancellationToken ct) =>
        _connection.ExecuteAsync(Command(_sql.DemoteOtherLanguages, new { Code = code, True = true, False = false }, transaction, ct));

    private CommandDefinition Command(
        string sql, object? parameters = null, IDbTransaction? transaction = null, CancellationToken ct = default) =>
        new(sql, parameters, transaction ?? _transaction, cancellationToken: ct);
}
