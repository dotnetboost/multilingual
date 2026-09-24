using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.MongoDb;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace DotNetBoost.MultiLingual.IntegrationTests;

/// <summary>
/// One MongoDB container for the whole collection, as a single-node replica set so the tests can
/// use transactions — a standalone <c>mongod</c> refuses to start one, which is exactly the
/// deployment prerequisite the provider documents.
/// <para>
/// Each call to <see cref="CreateDatabaseAsync"/> hands out a brand-new database, with the
/// provider's indexes already created, so every test starts genuinely empty without paying to boot
/// a container.
/// </para>
/// </summary>
public sealed class MongoDbFixture : IAsyncLifetime, IDisposable
{
    // Pinned to a patch, not the 8.0 tag: MongoDB 8.0.18 and later refuse to start on Linux kernel
    // 6.19+ (SERVER-121912), which is what a current Docker Desktop VM runs. Same version as
    // DotNetBoost.Settings, so the two libraries share one pulled image.
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8.0.4")
        .WithReplicaSet()
        .Build();

    private int _databaseCounter;
    private MongoClient? _client;

    /// <summary>The client every test shares; the driver owns one connection pool behind it.</summary>
    public IMongoClient Client =>
        _client ?? throw new InvalidOperationException("The MongoDB fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Before anything is serialized: the class maps decide that Language.Code is _id and that
        // translatable properties are unmapped, and a class map the driver has already built for a
        // type cannot be replaced.
        MultiLingualBsonMapping.Register(new MultiLingualOptions());

        _client = new MongoClient(_container.GetConnectionString());
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Releases the client's connection pool; xunit calls this alongside <see cref="DisposeAsync"/>.</summary>
    public void Dispose() => _client?.Dispose();

    /// <summary>An empty database with the translations indexes in place.</summary>
    public async Task<IMongoDatabase> CreateDatabaseAsync()
    {
        // Database names cap at 63 bytes; this is 39.
        var database = Client.GetDatabase($"test_{Interlocked.Increment(ref _databaseCounter)}_{Guid.NewGuid():N}");
        await MongoIndexInitializer.EnsureAsync(database, new MongoDbOptions());
        return database;
    }
}

[CollectionDefinition(Name)]
public sealed class MongoDbCollection : ICollectionFixture<MongoDbFixture>
{
    public const string Name = "mongodb";
}
