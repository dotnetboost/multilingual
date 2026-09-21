using DotNetBoost.MultiLingual.Core.Attributes;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DotNetBoost.MultiLingual.Core.Metadata;

/// <summary>
/// A single translatable property, with compiled get/set delegates so the read and write paths
/// never pay reflection cost per row.
/// </summary>
public sealed class TranslatableProperty
{
    private readonly Func<object, string?> _getter;
    private readonly Action<object, string?> _setter;

    internal TranslatableProperty(PropertyInfo property)
    {
        Name    = property.Name;
        _getter = BuildGetter(property);
        _setter = BuildSetter(property);
    }

    /// <summary>The CLR property name, which is also what <c>Translation.PropertyName</c> stores.</summary>
    public string Name { get; }

    /// <summary>Reads the property off <paramref name="entity"/>.</summary>
    public string? GetValue(object entity) => _getter(entity);

    /// <summary>Assigns the property on <paramref name="entity"/>.</summary>
    public void SetValue(object entity, string? value) => _setter(entity, value);

    private static Func<object, string?> BuildGetter(PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var typed    = Expression.Convert(instance, property.DeclaringType!);
        var body     = Expression.Property(typed, property);
        return Expression.Lambda<Func<object, string?>>(body, instance).Compile();
    }

    private static Action<object, string?> BuildSetter(PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value    = Expression.Parameter(typeof(string), "value");
        var typed    = Expression.Convert(instance, property.DeclaringType!);
        var body     = Expression.Assign(Expression.Property(typed, property), value);
        return Expression.Lambda<Action<object, string?>>(body, instance, value).Compile();
    }
}

