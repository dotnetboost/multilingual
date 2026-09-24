namespace DotNetBoost.MultiLingual.MongoDb;

/// <summary>
/// Collection naming and index behaviour for the two collections this provider adds to your
/// database. Configured through the <c>configure</c> argument of <c>UseMongoDb(...)</c>.
/// </summary>
public sealed class MongoDbOptions
{
    /// <summary>Collection holding translation documents. Default: <c>translations</c>.</summary>
    public string TranslationsCollection { get; set; } = "translations";

    /// <summary>Collection holding languages, keyed by code. Default: <c>languages</c>.</summary>
    public string LanguagesCollection { get; set; } = "languages";

    /// <summary>
    /// Whether the provider creates its indexes when the host starts. Default: <c>true</c>.
    /// <para>
    /// The unique index on the natural key is what keeps one property of one entity in one
    /// language to a single document — an upsert has nothing else to match on — so turn this off
    /// only when the indexes are created out of band (a migration, an ops script), and never to
    /// work around a failure. Creating them needs <c>createIndex</c> on the database; a role
    /// without it fails the host at startup rather than running unprotected.
    /// </para>
    /// </summary>
    public bool CreateIndexes { get; set; } = true;
}
