using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// <c>LanguageTokenInterceptor</c> puts the request's languages into a compiled query just before
/// it runs, which means asking the language catalog for them.
///
/// <para>
/// It must ask asynchronously when the query runs asynchronously. Refreshing the catalog's
/// snapshot reads the language store and, when one is registered, the
/// <c>ILanguageChangeSignal</c> — for the built-in signal a round trip to <c>IDistributedCache</c>.
/// Taken synchronously, that blocks a thread-pool thread behind a semaphore every concurrent query
/// also waits on: a few times a minute, under load, every query in flight queues up on one
/// synchronous network call.
/// </para>
///
/// <para>
/// A request that went through <c>UseMultiLingual()</c> never reaches the catalog here — the
/// language is already on the scope. These tests use a scope with no language resolved, which is
/// what a background job looks like, and the other path that reaches it.
/// </para>
/// </summary>
public sealed class LanguageTokenInterceptorTests
{
    [Fact]
    public async Task AnAsynchronousQuery_ReadsTheCatalogAsynchronously()
    {
        var (shop, store, signal) = await StartAsync();
        await using var _ = shop;
        await SeedAsync(shop);

        await using var scope = shop.Scope();

        // Counted from here, so nothing the setup or the seed did can satisfy the assertions.
        var (syncStore, syncSignal, asyncStore) = (store.Synchronous, signal.Synchronous, store.Asynchronous);

        var names = await scope.Db.Products.Select(p => p.Name).ToListAsync();

        Assert.Equal(["Laptop"], names);

        // The catalog really was consulted — without this the test would pass on a query that
        // never reached the interceptor at all.
        Assert.True(store.Asynchronous > asyncStore, "the query should have read the language list");

        Assert.Equal(syncStore, store.Synchronous);
        Assert.Equal(syncSignal, signal.Synchronous);
    }

    [Fact]
    public async Task ASynchronousQuery_ReadsTheCatalogSynchronously()
    {
        var (shop, store, _) = await StartAsync();
        await using var __ = shop;
        await SeedAsync(shop);

        await using var scope = shop.Scope();
        var before = store.Synchronous;

        // The other half of the contract: a synchronous execution must not await anything, so it
        // reaches the catalog's synchronous members by design rather than by oversight.
        var names = scope.Db.Products.Select(p => p.Name).ToList();

        Assert.Equal(["Laptop"], names);
        Assert.True(store.Synchronous > before, "a synchronous query should read the language list synchronously");
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>
    /// A shop whose language store and change signal count how they are called, and whose cached
    /// language list is never fresh — so every read of it goes all the way to the store, which is
    /// the slow path these tests are about, on every query rather than once per cache duration.
    /// </summary>
    private static async Task<(Shop Shop, CallLog Store, RecordingSignal Signal)> StartAsync()
    {
        var store  = new CallLog();
        var signal = new RecordingSignal();

        var shop = await Shop.StartWithServicesAsync(services =>
        {
            var options = (MultiLingualOptions)services
                .Single(d => d.ServiceType == typeof(MultiLingualOptions)).ImplementationInstance!;

            options.LanguageCacheDuration      = TimeSpan.Zero;
            options.LanguageChangeCheckInterval = TimeSpan.Zero;

            services.AddSingleton<ILanguageChangeSignal>(signal);
            CountLanguageStoreCalls(services, store);
        });

        return (shop, store, signal);
    }

    private static async Task SeedAsync(Shop shop)
    {
        await using var scope = shop.Scope();

        var product = new ShopProduct { Sku = "LAPTOP" };
        scope.Translations.Stage(product, "en", new Dictionary<string, string?> { ["Name"] = "Laptop" });
        scope.Db.Products.Add(product);

        await scope.Db.SaveChangesAsync();
    }

    /// <summary>
    /// Wraps whatever <c>ILanguageStore</c> the provider registered in one that counts, keeping the
    /// registration's own lifetime — the real store is scoped, on the DbContext.
    /// </summary>
    private static void CountLanguageStoreCalls(IServiceCollection services, CallLog log)
    {
        var registered = services.Last(d => d.ServiceType == typeof(ILanguageStore));
        services.Remove(registered);

        services.Add(new ServiceDescriptor(typeof(ILanguageStore), sp =>
        {
            var inner = (ILanguageStore)(registered.ImplementationInstance
                ?? registered.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!));

            return new CountingLanguageStore(inner, log);
        }, registered.Lifetime));
    }

    private sealed class CallLog
    {
        private int _synchronous;
        private int _asynchronous;

        public int Synchronous  => Volatile.Read(ref _synchronous);
        public int Asynchronous => Volatile.Read(ref _asynchronous);

        public void CalledSynchronously()  => Interlocked.Increment(ref _synchronous);
        public void CalledAsynchronously() => Interlocked.Increment(ref _asynchronous);
    }

    /// <summary>Counts how the language list is read; everything else goes straight through.</summary>
    private sealed class CountingLanguageStore(ILanguageStore inner, CallLog log) : ILanguageStore
    {
        public Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default)
        {
            log.CalledAsynchronously();
            return inner.GetAllAsync(ct);
        }

        public IReadOnlyList<Language> GetAll()
        {
            log.CalledSynchronously();
            return inner.GetAll();
        }

        public Task<Language?> FindAsync(string code, CancellationToken ct = default) => inner.FindAsync(code, ct);
        public Task AddAsync(Language language, CancellationToken ct = default) => inner.AddAsync(language, ct);
        public Task UpdateAsync(Language language, CancellationToken ct = default) => inner.UpdateAsync(language, ct);
        public Task DeleteAsync(string code, CancellationToken ct = default) => inner.DeleteAsync(code, ct);
        public Task SetDefaultAsync(string code, CancellationToken ct = default) => inner.SetDefaultAsync(code, ct);
    }

    /// <summary>
    /// Stands in for the distributed-cache signal. One version, never changing, so the catalog
    /// reloads because it expired rather than because this said anything — and a count of the
    /// synchronous reads, which is the blocking cache round trip in a real deployment.
    /// </summary>
    private sealed class RecordingSignal : ILanguageChangeSignal
    {
        private int _synchronous;

        public int Synchronous => Volatile.Read(ref _synchronous);

        public Task<string?> GetVersionAsync(CancellationToken ct = default) => Task.FromResult<string?>("v1");

        public string? GetVersion()
        {
            Interlocked.Increment(ref _synchronous);
            return "v1";
        }

        public Task SignalChangeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
