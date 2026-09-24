using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace DotNetBoost.MultiLingual.MongoDb;

/// <summary>
/// Unmaps translatable properties, so an entity's own document never carries them: they live in
/// the translations collection, one document per language.
/// <para>
/// A convention rather than a pass over the loaded assemblies, because it has to answer two
/// questions late. The driver builds a class map the first time it serializes a type, which is
/// after the whole <c>AddMultiLingual(...).UseMongoDb(...).Build()</c> chain has run — so by then
/// <see cref="MultiLingualOptions.HasBaseLanguage"/> is final however the chain was ordered, and
/// there is no assembly scan to miss a type loaded later.
/// </para>
/// </summary>
/// <param name="options">The engine options; read when a class map is built, not now.</param>
internal sealed class UnmapTranslatablePropertiesConvention(MultiLingualOptions options)
    : ConventionBase("DotNetBoost.MultiLingual.UnmapTranslatableProperties"), IClassMapConvention
{
    /// <inheritdoc/>
    public void Apply(BsonClassMap classMap)
    {
        ArgumentNullException.ThrowIfNull(classMap);

        // In base-language mode a translatable property KEEPS its field: that field holds the base
        // language's value, which every missing translation falls back to. Unmapping it would
        // throw the base text away on the first save.
        if (options.HasBaseLanguage)
            return;

        var translatable = MultiLingualMetadata.For(classMap.ClassType)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Declared members only. An inherited property belongs to the base type's own class map,
        // which the driver builds — and this convention is applied to — separately.
        foreach (var member in classMap.DeclaredMemberMaps.Where(m => translatable.Contains(m.MemberName)).ToList())
            classMap.UnmapMember(member.MemberInfo);
    }
}

/// <summary>
/// The BSON mapping this provider needs: how <see cref="Translation"/> and <see cref="Language"/>
/// are stored, and the convention that keeps translatable properties out of the application's own
/// documents. Registered once per process by <c>UseMongoDb(...)</c>.
/// </summary>
internal static class MultiLingualBsonMapping
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>
    /// Registers the class maps and the convention. Idempotent, and it never overwrites a map the
    /// application registered for <see cref="Translation"/> or <see cref="Language"/> itself.
    /// </summary>
    /// <param name="options">The engine options the convention reads when it runs.</param>
    public static void Register(MultiLingualOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (Gate)
        {
            if (_registered)
                return;

            RegisterTranslation();
            RegisterLanguage();

            // Filtered to decorated types, so the driver's own mapping of everything else is
            // untouched. MultiLingualMetadata caches per type, so the filter costs one reflection
            // pass per class the driver maps.
            ConventionRegistry.Register(
                "DotNetBoost.MultiLingual",
                new ConventionPack { new UnmapTranslatablePropertiesConvention(options) },
                MultiLingualMetadata.IsMultiLingual);

            _registered = true;
        }
    }

    /// <summary>
    /// <see cref="Translation"/> has no surrogate key, and deliberately no <c>_id</c> member: the
    /// server generates one, the unique index on the natural key enforces identity, and that
    /// index's leading fields are what every read filters on. Mapping the natural key as a
    /// composite <c>_id</c> instead would cost those reads the index, since a query on the
    /// sub-fields of <c>_id</c> cannot use it.
    /// </summary>
    private static void RegisterTranslation()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(Translation)))
            return;

        BsonClassMap.RegisterClassMap<Translation>(map =>
        {
            map.AutoMap();
            // The server-generated _id comes back on every read and maps to nothing.
            map.SetIgnoreExtraElements(true);
        });
    }

    /// <summary>
    /// <see cref="Language.Code"/> IS the language's identity, so it maps to <c>_id</c>: uniqueness
    /// and the lookup index come for free, and no second index has to be kept in step with it.
    /// </summary>
    private static void RegisterLanguage()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(Language)))
            return;

        BsonClassMap.RegisterClassMap<Language>(map =>
        {
            map.AutoMap();
            map.MapIdMember(l => l.Code);
            // As a string, so the stored document reads as "Rtl" rather than 1 and reordering the
            // enum cannot silently reinterpret existing data.
            map.MapMember(l => l.Direction).SetSerializer(new EnumSerializer<TextDirection>(BsonType.String));
            map.SetIgnoreExtraElements(true);
        });
    }
}
