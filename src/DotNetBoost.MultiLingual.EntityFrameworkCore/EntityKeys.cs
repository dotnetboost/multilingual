using System.Linq.Expressions;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// <see cref="IEntityKeyAccessor"/> that reads keys from the EF model rather than by naming
/// convention, so any key EF understands works — including shadow keys on tracked entities.
/// Types outside the model fall back to <see cref="ConventionEntityKeyAccessor"/>.
/// </summary>
/// <param name="context">The context whose model describes the entities.</param>
public sealed class EfCoreEntityKeyAccessor(DbContext context) : IEntityKeyAccessor
{
    private static readonly ConventionEntityKeyAccessor Fallback = new();

    /// <inheritdoc/>
    public string? GetKey(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entityType = context.Model.FindEntityType(entity.GetType());
        if (entityType is null)
            return Fallback.GetKey(entity);

        var keyProperty = EntityKeys.SingleKeyProperty(entityType);

        // Public metadata only: PropertyInfo for a normal property, FieldInfo for a backing
        // field, and the change tracker for a shadow key (which only a tracked entity carries).
        var value = keyProperty.PropertyInfo is { } info ? info.GetValue(entity)
            : keyProperty.FieldInfo is { } field ? field.GetValue(entity)
            : context.Entry(entity).Property(keyProperty.Name).CurrentValue;

        return EntityKey.IsUnset(value) ? null : EntityKey.ToKeyString(value!);
    }
}

/// <summary>Key helpers shared by the interceptors and the accessor.</summary>
internal static class EntityKeys
{
    /// <summary>
    /// Reads the key off a tracked entry. Returns <c>null</c> when the key has not been
    /// generated yet (an <c>Added</c> entity before <c>SaveChanges</c> completes) — which is
    /// exactly why translation rows are written after the save, not before it.
    /// </summary>
    public static string? TryGetKey(EntityEntry entry)
    {
        var keyProperty = SingleKeyProperty(entry.Metadata);
        var value       = entry.Property(keyProperty.Name).CurrentValue;

        return EntityKey.IsUnset(value) ? null : EntityKey.ToKeyString(value!);
    }

    public static IReadOnlyProperty SingleKeyProperty(IReadOnlyEntityType entityType)
    {
        var key = entityType.FindPrimaryKey() ?? throw new InvalidOperationException(
            $"Entity '{entityType.DisplayName()}' has no primary key, so its translations cannot be " +
            "addressed. Give it a key or remove [MultiLingual].");

        if (key.Properties.Count != 1)
        {
            throw new NotSupportedException(
                $"Entity '{entityType.DisplayName()}' has a composite primary key. " +
                "Translation.EntityId holds a single value; add a surrogate key before marking it [MultiLingual].");
        }

        return key.Properties[0];
    }

    /// <summary>
    /// A query expression for <paramref name="entity"/>'s key as the string
    /// <see cref="EntityKey.ToKeyString"/> stores, so it can be compared with
    /// <c>Translation.EntityId</c> in SQL. Guids are lower-cased: providers disagree on the case of
    /// a Guid cast to text, and the stored form is lower case.
    /// </summary>
    public static Expression KeyAsString(DbContext context, Expression entity)
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
}
