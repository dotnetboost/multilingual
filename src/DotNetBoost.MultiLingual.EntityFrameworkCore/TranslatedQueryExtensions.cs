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
/// Filtering and sorting on translated text, in the database. A translatable property has no
/// column in the default mode, so <c>Where(p =&gt; p.Name.Contains(term))</c> cannot be translated;
/// these build the equivalent over the translations table instead:
/// <code>
/// var products = await db.Products
///     .WhereTranslation(db, p =&gt; p.Name, name =&gt; name!.Contains(term))
///     .OrderByTranslation(db, p =&gt; p.Name)
///     .ToLocalizedListAsync(hydrator, ct);
/// </code>
///
/// <para><b>Which value is compared.</b> The one a read would show: the requested language,
/// then the default language (unless fallback is disabled), then — in base-language mode — the
/// base column. The language is the current one unless <c>languageCode</c> is given. Comparison
/// follows the database's collation, as any other string predicate does.</para>
///
/// <para><b>Cost.</b> Each call adds a correlated subquery per language in the chain, served by
/// the translations table's primary key. Fine for filtering and sorting a page; for full-text
/// search across large catalogues, index the text in a search engine instead.</para>
///
/// <para>The <c>context</c> argument must be the context the query runs on (it
/// supplies the translations table and the request's language).</para>
/// </summary>
public static class TranslatedQueryExtensions
{
    /// <summary>Keeps the entities whose translated <paramref name="property"/> satisfies <paramref name="predicate"/>.</summary>
    /// <param name="query">The query to filter.</param>
    /// <param name="context">The context <paramref name="query"/> runs on.</param>
    /// <param name="property">A translatable property, e.g. <c>p =&gt; p.Name</c>.</param>
    /// <param name="predicate">The test for the translated value; it can be <c>null</c> when nothing is translated.</param>
    /// <param name="languageCode">The language to compare in; the current one when omitted.</param>
    public static IQueryable<T> WhereTranslation<T>(
        this IQueryable<T> query,
        DbContext context,
        Expression<Func<T, string?>> property,
        Expression<Func<string?, bool>> predicate,
        string? languageCode = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(predicate);

        var value = TranslatedValue(context, property, languageCode);
        var body  = new ParameterReplacer(predicate.Parameters[0], value.Body).Visit(predicate.Body);

        return query.Where(Expression.Lambda<Func<T, bool>>(body, value.Parameters));
    }

    /// <summary>
    /// Projects the query, reading every translatable property in <paramref name="selector"/> in
    /// the current language (or <paramref name="languageCode"/>) instead of from the entity:
    /// <code>
    /// var rows = await db.Products
    ///     .SelectTranslated(db, p =&gt; new ProductDto { Sku = p.Sku, Name = p.Name, Category = p.Category!.Name })
    ///     .ToListAsync(ct);
    /// </code>
    /// A plain <c>Select</c> cannot do this: in the default mode a translatable property has no
    /// column, and in base-language mode its column only holds the base language. Properties
    /// reached through navigations and nested collection projections are translated too.
    /// </summary>
    /// <param name="query">The query to project.</param>
    /// <param name="context">The context <paramref name="query"/> runs on.</param>
    /// <param name="selector">The projection, written as for <c>Select</c>.</param>
    /// <param name="languageCode">The language to read; the current one when omitted.</param>
    public static IQueryable<TResult> SelectTranslated<T, TResult>(
        this IQueryable<T> query,
        DbContext context,
        Expression<Func<T, TResult>> selector,
        string? languageCode = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);

