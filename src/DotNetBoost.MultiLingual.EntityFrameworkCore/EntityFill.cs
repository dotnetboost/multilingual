using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// The translated values a query loads next to one entity: for each translatable property, in
/// <see cref="MultiLingualMetadata.For"/> order, the stored value in the requested language and in
/// its fallback — and the same for the included navigations.
/// </summary>
internal sealed class LoadedValues
{
    /// <summary>The entity's key as stored in <c>Translation.EntityId</c>; set for collection items, to match them.</summary>
    public string? Key { get; set; }

    /// <summary>Two per translatable property: requested language, then fallback.</summary>
    public string?[] Values { get; set; } = [];

    /// <summary>Included navigations, or null.</summary>
    public NavigationValues[]? Navigations { get; set; }
}

/// <summary>The values for one included navigation.</summary>
internal sealed class NavigationValues
{
    /// <summary>The navigation's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>For a reference navigation.</summary>
    public LoadedValues? Single { get; set; }

    /// <summary>For a collection navigation.</summary>
    public List<LoadedValues>? Items { get; set; }
}

/// <summary>
/// The read path. EF materializes an entity; the query's projection — added by
/// <see cref="QueryTranslation"/> — then hands it to <see cref="Fill{T}"/> together with its
/// translated values, which were loaded in the same SQL statement. No second query, and nothing
/// for application code to call.
/// <para>
/// <b>Finding the request.</b> The projection is compiled once and shared by every request, so
/// it cannot carry the request's services. <see cref="MultiLingualMaterializationInterceptor"/>
/// records which context materialized each entity (<see cref="Record"/>); <see cref="Fill{T}"/>
/// looks it up and serves the values through that context's scoped engine.
/// </para>
/// </summary>
internal static class EntityFill
{
    internal static readonly System.Reflection.MethodInfo FillMethod =
        typeof(EntityFill).GetMethod(nameof(Fill), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

    /// <summary>What materialization left for the fill: the context, and the values as materialized.</summary>
    private sealed class Materialized(DbContext context, Dictionary<string, string?>? values)
    {
        public WeakReference<DbContext> Context { get; } = new(context);

        public Dictionary<string, string?>? Values { get; set; } = values;
    }

    /// <summary>The engine of one context's scope, resolved once per context.</summary>
    private sealed record Scope(TranslationManager Engine, IEntityKeyAccessor Keys);

    private static readonly ConditionalWeakTable<object, Materialized> Entities = new();
    private static readonly ConditionalWeakTable<DbContext, Scope> Scopes = new();
    private static readonly ConcurrentDictionary<(Type, string), (Func<object, object?> Get, Type Target)?> Navigations = new();

    /// <summary>Called by the materialization interceptor for every entity that may need filling.</summary>
    public static void Record(object entity, DbContext context)
    {
        var properties = MultiLingualMetadata.For(entity.GetType());

        Dictionary<string, string?>? values = null;
        if (properties.Count > 0)
        {
            values = new Dictionary<string, string?>(properties.Count, StringComparer.Ordinal);
            foreach (var property in properties)
                values[property.Name] = property.GetValue(entity);
        }

        Entities.AddOrUpdate(entity, new Materialized(context, values));
    }

    /// <summary>Called once per result row by the query's projection.</summary>
    internal static T Fill<T>(T entity, LoadedValues? values, string? language)
    {
        if (entity is null || values is null)
            return entity;

        if (!Entities.TryGetValue(entity, out var materialized) || !materialized.Context.TryGetTarget(out var context))
            return entity;   // not materialized by a MultiLingual context — nothing to serve it with

        var scope = Scopes.GetValue(context, static c =>
        {
            var services = MultiLingualServices.For(c);
            return new Scope(services.GetRequiredService<TranslationManager>(), services.GetRequiredService<IEntityKeyAccessor>());
        });

        // Synchronous, and it has to be: this runs inside the projection EF compiled into the
        // query, which is a plain Func invoked per row. It is cheap anyway — the command that
        // produced this row has already been through LanguageTokenInterceptor, which asked for the
        // same languages and left the catalog's snapshot fresh.
        var (served, fallback) = scope.Engine.ReadLanguages(language);
        Apply(entity, typeof(T), values, context, scope, served, fallback);
        return entity;
    }

    /// <param name="entity">The entity.</param>
    /// <param name="declared">The type the query declared for it — the one its values were built for.</param>
    /// <param name="values">Its values.</param>
    /// <param name="context">The context that materialized it.</param>
    /// <param name="scope">That context's engine.</param>
    /// <param name="language">The language served.</param>
    /// <param name="fallback">Its fallback, or null.</param>
    private static void Apply(object entity, Type declared, LoadedValues values, DbContext context, Scope scope, string language, string? fallback)
    {
        if (values.Values.Length > 0)
        {
            Dictionary<string, string?>? materialized = null;
            if (Entities.TryGetValue(entity, out var record))
            {
                // The snapshot says "as this query materialized it" only once: a later query that
                // finds the entity already tracked must see edits made in between.
                materialized  = record.Values;
                record.Values = null;
            }

            var (properties, loaded) = Applicable(context.Model, declared, entity.GetType(), values.Values);
            scope.Engine.ApplyLoaded(entity, properties, language, fallback, loaded, materialized);
        }

        if (values.Navigations is null)
            return;

        foreach (var navigation in values.Navigations)
        {
            if (Navigation(context.Model, declared, navigation.Name) is not { } member || member.Get(entity) is not { } target)
                continue;

            if (navigation.Single is { } single)
            {
                Apply(target, member.Target, single, context, scope, language, fallback);
            }
            else if (navigation.Items is { Count: > 0 } items && target is IEnumerable children)
            {
                var byKey = new Dictionary<string, LoadedValues>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    if (item.Key is not null)
                        byKey.TryAdd(item.Key, item);
                }

                foreach (var child in children)
                {
                    if (child is not null && scope.Keys.GetKey(child) is { } key && byKey.TryGetValue(key, out var childValues))
                        Apply(child, member.Target, childValues, context, scope, language, fallback);
                }
            }
        }
    }

