namespace DotNetBoost.MultiLingual.Core.Models;

/// <summary>
/// A language content can be served in. Exactly one language is the default at any moment —
/// an invariant <see cref="Interfaces.ILanguageManager"/> maintains and every
/// <see cref="Interfaces.ILanguageStore"/> must preserve atomically.
/// <para>
/// Not sealed, on purpose: it is an entity type in the application's own context, and EF Core's
/// lazy-loading proxies refuse a model that contains a sealed entity type.
/// </para>
/// </summary>
public class Language
{
    /// <summary>
    /// Canonical ISO 639-1 code, optionally with a region: <c>en</c>, <c>ar</c>, <c>pt-BR</c>.
    /// This is the language's identity and is immutable once created — translations refer to
    /// it directly, so renaming it would orphan them.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Display name, e.g. "English". Deliberately NOT translatable — a language picker shows
    /// each language's name in its own language.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Left-to-right or right-to-left, so clients can lay content out correctly.</summary>
    public TextDirection Direction { get; set; } = TextDirection.Ltr;

    /// <summary>Whether this is the system default, used as the read-path fallback.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Inactive languages are kept with their content but never served.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>Writing direction of a language.</summary>
public enum TextDirection
{
    /// <summary>Left to right.</summary>
    Ltr,

    /// <summary>Right to left.</summary>
    Rtl
}

/// <summary>An immutable snapshot of one language, cheap to cache and pass around.</summary>
/// <param name="Code">Canonical language code.</param>
/// <param name="Name">Display name.</param>
/// <param name="Direction">Writing direction.</param>
/// <param name="IsDefault">Whether this is the system default.</param>
/// <param name="IsActive">Whether this language is served.</param>
public sealed record LanguageInfo(
    string Code,
    string Name,
    TextDirection Direction,
    bool IsDefault,
    bool IsActive)
{
    /// <summary>Snapshots a stored <see cref="Language"/>.</summary>
    public static LanguageInfo From(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return new LanguageInfo(language.Code, language.Name, language.Direction, language.IsDefault, language.IsActive);
    }
}

/// <summary>Input for <see cref="Interfaces.ILanguageManager.CreateAsync"/>.</summary>
public sealed record NewLanguage
{
    /// <summary>ISO 639-1 code, optionally with a region. Normalised before storing.</summary>
    public required string Code { get; init; }

    /// <summary>Display name, 1–128 characters.</summary>
    public required string Name { get; init; }

    /// <summary>Omit to take the direction from the runtime's culture data for the code.</summary>
    public TextDirection? Direction { get; init; }

    /// <summary>Whether the language is served. The first language is always active.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Make this the default immediately. The very first language created is always the
    /// default regardless of this flag — the system cannot function without one.
    /// </summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// Input for <see cref="Interfaces.ILanguageManager.UpdateAsync"/>. A full replacement of the
/// mutable fields; the code is the identity and cannot change.
/// </summary>
public sealed record LanguageUpdate
{
    /// <summary>Display name, 1–128 characters.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Omit to re-derive the direction from the code, rather than keep the stored value —
    /// consistent with this being a full replacement.
    /// </summary>
    public TextDirection? Direction { get; init; }

    /// <summary>The default language cannot be deactivated; promote another one first.</summary>
    public bool IsActive { get; init; } = true;
}
