using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.MongoDb;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace DotNetBoost.MultiLingual.IntegrationTests;

/// <summary>A document with a translatable property and a store-generated <see cref="ObjectId"/> key.</summary>
public sealed class Dish
{
    public ObjectId Id { get; set; }

    [MultiLingual] public string? Name { get; set; }

    public decimal Price { get; set; }
}

/// <summary>An application over MongoDB: the engine, its stores, and a scope per language.</summary>
internal sealed class MongoApp : IAsyncDisposable
{
    private readonly ServiceProvider _root;

    private MongoApp(IMongoDatabase database)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(database);
        services.AddMultiLingual().UseMongoDb().Build();

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>An application with en (default), ar and fr.</summary>
    public static async Task<MongoApp> StartAsync(IMongoDatabase database)
    {
        var app = new MongoApp(database);

        await using var scope = app.NewScope();
        foreach (var (code, name) in new[] { ("en", "English"), ("ar", "Arabic"), ("fr", "French") })
            await scope.MultiLingual.Languages.CreateAsync(new NewLanguage { Code = code, Name = name });

        return app;
    }

    /// <summary>A unit of work reading and writing in <paramref name="languageCode"/>.</summary>
    public async Task<Scope> ScopeInAsync(string languageCode)
    {
        var scope = NewScope();
        await scope.MultiLingual.UseLanguageAsync(languageCode);
        return scope;
    }

    private Scope NewScope() => new(_root.CreateScope());

    public ValueTask DisposeAsync() => _root.DisposeAsync();

    internal sealed class Scope(IServiceScope scope) : IAsyncDisposable
    {
        public IMultiLingualManager MultiLingual { get; } = scope.ServiceProvider.GetRequiredService<IMultiLingualManager>();

        public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        public ValueTask DisposeAsync()
        {
            scope.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// The provider as an application uses it. There is no change tracker to hook, so this is the flow
/// the documentation promises: insert the document, save its translations, localize after a query.
/// </summary>
[Collection(MongoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MongoProviderTests(MongoDbFixture fixture)
{
    private static readonly MongoDbOptions Options = new();

    [Fact]
    public async Task UntrackedFlow_SaveLocalizeAndDelete_GoThroughTheManager()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var app = await MongoApp.StartAsync(database);
        var dishes = database.GetCollection<Dish>("dishes");

        var dish = new Dish { Id = ObjectId.GenerateNewId(), Name = "Hummus", Price = 4.5m };
        await dishes.InsertOneAsync(dish);

        await using (var write = await app.ScopeInAsync("en"))
        {
            write.MultiLingual.For(dish).Set("ar", d => d.Name, "حمص");
            await write.MultiLingual.For(dish).SaveAsync();
        }

        // The entity's own document never carries the translatable property: the class-map
        // convention unmapped it, so "Hummus" was stored as the English translation and nowhere else.
        var stored = await database.GetCollection<BsonDocument>("dishes")
            .Find(Builders<BsonDocument>.Filter.Empty).FirstAsync();

        Assert.False(stored.Contains(nameof(Dish.Name)));
        Assert.True(stored.Contains(nameof(Dish.Price)));

        await using (var read = await app.ScopeInAsync("ar"))
        {
            var loaded = await dishes.Find(d => d.Id == dish.Id).FirstAsync();
            Assert.Null(loaded.Name);

            await read.MultiLingual.LocalizeAsync([loaded]);
            Assert.Equal("حمص", loaded.Name);

            var all = await read.MultiLingual.For(loaded).GetAllAsync();
            Assert.Equal("Hummus", all["en"][nameof(Dish.Name)]);
            Assert.Equal("حمص", all["ar"][nameof(Dish.Name)]);

            await read.MultiLingual.For(loaded).DeleteAsync();
            Assert.Empty(await read.MultiLingual.For(loaded).GetAllAsync());
        }
    }

    [Fact]
    public async Task Localize_FallsBackToTheDefaultLanguage()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var app = await MongoApp.StartAsync(database);

        var dish = new Dish { Id = ObjectId.GenerateNewId(), Name = "Falafel" };

        await using (var write = await app.ScopeInAsync("en"))
            await write.MultiLingual.For(dish).SaveAsync();

        await using var read = await app.ScopeInAsync("fr");
        var loaded = new Dish { Id = dish.Id };
        await read.MultiLingual.LocalizeAsync([loaded]);

        Assert.Equal("Falafel", loaded.Name);
    }

    /// <summary>
    /// The unit-of-work hook: with a session, the document and its translations are one
    /// transaction, so aborting it leaves neither behind.
    /// </summary>
    [Fact]
    public async Task WithSession_TheEntityAndItsTranslationsRollBackTogether()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var app = await MongoApp.StartAsync(database);
        var dishes = database.GetCollection<Dish>("dishes");
        var translations = new MongoTranslationStore(database, Options);

        var dish = new Dish { Id = ObjectId.GenerateNewId(), Name = "Tabbouleh" };
        var entityId = dish.Id.ToString();

        using (var session = await fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await dishes.InsertOneAsync(session, dish);

            await using var scope = await app.ScopeInAsync("en");
            var writer = scope.Get<ITranslationWriter>();
            var writes = new[] { new TranslationWrite("Dish", entityId, writer.CaptureValues(dish)) };

            await writer.WithStore(translations.WithSession(session)).WriteAsync(writes);

            // Visible inside the transaction, through the same session.
            Assert.NotEmpty(await translations.WithSession(session).GetAllAsync("Dish", entityId));

            await session.AbortTransactionAsync();
        }

        Assert.Equal(0, await dishes.CountDocumentsAsync(Builders<Dish>.Filter.Empty));
        Assert.Empty(await translations.GetAllAsync("Dish", entityId));
    }

    [Fact]
    public async Task KeyAccessor_ReadsTheMappedId_AndRefusesAnUninsertedDocument()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var app = await MongoApp.StartAsync(database);
        await using var scope = await app.ScopeInAsync("en");

        var accessor = scope.Get<IEntityKeyAccessor>();
        var id = ObjectId.GenerateNewId();

        Assert.Equal(id.ToString(), accessor.GetKey(new Dish { Id = id }));

        // ObjectId.Empty is what a document the driver has not inserted yet holds. Reading it as a
        // key would store every such entity's translations under 000000000000000000000000.
        Assert.Null(accessor.GetKey(new Dish { Id = ObjectId.Empty }));
    }

