using System.Linq.Expressions;
using System.Reflection;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Plain LINQ over translated text, in the database. After <see cref="AsTranslated"/>, every read
/// of a translatable property in the query — in <c>Where</c>, <c>OrderBy</c>, <c>Select</c>,
/// through navigations and in nested collections — is the value a read would show:
/// <code>
/// var rows = await db.Products
///     .AsTranslated(db)
///     .Where(p =&gt; p.CategoryId == categoryId &amp;&amp; p.Name!.Contains(term))
///     .OrderBy(p =&gt; p.Name)
///     .Select(p =&gt; new ProductDto(p.Sku, p.Name, p.Category!.Name))
///     .ToListAsync(ct);
/// </code>
///
/// <para><b>Which value.</b> The requested language, then the default language (unless fallback
/// is disabled), then — in base-language mode — the base column. The language is the current one
/// unless <c>languageCode</c> is given. Comparison follows the database's collation, as any other
/// string predicate does.</para>
///
/// <para><b>Cost.</b> Each translated property read becomes an indexed lookup per language in the
/// chain. Returning a page is cheap at any size; filtering or sorting ON translated text reads it
/// for every candidate row, so combine it with a filter on an ordinary column where you can, and
/// use a search engine for full-text search over large catalogues.</para>
/// </summary>
public static class TranslatedQueryExtensions
{
    /// <summary>
    /// Reads every translatable property in the query in the current language (or
    /// <paramref name="languageCode"/>), with the usual fallback, instead of from the entity.
    /// <para>
    /// A plain query cannot do this: in the default mode a translatable property has no column,
    /// and in base-language mode its column only holds the base language. Ordinary columns are
    /// untouched, so their filters keep using their indexes. Call it once per query; it covers the
    /// whole query, wherever it is placed. Loading whole entities still needs
    /// <see cref="LocalizedQueryExtensions.ToLocalizedListAsync"/> to fill their properties.
    /// </para>
    /// </summary>
    /// <param name="query">The query to translate.</param>
    /// <param name="context">The context <paramref name="query"/> runs on; it supplies the request's language.</param>
    /// <param name="languageCode">The language to read; the current one when omitted.</param>
    /// <exception cref="UnknownLanguageException"><paramref name="languageCode"/> does not exist.</exception>
    public static IQueryable<T> AsTranslated<T>(this IQueryable<T> query, DbContext context, string? languageCode = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        var services = ApplicationServices(context);
        EnsureQueryInterceptor(context);

        var options = services.GetRequiredService<MultiLingualOptions>();
        var (language, fallback) = Languages(services, languageCode);

        // The base language is the entity's own column: authoritative, and the end of the chain.
        // A plain query already reads it.
        if (options.IsBaseLanguage(language))
            return query;

        var useFallback = options.FallbackToDefaultLanguage
                          && !string.Equals(language, fallback, StringComparison.OrdinalIgnoreCase);

        var codes = (useFallback ? new[] { language, fallback } : [language])
            .Where(code => !options.IsBaseLanguage(code))
            .ToArray();

        return query.Provider.CreateQuery<T>(TranslatedQuery.Mark(query.Expression, codes, options.HasBaseLanguage));
    }

    /// <summary>The request's services — the scoped provider the context's options were built from.</summary>
    internal static IServiceProvider ApplicationServices(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
        ?? throw new InvalidOperationException(
            $"'{context.GetType().Name}' has no MultiLingual services. Register it with " +
            "AddDbContext((sp, options) => options.UseMultiLingual(sp)).");

    private static void EnsureQueryInterceptor(DbContext context)
    {
        var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors is null || !interceptors.Contains(MultiLingualQueryInterceptor.Instance))
        {
            throw new InvalidOperationException(
                $"'{context.GetType().Name}' is not configured with UseMultiLingual(sp), which AsTranslated() " +
                "needs to rewrite the query. Register it with AddDbContext((sp, options) => options.UseMultiLingual(sp)).");
        }
    }

