using System.Linq.Expressions;
using System.Reflection;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Removing translation rows whose entity no longer exists.
///
/// <para><b>Where orphans come from.</b> The save interceptor removes an entity's translations
/// when <c>SaveChanges</c> deletes it. Deletes that never pass through <c>SaveChanges</c> leave
/// them behind:</para>
/// <list type="bullet">
///   <item><c>ExecuteDelete</c> / <c>ExecuteDeleteAsync</c>.</item>
///   <item>A database cascade deleting rows EF never loaded — deleting a category whose products
///   the database cascades away, say.</item>
///   <item>Raw SQL, and other applications writing to the same database.</item>
/// </list>
/// <para>An orphan is never read, so mostly it only costs space — until an entity is created
/// again under the same key (a string or Guid key can come back), and silently inherits the old
/// entity's translations.</para>
/// </summary>
public static class OrphanedTranslationExtensions
{
    private static readonly MethodInfo SetMethod =
        typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;

    private static readonly MethodInfo AnyMethod = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2);

    /// <summary>
    /// Deletes every translation row whose entity no longer exists, and returns how many were
    /// removed. One set-based <c>DELETE … WHERE NOT EXISTS</c> per translatable entity type, run
    /// by the database; safe to schedule, and to call after a bulk delete:
    /// <code>
    /// await db.Products.Where(p =&gt; p.Discontinued).ExecuteDeleteAsync(ct);
    /// await db.PurgeOrphanedTranslationsAsync(ct);
    /// </code>
    /// <para>
    /// Only rows filed under an entity name this context's model knows are considered. A row for
    /// any other name is left alone: it may belong to another context sharing the table, or to a
    /// class renamed without fixing its entity name — see <c>[MultiLingualEntity]</c>.
    /// </para>
    /// <para>
    /// Run it on a context with no pending translation changes, such as one from a fresh scope:
    /// the rows are deleted in the database, not through the change tracker.
    /// </para>
    /// </summary>
    public static async Task<int> PurgeOrphanedTranslationsAsync<TContext>(this TContext context, CancellationToken ct = default)
        where TContext : DbContext, IMultiLingualDbContext
    {
        ArgumentNullException.ThrowIfNull(context);

        var removed = 0;
        foreach (var orphans in OrphanQueries(context))
            removed += await orphans.ExecuteDeleteAsync(ct);

        return removed;
    }

    /// <summary>Synchronous <see cref="PurgeOrphanedTranslationsAsync{TContext}"/>.</summary>
    public static int PurgeOrphanedTranslations<TContext>(this TContext context)
        where TContext : DbContext, IMultiLingualDbContext
    {
        ArgumentNullException.ThrowIfNull(context);
        return OrphanQueries(context).Sum(orphans => orphans.ExecuteDelete());
    }

    /// <summary>
    /// For each entity name in the model, the translation rows filed under it whose entity is
    /// gone. Types sharing a name — a hierarchy with <c>[MultiLingualEntity]</c> on its base — are
    /// checked together: a row is an orphan only if no entity of any of them has its key.
    /// </summary>
    private static IEnumerable<IQueryable<Translation>> OrphanQueries(DbContext context)
    {
        var byName = context.Model.GetEntityTypes()
            .Where(IsAddressable)
            .GroupBy(e => MultiLingualMetadata.EntityNameFor(e.ClrType), StringComparer.Ordinal);

        foreach (var group in byName)
        {
            var translation = Expression.Parameter(typeof(Translation), "t");
            var entityId    = Expression.Property(translation, nameof(Translation.EntityId));

            Expression? exists = null;
            foreach (var entityType in group)
            {
                var anyWithThisKey = AnyWithKey(context, entityType.ClrType, entityId);
                exists = exists is null ? anyWithThisKey : Expression.OrElse(exists, anyWithThisKey);
            }

            var sameName = Expression.Equal(
                Expression.Property(translation, nameof(Translation.EntityName)),
                Expression.Constant(group.Key));

            var orphan = Expression.Lambda<Func<Translation, bool>>(
                Expression.AndAlso(sameName, Expression.Not(exists!)), translation);

            yield return context.Set<Translation>().Where(orphan);
        }
    }

    /// <summary><c>context.Set&lt;TEntity&gt;().Any(e =&gt; key(e) == t.EntityId)</c>.</summary>
    private static MethodCallExpression AnyWithKey(DbContext context, Type clrType, Expression entityId)
    {
        var set    = (IQueryable)SetMethod.MakeGenericMethod(clrType).Invoke(context, null)!;
        var entity = Expression.Parameter(clrType, "e");
        var match  = Expression.Lambda(Expression.Equal(EntityKeys.KeyAsString(context, entity), entityId), entity);

        return Expression.Call(AnyMethod.MakeGenericMethod(clrType), set.Expression, match);
    }

    /// <summary>
    /// A translatable entity type whose rows can be matched to translations: its own table-backed
    /// type with a single-column key. Owned and keyless types never have translation rows of
    /// their own.
    /// </summary>
    private static bool IsAddressable(IEntityType entityType) =>
        MultiLingualMetadata.IsMultiLingual(entityType.ClrType)
        && !entityType.IsOwned()
        && entityType.FindPrimaryKey() is { Properties.Count: 1 };
}