    /// <summary>
    /// Saving an entity whose key does not exist yet must fail loudly rather than write under the
    /// empty id — which is what the accessor returning <c>null</c> buys.
    /// </summary>
    [Fact]
    public async Task Save_BeforeTheDocumentIsInserted_IsRefused()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var app = await MongoApp.StartAsync(database);
        await using var scope = await app.ScopeInAsync("en");

        var unsaved = new Dish { Name = "Not inserted yet" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.MultiLingual.For(unsaved).SaveAsync());
        Assert.Equal(0, await new MongoTranslationStore(database, Options).CountByLanguageAsync("en"));
    }
}

/// <summary>
/// The class-map convention on its own. Members are mapped explicitly rather than by
/// <c>AutoMap()</c>, so the process-wide convention another test registers cannot decide the
/// outcome first.
/// </summary>
public sealed class MongoClassMapConventionTests
{
    private static BsonClassMap<Dish> MappedDish()
    {
        var map = new BsonClassMap<Dish>();
        map.MapIdProperty(d => d.Id);
        map.MapProperty(d => d.Name);
        map.MapProperty(d => d.Price);
        return map;
    }

    [Fact]
    public void UnmapsTranslatableProperties()
    {
        var map = MappedDish();

        new UnmapTranslatablePropertiesConvention(new MultiLingualOptions()).Apply(map);

        Assert.DoesNotContain(map.DeclaredMemberMaps, m => m.MemberName == nameof(Dish.Name));
        Assert.Contains(map.DeclaredMemberMaps, m => m.MemberName == nameof(Dish.Price));
    }

    [Fact]
    public void KeepsThemInBaseLanguageMode_WhereTheFieldHoldsTheBaseValue()
    {
        var map = MappedDish();

        new UnmapTranslatablePropertiesConvention(new MultiLingualOptions { BaseLanguage = "en" }).Apply(map);

        Assert.Contains(map.DeclaredMemberMaps, m => m.MemberName == nameof(Dish.Name));
    }
}
