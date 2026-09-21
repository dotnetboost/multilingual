using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>
/// Default <see cref="IEntityKeyAccessor"/>, for providers with no model to consult. The key
/// is, in order: the single property marked <see cref="KeyAttribute"/>, a property named
/// <c>Id</c>, or a property named <c>{TypeName}Id</c>.
/// </summary>
public sealed class ConventionEntityKeyAccessor : IEntityKeyAccessor
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo> KeyProperties = new();

    /// <inheritdoc/>
    public string? GetKey(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var property = KeyProperties.GetOrAdd(entity.GetType(), static type => FindKey(type));
        var value    = property.GetValue(entity);

        return EntityKey.IsUnset(value) ? null : EntityKey.ToKeyString(value!);
    }

    private static PropertyInfo FindKey(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
            .ToArray();

        var annotated = properties.Where(p => p.GetCustomAttribute<KeyAttribute>(inherit: true) is not null).ToArray();
        if (annotated.Length > 1)
        {
            throw new NotSupportedException(
                $"'{type.Name}' has a composite key ({string.Join(", ", annotated.Select(p => p.Name))}). " +
                "Translation.EntityId holds a single value; add a surrogate key before marking it [MultiLingual].");
        }

        return annotated.FirstOrDefault()
               ?? properties.FirstOrDefault(p => p.Name == "Id")
               ?? properties.FirstOrDefault(p => p.Name == type.Name + "Id")
               ?? throw new InvalidOperationException(
                   $"Cannot find the key of '{type.Name}'. Mark it with [Key], name it 'Id' or " +
                   $"'{type.Name}Id', or register a custom IEntityKeyAccessor.");
    }
}
