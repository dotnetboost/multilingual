using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.Core.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>A signal whose backend is down — for reads, writes, or both.</summary>
internal sealed class BrokenSignal(bool reads = true, bool writes = true) : ILanguageChangeSignal
{
    public Task<string?> GetVersionAsync(CancellationToken ct = default) =>
        reads ? throw new InvalidOperationException("cache down") : Task.FromResult<string?>(null);

    public string? GetVersion() => reads ? throw new InvalidOperationException("cache down") : null;

    public Task SignalChangeAsync(CancellationToken ct = default) =>
        writes ? throw new InvalidOperationException("cache down") : Task.CompletedTask;
}

/// <summary>
/// Two instances of the application over one database: a language change on one reaches the
/// other through the change signal, instead of when the other's cache expires.
/// </summary>
public sealed class LanguageSyncTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private static MemoryDistributedCache NewSharedCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    /// <summary>Server A (which seeds en, ar, fr) and server B, sharing the stores and <paramref name="cache"/>.</summary>
    private static async Task<(Engine A, Engine B)> TwoServersAsync(IDistributedCache cache)
    {
        var a = await Engine.StartAsync(
            b => b.SynchronizeLanguagesThroughDistributedCache(Interval),
            s => s.AddSingleton(cache));

        var b = a.StartSibling(
            b => b.SynchronizeLanguagesThroughDistributedCache(Interval),
            s => s.AddSingleton(cache));

        return (a, b);
    }

    private static async Task CreateLanguageAsync(Engine server, string code)
    {
        await using var scope = server.Scope();
        await scope.Get<ILanguageManager>().CreateAsync(new NewLanguage { Code = code, Name = code });
    }

    private static async Task<string[]> CodesAsync(Engine server) =>
        (await server.GetSingleton<ILanguageCatalog>().GetAllAsync()).Select(l => l.Code).ToArray();

    [Fact]
    public async Task ChangeOnOneServer_ReachesTheOther_WithinTheCheckInterval()
    {
        var (a, b) = await TwoServersAsync(NewSharedCache());
        await using var _ = a;
        await using var __ = b;

        Assert.Equal(["ar", "en", "fr"], await CodesAsync(b));

        await CreateLanguageAsync(a, "de");
        Assert.Equal(["ar", "de", "en", "fr"], await CodesAsync(a));   // A invalidated itself
        Assert.Equal(["ar", "en", "fr"], await CodesAsync(b));         // B has not checked yet

        b.Clock.Now += Interval;
        Assert.Equal(["ar", "de", "en", "fr"], await CodesAsync(b));
    }

    [Fact]
    public async Task SynchronousReads_SeeTheChangeToo()
    {
        var (a, b) = await TwoServersAsync(NewSharedCache());
        await using var _ = a;
        await using var __ = b;

        var catalog = b.GetSingleton<ILanguageCatalog>();
        Assert.Equal(3, catalog.GetAll().Count);

        await CreateLanguageAsync(a, "de");
        b.Clock.Now += Interval;

        Assert.Contains(catalog.GetAll(), l => l.Code == "de");
    }

    [Fact]
    public async Task WithoutAChange_ACheckReadsOnlyTheVersion()
    {
        var (a, b) = await TwoServersAsync(NewSharedCache());
        await using var _ = a;
        await using var __ = b;

        await CodesAsync(b);
        var loads = b.Languages.GetAllCalls;

        for (var i = 0; i < 3; i++)
        {
            b.Clock.Now += Interval;
            await CodesAsync(b);
        }

        Assert.Equal(loads, b.Languages.GetAllCalls);
    }

    [Fact]
    public async Task WithoutASignal_TheOtherServerWaitsForItsCacheToExpire()
    {
        await using var a = await Engine.StartAsync();
        await using var b = a.StartSibling();

        await CodesAsync(b);
        await CreateLanguageAsync(a, "de");

        b.Clock.Now += Interval;
        Assert.DoesNotContain("de", await CodesAsync(b));

        b.Clock.Now += new MultiLingualOptions().LanguageCacheDuration;
        Assert.Contains("de", await CodesAsync(b));
    }

    [Fact]
    public async Task AnEvictedVersion_CausesOneReload_ThenSettles()
    {
        var cache = NewSharedCache();
        var (a, b) = await TwoServersAsync(cache);
        await using var _ = a;
        await using var __ = b;

        await CodesAsync(b);
        await cache.RemoveAsync(DistributedCacheLanguageChangeSignal.DefaultKey);
        var loads = b.Languages.GetAllCalls;

        b.Clock.Now += Interval;
        await CodesAsync(b);
        Assert.Equal(loads + 1, b.Languages.GetAllCalls);

        b.Clock.Now += Interval;
        await CodesAsync(b);
        Assert.Equal(loads + 1, b.Languages.GetAllCalls);
    }

    [Fact]
    public async Task AnUnreachableSignal_KeepsServingTheCachedList()
    {
        await using var engine = await Engine.StartAsync(b => b.UseLanguageChangeSignal(_ => new BrokenSignal(), Interval));
        var catalog = engine.GetSingleton<ILanguageCatalog>();

        Assert.Equal(3, (await catalog.GetAllAsync()).Count);
        var loads = engine.Languages.GetAllCalls;

        engine.Clock.Now += Interval;
        Assert.Equal(3, (await catalog.GetAllAsync()).Count);
        Assert.Equal(3, catalog.GetAll().Count);
        Assert.Equal(loads, engine.Languages.GetAllCalls);

        // The cache expiry still applies.
        engine.Clock.Now += new MultiLingualOptions().LanguageCacheDuration;
        await catalog.GetAllAsync();
        Assert.Equal(loads + 1, engine.Languages.GetAllCalls);
    }

    [Fact]
    public async Task ASignalThatCannotBeSent_DoesNotFailTheWrite()
    {
        await using var engine = await Engine.StartAsync(b => b.UseLanguageChangeSignal(_ => new BrokenSignal(reads: false), Interval));

        await CreateLanguageAsync(engine, "de");

        Assert.Contains("de", await CodesAsync(engine));
    }

    [Fact]
    public async Task DistributedCacheSignal_WithoutACache_SaysWhatToRegister()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Engine.StartAsync(b => b.SynchronizeLanguagesThroughDistributedCache()));

        Assert.Contains("IDistributedCache", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckInterval_IsConfigurable_AndMustBePositive()
    {
        var services = new ServiceCollection();
        var builder  = services.AddMultiLingual().SynchronizeLanguagesThroughDistributedCache(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(2), services.BuildServiceProvider().GetRequiredService<MultiLingualOptions>().LanguageChangeCheckInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.SynchronizeLanguagesThroughDistributedCache(TimeSpan.Zero));
    }
}
