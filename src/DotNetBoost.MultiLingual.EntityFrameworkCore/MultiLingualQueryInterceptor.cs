using System.Linq.Expressions;
using System.Reflection;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Refuses a query that reads a translatable property the database has no column for.
///
/// <para>
/// In the default mode a translatable property is not mapped. EF rejects it in a <c>Where</c> or
/// <c>OrderBy</c> with a generic "could not be translated" — and in a final <c>Select</c> it does
/// not reject it at all: it materializes the entity, whose translations were never loaded, and
/// quietly projects <c>null</c>. This turns both into one clear error that names the property and
/// the helpers that do read it: <see cref="TranslatedQueryExtensions.SelectTranslated"/>,
/// <see cref="TranslatedQueryExtensions.WhereTranslation"/>,
/// <see cref="TranslatedQueryExtensions.OrderByTranslation"/>, or loading whole entities with
/// <see cref="LocalizedQueryExtensions.ToLocalizedListAsync"/>.
/// </para>
///
/// <para>
/// In base-language mode the property has a column, so a plain query is valid and reads the base
/// language — which is sometimes exactly what is wanted — and nothing is refused.
/// </para>
///
/// <para>
/// It only inspects: EF calls it once per query shape, before caching the compiled query, so it
/// must not depend on anything that varies between executions, such as the request's language.
/// Shared and stateless, like <see cref="MultiLingualMaterializationInterceptor"/>, because EF
/// treats query expression interceptors as singletons.
/// </para>
/// </summary>
public sealed class MultiLingualQueryInterceptor : IQueryExpressionInterceptor
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

        var finder = new UnmappedTranslatableRead(context.Model);
        finder.Visit(queryExpression);

        if (finder.Found is { } read)
        {
            throw new InvalidOperationException(
                $"This query reads '{read.DeclaringType}.{read.Property}', which is translatable and has no column: its " +
                "values live in the translations table, so a plain Select would return null and a Where or " +
                $"OrderBy cannot run. Use SelectTranslated(db, …) to project it, WhereTranslation / OrderByTranslation " +
                "to filter or sort on it, or load whole entities with ToLocalizedListAsync.");
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
