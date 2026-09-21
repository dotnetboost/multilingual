using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>
/// Default <see cref="ILanguageCatalog"/>: a process-wide snapshot of the language list,
/// reloaded on <see cref="Invalidate"/> or once <see cref="MultiLingualOptions.LanguageCacheDuration"/>
/// has passed. Singleton, so it opens its own scope to reach the (scoped) store.
/// </summary>
public sealed class LanguageCatalog(
    IServiceScopeFactory scopeFactory,
    MultiLingualOptions options,
    TimeProvider timeProvider) : ILanguageCatalog, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot? _snapshot;

    /// <inheritdoc/>
    public void Invalidate() => _snapshot = null;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LanguageInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var current = _snapshot;
        if (current is not null && !IsExpired(current))
            return current.Languages;

        await _gate.WaitAsync(ct);
        try
        {
            current = _snapshot;
            if (current is not null && !IsExpired(current))
                return current.Languages;

            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ILanguageStore>();

            var languages = (await store.GetAllAsync(ct))
                .Select(LanguageInfo.From)
                .OrderBy(l => l.Code, StringComparer.Ordinal)
                .ToArray();

            _snapshot = new Snapshot(languages, timeProvider.GetUtcNow());
            return languages;
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

    private bool IsExpired(Snapshot snapshot) =>
        timeProvider.GetUtcNow() - snapshot.LoadedAt >= options.LanguageCacheDuration;

    private sealed record Snapshot(IReadOnlyList<LanguageInfo> Languages, DateTimeOffset LoadedAt);
}
