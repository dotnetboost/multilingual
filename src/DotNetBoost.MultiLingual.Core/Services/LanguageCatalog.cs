using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>
/// Default <see cref="ILanguageCatalog"/>: a process-wide snapshot of the language list,
/// reloaded on <see cref="Invalidate"/> or once <see cref="MultiLingualOptions.LanguageCacheDuration"/>
/// has passed. Singleton, so it opens its own scope to reach the (scoped) store.
///
/// <para><b>Other instances' changes.</b> <see cref="Invalidate"/> only reaches this process.
/// With an <see cref="ILanguageChangeSignal"/> registered, the snapshot also records the version
/// it was loaded under, and once per <see cref="MultiLingualOptions.LanguageChangeCheckInterval"/>
/// the version is read again; a different one means another instance changed the list, and it is
/// reloaded. An unreachable signal is logged and treated as "no change".</para>
/// </summary>
/// <param name="scopeFactory">Opens the scope the store is resolved in.</param>
/// <param name="options">Cache duration and change-check interval.</param>
/// <param name="timeProvider">The clock both are measured against.</param>
/// <param name="signal">Optional; see the remarks.</param>
/// <param name="logger">Optional; reports an unreachable <paramref name="signal"/>.</param>
public sealed partial class LanguageCatalog(
    IServiceScopeFactory scopeFactory,
    MultiLingualOptions options,
    TimeProvider timeProvider,
    ILanguageChangeSignal? signal = null,
    ILogger<LanguageCatalog>? logger = null) : ILanguageCatalog, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private volatile Snapshot? _snapshot;

    /// <inheritdoc/>
    public void Invalidate() => _snapshot = null;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LanguageInfo>> GetAllAsync(CancellationToken ct = default)
    {
        if (_snapshot is { } fresh && IsFresh(fresh))
            return fresh.Languages;

        await _gate.WaitAsync(ct);
        try
        {
            var current = _snapshot;
            if (current is not null && IsFresh(current))
                return current.Languages;

            // Only the change check is due: one small read, and usually nothing else.
            var version = await ReadVersionAsync(current, ct);
            if (current is not null && !IsExpired(current) && version.Unchanged)
                return Checked(current);

            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ILanguageStore>();

            return Store(await store.GetAllAsync(ct), version.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<LanguageInfo> GetAll()
    {
        if (_snapshot is { } fresh && IsFresh(fresh))
            return fresh.Languages;

        _gate.Wait();
        try
        {
            var current = _snapshot;
            if (current is not null && IsFresh(current))
                return current.Languages;

            var version = ReadVersion(current);
            if (current is not null && !IsExpired(current) && version.Unchanged)
                return Checked(current);

            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ILanguageStore>();

            return Store(store.GetAll(), version.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<LanguageInfo> GetDefaultAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.FirstOrDefault(l => l.IsDefault) ?? throw new NoLanguageConfiguredException();
    }

    /// <inheritdoc/>
    public async Task<LanguageInfo?> FindActiveByCodeAsync(string? code, CancellationToken ct = default)
    {
        var match = await FindByCodeAsync(code, ct);
        return match is { IsActive: true } ? match : null;
    }

    /// <inheritdoc/>
    public async Task<LanguageInfo?> FindByCodeAsync(string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var all = await GetAllAsync(ct);
        var trimmed = code.Trim();
        return all.FirstOrDefault(l => string.Equals(l.Code, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    // ------------------------------------------------------------------ snapshot

    private LanguageInfo[] Store(IReadOnlyList<Language> stored, string? version)
    {
        var languages = stored
            .Select(LanguageInfo.From)
            .OrderBy(l => l.Code, StringComparer.Ordinal)
            .ToArray();

        var now = timeProvider.GetUtcNow();
        _snapshot = new Snapshot(languages, now, version, now);
        return languages;
    }

    private LanguageInfo[] Checked(Snapshot current)
    {
        _snapshot = current with { CheckedAt = timeProvider.GetUtcNow() };
        return current.Languages;
    }

    private bool IsFresh(Snapshot snapshot) => !IsExpired(snapshot) && !IsCheckDue(snapshot);

    private bool IsExpired(Snapshot snapshot) =>
        timeProvider.GetUtcNow() - snapshot.LoadedAt >= options.LanguageCacheDuration;

    private bool IsCheckDue(Snapshot snapshot) =>
        signal is not null && timeProvider.GetUtcNow() - snapshot.CheckedAt >= options.LanguageChangeCheckInterval;

    // ------------------------------------------------------------------ change signal

    /// <summary>
    /// The signalled version, and whether it matches <paramref name="current"/>'s. Read BEFORE any
    /// reload, so a change landing mid-reload leaves a mismatch for the next check to catch.
    /// </summary>
    private async Task<VersionRead> ReadVersionAsync(Snapshot? current, CancellationToken ct)
    {
        if (signal is null)
            return new VersionRead(null, Unchanged: true);

        try
        {
            var version = await signal.GetVersionAsync(ct);
            return new VersionRead(version, current is not null && string.Equals(version, current.Version, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreachable(current, ex);
        }
    }

    private VersionRead ReadVersion(Snapshot? current)
    {
        if (signal is null)
            return new VersionRead(null, Unchanged: true);

        try
        {
            var version = signal.GetVersion();
            return new VersionRead(version, current is not null && string.Equals(version, current.Version, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreachable(current, ex);
        }
    }

    /// <summary>Keep serving what there is: the cache expiry still bounds how stale it gets.</summary>
    private VersionRead Unreachable(Snapshot? current, Exception ex)
    {
        LogSignalUnreachable(_logger, ex);
        return new VersionRead(current?.Version, Unchanged: true);
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning,
        Message = "Could not read the language change signal; serving the cached language list until it expires.")]
    private static partial void LogSignalUnreachable(ILogger logger, Exception exception);

    private sealed record Snapshot(LanguageInfo[] Languages, DateTimeOffset LoadedAt, string? Version, DateTimeOffset CheckedAt);

    private readonly record struct VersionRead(string? Value, bool Unchanged);
}
