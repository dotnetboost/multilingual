using System.Linq.Expressions;
using System.Reflection;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Translates every query on the context when EF compiles it — see <see cref="QueryTranslation"/>
/// — unless the query opts out with <see cref="MultiLingualQueryableExtensions.IgnoreTranslations"/>.
///
/// <para>
/// An opted-out query that reads a translatable property the database has no column for (the
/// default mode) is refused. EF would reject it in a <c>Where</c> or <c>OrderBy</c> with a generic
/// "could not be translated" — and in a final <c>Select</c> not at all: it would quietly project
/// <c>null</c>. This turns both into one clear error that names the property. In base-language mode
/// the property has a column, so an opted-out query reads the base language and nothing is refused.
/// </para>
///
/// <para>
/// EF calls it once per query shape, before caching the compiled query, so it must not depend on
/// anything that varies between executions, such as the request's language: the rewritten query
/// holds <see cref="LanguageTokens"/>, which <see cref="LanguageTokenInterceptor"/> fills in per
/// execution. Shared and stateless, because EF treats query expression interceptors as singletons.
/// </para>
/// </summary>
internal sealed class MultiLingualQueryInterceptor : IQueryExpressionInterceptor
{
    /// <summary>The shared instance <see cref="MultiLingualDbContextOptionsExtensions.UseMultiLingual"/> attaches.</summary>
    public static MultiLingualQueryInterceptor Instance { get; } = new();

    private MultiLingualQueryInterceptor() { }

    /// <inheritdoc/>
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(queryExpression);
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not { } context)
            return queryExpression;

        queryExpression = QueryTranslation.Rewrite(queryExpression, context);

        var finder = new UnmappedTranslatableRead(context.Model);
        finder.Visit(queryExpression);

        if (finder.Found is { } read)
        {
            throw new InvalidOperationException(
                $"This query reads '{read.DeclaringType}.{read.Property}', which is translatable and has no column: its " +
                "values live in the translations table, and this query opts out of translation with " +
                "IgnoreTranslations(), so a Select would return null and a Where or OrderBy cannot run. " +
                "Remove IgnoreTranslations() to read it in the request's language.");
        }

        return queryExpression;
    }

    private sealed class UnmappedTranslatableRead(IModel model) : ExpressionVisitor
    {
        public (string DeclaringType, string Property)? Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (Found is null
                && node.Expression is { } owner
                && node.Member is PropertyInfo
                && model.FindEntityType(owner.Type) is { } entityType
                && entityType.FindProperty(node.Member.Name) is null
                && MultiLingualMetadata.For(owner.Type).Any(p => string.Equals(p.Name, node.Member.Name, StringComparison.Ordinal)))
            {
                Found = (owner.Type.Name, node.Member.Name);
            }

            return base.VisitMember(node);
        }
    }
}
