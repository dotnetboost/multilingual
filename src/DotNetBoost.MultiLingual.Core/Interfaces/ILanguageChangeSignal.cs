namespace DotNetBoost.MultiLingual.Core.Interfaces;

/// <summary>
/// Tells every instance of the application that the language list changed, so each one reloads
/// its cached copy within seconds instead of waiting out <see cref="MultiLingualOptions.LanguageCacheDuration"/>.
///
/// <para>
/// A version token in some store every instance can reach: <see cref="ILanguageManager"/> calls
/// <see cref="SignalChangeAsync"/> after each write, and the language catalog compares
/// <see cref="GetVersionAsync"/> against the version its copy was loaded with, at most once per
/// <see cref="MultiLingualOptions.LanguageChangeCheckInterval"/>. The built-in implementation uses
/// <c>IDistributedCache</c> (<c>SynchronizeLanguagesThroughDistributedCache()</c>); register your
/// own with <c>UseLanguageChangeSignal&lt;T&gt;()</c>.
/// </para>
///
/// <para>
/// Optional. Without one, each instance only sees another's change when its cache expires.
/// Failures are logged and tolerated on both sides: a signal is an optimisation, and the cache
/// expiry still bounds how stale a list can get.
/// </para>
/// </summary>
public interface ILanguageChangeSignal
{
    /// <summary>The current version token, or <c>null</c> when no change has been signalled (or it was evicted).</summary>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>Synchronous <see cref="GetVersionAsync"/>, for a synchronous <c>SaveChanges</c>.</summary>
    string? GetVersion();

    /// <summary>Publishes a new version token.</summary>
    Task SignalChangeAsync(CancellationToken ct = default);
}
