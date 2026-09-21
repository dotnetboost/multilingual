using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;

namespace DotNetBoost.MultiLingual.TestSupport;

/// <summary>
/// Dictionary-backed <see cref="ITranslationStore"/>. Hands out copies, so callers mutating
/// what they read cannot change what is stored — the same isolation a database gives.
/// </summary>
public sealed class InMemoryTranslationStore : ITranslationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string EntityName, string EntityId, string PropertyName, string LanguageCode), Translation> _rows = [];

    /// <summary>How many upsert batches have been written — lets tests assert "nothing was written".</summary>
    public int UpsertCalls { get; private set; }

    public IReadOnlyList<Translation> All
    {
        get { lock (_gate) return _rows.Values.Select(Copy).ToList(); }
    }

    public Task<IReadOnlyList<Translation>> GetAsync(
        string entityName, IReadOnlyCollection<string> entityIds, IReadOnlyCollection<string> languageCodes, CancellationToken ct = default) =>
        Task.FromResult(GetMany(entityName, entityIds, languageCodes));

    public IReadOnlyList<Translation> GetMany(
        string entityName, IReadOnlyCollection<string> entityIds, IReadOnlyCollection<string> languageCodes)
    {
        lock (_gate)
        {
            return _rows.Values
                .Where(t => t.EntityName == entityName && entityIds.Contains(t.EntityId) && languageCodes.Contains(t.LanguageCode))
                .Select(Copy)
                .ToList();
        }
    }

    public Task<IReadOnlyList<Translation>> GetAllAsync(string entityName, string entityId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Translation> rows = _rows.Values
                .Where(t => t.EntityName == entityName && t.EntityId == entityId)
                .Select(Copy)
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task UpsertManyAsync(IReadOnlyCollection<Translation> translations, CancellationToken ct = default)
    {
        UpsertMany(translations);
        return Task.CompletedTask;
    }

    public void UpsertMany(IReadOnlyCollection<Translation> translations)
    {
        lock (_gate)
        {
            UpsertCalls++;
            foreach (var row in translations)
            {
                var key = (row.EntityName, row.EntityId, row.PropertyName, row.LanguageCode);
                if (row.Value is null)
                    _rows.Remove(key);
                else
                    _rows[key] = Copy(row);
            }
        }
    }

    public Task<int> DeleteEntitiesAsync(string entityName, IReadOnlyCollection<string> entityIds, CancellationToken ct = default) =>
        Task.FromResult(DeleteEntities(entityName, entityIds));

    public int DeleteEntities(string entityName, IReadOnlyCollection<string> entityIds) =>
        RemoveWhere(k => k.EntityName == entityName && entityIds.Contains(k.EntityId));

    public Task<int> CountByLanguageAsync(string languageCode, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_rows.Keys.Count(k => k.LanguageCode == languageCode));
    }

    public Task<int> DeleteByLanguageAsync(string languageCode, CancellationToken ct = default) =>
        Task.FromResult(RemoveWhere(k => k.LanguageCode == languageCode));

    private int RemoveWhere(Func<(string EntityName, string EntityId, string PropertyName, string LanguageCode), bool> predicate)
    {
        lock (_gate)
        {
            var keys = _rows.Keys.Where(predicate).ToList();
            foreach (var key in keys)
                _rows.Remove(key);
            return keys.Count;
        }
    }

    private static Translation Copy(Translation t) => new()
    {
        EntityName   = t.EntityName,
        EntityId     = t.EntityId,
        PropertyName = t.PropertyName,
        LanguageCode = t.LanguageCode,
        Value        = t.Value
    };
}

/// <summary>Dictionary-backed <see cref="ILanguageStore"/>.</summary>
public sealed class InMemoryLanguageStore : ILanguageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Language> _languages = new(StringComparer.Ordinal);

    /// <summary>How many times the list was read — lets tests observe caching.</summary>
    public int GetAllCalls { get; private set; }

    public Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(GetAll());

    public IReadOnlyList<Language> GetAll()
    {
        lock (_gate)
        {
            GetAllCalls++;
            return _languages.Values.Select(Copy).ToList();
        }
    }

    public Task<Language?> FindAsync(string code, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_languages.TryGetValue(code, out var l) ? Copy(l) : null);
    }

    public Task AddAsync(Language language, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_languages.ContainsKey(language.Code))
                throw new InvalidOperationException($"Duplicate language '{language.Code}'.");

            if (language.IsDefault)
            {
                foreach (var existing in _languages.Values)
                    existing.IsDefault = false;
            }

            _languages[language.Code] = Copy(language);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Language language, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_languages.TryGetValue(language.Code, out var stored))
                throw new LanguageNotFoundException(language.Code);

            stored.Name      = language.Name;
            stored.Direction = language.Direction;
            stored.IsActive  = language.IsActive;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string code, CancellationToken ct = default)
    {
        lock (_gate) _languages.Remove(code);
        return Task.CompletedTask;
    }

    public Task SetDefaultAsync(string code, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_languages.ContainsKey(code))
                throw new LanguageNotFoundException(code);

            foreach (var language in _languages.Values)
                language.IsDefault = language.Code == code;
        }

        return Task.CompletedTask;
    }

    private static Language Copy(Language l) => new()
    {
        Code      = l.Code,
        Name      = l.Name,
        Direction = l.Direction,
        IsDefault = l.IsDefault,
        IsActive  = l.IsActive
    };
}
