using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// A UI thread blocked in a synchronous call: work posted to it never runs. Any
/// <c>.GetAwaiter().GetResult()</c> whose task needs to resume here waits forever.
/// </summary>
internal sealed class BlockedUiThreadContext : SynchronizationContext
{
    public int Posted { get; private set; }

    public override void Post(SendOrPostCallback d, object? state) => Posted++;

    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>
/// An EF Core language store whose async members genuinely yield, as a network round trip
/// does — SQLite's complete synchronously, which would hide a blocked continuation.
/// </summary>
internal sealed class YieldingLanguageStore(ILanguageStore inner) : ILanguageStore
{
    public async Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default)
    {
        await Task.Yield();
        return await inner.GetAllAsync(ct);
    }

    public IReadOnlyList<Language> GetAll() => inner.GetAll();

    public async Task<Language?> FindAsync(string code, CancellationToken ct = default)
    {
        await Task.Yield();
        return await inner.FindAsync(code, ct);
    }

    public async Task AddAsync(Language language, CancellationToken ct = default)
    {
        await Task.Yield();
        await inner.AddAsync(language, ct);
    }

    public async Task UpdateAsync(Language language, CancellationToken ct = default)
    {
        await Task.Yield();
        await inner.UpdateAsync(language, ct);
    }

    public async Task DeleteAsync(string code, CancellationToken ct = default)
    {
        await Task.Yield();
        await inner.DeleteAsync(code, ct);
    }

    public async Task SetDefaultAsync(string code, CancellationToken ct = default)
    {
        await Task.Yield();
        await inner.SetDefaultAsync(code, ct);
    }
}

/// <summary>
/// Synchronous <c>SaveChanges</c> runs synchronously all the way down, so it cannot deadlock on
/// a thread with a single-threaded synchronization context (WPF, WinForms).
/// </summary>
public sealed class SyncSaveChangesTests
{
    private static Dictionary<string, string?> Name(string value) => new() { ["Name"] = value };

    private static Task<Shop> StartYieldingShopAsync() =>
        Shop.StartWithServicesAsync(services => services.Replace(ServiceDescriptor.Scoped<ILanguageStore>(
            sp => new YieldingLanguageStore(new EfCoreLanguageStore(sp.GetRequiredService<ShopDbContext>())))));

    /// <summary>Runs <paramref name="action"/> on its own thread, under <see cref="BlockedUiThreadContext"/>.</summary>
    private static (bool Finished, Exception? Error, int Posted) RunOnBlockedUiThread(Action action)
    {
        var context = new BlockedUiThreadContext();
        Exception? error = null;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        { IsBackground = true };   // a deadlocked thread must not keep the test host alive

        thread.Start();
        var finished = thread.Join(TimeSpan.FromSeconds(10));
        return (finished, error, context.Posted);
    }

    [Fact]
    public async Task SaveChanges_OnABlockedUiThread_DoesNotDeadlock()
    {
        await using var shop  = await StartYieldingShopAsync();
        await using var scope = await shop.ScopeInAsync("en");

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Translations.Stage(product, "ar", Name("قلم"));
        scope.Db.Products.Add(product);

        // An expired language cache makes the save reload it — through the yielding store.
        scope.Get<ILanguageCatalog>().Invalidate();

        var (finished, error, posted) = RunOnBlockedUiThread(() => scope.Db.SaveChanges());

        Assert.True(finished, "SaveChanges deadlocked waiting for work posted to the UI thread.");
        Assert.Null(error);
        Assert.Equal(0, posted);
        Assert.Equal(["ar", "en"], (await shop.StoredTranslationsAsync()).Select(t => t.LanguageCode).Order());
    }

    [Fact]
    public async Task SaveChanges_Delete_OnABlockedUiThread_RemovesTranslations()
    {
        await using var shop = await StartYieldingShopAsync();

        int id;
        await using (var seed = await shop.ScopeInAsync("en"))
        {
            var created = new ShopProduct { Sku = "PEN", Name = "Pen" };
            seed.Db.Products.Add(created);
            await seed.Db.SaveChangesAsync();
            id = created.Id;
        }

        await using var scope = await shop.ScopeInAsync("en");
        scope.Db.Products.Remove(await scope.Db.Products.SingleAsync(p => p.Id == id));
        scope.Get<ILanguageCatalog>().Invalidate();

        var (finished, error, posted) = RunOnBlockedUiThread(() => scope.Db.SaveChanges());

        Assert.True(finished, "SaveChanges deadlocked waiting for work posted to the UI thread.");
        Assert.Null(error);
        Assert.Equal(0, posted);
        Assert.Empty(await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task ToLocalizedList_OnABlockedUiThread_DoesNotDeadlock()
    {
        await using var shop = await StartYieldingShopAsync();
        await using (var seed = await shop.ScopeInAsync("en"))
        {
            var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
            seed.Translations.Stage(product, "fr", Name("Stylo"));
            seed.Db.Products.Add(product);
            await seed.Db.SaveChangesAsync();
        }

        // No language resolved, so hydration has to ask the catalog for the default — and with the
        // cache invalidated, the catalog reloads through the yielding store. (With a resolved
        // language the read never touches the catalog, and SQLite never yields, so a blocking
        // implementation would slip through.)
        await using var scope = shop.Scope();
        scope.Get<ILanguageCatalog>().Invalidate();

        List<ShopProduct>? products = null;
        ShopProduct? first = null;
        var (finished, error, posted) = RunOnBlockedUiThread(() =>
        {
            products = scope.Db.Products.AsNoTracking().ToLocalizedList(scope.Hydrator);
            first    = scope.Db.Products.AsNoTracking().FirstLocalizedOrDefault(scope.Hydrator);
        });

        Assert.True(finished, "ToLocalizedList deadlocked waiting for work posted to the UI thread.");
        Assert.Null(error);
        Assert.Equal(0, posted);
        Assert.Equal("Pen", Assert.Single(products!).Name);   // the default language, English
        Assert.Equal("Pen", first!.Name);
    }

    [Fact]
    public async Task FailedSaveChanges_OnABlockedUiThread_RollsBack_WithoutDeadlocking()
    {
        await using var shop  = await StartYieldingShopAsync();
        await using var scope = await shop.ScopeInAsync("en");

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Translations.Stage(product, "de", Name("Stift"));   // no such language
        scope.Db.Products.Add(product);
        scope.Get<ILanguageCatalog>().Invalidate();

        var (finished, error, _) = RunOnBlockedUiThread(() => scope.Db.SaveChanges());

        Assert.True(finished, "SaveChanges deadlocked waiting for work posted to the UI thread.");
        Assert.IsType<Core.UnknownLanguageException>(error);
        Assert.Equal(EntityState.Added, scope.Db.Entry(product).State);

        await using var check = shop.Scope();
        Assert.False(await check.Db.Products.AnyAsync());
    }
}
