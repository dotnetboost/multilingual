using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Per-query switches for the automatic translation. Every query on a context configured with
/// <c>UseMultiLingual(sp)</c> is translated by default — plain EF Core, nothing to call:
/// <code>
/// var products = await db.Products
///     .Where(p =&gt; p.Name!.Contains(term))        // compares the request's language
///     .OrderBy(p =&gt; p.Name)
///     .Include(p =&gt; p.Category)
///     .ToListAsync(ct);                            // Name and Category.Name filled in
/// </code>
/// These change that for one query.
/// </summary>
public static class MultiLingualQueryableExtensions
{
    internal static readonly MethodInfo IgnoreTranslationsMethod =
        typeof(MultiLingualQueryableExtensions).GetMethod(nameof(IgnoreTranslations))!;

    internal static readonly MethodInfo InLanguageMethod =
        typeof(MultiLingualQueryableExtensions).GetMethod(nameof(InLanguage))!;

    /// <summary>
    /// Runs this query without translation: translatable properties are read as they are stored
    /// on the entity. In base-language mode that is the base language's column — useful to search
    /// or show the base text whatever the request's language. In the default mode a translatable
    /// property has no column, so a query that reads one is refused, and entities come back with
    /// those properties <c>null</c>.
    /// </summary>
    /// <param name="source">The query.</param>
    public static IQueryable<T> IgnoreTranslations<T>(this IQueryable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Provider is IAsyncQueryProvider
            ? source.Provider.CreateQuery<T>(
                Expression.Call(IgnoreTranslationsMethod.MakeGenericMethod(typeof(T)), source.Expression))
            : source;
    }

    /// <summary>
    /// Reads this query in <paramref name="languageCode"/> instead of the request's language, with
    /// the usual fallback — for filters, sorting, projections and the entities it returns.
    /// <para>
    /// Each distinct code is compiled as its own query, like any other constant in a query: pass
    /// one of the application's languages, not free user input.
    /// </para>
    /// </summary>
    /// <param name="source">The query.</param>
    /// <param name="languageCode">The language to read, e.g. <c>"fr"</c>.</param>
    /// <exception cref="Core.UnknownLanguageException">
    /// Thrown when the query runs, if <paramref name="languageCode"/> does not exist.
    /// </exception>
    public static IQueryable<T> InLanguage<T>(this IQueryable<T> source, [NotParameterized] string languageCode)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        return source.Provider is IAsyncQueryProvider
            ? source.Provider.CreateQuery<T>(
                Expression.Call(InLanguageMethod.MakeGenericMethod(typeof(T)), source.Expression, Expression.Constant(languageCode.Trim())))
            : source;
    }
}
