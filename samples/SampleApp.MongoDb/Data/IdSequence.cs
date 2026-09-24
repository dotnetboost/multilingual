using MongoDB.Bson;
using MongoDB.Driver;

namespace SampleApp.MongoDb.Data;

/// <summary>
/// Hands out sequential <c>int</c> keys, which MongoDB does not generate. The EF Core sample gets
/// them from the database's identity column; here they come from a <c>counters</c> collection —
/// the standard MongoDB pattern, and atomic, because <c>findAndModify</c> is.
/// <para>
/// A greenfield MongoDB model would use an <c>ObjectId</c> and delete this: the driver assigns one
/// during the insert, and the provider treats <c>ObjectId.Empty</c> as "not saved yet", so saving
/// translations before the insert is refused rather than filed under an empty key. These documents
/// keep <c>int</c> keys so the responses, the routes and the dashboard are unchanged from the EF
/// Core sample.
/// </para>
/// </summary>
/// <param name="database">The database holding the counters collection.</param>
public sealed class IdSequence(IMongoDatabase database)
{
    private readonly IMongoCollection<BsonDocument> _counters = database.GetCollection<BsonDocument>("counters");

    /// <summary>The next key for <paramref name="name"/>, starting at 1.</summary>
    public async Task<int> NextAsync(string name, CancellationToken ct = default)
    {
        var updated = await _counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", name),
            Builders<BsonDocument>.Update.Inc("value", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            ct);

        return updated["value"].AsInt32;
    }

    /// <summary>
    /// Moves a counter up to <paramref name="value"/> if it is behind, so keys assigned by the
    /// seeder are not handed out again.
    /// </summary>
    public Task EnsureAtLeastAsync(string name, int value, CancellationToken ct = default) =>
        _counters.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", name),
            Builders<BsonDocument>.Update.Max("value", value),
            new UpdateOptions { IsUpsert = true },
            ct);
}
