using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Services;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace DotNetBoost.MultiLingual.MongoDb;

/// <summary>
/// <see cref="IEntityKeyAccessor"/> that reads the id the driver itself would write — the member
/// mapped to <c>_id</c>, whether by <c>[BsonId]</c>, a class map or the driver's conventions —
/// rather than by naming convention. Types with no mapped id fall back to
/// <see cref="ConventionEntityKeyAccessor"/>.
/// </summary>
internal sealed class MongoEntityKeyAccessor : IEntityKeyAccessor
{
    private static readonly ConventionEntityKeyAccessor Fallback = new();

    /// <inheritdoc/>
    public string? GetKey(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var idMember = BsonClassMap.LookupClassMap(MultiLingualMetadata.EntityClass(entity.GetType())).IdMemberMap;
        if (idMember is null)
            return Fallback.GetKey(entity);

        var value = idMember.Getter(entity);

        // ObjectId.Empty is what a document that has not been inserted yet holds: the driver
        // assigns the real id during the insert. EntityKey.IsUnset cannot recognise it — Core does
        // not reference the driver — and an empty ObjectId would otherwise read as a perfectly good
        // key, storing the entity's translations under 000000000000000000000000 instead of telling
        // the caller to save the entity first.
        if (value is ObjectId id && id == ObjectId.Empty)
            return null;

        return EntityKey.IsUnset(value) ? null : EntityKey.ToKeyString(value!);
    }
}
