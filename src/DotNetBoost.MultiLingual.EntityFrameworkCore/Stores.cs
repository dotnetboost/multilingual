using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// EF Core <see cref="ITranslationStore"/>. Works through whichever <see cref="DbContext"/> it
/// is given, so writes join that context's connection and transaction.
/// <para>
/// <see cref="UpsertManyAsync"/> ends in <c>SaveChangesAsync</c>, which saves every pending
/// change on the context — not only translation rows.
/// </para>
/// </summary>
/// <param name="context">A context whose model includes <see cref="Translation"/>.</param>
public sealed class EfCoreTranslationStore(DbContext context) : ITranslationStore
{
    private DbSet<Translation> Translations => context.Set<Translation>();

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

        var ids   = entityIds.ToArray();
        var codes = languageCodes.ToArray();

        return await Translations
            .AsNoTracking()
            .Where(t => t.EntityName == entityName && ids.Contains(t.EntityId) && codes.Contains(t.LanguageCode))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Translation>> GetAllAsync(string entityName, string entityId, CancellationToken ct = default) =>
        await Translations
            .AsNoTracking()
            .Where(t => t.EntityName == entityName && t.EntityId == entityId)
            .ToListAsync(ct);

    /// <inheritdoc/>
    public async Task UpsertManyAsync(IReadOnlyCollection<Translation> translations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(translations);
        if (translations.Count == 0)
            return;

        // One round trip per entity type to load whatever already exists, then upsert in memory.
        foreach (var group in translations.GroupBy(t => t.EntityName, StringComparer.Ordinal))
            Merge(group, await ExistingRows(group).ToListAsync(ct));

        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public void UpsertMany(IReadOnlyCollection<Translation> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);
        if (translations.Count == 0)
            return;

        foreach (var group in translations.GroupBy(t => t.EntityName, StringComparer.Ordinal))
            Merge(group, ExistingRows(group).ToList());

        context.SaveChanges();
    }

    /// <summary>The stored rows an upsert of <paramref name="group"/> (one entity type) may touch — tracked.</summary>
    private IQueryable<Translation> ExistingRows(IGrouping<string, Translation> group)
    {
        var entityName = group.Key;
        var ids        = group.Select(t => t.EntityId).Distinct(StringComparer.Ordinal).ToArray();
        var codes      = group.Select(t => t.LanguageCode).Distinct(StringComparer.Ordinal).ToArray();

        return Translations
            .Where(t => t.EntityName == entityName && ids.Contains(t.EntityId) && codes.Contains(t.LanguageCode));
    }

    /// <summary>Applies <paramref name="group"/> over the rows already stored, leaving the changes tracked.</summary>
    private void Merge(IEnumerable<Translation> group, List<Translation> existing)
    {
        var index = existing.ToDictionary(t => (t.EntityId, t.PropertyName, t.LanguageCode));

        foreach (var row in group)
        {
            var key = (row.EntityId, row.PropertyName, row.LanguageCode);

            if (index.TryGetValue(key, out var current))
            {
                // Assign only on change, so an unchanged value never costs an UPDATE.
                if (!string.Equals(current.Value, row.Value, StringComparison.Ordinal))
                    current.Value = row.Value;
            }
            else
            {
                // A copy: the caller's instance must not become tracked by this context.
                var created = new Translation
                {
                    EntityName   = row.EntityName,
                    EntityId     = row.EntityId,
                    PropertyName = row.PropertyName,
                    LanguageCode = row.LanguageCode,
                    Value        = row.Value
                };

                Translations.Add(created);
                index[key] = created;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<int> DeleteEntitiesAsync(
        string entityName, IReadOnlyCollection<string> entityIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0)
            return 0;

        var ids = entityIds.ToArray();
        var removed = await Translations
            .Where(t => t.EntityName == entityName && ids.Contains(t.EntityId))
            .ExecuteDeleteAsync(ct);

        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        DetachTracked(t => t.EntityName == entityName && idSet.Contains(t.EntityId));
        return removed;
    }

    /// <inheritdoc/>
    public int DeleteEntities(string entityName, IReadOnlyCollection<string> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        if (entityIds.Count == 0)
            return 0;

        var ids = entityIds.ToArray();
        var removed = Translations
            .Where(t => t.EntityName == entityName && ids.Contains(t.EntityId))
            .ExecuteDelete();

        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        DetachTracked(t => t.EntityName == entityName && idSet.Contains(t.EntityId));
        return removed;
    }

    /// <inheritdoc/>
    public Task<int> CountByLanguageAsync(string languageCode, CancellationToken ct = default) =>
        Translations.CountAsync(t => t.LanguageCode == languageCode, ct);

    /// <inheritdoc/>
    public async Task<int> DeleteByLanguageAsync(string languageCode, CancellationToken ct = default)
    {
        var removed = await Translations.Where(t => t.LanguageCode == languageCode).ExecuteDeleteAsync(ct);
        DetachTracked(t => t.LanguageCode == languageCode);
        return removed;
    }

    /// <summary>
    /// ExecuteDelete bypasses the change tracker, so instances an earlier upsert left tracked
    /// would outlive their rows — and make a later upsert of the same key fail as a duplicate.
    /// </summary>
    private void DetachTracked(Func<Translation, bool> deleted)
    {
        foreach (var entry in context.ChangeTracker.Entries<Translation>().Where(e => deleted(e.Entity)).ToList())
            entry.State = EntityState.Detached;
    }
}

/// <summary>
/// EF Core <see cref="ILanguageStore"/>. Multi-row changes (moving the default) go through a
/// single <c>SaveChangesAsync</c>, which EF already makes atomic — no explicit transaction, so
/// it also works under a retrying execution strategy.
/// </summary>
/// <param name="context">A context whose model includes <see cref="Language"/>.</param>
public sealed class EfCoreLanguageStore(DbContext context) : ILanguageStore
{
    private DbSet<Language> Languages => context.Set<Language>();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default) =>
        await Languages.AsNoTracking().ToListAsync(ct);

    /// <inheritdoc/>
    public IReadOnlyList<Language> GetAll() => Languages.AsNoTracking().ToList();

    /// <inheritdoc/>
    public Task<Language?> FindAsync(string code, CancellationToken ct = default) =>
        Languages.AsNoTracking().FirstOrDefaultAsync(l => l.Code == code, ct);

    /// <inheritdoc/>
    public async Task AddAsync(Language language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(language);

        if (language.IsDefault)
        {
            foreach (var existing in await Languages.Where(l => l.IsDefault).ToListAsync(ct))
                existing.IsDefault = false;
        }

        Languages.Add(Copy(language));
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(Language language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(language);

        var stored = await Languages.FirstOrDefaultAsync(l => l.Code == language.Code, ct)
                     ?? throw new LanguageNotFoundException(language.Code);

        stored.Name      = language.Name;
        stored.Direction = language.Direction;
        stored.IsActive  = language.IsActive;

        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string code, CancellationToken ct = default)
    {
        var stored = await Languages.FirstOrDefaultAsync(l => l.Code == code, ct);
        if (stored is null)
            return;

        Languages.Remove(stored);
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SetDefaultAsync(string code, CancellationToken ct = default)
    {
        var affected = await Languages.Where(l => l.IsDefault || l.Code == code).ToListAsync(ct);

        if (!affected.Any(l => l.Code == code))
            throw new LanguageNotFoundException(code);

        foreach (var language in affected)
            language.IsDefault = language.Code == code;

        await context.SaveChangesAsync(ct);
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
