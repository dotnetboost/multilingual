using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using DotNetBoost.MultiLingual.ProviderTests.Stores;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.IntegrationTests;

/// <summary>
/// Hands out contexts over fresh databases on a real server and disposes them afterwards.
/// </summary>
internal sealed class ContextPool(Func<Task<string>> createDatabase, Action<DbContextOptionsBuilder, string> useProvider)
    : IAsyncDisposable
{
    private readonly List<StoreDbContext> _contexts = [];

    public async Task<StoreDbContext> NewAsync()
    {
        var builder = new DbContextOptionsBuilder<StoreDbContext>();
        useProvider(builder, await createDatabase());

        var context = new StoreDbContext(builder.Options);
        await context.Database.EnsureCreatedAsync();
        _contexts.Add(context);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
            await context.DisposeAsync();
    }
}

/// <summary>
/// The full <see cref="TranslationStoreContractTests"/> suite against PostgreSQL: the composite
/// key, the foreign key to Languages and unbounded text values on a real engine.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCorePostgresTranslationStoreTests(PostgreSqlFixture fixture) : TranslationStoreContractTests, IAsyncDisposable
{
    private readonly ContextPool _pool = new(fixture.CreateDatabaseAsync, (b, cs) => b.UseNpgsql(cs));

    protected override async Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync()
    {
        var context = await _pool.NewAsync();
        return (new EfCoreTranslationStore(context), new EfCoreLanguageStore(context));
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCorePostgresLanguageStoreTests(PostgreSqlFixture fixture) : LanguageStoreContractTests, IAsyncDisposable
{
    private readonly ContextPool _pool = new(fixture.CreateDatabaseAsync, (b, cs) => b.UseNpgsql(cs));

    protected override async Task<ILanguageStore> CreateStoreAsync() => new EfCoreLanguageStore(await _pool.NewAsync());

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

/// <summary>
/// The same suite against SQL Server, where the composite primary key has to fit the 900-byte
/// clustered index limit (128 + 64 + 128 + 16 nvarchar characters = 672 bytes).
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCoreSqlServerTranslationStoreTests(SqlServerFixture fixture) : TranslationStoreContractTests, IAsyncDisposable
{
    private readonly ContextPool _pool = new(fixture.CreateDatabaseAsync, (b, cs) => b.UseSqlServer(cs));

    protected override async Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync()
    {
        var context = await _pool.NewAsync();
        return (new EfCoreTranslationStore(context), new EfCoreLanguageStore(context));
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCoreSqlServerLanguageStoreTests(SqlServerFixture fixture) : LanguageStoreContractTests, IAsyncDisposable
{
    private readonly ContextPool _pool = new(fixture.CreateDatabaseAsync, (b, cs) => b.UseSqlServer(cs));

    protected override async Task<ILanguageStore> CreateStoreAsync() => new EfCoreLanguageStore(await _pool.NewAsync());

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}