        var visitor = new TranslatingVisitor(context, Resolve(context, languageCode));
        return query.Select((Expression<Func<T, TResult>>)visitor.Visit(selector));
    }

    /// <summary>Sorts by the translated <paramref name="property"/>, ascending.</summary>
    public static IOrderedQueryable<T> OrderByTranslation<T>(
        this IQueryable<T> query, DbContext context, Expression<Func<T, string?>> property, string? languageCode = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.OrderBy(TranslatedValue(context, property, languageCode));
    }

    /// <summary>Sorts by the translated <paramref name="property"/>, descending.</summary>
    public static IOrderedQueryable<T> OrderByTranslationDescending<T>(
        this IQueryable<T> query, DbContext context, Expression<Func<T, string?>> property, string? languageCode = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.OrderByDescending(TranslatedValue(context, property, languageCode));
    }

    /// <summary>Then sorts by the translated <paramref name="property"/>, ascending.</summary>
    public static IOrderedQueryable<T> ThenByTranslation<T>(
        this IOrderedQueryable<T> query, DbContext context, Expression<Func<T, string?>> property, string? languageCode = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.ThenBy(TranslatedValue(context, property, languageCode));
    }

    /// <summary>Then sorts by the translated <paramref name="property"/>, descending.</summary>
    public static IOrderedQueryable<T> ThenByTranslationDescending<T>(
        this IOrderedQueryable<T> query, DbContext context, Expression<Func<T, string?>> property, string? languageCode = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.ThenByDescending(TranslatedValue(context, property, languageCode));
    }

    // ------------------------------------------------------------------ the value expression

    /// <summary>
    /// <c>p =&gt; COALESCE(translation(requested), translation(default), p.Column)</c> for one
    /// property given as a lambda.
    /// </summary>
    private static Expression<Func<T, string?>> TranslatedValue<T>(
        DbContext context, Expression<Func<T, string?>> property, string? languageCode)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(property);

        var member = TranslatableMember(property);
        var body   = Translated(context, Resolve(context, languageCode), property.Parameters[0], member);

        return Expression.Lambda<Func<T, string?>>(body, property.Parameters);
    }

    /// <summary>The configuration and languages every value in one query is translated with.</summary>
    private sealed record Resolution(MultiLingualOptions Options, string Language, string Fallback);

    private static Resolution Resolve(DbContext context, string? languageCode)
    {
        var services = ApplicationServices(context);
        var (language, fallback) = Languages(services, languageCode);
        return new Resolution(services.GetRequiredService<MultiLingualOptions>(), language, fallback);
    }

    /// <summary>
    /// The translated value of <paramref name="member"/> on <paramref name="entity"/> — any entity
    /// expression, a lambda parameter or a navigation such as <c>p.Category</c> — following the
    /// same chain as <see cref="ITranslationManager.HydrateAsync(IEnumerable{object}, CancellationToken)"/>:
    /// <c>COALESCE(translation(requested), translation(default), entity.Column)</c>.
    /// </summary>
    private static Expression Translated(DbContext context, Resolution resolution, Expression entity, MemberInfo member)
    {
        var options = resolution.Options;
        var column  = Expression.MakeMemberAccess(entity, member);

        // The base language is the entity's own column: authoritative, and the end of the chain.
        if (options.IsBaseLanguage(resolution.Language))
            return column;

        var codes = options.FallbackToDefaultLanguage
                    && !string.Equals(resolution.Language, resolution.Fallback, StringComparison.OrdinalIgnoreCase)
            ? new[] { resolution.Language, resolution.Fallback }
            : [resolution.Language];

        var key        = KeyAsString(context, entity);
        var entityName = MultiLingualMetadata.EntityNameFor(entity.Type);

        var chain = codes
            .Where(code => !options.IsBaseLanguage(code))
            .Select(code => Translation(context, entityName, member.Name, code, key))
            .ToList();

        if (options.HasBaseLanguage)
            chain.Add(column);

        var body = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--)
            body = Expression.Coalesce(chain[i], body);

        return body;
    }

    /// <summary>
    /// <c>translations.Where(t =&gt; t.EntityName == … &amp;&amp; t.EntityId == key).Select(t =&gt; t.Value).FirstOrDefault()</c>.
    /// </summary>
    private static Expression Translation(DbContext context, string entityName, string propertyName, string languageCode, Expression key)
    {
        var translations = context.Set<Translation>();

        Expression<Func<string, string?>> template = entityId => translations
            .Where(t => t.EntityName == entityName
                        && t.PropertyName == propertyName
                        && t.LanguageCode == languageCode
                        && t.EntityId == entityId)
            .Select(t => t.Value)
            .FirstOrDefault();

        return new ParameterReplacer(template.Parameters[0], key).Visit(template.Body);
    }

    /// <summary>
    /// The entity's key as the string <see cref="EntityKey.ToKeyString"/> stores. Guids are
    /// lower-cased: providers disagree on the case of a Guid cast to text, and the stored form is
    /// lower case.
    /// </summary>
    private static Expression KeyAsString(DbContext context, Expression entity)
    {
        var entityType = context.Model.FindEntityType(entity.Type) ?? throw new InvalidOperationException(
            $"'{entity.Type.Name}' is not an entity type of '{context.GetType().Name}'.");

        var keyProperty = EntityKeys.SingleKeyProperty(entityType);

        Expression key = keyProperty.PropertyInfo is not null || keyProperty.FieldInfo is not null
            ? Expression.PropertyOrField(entity, keyProperty.Name)
            : Expression.Call(typeof(EF), nameof(EF.Property), [keyProperty.ClrType], entity, Expression.Constant(keyProperty.Name));

        var keyType = Nullable.GetUnderlyingType(key.Type) ?? key.Type;

        if (keyType == typeof(string))
            return key;

        var asString = Expression.Call(key, key.Type.GetMethod(nameof(ToString), Type.EmptyTypes)!);

        return keyType == typeof(Guid)
            ? Expression.Call(asString, typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!)
            : asString;
    }

    private static MemberInfo TranslatableMember<T>(Expression<Func<T, string?>> property)
    {
        if (property.Body is not MemberExpression { Member: { } member } access || access.Expression != property.Parameters[0])
        {
            throw new ArgumentException(
                "Pass a translatable property of the entity itself, such as p => p.Name.", nameof(property));
        }

        if (!MultiLingualMetadata.For(typeof(T)).Any(p => string.Equals(p.Name, member.Name, StringComparison.Ordinal)))
            throw new ArgumentException($"'{typeof(T).Name}.{member.Name}' is not a translatable property.", nameof(property));

        return member;
    }

    /// <summary>
    /// Replaces every read of a translatable property of an entity — on the projection's own
    /// parameter, through a navigation, or inside a nested collection projection — with its
    /// translated value. Anything else, including translatable-looking properties on types that
    /// are not entities of this model, is left alone.
    /// </summary>
    private sealed class TranslatingVisitor(DbContext context, Resolution resolution) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is { } owner
                && node.Member is PropertyInfo { PropertyType: var type }
                && type == typeof(string)
                && context.Model.FindEntityType(owner.Type) is not null
                && MultiLingualMetadata.For(owner.Type).Any(p => string.Equals(p.Name, node.Member.Name, StringComparison.Ordinal)))
            {
                return Translated(context, resolution, Visit(owner), node.Member);
            }

            return base.VisitMember(node);
        }
    }

    /// <summary>The request's services — the scoped provider the context's options were built from.</summary>
    private static IServiceProvider ApplicationServices(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
        ?? throw new InvalidOperationException(
            $"'{context.GetType().Name}' has no MultiLingual services. Register it with " +
            "AddDbContext((sp, options) => options.UseMultiLingual(sp)).");

    /// <summary>The language to compare in, and the default it falls back to — both canonical.</summary>
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
                        ?? throw new LanguageNotFoundException(languageCode);
            return (match.Code, fallback);
        }

        return (current.IsResolved ? current.Current.Code : fallback, fallback);
    }

    private sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}
