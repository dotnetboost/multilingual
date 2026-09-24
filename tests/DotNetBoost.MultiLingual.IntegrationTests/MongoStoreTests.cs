using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.MongoDb;
using DotNetBoost.MultiLingual.ProviderTests.Stores;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DotNetBoost.MultiLingual.IntegrationTests;

/// <summary>
/// The full <see cref="TranslationStoreContractTests"/> suite against a real MongoDB: the unique
/// natural key, upserts that fold a batch, and "no translation" as a missing document.
/// </summary>
[Collection(MongoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MongoTranslationStoreTests(MongoDbFixture fixture) : TranslationStoreContractTests
{
    protected override async Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync()
    {
        var database = await fixture.CreateDatabaseAsync();
        var options  = new MongoDbOptions();
        return (new MongoTranslationStore(database, options), new MongoLanguageStore(database, options));
    }

    [Fact]
    public async Task UniqueIndex_RejectsASecondDocumentForTheSameKey()
    {
        var database = await fixture.CreateDatabaseAsync();
        var collection = database.GetCollection<Translation>(new MongoDbOptions().TranslationsCollection);

        var row = Row("1", "Name", "en", "Laptop");
        await collection.InsertOneAsync(row);

        // Not through the store, which upserts: this is the index itself refusing the duplicate
        // that two racing upserts would otherwise leave behind.
        var duplicate = await Assert.ThrowsAsync<MongoWriteException>(
            () => collection.InsertOneAsync(Row("1", "Name", "en", "Notebook")));

        Assert.Equal(ServerErrorCategory.DuplicateKey, duplicate.WriteError.Category);
    }
}

/// <summary>The <see cref="LanguageStoreContractTests"/> suite against a real MongoDB.</summary>
[Collection(MongoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MongoLanguageStoreTests(MongoDbFixture fixture) : LanguageStoreContractTests
{
    protected override async Task<ILanguageStore> CreateStoreAsync() =>
        new MongoLanguageStore(await fixture.CreateDatabaseAsync(), new MongoDbOptions());

    [Fact]
    public async Task CodeIsTheDocumentId_SoTheListIsUniqueByCode()
    {
        var database = await fixture.CreateDatabaseAsync();
        var store = new MongoLanguageStore(database, new MongoDbOptions());
        await store.AddAsync(new Language { Code = "en", Name = "English", IsDefault = true });

        var raw = await database
            .GetCollection<BsonDocument>(new MongoDbOptions().LanguagesCollection)
            .Find(Builders<BsonDocument>.Filter.Empty)
            .FirstAsync();

        Assert.Equal("en", raw["_id"].AsString);
        // Stored as a name rather than an ordinal, so reordering TextDirection cannot reinterpret it.
        Assert.Equal("Ltr", raw["Direction"].AsString);
    }
}
