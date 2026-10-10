using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using DotNetBoost.MultiLingual.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.Stores;

/// <summary>
/// A context holding nothing but the two MultiLingual tables — and declaring neither of them as a
/// <c>DbSet</c>, which is the point: the provider reaches them through <c>Set&lt;T&gt;()</c>, so
/// <c>ApplyMultiLingual()</c> is the whole contract.
/// </summary>
public sealed class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyMultiLingual();
}

/// <summary>Fresh in-memory SQLite databases; each lives as long as its open connection.</summary>
public abstract class SqliteStoreFixture : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    protected async Task<StoreDbContext> NewContextAsync()
    {
        // Opened here rather than by EF, so EF's own "PRAGMA foreign_keys" never runs: ask for
        // it in the connection string, or the foreign key the provider maps is not enforced.
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();

        var context = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        _owned.Add(context);
        _owned.Add(connection);
        return context;
    }

    public void Dispose()
    {
        foreach (var item in _owned)
            item.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class EfCoreTranslationStoreTests : TranslationStoreContractTests, IDisposable
{
    private readonly Fixture _fixture = new();

    protected override async Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync()
    {
        var context = await _fixture.NewAsync();
        return (new EfCoreTranslationStore(context), new EfCoreLanguageStore(context));
    }

    [Fact]
    public async Task ForeignKey_RejectsAnUnknownLanguage()
    {
        var context = await _fixture.NewAsync();

        // ILanguageManager refuses unknown codes before they get here; the database must too.
        await Assert.ThrowsAsync<DbUpdateException>(() => new EfCoreTranslationStore(context).UpsertManyAsync(
            [Row("1", "Name", "de", "Laptop")]));
    }

    [Fact]
    public async Task ForeignKey_BlocksDeletingALanguageWithContent()
    {
        var context = await _fixture.NewAsync();
        await new EfCoreLanguageStore(context).AddAsync(new Language { Code = "en", Name = "English", IsDefault = true });
        await new EfCoreTranslationStore(context).UpsertManyAsync([Row("1", "Name", "en", "Laptop")]);

        // A fresh context over the same database: with the rows tracked, EF itself would refuse
        // the delete before the database got a say, and it is the database rule under test.
        await using var fresh = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>()
            .UseSqlite(context.Database.GetDbConnection()).Options);

        await Assert.ThrowsAsync<DbUpdateException>(() => new EfCoreLanguageStore(fresh).DeleteAsync("en"));
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class Fixture : SqliteStoreFixture
    {
        public Task<StoreDbContext> NewAsync() => NewContextAsync();
    }
}

public sealed class EfCoreLanguageStoreTests : LanguageStoreContractTests, IDisposable
{
    private readonly Fixture _fixture = new();

    protected override async Task<ILanguageStore> CreateStoreAsync() => new EfCoreLanguageStore(await _fixture.NewAsync());

    public void Dispose() => _fixture.Dispose();

    private sealed class Fixture : SqliteStoreFixture
    {
        public Task<StoreDbContext> NewAsync() => NewContextAsync();
    }
}

/// <summary>
/// The in-memory doubles the unit and API tests rely on have to honour the same contract,
/// or those tests would be proving behaviour no real store has.
/// </summary>
public sealed class InMemoryTranslationStoreTests : TranslationStoreContractTests
{
    protected override Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync() =>
        Task.FromResult<(ITranslationStore, ILanguageStore)>((new InMemoryTranslationStore(), new InMemoryLanguageStore()));
}

public sealed class InMemoryLanguageStoreTests : LanguageStoreContractTests
{
    protected override Task<ILanguageStore> CreateStoreAsync() => Task.FromResult<ILanguageStore>(new InMemoryLanguageStore());
}
