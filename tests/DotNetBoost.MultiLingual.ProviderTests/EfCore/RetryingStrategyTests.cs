using System.Data.Common;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>What a retrying execution strategy retries on in these tests.</summary>
public sealed class TransientTestException() : Exception("Simulated transient failure.");

/// <summary>A retrying strategy, as <c>EnableRetryOnFailure</c> configures on SQL Server and PostgreSQL.</summary>
internal sealed class TestRetryingStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
{
    // One cached delegate, so every test's options look alike to EF's internal provider cache.
    public static readonly Action<SqliteDbContextOptionsBuilder> Configure =
        sqlite => sqlite.ExecutionStrategy(d => new TestRetryingStrategy(d));

    protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;

    protected override TimeSpan? GetNextDelay(Exception lastException) =>
        base.GetNextDelay(lastException) is null ? null : TimeSpan.Zero;
}

/// <summary>Fails the next <paramref name="failures"/> commands that insert translation rows.</summary>
internal sealed class FailTranslationInserts(int failures) : DbCommandInterceptor
{
    private int _failures = failures;

    public int Failed { get; private set; }

    /// <summary>Fails the next <paramref name="count"/> inserts, and restarts the count.</summary>
    public void Arm(int count)
    {
        _failures = count;
        Failed    = 0;
    }

