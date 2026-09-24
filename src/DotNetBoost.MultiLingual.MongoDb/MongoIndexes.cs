using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DotNetBoost.MultiLingual.MongoDb;

/// <summary>
/// Creates the translations collection's indexes once, when the host starts. MongoDB has no
/// migrations to hang this off, and <c>createIndex</c> is idempotent, so running it every boot is
/// both the simplest and the only place it is guaranteed to happen.
/// </summary>
/// <param name="services">Resolves the database, in a scope of its own in case it is scoped.</param>
/// <param name="database">How to reach the database from that scope.</param>
/// <param name="options">Collection naming.</param>
/// <param name="logger">Records what was ensured, or why it could not be.</param>
internal sealed partial class MongoIndexInitializer(
    IServiceProvider services,
    Func<IServiceProvider, IMongoDatabase> database,
    MongoDbOptions options,
    ILogger<MongoIndexInitializer> logger) : IHostedService
{
    /// <summary>The unique index that gives a translation document its identity.</summary>
    public const string NaturalKeyIndex = "ux_multilingual_translation_key";

    /// <summary>Serves the per-language count and purge, which no other index's prefix covers.</summary>
    public const string LanguageIndex = "ix_multilingual_translation_language";

    /// <summary>
    /// Creates both indexes on the translations collection. Idempotent, so it is safe to call on
    /// every boot — and on every fresh database a test hands out.
    /// </summary>
    /// <param name="database">The database holding the collection.</param>
    /// <param name="options">Collection naming.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <exception cref="InvalidOperationException">The indexes could not be created.</exception>
    public static async Task EnsureAsync(IMongoDatabase database, MongoDbOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var collection = MongoTranslationStore.Collection(database, options);

        var naturalKey = Builders<Translation>.IndexKeys
            .Ascending(t => t.EntityName)
            .Ascending(t => t.EntityId)
            .Ascending(t => t.PropertyName)
            .Ascending(t => t.LanguageCode);

        try
        {
            await collection.Indexes.CreateManyAsync(
                [
                    // Unique: an upsert matches on the natural key and nothing else, so without
                    // this a race could leave one property in one language with two documents and
                    // reads would start picking between them.
                    new CreateIndexModel<Translation>(
                        naturalKey,
                        new CreateIndexOptions { Unique = true, Name = NaturalKeyIndex }),
                    // The leading fields above are what every read filters on, so reads need no
                    // index of their own; a language-wide count or purge has no such prefix.
                    new CreateIndexModel<Translation>(
                        Builders<Translation>.IndexKeys.Ascending(t => t.LanguageCode),
                        new CreateIndexOptions { Name = LanguageIndex })
                ],
                ct).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new InvalidOperationException(
                $"Could not create the MultiLingual indexes on collection '{options.TranslationsCollection}'. " +
                "The database role needs 'createIndex', and existing documents must not already breach the " +
                "unique key (EntityName, EntityId, PropertyName, LanguageCode). Set " +
                "MongoDbOptions.CreateIndexes to false if the indexes are created out of band.",
                ex);
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        try
        {
            await EnsureAsync(database(scope.ServiceProvider), options, cancellationToken).ConfigureAwait(false);
            LogIndexesEnsured(logger, options.TranslationsCollection);
        }
        catch (InvalidOperationException ex)
        {
            // Deliberately fatal. A missing unique index is invisible until two writers duplicate a
            // translation, and the usual causes — a role without createIndex, or documents that
            // already violate the key — are both worth failing the boot for. Set
            // MongoDbOptions.CreateIndexes to false only when they are created out of band.
            LogIndexesFailed(logger, options.TranslationsCollection, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 6000, Level = LogLevel.Debug,
        Message = "MultiLingual indexes ensured on collection '{collection}'.")]
    private static partial void LogIndexesEnsured(ILogger logger, string collection);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Error,
        Message = "Could not create the MultiLingual indexes on collection '{collection}'.")]
    private static partial void LogIndexesFailed(ILogger logger, string collection, Exception exception);
}