/// <summary>
/// Reflection cache that answers "which properties of this CLR type are translatable?".
/// Computed once per type and reused for the lifetime of the process. Provider-neutral:
/// every provider asks this, never the attributes directly.
/// </summary>
public static class MultiLingualMetadata
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<TranslatableProperty>> Cache = new();
    private static readonly ConcurrentDictionary<Type, string> EntityNames = new();

    /// <summary>The longest name <c>Translation.EntityName</c> can hold.</summary>
    public const int MaxEntityNameLength = 128;

    private static readonly IReadOnlyList<TranslatableProperty> None = [];

    /// <summary>
    /// Returns the translatable properties for <paramref name="clrType"/>, or an empty list
    /// when the type carries no <see cref="MultiLingualAttribute"/> at all.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The attribute is applied somewhere it cannot work: a non-string property, a property
    /// without a public setter, or an <c>init</c>-only setter.
    /// </exception>
    public static IReadOnlyList<TranslatableProperty> For(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return Cache.GetOrAdd(clrType, static type => Build(type));
    }

    /// <summary>True when the type has at least one translatable property.</summary>
    public static bool IsMultiLingual(Type clrType) => For(clrType).Count > 0;

    /// <summary>True when <paramref name="clrType"/> carries the attribute at class or property level.</summary>
    public static bool IsDecorated(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return clrType.GetCustomAttribute<MultiLingualAttribute>(inherit: true) is not null
               || clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .Any(p => p.GetCustomAttribute<MultiLingualAttribute>(inherit: true) is not null);
    }

    /// <summary>
    /// The value stored in <c>Translation.EntityName</c>: the name fixed by
    /// <see cref="MultiLingualEntityAttribute"/> or <c>[MultiLingual(EntityName = ...)]</c>,
    /// otherwise the CLR type name (not the full name, so moving a type between namespaces
    /// changes nothing). Types sharing a name are rejected by <c>Build()</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The fixed name is blank or too long, or both attributes fix one.
    /// </exception>
    public static string EntityNameFor(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return EntityNames.GetOrAdd(clrType, static type => ResolveEntityName(type));
    }

    private static string ResolveEntityName(Type type)
    {
        var fromEntity = type.GetCustomAttribute<MultiLingualEntityAttribute>(inherit: true)?.Name;
        var fromClass  = type.GetCustomAttribute<MultiLingualAttribute>(inherit: true)?.EntityName;

        if (fromEntity is not null && fromClass is not null)
        {
            throw new InvalidOperationException(
                $"'{type.Name}' names its translation rows twice, with [MultiLingualEntity(\"{fromEntity}\")] " +
                $"and [MultiLingual(EntityName = \"{fromClass}\")]. Keep one.");
        }

        var name = fromEntity ?? fromClass;
        if (name is null)
            return type.Name;

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxEntityNameLength || name.Trim().Length != name.Length)
        {
            throw new InvalidOperationException(
                $"'{type.Name}' has an invalid entity name '{name}': it must be non-blank, have no leading " +
                $"or trailing spaces, and be at most {MaxEntityNameLength} characters.");
        }

        return name;
    }

    private static IReadOnlyList<TranslatableProperty> Build(Type type)
    {
        var classLevel = type.GetCustomAttribute<MultiLingualAttribute>(inherit: true) is not null;

        var candidates = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToArray();

        var propertyLevel = candidates
            .Where(p => p.GetCustomAttribute<MultiLingualAttribute>(inherit: true) is not null)
            .ToArray();

        // Fail fast on [MultiLingual] applied to a non-string property: silently ignoring it
        // would leave the developer with a member that never translates and no explanation.
        foreach (var property in propertyLevel.Where(p => p.PropertyType != typeof(string)))
        {
            throw new InvalidOperationException(
                $"[MultiLingual] on '{type.Name}.{property.Name}' is invalid: only string properties " +
                $"can be translated, but this property is '{property.PropertyType.Name}'.");
        }

        // The stored name belongs to the type, not to one property; accepting it here would
        // raise the question of which property's name wins.
        foreach (var property in propertyLevel.Where(p => p.GetCustomAttribute<MultiLingualAttribute>(inherit: true)!.EntityName is not null))
        {
            throw new InvalidOperationException(
                $"[MultiLingual(EntityName = ...)] on '{type.Name}.{property.Name}' is invalid: the entity " +
                "name is set once per class. Put [MultiLingualEntity(\"...\")] on the class instead.");
        }

        if (!classLevel && propertyLevel.Length == 0)
            return None;

        // Conflict rule: class-level wins. See MultiLingualAttribute for the rationale.
        var selected = classLevel
            ? candidates.Where(IsEligibleUnderClassLevel)
            : propertyLevel.AsEnumerable();

        var result = selected
            .Where(HasUsableAccessors)
            .Select(p => new TranslatableProperty(p))
            .ToArray();

        return result.Length == 0 ? None : result;
    }

    private static bool IsEligibleUnderClassLevel(PropertyInfo property) =>
        property.PropertyType == typeof(string)
        && property.GetCustomAttribute<NotMultiLingualAttribute>(inherit: true) is null
        && !IsConventionalKey(property);

    /// <summary>
    /// Excludes obvious key properties from class-level application. Providers re-check the
    /// real key before relying on this (the EF Core provider refuses to ignore a key property).
    /// </summary>
    private static bool IsConventionalKey(PropertyInfo property) =>
        string.Equals(property.Name, "Id", StringComparison.Ordinal)
        || string.Equals(property.Name, property.DeclaringType?.Name + "Id", StringComparison.Ordinal);

    private static bool HasUsableAccessors(PropertyInfo property)
    {
        if (property.GetMethod is null || !property.GetMethod.IsPublic)
            return false;

        var setter = property.SetMethod;
        if (setter is null || !setter.IsPublic)
        {
            throw new InvalidOperationException(
                $"Translatable property '{property.DeclaringType?.Name}.{property.Name}' needs a public " +
                "setter so the read path can populate it after the entity is loaded.");
        }

        // init-only setters cannot be assigned outside an object initializer.
        var isInitOnly = setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m == typeof(IsExternalInit));

        if (isInitOnly)
        {
            throw new InvalidOperationException(
                $"Translatable property '{property.DeclaringType?.Name}.{property.Name}' uses an 'init' " +
                "setter. Use a normal 'set' so translations can be applied after the entity is loaded.");
        }

        return true;
    }
}
