using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using DotNetBoost.MultiLingual.ProviderTests.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.IntegrationTests;

/// <summary>
/// The save interceptor beside <see cref="FollowUpSaveInterceptor"/> — the shape of an audit trail's
/// deferred pass — on real engines, where SQLite could hide the difference: a real driver's
/// synchronous commit inside an asynchronous save, the provider's own retrying strategy, and what a
/// failed statement does to the transaction around it (PostgreSQL aborts the whole transaction,
/// SQL Server only the statement). See <c>SaveCompositionTests</c> for the full matrix on SQLite.
/// </summary>
public abstract class SaveCompositionIntegrationTests
{
    private const string Gap2Retrying = SaveCompositionTests.Gap2Retrying;

    /// <summary>A fresh database's connection string.</summary>
    protected abstract Task<string> CreateDatabaseAsync();

    protected abstract void UseProvider(DbContextOptionsBuilder options, string connectionString, bool retrying);

    private sealed class Database(ServiceProvider root, FollowUpProbe probe) : IAsyncDisposable
    {
        public FollowUpProbe Probe { get; } = probe;

        public async Task<AsyncServiceScope> ScopeInAsync(string languageCode)
        {
            var scope = root.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ILanguageContext>().UseAsync(languageCode);
            return scope;
        }

        public AsyncServiceScope Scope() => root.CreateAsyncScope();

        public async Task<(int Products, int Translations, int Notes)> StoredCountsAsync()
        {
            await using var scope = root.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>();
            return (await db.Products.IgnoreTranslations().CountAsync(), await db.Translations.CountAsync(), await db.Notes.CountAsync());
        }

        public ValueTask DisposeAsync() => root.DisposeAsync();
    }

    private async Task<Database> StartAsync(FollowUpOrder order, bool retrying)
    {
        var connectionString = await CreateDatabaseAsync();
        var probe = new FollowUpProbe();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddMultiLingual().UseEntityFrameworkCore<ComposedShopDbContext>().Build();
        services.AddDbContext<ComposedShopDbContext>((sp, options) =>
        {
            UseProvider(options, connectionString, retrying);

            if (order == FollowUpOrder.Before)
                options.AddInterceptors(new FollowUpSaveInterceptor(probe));

            options.UseMultiLingual(sp);

            if (order == FollowUpOrder.After)
                options.AddInterceptors(new FollowUpSaveInterceptor(probe));
        });

        var database = new Database(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), probe);

        probe.Enabled = false;
        await using (var scope = database.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>().Database.EnsureCreatedAsync();
            var languages = scope.ServiceProvider.GetRequiredService<ILanguageManager>();
            await languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
            await languages.CreateAsync(new NewLanguage { Code = "fr", Name = "French" });
        }

        probe.Enabled = true;
        return database;
    }

    private static ShopProduct Mouse() => new() { Sku = "MOUSE", Price = 1, Name = "Souris" };

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Create_RunsTheFollowUpInsideTheTransaction(FollowUpOrder order, bool retrying)
    {
        await using var database = await StartAsync(order, retrying);

        await using (var scope = await database.ScopeInAsync("fr"))
        {
            var db = scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>();
            db.Products.Add(Mouse());
            await db.SaveChangesAsync();
        }

        Assert.Equal([true], database.Probe.RanInsideTransaction);
        Assert.Equal((1, 1, 1), await database.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Create_ThroughTheSynchronousSave_RunsTheFollowUpInsideTheTransaction(FollowUpOrder order, bool retrying)
    {
        await using var database = await StartAsync(order, retrying);

        await using (var scope = await database.ScopeInAsync("fr"))
        {
            var db = scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>();
            db.Products.Add(Mouse());
            db.SaveChanges();
        }

        Assert.Equal([true], database.Probe.RanInsideTransaction);
        Assert.Equal((1, 1, 1), await database.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task FailureAfterTheFollowUp_RollsBackEverything(FollowUpOrder order, bool retrying)
    {
        await using var database = await StartAsync(order, retrying);
        database.Probe.FailAfterFollowUp = true;

        await using (var scope = await database.ScopeInAsync("fr"))
        {
            var db = scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>();
            db.Products.Add(Mouse());
            await Assert.ThrowsAsync<FollowUpFailedException>(() => db.SaveChangesAsync());
        }

        Assert.Equal((0, 0, 0), await database.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task FailureOfTheFollowUpSave_RollsBackEverything(FollowUpOrder order, bool retrying)
    {
        await using var database = await StartAsync(order, retrying);
        database.Probe.FailTheFollowUpSave = true;

        await using (var scope = await database.ScopeInAsync("fr"))
        {
            var db = scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>();
            db.Products.Add(Mouse());
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        Assert.Equal((0, 0, 0), await database.StoredCountsAsync());
    }

    /// <summary>
    /// The follow-up save fails inside this library's transaction, and the interceptor that issued
    /// it carries on. A failed statement aborts a whole PostgreSQL transaction, but EF Core sets a
    /// savepoint before a <c>SaveChanges</c> inside an existing transaction and rolls back to it, so
    /// on both engines only the follow-up's own row is lost: the save succeeds, with the entity and
    /// its translations stored.
    /// </summary>
    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task ASwallowedFollowUpFailure_LosesOnlyTheFollowUpsOwnRow(FollowUpOrder order, bool retrying)
    {
        await using var database = await StartAsync(order, retrying);
        database.Probe.FailTheFollowUpSave    = true;
        database.Probe.SwallowFollowUpFailure = true;

        await using (var scope = await database.ScopeInAsync("fr"))
        {
            var db = scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>();
            db.Products.Add(Mouse());
            await db.SaveChangesAsync();
        }

        Assert.Equal((1, 1, 0), await database.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Delete_RemovesTheTranslations(FollowUpOrder order, bool retrying)
    {
        await using var database = await StartAsync(order, retrying);

        int id;
        database.Probe.Enabled = false;
        await using (var scope = await database.ScopeInAsync("fr"))
        {
            var db = scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>();
            var product = Mouse();
            db.Products.Add(product);
            await db.SaveChangesAsync();
            id = product.Id;
        }

        database.Probe.Enabled = true;
        await using (var scope = database.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ComposedShopDbContext>();
            db.Products.Remove(await db.Products.SingleAsync(p => p.Id == id));
            await db.SaveChangesAsync();
        }

        Assert.Equal((0, 0, 1), await database.StoredCountsAsync());
    }
}

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresSaveCompositionTests(PostgreSqlFixture fixture) : SaveCompositionIntegrationTests
{
    // Cached, so every test's options look alike to EF's internal service provider cache.
    private static readonly Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder> Retrying =
        npgsql => npgsql.EnableRetryOnFailure();

    protected override Task<string> CreateDatabaseAsync() => fixture.CreateDatabaseAsync();

    protected override void UseProvider(DbContextOptionsBuilder options, string connectionString, bool retrying) =>
        options.UseNpgsql(connectionString, retrying ? Retrying : null);
}

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerSaveCompositionTests(SqlServerFixture fixture) : SaveCompositionIntegrationTests
{
    // Cached, so every test's options look alike to EF's internal service provider cache.
    private static readonly Action<Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder> Retrying =
        sqlServer => sqlServer.EnableRetryOnFailure();

    protected override Task<string> CreateDatabaseAsync() => fixture.CreateDatabaseAsync();

    protected override void UseProvider(DbContextOptionsBuilder options, string connectionString, bool retrying) =>
        options.UseSqlServer(connectionString, retrying ? Retrying : null);
}
