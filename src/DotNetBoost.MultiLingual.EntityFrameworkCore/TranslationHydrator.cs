using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// The read path, second half: fills in every <c>[MultiLingual]</c> entity EF materialized
/// since the last call — in the current language with the default-language fallback.
/// Internal: applications get it through <see cref="LocalizedQueryExtensions"/>.
/// </summary>
internal interface ITranslationHydrator
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
/// One localized read in progress. The materialization interceptor reports the context it is
/// materializing for, so the extension methods can find that context's scoped services without
/// being handed them — <c>query.ToLocalizedListAsync()</c> needs no <c>DbContext</c> and no
/// hydrator argument.
/// <para>
/// It travels in an <see cref="AsyncLocal{T}"/>: set before the query runs, so the query's
/// execution — and with it every materialization callback — sees it, whether the query runs
/// synchronously or not. Parallel reads on different contexts each carry their own.
/// </para>
/// </summary>
internal sealed class LocalizedRead
{
    private static readonly AsyncLocal<LocalizedRead?> Active = new();

    private readonly LocalizedRead? _outer;
    private DbContext? _context;

    private LocalizedRead(LocalizedRead? outer) => _outer = outer;

    /// <summary>Starts a read; <see cref="End"/> it once the query has run.</summary>
    public static LocalizedRead Begin() => Active.Value = new LocalizedRead(Active.Value);

    /// <summary>Called by the materialization interceptor for every [MultiLingual] entity.</summary>
    public static void Record(DbContext context)
    {
        if (Active.Value is { } read)
            read._context ??= context;
    }

    /// <summary>Restores whichever read was active before this one.</summary>
    public void End() => Active.Value = _outer;

    /// <summary>Hydrates everything the context materialized, if the query materialized anything.</summary>
    public Task HydrateAsync(CancellationToken ct) =>
        Hydrator() is { } hydrator ? hydrator.HydratePendingAsync(ct) : Task.CompletedTask;

    /// <summary>Synchronous <see cref="HydrateAsync"/>.</summary>
    public void Hydrate() => Hydrator()?.HydratePending();

    private ITranslationHydrator? Hydrator() =>
        _context is null ? null : TranslatedQueryExtensions.ApplicationServices(_context).GetRequiredService<ITranslationHydrator>();
}

/// <summary>
/// "Run the query, then fill in the translated properties" as one call — the way read endpoints
/// load whole entities:
/// <code>
/// var products = await db.Products.Where(p => p.CategoryId == id).ToLocalizedListAsync(ct);
/// </code>
/// Entities pulled in with <c>Include</c> are localized as well, in one extra query per entity
/// type however many rows there are.
///
/// <para>
/// Write endpoints deliberately do NOT localize: an entity loaded for update should keep its
/// translatable properties <c>null</c> so that "not supplied" stays distinguishable from
/// "set to this value", and the save interceptor only writes what was actually assigned.
/// </para>
/// </summary>
public static class LocalizedQueryExtensions
{
    /// <summary><c>ToListAsync</c>, then localize.</summary>
    public static async Task<List<T>> ToLocalizedListAsync<T>(this IQueryable<T> query, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        var read = LocalizedRead.Begin();
        List<T> items;
        try
        {
            items = await query.ToListAsync(ct);
        }
        finally
        {
            read.End();
        }

        await read.HydrateAsync(ct);
        return items;
    }

    /// <summary><c>FirstOrDefaultAsync</c>, then localize.</summary>
    public static async Task<T?> FirstLocalizedOrDefaultAsync<T>(this IQueryable<T> query, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        var read = LocalizedRead.Begin();
        T? item;
        try
        {
            item = await query.FirstOrDefaultAsync(ct);
        }
        finally
        {
            read.End();
        }

        await read.HydrateAsync(ct);
        return item;
    }

    /// <summary><c>SingleOrDefaultAsync</c>, then localize.</summary>
    public static async Task<T?> SingleLocalizedOrDefaultAsync<T>(this IQueryable<T> query, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        var read = LocalizedRead.Begin();
        T? item;
        try
        {
            item = await query.SingleOrDefaultAsync(ct);
        }
        finally
        {
            read.End();
        }

        await read.HydrateAsync(ct);
        return item;
    }

    /// <summary><c>ToList</c>, then localize — for synchronous code paths.</summary>
    public static List<T> ToLocalizedList<T>(this IQueryable<T> query)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        var read = LocalizedRead.Begin();
        List<T> items;
        try
        {
            items = query.ToList();
        }
        finally
        {
            read.End();
        }

        read.Hydrate();
        return items;
    }

    /// <summary><c>FirstOrDefault</c>, then localize — for synchronous code paths.</summary>
    public static T? FirstLocalizedOrDefault<T>(this IQueryable<T> query)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        var read = LocalizedRead.Begin();
        T? item;
        try
        {
            item = query.FirstOrDefault();
        }
        finally
        {
            read.End();
        }

        read.Hydrate();
        return item;
    }

    /// <summary><c>SingleOrDefault</c>, then localize — for synchronous code paths.</summary>
    public static T? SingleLocalizedOrDefault<T>(this IQueryable<T> query)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        var read = LocalizedRead.Begin();
        T? item;
        try
        {
            item = query.SingleOrDefault();
        }
        finally
        {
            read.End();
        }

        read.Hydrate();
        return item;
    }
}
