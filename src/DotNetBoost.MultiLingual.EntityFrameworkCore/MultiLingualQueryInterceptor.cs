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
///
/// <para>
/// An <c>ExecuteUpdate</c> whose <c>SetProperty</c> targets a translatable property is refused
/// outright, in every mode: <c>ExecuteUpdate</c> bypasses <c>SaveChanges</c> entirely, so none of
/// <see cref="MultiLingualSaveChangesInterceptor"/>'s base-language rules would ever see it, and in
/// base-language mode the property is a real column — a silent, unchecked write to it either
/// corrupts the base language's canonical text or (whichever language happens to be current when
/// the query first compiles) writes some other language's text into it instead.
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

        // Checked on the ORIGINAL tree, before Rewrite and regardless of IgnoreTranslations() /
        // InLanguage(): ExecuteUpdate never runs through SaveChanges, so none of the interceptor's
        // base-language rules ever see it. Left unchecked, SetProperty(p => translatableProperty, …)
        // would compile: in the default mode EF itself refuses it (the property has no column), but
        // in base-language mode the property IS a column, so it would silently write straight into
        // it — whatever the request's language — bypassing BaseLanguageRequiredException and the
        // "don't promote a fallback" tracker alike. One compiled query shape is reused for every
        // request, so this cannot be allowed for the base language and refused for others; it is
        // refused outright, with the same store-and-load path (multiLingual.For(entity).Set(…) plus
        // SaveChanges) as everywhere else translations are written.
        var setTarget = new TranslatableSetPropertyTarget(context.Model);
        setTarget.Visit(queryExpression);

        if (setTarget.Found is { } set)
        {
            throw new InvalidOperationException(
                $"This ExecuteUpdate call sets '{set.DeclaringType}.{set.Property}' with SetProperty, but it is " +
                "translatable: its value belongs in the translations table, not a plain column write, and " +
                "ExecuteUpdate never runs through SaveChanges, so none of the usual rules can catch a mistake here. " +
                "Load the entity and call multiLingual.For(entity).Set(languageCode, p => p.Property, value), then " +
                "SaveChanges — or, to change the base language's own value, assign the property and call " +
                "SaveChanges instead of ExecuteUpdate.");
        }

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

    /// <summary>
    /// <c>ExecuteUpdate</c>'s <c>SetProperty(p => p.Name, value)</c>: true when the property it
    /// names is translatable.
    /// <para>
    /// EF Core represents the call two different ways depending on version, so both are matched:
    /// on EF Core 8 it survives as a literal two-argument <c>SetProperty</c> method call (matched
    /// by name, like <see cref="QueryTranslation"/>'s own handling of the same call — the setter
    /// type is <c>SetPropertyCalls&lt;T&gt;</c>); from EF Core 9 the fluent setter builder
    /// (<c>UpdateSettersBuilder&lt;T&gt;</c>) is evaluated eagerly, in C#, before the query
    /// expression is ever built, and what reaches this interceptor is the pairs it recorded, as
    /// plain <c>Tuple&lt;,&gt;</c> construction — the selector lambda no longer named "SetProperty"
    /// anywhere in the tree at all.
    /// </para>
    /// </summary>
    private sealed class TranslatableSetPropertyTarget(IModel model) : ExpressionVisitor
    {
        public (string DeclaringType, string Property)? Found { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (Found is null && node.Method.Name == "SetProperty" && node.Arguments.Count == 2 && TargetOf(node.Arguments[0]) is { } target)
                Found = target;

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            if (Found is null
                && node.Arguments.Count == 2
                && node.Type.IsGenericType
                && node.Type.GetGenericTypeDefinition() == typeof(Tuple<,>)
                && TargetOf(node.Arguments[0]) is { } target)
            {
                Found = target;
            }

            return base.VisitNew(node);
        }

        private (string DeclaringType, string Property)? TargetOf(Expression propertySelector)
        {
            var lambda = propertySelector switch
            {
                UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression l } => l,
                LambdaExpression l => l,
                _ => null
            };

            if (lambda?.Body is not MemberExpression { Expression: { } owner, Member: PropertyInfo } member)
                return null;

            return model.FindEntityType(owner.Type) is not null
                   && MultiLingualMetadata.For(owner.Type).Any(p => string.Equals(p.Name, member.Member.Name, StringComparison.Ordinal))
                ? (owner.Type.Name, member.Member.Name)
                : null;
        }
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
