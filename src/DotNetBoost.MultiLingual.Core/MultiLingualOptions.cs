namespace DotNetBoost.MultiLingual.Core;

/// <summary>
/// Runtime options for the translation engine. Registered as a singleton by
/// <c>services.AddMultiLingual()</c> and configured through <see cref="MultiLingualBuilder"/>.
/// </summary>
public sealed class MultiLingualOptions
{
    private string? _baseLanguage;

    /// <summary>
    /// How long the language list is cached before it is re-read from the store. Default:
    /// 5 minutes. Writes through <c>ILanguageManager</c> invalidate the local cache
    /// immediately; this bounds how long OTHER instances of the application serve a stale list —
    /// unless an <c>ILanguageChangeSignal</c> tells them sooner (see
    /// <see cref="LanguageChangeCheckInterval"/>). Set via <c>WithLanguageCacheDuration()</c>.
    /// </summary>
    public TimeSpan LanguageCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often, at most, the cached language list asks the <c>ILanguageChangeSignal</c> whether
    /// another instance changed it. Default: 5 seconds. Only used when a signal is registered —
    /// <c>SynchronizeLanguagesThroughDistributedCache()</c> or <c>UseLanguageChangeSignal&lt;T&gt;()</c>,
    /// which both set it.
    /// </summary>
    public TimeSpan LanguageChangeCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether a value missing in the requested language is read from the default language.
    /// Default: <c>true</c>. When false, a missing translation reads as <c>null</c> — or, in
    /// base-language mode, as the base language's value, which is always the last resort.
    /// Turn it off via <c>DisableDefaultLanguageFallback()</c>.
    /// </summary>
    public bool FallbackToDefaultLanguage { get; set; } = true;

    /// <summary>
    /// The language stored in each entity's OWN table rather than in translation rows. Default:
    /// <c>null</c> — every language, including the default, lives in the translations table and
    /// translatable properties have no column at all.
    ///
    /// <para>
    /// Setting it (<c>o.BaseLanguage = "en"</c>) switches on <b>base-language mode</b>:
    /// </para>
    /// <list type="bullet">
    ///   <item>Translatable properties keep their column, and that column holds this language.</item>
    ///   <item>Every other language is a row in the translations table, exactly as before.</item>
    ///   <item>A missing translation falls back to this language's column, so content is never null
    ///   once it has been created.</item>
    ///   <item>An entity may only be CREATED while this is the current language, since the base
    ///   value is what every translation hangs off.</item>
    ///   <item>This language cannot be deleted or deactivated.</item>
    /// </list>
    ///
    /// <para>
    /// It is deliberately NOT the same thing as the default language. The default is a row in the
    /// language table and can be changed at runtime; the base language is a property of the
    /// SCHEMA and is fixed for the life of the database — changing it means moving content
    /// between columns and translation rows. Keeping them separate is what lets the default move
    /// freely without touching a single stored value.
    /// </para>
    ///
    /// <para>
    /// The value is validated and canonicalised on assignment, so <c>"EN"</c> is stored as
    /// <c>"en"</c>. The EF Core provider needs it at model-build time too: call
    /// <c>modelBuilder.ApplyMultiLingual(this)</c> in <c>OnModelCreating</c> so the mapping and
    /// the engine agree.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidLanguageException">The code is malformed or unknown.</exception>
    public string? BaseLanguage
    {
        get => _baseLanguage;
        set => _baseLanguage = string.IsNullOrWhiteSpace(value) ? null : LanguageCodes.Normalize(value);
    }

    /// <summary>
    /// Whether the application checks at startup that <see cref="BaseLanguage"/> exists in the
    /// language store and is active. Default: <c>true</c>, and a no-op when no base language is
    /// configured. Turn it off via <c>DisableStartupValidation()</c>, or run it yourself at a
    /// moment you choose with <c>services.ValidateMultiLingualAsync()</c>.
    /// </summary>
    public bool ValidateBaseLanguageAtStartup { get; set; } = true;

    /// <summary>True when <see cref="BaseLanguage"/> is configured, i.e. base-language mode is on.</summary>
    public bool HasBaseLanguage => _baseLanguage is not null;

    /// <summary>
    /// True when base-language mode is on and <paramref name="code"/> is the base language.
    /// Always false when <see cref="BaseLanguage"/> is not configured, which is what keeps every
    /// base-language branch inert in the default mode.
    /// </summary>
    public bool IsBaseLanguage(string? code) =>
        _baseLanguage is not null && string.Equals(code, _baseLanguage, StringComparison.OrdinalIgnoreCase);
}