    /// <summary>The language to read in, and the default it falls back to — both canonical.</summary>
    private static (string Language, string Fallback) Languages(IServiceProvider services, string? languageCode)
    {
        var catalog  = services.GetRequiredService<ILanguageCatalog>();
        var current  = services.GetRequiredService<ILanguageContext>();

        var fallback = current.IsResolved
            ? current.DefaultLanguage.Code
            : catalog.GetAll().FirstOrDefault(l => l.IsDefault)?.Code ?? throw new NoLanguageConfiguredException();

        if (languageCode is not null)
        {
            var match = catalog.GetAll().FirstOrDefault(l => string.Equals(l.Code, languageCode.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new UnknownLanguageException(languageCode, "a translated query");
            return (match.Code, fallback);
        }

        return (current.IsResolved ? current.Current.Code : fallback, fallback);
    }
}

/// <summary>
/// How <see cref="TranslatedQueryExtensions.AsTranslated"/> works. EF compiles a query once per
/// shape and reuses it, so the request's language cannot be decided where the query is compiled.
/// Instead:
/// <list type="number">
///   <item><see cref="Mark"/> wraps the query in a marker call whose arguments carry the language
///   codes as captured values — which EF turns into SQL parameters, fresh for every execution.
///   The shape of the fallback chain (one language or two, with or without the base column) is
///   which of four marker methods is called: EF parameterizes every argument, even a constant,
///   but the method itself is part of its cache key.</item>
///   <item><see cref="Rewrite"/>, called from <see cref="MultiLingualQueryInterceptor"/> when EF
///   compiles the query, removes the marker and replaces every translatable property read with
///   <c>COALESCE(lookup(language), lookup(fallback), column)</c>, reusing those parameters.</item>
/// </list>
/// Entity and property names never vary, so they are written into the SQL as literals: a query
/// reading three properties in two languages has two parameters, not eighteen.
/// </summary>
internal static class TranslatedQuery
{
    /// <summary>The marker methods, and the chain shape each one stands for.</summary>
    private static readonly Dictionary<MethodInfo, (bool Fallback, bool BaseColumn)> Markers = new()
    {
        [MarkerMethod(nameof(Language))]                        = (false, false),
        [MarkerMethod(nameof(LanguageThenFallback))]            = (true,  false),
        [MarkerMethod(nameof(LanguageThenColumn))]              = (false, true),
        [MarkerMethod(nameof(LanguageThenFallbackThenColumn))]  = (true,  true)
    };

    private static MethodInfo MarkerMethod(string name) =>
        typeof(TranslatedQuery).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>One translation value: <c>translations.Where(key matches).Select(t =&gt; t.Value).FirstOrDefault()</c>.</summary>
    private static readonly Expression<Func<IQueryable<Translation>, string, string, string, string, string?>> LookupTemplate =
        (translations, entityName, propertyName, languageCode, entityId) => translations
            .Where(t => t.EntityName == entityName
                        && t.PropertyName == propertyName
                        && t.LanguageCode == languageCode
                        && t.EntityId == entityId)
            .Select(t => t.Value)
            .FirstOrDefault();

    // The markers. They never run against a database — the interceptor removes them — and return
    // their source unchanged anywhere else, such as in-memory LINQ.

    private static IQueryable<T> Language<T>(IQueryable<T> source, string language) => source;

    private static IQueryable<T> LanguageThenFallback<T>(IQueryable<T> source, string language, string fallback) => source;

    private static IQueryable<T> LanguageThenColumn<T>(IQueryable<T> source, string language) => source;

    private static IQueryable<T> LanguageThenFallbackThenColumn<T>(IQueryable<T> source, string language, string fallback) => source;

    /// <summary>Wraps <paramref name="source"/> in the marker for <paramref name="codes"/> (one or two).</summary>
    public static Expression Mark(Expression source, IReadOnlyList<string> codes, bool baseColumn)
    {
        var fallback  = codes.Count > 1;
        var marker    = Markers.Single(m => m.Value == (fallback, baseColumn)).Key;
        var languages = Expression.Constant(new CodesHolder(codes[0], fallback ? codes[1] : null));

        Expression[] arguments = fallback
            ? [source, Expression.Property(languages, nameof(CodesHolder.Language)), Expression.Property(languages, nameof(CodesHolder.Fallback))]
            : [source, Expression.Property(languages, nameof(CodesHolder.Language))];

        return Expression.Call(marker.MakeGenericMethod(source.Type.GetGenericArguments()[0]), arguments);
    }

    /// <summary>
    /// Removes the marker from <paramref name="query"/> and translates every translatable property
    /// read in it. Returns the query unchanged when it has no marker.
    /// </summary>
    /// <exception cref="InvalidOperationException">The query is marked more than once.</exception>
    public static Expression Rewrite(Expression query, DbContext context)
    {
        var finder = new MarkerFinder();
        finder.Visit(query);

        if (finder.Markers.Count == 0)
            return query;

        if (finder.Markers.Count > 1)
        {
            throw new InvalidOperationException(
                "AsTranslated() is called more than once in this query. Call it once, on the query as a whole " +
                "(after Concat or Union, for instance): it translates every part of the query.");
        }

        var marker     = finder.Markers[0];
        var baseColumn = Markers[marker.Method.GetGenericMethodDefinition()].BaseColumn;
        var codes      = marker.Arguments.Skip(1).ToArray();   // the language, then the fallback if any

        return new TranslatingVisitor(context, codes, baseColumn).Visit(query);
    }

    private static bool IsMarker(MethodCallExpression node) =>
        node.Method.IsGenericMethod && Markers.ContainsKey(node.Method.GetGenericMethodDefinition());

    /// <summary>
    /// The translated value of <paramref name="member"/> on <paramref name="entity"/> — any entity
    /// expression, a lambda parameter or a navigation such as <c>p.Category</c>.
    /// </summary>
    private static Expression Translated(DbContext context, Expression entity, MemberInfo member, Expression[] codes, bool baseColumn)
    {
        var key          = EntityKeys.KeyAsString(context, entity);
        var entityName   = Expression.Constant(MultiLingualMetadata.EntityNameFor(entity.Type));
        var propertyName = Expression.Constant(member.Name);
        var translations = context.Set<Translation>().AsQueryable().Expression;

        var chain = codes
            .Select(code => Lookup(translations, entityName, propertyName, code, key))
            .ToList();

        if (baseColumn)
            chain.Add(Expression.MakeMemberAccess(entity, member));

        var body = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--)
            body = Expression.Coalesce(chain[i], body);

        return body;
    }

    private static Expression Lookup(Expression translations, Expression entityName, Expression propertyName, Expression code, Expression key)
    {
        var parameters = LookupTemplate.Parameters;
        return new Replacer(new Dictionary<ParameterExpression, Expression>
        {
            [parameters[0]] = translations,
            [parameters[1]] = entityName,
            [parameters[2]] = propertyName,
            [parameters[3]] = code,
            [parameters[4]] = key
        }).Visit(LookupTemplate.Body);
    }

    /// <summary>The captured language codes; EF reads them on every execution.</summary>
    private sealed class CodesHolder(string language, string? fallback)
    {
        public string Language { get; } = language;

        public string? Fallback { get; } = fallback;
    }

    private sealed class MarkerFinder : ExpressionVisitor
    {
        public List<MethodCallExpression> Markers { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (IsMarker(node))
                Markers.Add(node);

            return base.VisitMethodCall(node);
        }
    }

    /// <summary>
    /// Drops the marker and replaces every read of a translatable property of an entity — on a
    /// lambda parameter, through a navigation, or inside a nested collection projection — with its
    /// translated value. Anything else, including translatable-looking properties on types that are
    /// not entities of this model, is left alone.
    /// </summary>
    private sealed class TranslatingVisitor(DbContext context, Expression[] codes, bool baseColumn) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (IsMarker(node))
                return Visit(node.Arguments[0]);

            // ExecuteUpdate's SetProperty(p => p.Name, value): the first argument names the column
            // to write, so it must stay a column. Only the value is translated. Matched by name:
            // the setter type is SetPropertyCalls<T> in EF Core 8 and UpdateSettersBuilder<T> later.
            if (node.Method.Name == "SetProperty" && node.Arguments.Count == 2)
            {
                return node.Update(Visit(node.Object), [node.Arguments[0], .. node.Arguments.Skip(1).Select(a => Visit(a))]);
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is { } owner
                && node.Member is PropertyInfo { PropertyType: var type }
                && type == typeof(string)
                && context.Model.FindEntityType(owner.Type) is not null
                && MultiLingualMetadata.For(owner.Type).Any(p => string.Equals(p.Name, node.Member.Name, StringComparison.Ordinal)))
            {
                return Translated(context, Visit(owner), node.Member, codes, baseColumn);
            }

            return base.VisitMember(node);
        }
    }

    private sealed class Replacer(Dictionary<ParameterExpression, Expression> replacements) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            replacements.TryGetValue(node, out var replacement) ? replacement : base.VisitParameter(node);
    }
}
