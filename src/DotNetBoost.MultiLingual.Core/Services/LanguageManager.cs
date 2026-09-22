using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>Default <see cref="ILanguageManager"/>: the language rules, over any store.</summary>
/// <param name="languages">The language store.</param>
/// <param name="translations">The translation store, to count and purge a language's rows.</param>
/// <param name="catalog">Invalidated after every write.</param>
/// <param name="options">The base language, among others.</param>
/// <param name="signal">Optional; told about every write, so other instances reload too.</param>
/// <param name="logger">Optional; reports a signal that could not be sent.</param>
internal sealed partial class LanguageManager(
    ILanguageStore languages,
    ITranslationStore translations,
    ILanguageCatalog catalog,
    MultiLingualOptions options,
    ILanguageChangeSignal? signal = null,
    ILogger<LanguageManager>? logger = null) : ILanguageManager
{
    /// <summary>Longest display name accepted.</summary>
    public const int MaxNameLength = 128;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LanguageInfo>> GetAllAsync(CancellationToken ct = default) =>
        (await languages.GetAllAsync(ct))
            .OrderByDescending(l => l.IsDefault)
            .ThenBy(l => l.Code, StringComparer.Ordinal)
            .Select(LanguageInfo.From)
            .ToList();

    /// <inheritdoc/>
    public async Task<LanguageInfo> GetAsync(string code, CancellationToken ct = default) =>
        LanguageInfo.From(await FindRequiredAsync(code, ct));

    /// <inheritdoc/>
    public async Task<LanguageInfo> CreateAsync(NewLanguage language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(language);

        var code = LanguageCodes.Normalize(language.Code);
        var name = ValidateName(language.Name);

        if (await languages.FindAsync(code, ct) is not null)
            throw new LanguageConflictException($"Language code '{code}' already exists.");

        // The system needs an active default at all times, so the first language always becomes one.
        var isFirst = (await languages.GetAllAsync(ct)).Count == 0;
        var created = new Language
        {
            Code      = code,
            Name      = name,
            Direction = language.Direction ?? LanguageCodes.InferDirection(code),
            IsDefault = isFirst || language.IsDefault,
            IsActive  = isFirst || language.IsActive
        };

        if (created is { IsDefault: true, IsActive: false })
            throw new LanguageConflictException("The default language must be active.");

        await languages.AddAsync(created, ct);
        await ChangedAsync(ct);

        return LanguageInfo.From(created);
    }

    /// <inheritdoc/>
    public async Task<LanguageInfo> UpdateAsync(string code, LanguageUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var language = await FindRequiredAsync(code, ct);
        var name     = ValidateName(update.Name);

        if (language.IsDefault && !update.IsActive)
        {
            throw new LanguageConflictException(
                "The default language cannot be deactivated. Promote another language to default first.");
        }

        // Deactivating the base language would make every entity's own column unreadable while
        // its content is still the last fallback for every other language.
        if (options.IsBaseLanguage(language.Code) && !update.IsActive)
        {
            throw new LanguageConflictException(
                $"'{language.Code}' is the base language and cannot be deactivated. Content in every " +
                "other language falls back to it.");
        }

        language.Name      = name;
        language.Direction = update.Direction ?? LanguageCodes.InferDirection(language.Code);
        language.IsActive  = update.IsActive;

        await languages.UpdateAsync(language, ct);
        await ChangedAsync(ct);

        return LanguageInfo.From(language);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string code, CancellationToken ct = default)
    {
        var language = await FindRequiredAsync(code, ct);

        // Checked before the default rule: the base language is usually the default too, and this
        // is the more informative of the two messages.
        if (options.IsBaseLanguage(language.Code))
        {
            throw new LanguageConflictException(
                $"'{language.Code}' is the base language and cannot be deleted. Its content is stored " +
                "in each entity's own table and is the last fallback for every other language. " +
                "Change BaseLanguage in configuration and move the stored content first.");
        }

        if (language.IsDefault)
        {
            throw new LanguageConflictException(
                "The default language cannot be deleted. Promote another language to default first.");
        }

        // An accidental cascade here would silently delete translated content across every
        // entity in the system, so content has to be purged as a separate, deliberate step.
        var count = await translations.CountByLanguageAsync(language.Code, ct);
        if (count > 0)
        {
            throw new LanguageConflictException(
                $"Language '{language.Code}' still has {count} translation(s). Purge them first, " +
                "or deactivate the language instead of deleting it.");
        }

        await languages.DeleteAsync(language.Code, ct);
        await ChangedAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<LanguageInfo> SetDefaultAsync(string code, CancellationToken ct = default)
    {
        var language = await FindRequiredAsync(code, ct);

        if (language.IsDefault)
            return LanguageInfo.From(language);

        if (!language.IsActive)
        {
            throw new LanguageConflictException(
                $"Language '{language.Code}' is inactive and cannot become the default. Activate it first.");
        }

        await languages.SetDefaultAsync(language.Code, ct);
        await ChangedAsync(ct);

        language.IsDefault = true;
        return LanguageInfo.From(language);
    }

    /// <inheritdoc/>
    public async Task<int> PurgeTranslationsAsync(string code, CancellationToken ct = default)
    {
        var language = await FindRequiredAsync(code, ct);
        return await translations.DeleteByLanguageAsync(language.Code, ct);
    }

    /// <summary>
    /// Looks a language up by any spelling of its code. A malformed code cannot name a stored
    /// language, so it is reported as not found rather than as a validation error.
    /// </summary>
    private async Task<Language> FindRequiredAsync(string code, CancellationToken ct)
    {
        if (!LanguageCodes.TryNormalize(code, out var canonical))
            throw new LanguageNotFoundException(code);

        return await languages.FindAsync(canonical, ct) ?? throw new LanguageNotFoundException(canonical);
    }

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            throw new InvalidLanguageException("A language name is required.");

        if (trimmed.Length > MaxNameLength)
            throw new InvalidLanguageException($"A language name can be at most {MaxNameLength} characters.");

        return trimmed;
    }

    /// <summary>
    /// Drops this instance's cached list, and tells the others. The write itself has already
    /// succeeded, so a signal that cannot be sent is logged rather than thrown: failing the call
    /// would suggest the change did not happen. Other instances then catch up when their cache
    /// expires.
    /// </summary>
    private async Task ChangedAsync(CancellationToken ct)
    {
        catalog.Invalidate();

        if (signal is null)
            return;

        try
        {
            await signal.SignalChangeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSignalFailed((ILogger?)logger ?? NullLogger.Instance, ex);
        }
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "The language list changed, but the change signal could not be sent. Other instances " +
                  "will serve their cached list until it expires.")]
    private static partial void LogSignalFailed(ILogger logger, Exception exception);
}