    /// <summary>
    /// The properties <paramref name="values"/> were loaded for — those of the query's declared type
    /// and of its derived types — narrowed to the ones the entity's actual type has.
    /// </summary>
    private static (IReadOnlyList<TranslatableProperty> Properties, IReadOnlyList<string?> Values) Applicable(
        IModel model, Type declared, Type actual, string?[] values)
    {
        var hierarchy = QueryTranslation.Hierarchy(model, declared);
        var entityClass = MultiLingualMetadata.EntityClass(actual);

        if (hierarchy.All(p => p.Owner.IsAssignableFrom(entityClass)) && hierarchy.Count * 2 == values.Length)
            return (hierarchy.Select(p => p.Property).ToList(), values);

        var own        = MultiLingualMetadata.For(entityClass);
        var properties = new List<TranslatableProperty>();
        var applicable = new List<string?>();

        for (var i = 0; i < hierarchy.Count && (2 * i) + 1 < values.Length; i++)
        {
            if (!hierarchy[i].Owner.IsAssignableFrom(entityClass)
                || own.FirstOrDefault(p => string.Equals(p.Name, hierarchy[i].Property.Name, StringComparison.Ordinal)) is not { } property)
            {
                continue;
            }

            properties.Add(property);
            applicable.Add(values[2 * i]);
            applicable.Add(values[(2 * i) + 1]);
        }

        return (properties, applicable);
    }

    /// <summary>How to read a navigation, and the entity type it leads to.</summary>
    private static (Func<object, object?> Get, Type Target)? Navigation(IModel model, Type type, string name) =>
        Navigations.GetOrAdd((type, name), static (key, model) =>
        {
            var entityType = model.FindEntityType(key.Item1);
            INavigationBase? navigation = (INavigationBase?)entityType?.FindNavigation(key.Item2) ?? entityType?.FindSkipNavigation(key.Item2);
            if (navigation is null)
                return null;

            var getter = navigation.GetGetter();
            return (getter.GetClrValue, navigation.TargetEntityType.ClrType);
        }, model);
}
