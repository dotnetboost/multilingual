using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// The read path, second half: fills in every <c>[MultiLingual]</c> entity EF materialized
/// since the last call — in the current language with the default-language fallback.
/// </summary>
public interface ITranslationHydrator
{
    /// <summary>Hydrates everything materialized since the last call, one query per entity type.</summary>
    Task HydratePendingAsync(CancellationToken ct = default);

    /// <summary>Synchronous <see cref="HydratePendingAsync"/>.</summary>
    void HydratePending();
}

/// <summary>Default <see cref="ITranslationHydrator"/>.</summary>
/// <param name="registry">What the materialization interceptor recorded.</param>
/// <param name="translations">The engine that does the hydrating.</param>
internal sealed class TranslationHydrator(MaterializedEntityRegistry registry, ITranslationManager translations)
    : ITranslationHydrator
{
    /// <inheritdoc/>
    public Task HydratePendingAsync(CancellationToken ct = default)
    {
        var pending = registry.Drain();
        return pending.Count == 0 ? Task.CompletedTask : translations.HydrateAsync(pending, ct);
    }

    /// <inheritdoc/>
    public void HydratePending()
    {
        var pending = registry.Drain();
        if (pending.Count > 0)
            translations.Hydrate(pending);
    }
}

/// <summary>
/// Sugar over "run the query, then hydrate what it materialized". Prefer these in read
/// endpoints — they make the two-step read path a single call.
///
/// <para>
/// Write endpoints deliberately do NOT hydrate: an entity loaded for update should keep its
/// translatable properties <c>null</c> so that "not supplied" stays distinguishable from
/// "set to this value", and the save interceptor only writes what was actually assigned.
/// </para>
/// </summary>
public static class LocalizedQueryExtensions
{
    /// <summary><c>ToListAsync</c>, then hydrate.</summary>
    public static async Task<List<T>> ToLocalizedListAsync<T>(
        this IQueryable<T> query,
        ITranslationHydrator hydrator,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(hydrator);

        var items = await query.ToListAsync(ct);
        await hydrator.HydratePendingAsync(ct);
        return items;
    }

    /// <summary><c>FirstOrDefaultAsync</c>, then hydrate.</summary>
    public static async Task<T?> FirstLocalizedOrDefaultAsync<T>(
        this IQueryable<T> query,
        ITranslationHydrator hydrator,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(hydrator);

        var item = await query.FirstOrDefaultAsync(ct);
        await hydrator.HydratePendingAsync(ct);
        return item;
    }

    /// <summary><c>SingleOrDefaultAsync</c>, then hydrate.</summary>
    public static async Task<T?> SingleLocalizedOrDefaultAsync<T>(
        this IQueryable<T> query,
        ITranslationHydrator hydrator,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(hydrator);

        var item = await query.SingleOrDefaultAsync(ct);
        await hydrator.HydratePendingAsync(ct);
        return item;
    }

    /// <summary><c>ToList</c>, then hydrate — for synchronous code paths.</summary>
    public static List<T> ToLocalizedList<T>(this IQueryable<T> query, ITranslationHydrator hydrator)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(hydrator);

        var items = query.ToList();
        hydrator.HydratePending();
        return items;
    }

    /// <summary><c>FirstOrDefault</c>, then hydrate — for synchronous code paths.</summary>
    public static T? FirstLocalizedOrDefault<T>(this IQueryable<T> query, ITranslationHydrator hydrator)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(hydrator);

        var item = query.FirstOrDefault();
        hydrator.HydratePending();
        return item;
    }

    /// <summary><c>SingleOrDefault</c>, then hydrate — for synchronous code paths.</summary>
    public static T? SingleLocalizedOrDefault<T>(this IQueryable<T> query, ITranslationHydrator hydrator)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(hydrator);

        var item = query.SingleOrDefault();
        hydrator.HydratePending();
        return item;
    }
}