    private void MaybeFail(DbCommand command)
    {
        if (Failed < _failures && command.CommandText.Contains("INSERT INTO \"Translations\"", StringComparison.Ordinal))
        {
            Failed++;
            throw new TransientTestException();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        MaybeFail(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        MaybeFail(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        MaybeFail(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        MaybeFail(command);
        return ValueTask.FromResult(result);
    }
}

/// <summary>Fails the next commits, before they reach the database, once <see cref="Arm"/>ed.</summary>
internal sealed class FailCommits : DbTransactionInterceptor
{
    private int _failures;

    public int Failed { get; private set; }

    /// <summary>Fails the next <paramref name="count"/> commits — armed after setup, which commits too.</summary>
    public void Arm(int count)
    {
        _failures = count;
        Failed    = 0;
    }

    private void MaybeFail()
    {
        if (Failed < _failures)
        {
            Failed++;
            throw new TransientTestException();
        }
    }

    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        MaybeFail();
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
    {
        MaybeFail();
        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// Saving under a retrying execution strategy, which refuses transactions it did not start. The
/// save interceptor needs one to keep an entity and its translations atomic, so it runs the save
/// inside the strategy instead.
/// </summary>
public sealed class RetryingStrategyTests
{
    private static Dictionary<string, string?> Name(string value) => new() { ["Name"] = value };

    private static async Task<(int Products, List<Translation> Rows)> StoredAsync(Shop shop)
    {
        await using var check = shop.Scope();
        return (await check.Db.Products.CountAsync(), await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task PlainSaveChanges_Works()
    {
        await using var shop  = await Shop.StartAsync(TestRetryingStrategy.Configure);
        await using var scope = await shop.ScopeInAsync("en");

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Translations.Stage(product, "ar", Name("قلم"));
        scope.Db.Products.Add(product);

        Assert.Equal(1, await scope.Db.SaveChangesAsync());

        var (products, rows) = await StoredAsync(shop);
        Assert.Equal(1, products);
        Assert.Equal(["ar", "en"], rows.Select(t => t.LanguageCode).Order());
        Assert.Equal(EntityState.Unchanged, scope.Db.Entry(product).State);
    }

    [Fact]
    public async Task PlainSaveChanges_Sync_Works()
    {
        await using var shop  = await Shop.StartAsync(TestRetryingStrategy.Configure);
        await using var scope = await shop.ScopeInAsync("en");

        scope.Db.Products.Add(new ShopProduct { Sku = "PEN", Name = "Pen" });

        Assert.Equal(1, scope.Db.SaveChanges());
        Assert.Single((await StoredAsync(shop)).Rows);
    }

    [Fact]
    public async Task TransientFailure_InTheTranslationWrite_IsRetried_AsAUnit()
    {
        var failing = new FailTranslationInserts(failures: 1);
        await using var shop  = await Shop.StartAsync(TestRetryingStrategy.Configure, failing);
        await using var scope = await shop.ScopeInAsync("en");

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Translations.Stage(product, "ar", Name("قلم"));
        scope.Db.Products.Add(product);

        await scope.Db.SaveChangesAsync();

        Assert.Equal(1, failing.Failed);
        var (products, rows) = await StoredAsync(shop);
        Assert.Equal(1, products);                                  // not inserted twice
        Assert.Equal(["ar", "en"], rows.Select(t => t.LanguageCode).Order());
        Assert.All(rows, t => Assert.Equal(product.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), t.EntityId));
    }

    [Fact]
    public async Task TransientFailure_OnCommit_IsRetried_WithoutLosingTranslations()
    {
        // The case that needs completion deferred until the commit: if the first attempt had
        // already recorded its values as stored, the retry would skip them.
        var failing = new FailCommits();
        await using var shop  = await Shop.StartAsync(TestRetryingStrategy.Configure, failing);
        await using var scope = await shop.ScopeInAsync("en");
        failing.Arm(1);

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Translations.Stage(product, "ar", Name("قلم"));
        scope.Db.Products.Add(product);

        await scope.Db.SaveChangesAsync();

        Assert.Equal(1, failing.Failed);
        var (products, rows) = await StoredAsync(shop);
        Assert.Equal(1, products);
        Assert.Equal(["ar", "en"], rows.Select(t => t.LanguageCode).Order());
    }

    [Fact]
    public async Task TransientFailure_OnAnUpdate_IsRetried()
    {
        var failing = new FailTranslationInserts(failures: 0);
        await using var shop = await Shop.StartAsync(TestRetryingStrategy.Configure, failing);

        int id;
        await using (var seed = await shop.ScopeInAsync("en"))
        {
            var created = new ShopProduct { Sku = "PEN", Price = 1, Name = "Pen" };
            seed.Db.Products.Add(created);
            await seed.Db.SaveChangesAsync();
            id = created.Id;
        }

        await using var scope = await shop.ScopeInAsync("fr");
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Price = 2;
        product.Name  = "Stylo";

        failing.Arm(1);
        await scope.Db.SaveChangesAsync();

        Assert.Equal(1, failing.Failed);
        await using var check = shop.Scope();
        Assert.Equal(2m, (await check.Db.Products.SingleAsync()).Price);
        Assert.Contains(await shop.StoredTranslationsAsync(), t => t is { LanguageCode: "fr", Value: "Stylo" });
    }

    [Fact]
    public async Task PersistentFailure_Surfaces_AndLeavesTheTrackerRetryable()
    {
        var failing = new FailTranslationInserts(failures: 100);
        await using var shop  = await Shop.StartAsync(TestRetryingStrategy.Configure, failing);
        await using var scope = await shop.ScopeInAsync("en");

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Db.Products.Add(product);

        await Assert.ThrowsAsync<RetryLimitExceededException>(() => scope.Db.SaveChangesAsync());
        Assert.Equal(EntityState.Added, scope.Db.Entry(product).State);
        Assert.Equal(0, (await StoredAsync(shop)).Products);

        failing.Arm(0);
        await scope.Db.SaveChangesAsync();
        Assert.Equal(1, (await StoredAsync(shop)).Products);
        Assert.Single((await StoredAsync(shop)).Rows);
    }

    [Fact]
    public async Task WithoutAStrategy_AFailedCommit_CanBeRetriedByTheCaller()
    {
        // Same deferral, on the ordinary path: the interceptor's own commit fails, and the
        // caller's retry must still find the staged and authored values to write.
        var failing = new FailCommits();
        await using var shop  = await Shop.StartAsync(configureSqlite: null, failing);
        await using var scope = await shop.ScopeInAsync("en");
        failing.Arm(1);

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Translations.Stage(product, "ar", Name("قلم"));
        scope.Db.Products.Add(product);

        await Assert.ThrowsAsync<TransientTestException>(() => scope.Db.SaveChangesAsync());
        Assert.Equal(EntityState.Added, scope.Db.Entry(product).State);

        await scope.Db.SaveChangesAsync();

        var (products, rows) = await StoredAsync(shop);
        Assert.Equal(1, products);
        Assert.Equal(["ar", "en"], rows.Select(t => t.LanguageCode).Order());
    }

    [Fact]
    public async Task CallerManagedStrategyAndTransaction_StillWorks()
    {
        await using var shop  = await Shop.StartAsync(TestRetryingStrategy.Configure);
        await using var scope = await shop.ScopeInAsync("en");

        scope.Db.Products.Add(new ShopProduct { Sku = "PEN", Name = "Pen" });

        await scope.Db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await scope.Db.Database.BeginTransactionAsync();
            await scope.Db.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        var (products, rows) = await StoredAsync(shop);
        Assert.Equal(1, products);
        Assert.Single(rows);
    }
}
