using System.Linq.Expressions;
using System.Reflection;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Reads an entity's base-language columns from the database, for
/// <c>GetAllAsync(includeBase: true)</c>. What the tracked instance holds cannot be trusted for
/// that: a read puts the current language in its properties, and a save in another language leaves
/// that language as the original value EF remembers, while the column keeps the base text.
/// </summary>
/// <param name="context">The provider's context; types outside its model are not read.</param>
internal sealed class EfCoreBaseColumnReader(DbContext context) : IBaseColumnReader
{
    private static readonly MethodInfo ReadMethod =
        typeof(EfCoreBaseColumnReader).GetMethod(nameof(ReadCoreAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, string?>?> ReadAsync(object entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var clrType    = MultiLingualMetadata.EntityClass(entity.GetType());
        var entityType = context.Model.FindEntityType(clrType);
        if (entityType is null)
            return Task.FromResult<IReadOnlyDictionary<string, string?>?>(null);

        var keyProperty = EntityKeys.SingleKeyProperty(entityType);
        var key         = context.Entry(entity).Property(keyProperty.Name).CurrentValue;
        if (key is null)
            return Task.FromResult<IReadOnlyDictionary<string, string?>?>(null);

        return (Task<IReadOnlyDictionary<string, string?>?>)ReadMethod
            .MakeGenericMethod(clrType, keyProperty.ClrType)
            .Invoke(null, [context, keyProperty.Name, key, ct])!;
    }

    private static async Task<IReadOnlyDictionary<string, string?>?> ReadCoreAsync<TEntity, TKey>(
        DbContext context, string keyName, TKey key, CancellationToken ct)
        where TEntity : class
    {
        // e => EF.Property<TKey>(e, keyName) == key, with the key a parameter, not a constant, so
        // every entity shares one cached query.
        var entity  = Expression.Parameter(typeof(TEntity), "e");
        var keyRead = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(TKey)], entity, Expression.Constant(keyName));
        Expression<Func<TKey>> captured = () => key;
        var match = Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(keyRead, captured.Body), entity);

        var row = await context.Set<TEntity>().AsNoTracking().IgnoreTranslations().Where(match).FirstOrDefaultAsync(ct);
        if (row is null)
            return null;

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in MultiLingualMetadata.For(typeof(TEntity)))
            values[property.Name] = property.GetValue(row);

        return values;
    }
}
