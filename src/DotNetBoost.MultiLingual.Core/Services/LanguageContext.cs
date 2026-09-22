using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>Default, scoped <see cref="ILanguageContext"/>.</summary>
internal sealed class LanguageContext(ILanguageCatalog catalog) : ILanguageContext
{
    private LanguageInfo? _current;
    private LanguageInfo? _default;

    /// <inheritdoc/>
    public bool IsResolved => _current is not null;

    /// <inheritdoc/>
    public LanguageInfo Current => _current ?? throw new NoLanguageConfiguredException();

    /// <inheritdoc/>
    public LanguageInfo DefaultLanguage => _default ?? throw new NoLanguageConfiguredException();

    /// <inheritdoc/>
    public bool WasRequestedLanguageHonoured { get; private set; } = true;

    /// <inheritdoc/>
    public void Apply(LanguageInfo current, LanguageInfo defaultLanguage, bool honoured = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(defaultLanguage);

        _current = current;
        _default = defaultLanguage;
        WasRequestedLanguageHonoured = honoured;
    }

    /// <inheritdoc/>
    public async Task<bool> UseAsync(string? languageCode, CancellationToken ct = default)
    {
        var defaultLanguage = await catalog.GetDefaultAsync(ct);
        var requested       = await catalog.FindActiveByCodeAsync(languageCode, ct);

        // No code at all is not a refusal: it asks for the default, and gets it.
        var honoured = requested is not null || string.IsNullOrWhiteSpace(languageCode);

        Apply(requested ?? defaultLanguage, defaultLanguage, honoured);
        return requested is not null;
    }
}
