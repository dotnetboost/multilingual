using System.Text;
using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.Extensions.Caching.Distributed;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>
/// <see cref="ILanguageChangeSignal"/> over <see cref="IDistributedCache"/> — Redis, SQL Server,
/// or whatever the application already shares between instances. The version is one small entry
/// with no expiry; if the cache evicts it anyway, every instance reloads once and carries on.
/// </summary>
/// <param name="cache">A cache every instance of the application shares.</param>
/// <param name="key">The entry holding the version. Distinct per application if several share a cache.</param>
public sealed class DistributedCacheLanguageChangeSignal(IDistributedCache cache, string key = DistributedCacheLanguageChangeSignal.DefaultKey)
    : ILanguageChangeSignal
{
    /// <summary>The entry used when none is given.</summary>
    public const string DefaultKey = "DotNetBoost.MultiLingual:languages:version";

    /// <inheritdoc/>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default) =>
        Decode(await cache.GetAsync(key, ct));

    /// <inheritdoc/>
    public string? GetVersion() => Decode(cache.Get(key));

    /// <inheritdoc/>
    public Task SignalChangeAsync(CancellationToken ct = default) =>
        cache.SetAsync(key, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")), new DistributedCacheEntryOptions(), ct);

    private static string? Decode(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);
}
