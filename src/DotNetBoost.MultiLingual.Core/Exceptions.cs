namespace DotNetBoost.MultiLingual.Core;

/// <summary>
/// Base for the expected, caller-facing failures of this library. The API package maps each
/// subtype to an HTTP status; applications can catch this one type to handle them all.
/// </summary>
public abstract class MultiLingualException : Exception
{
    /// <summary>Creates the exception with a caller-facing message.</summary>
    protected MultiLingualException(string message) : base(message) { }

    /// <summary>Creates the exception with a caller-facing message and its cause.</summary>
    protected MultiLingualException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>No language with the given code exists.</summary>
public sealed class LanguageNotFoundException : MultiLingualException
{
    /// <summary>The code that was looked up.</summary>
    public string Code { get; }

    /// <summary>Creates the exception for <paramref name="code"/>.</summary>
    public LanguageNotFoundException(string code)
        : base($"Language '{code}' was not found.")
    {
        Code = code;
    }
}

/// <summary>
/// The request is well-formed but conflicts with a rule that protects existing state — a
/// duplicate code, deleting the default language, deactivating it, and so on.
/// </summary>
public sealed class LanguageConflictException : MultiLingualException
{
    /// <summary>Creates the exception with the rule that was broken.</summary>
    public LanguageConflictException(string message) : base(message) { }
}

/// <summary>A language code, name or direction is not acceptable.</summary>
public sealed class InvalidLanguageException : MultiLingualException
{
    /// <summary>Creates the exception with what was wrong.</summary>
    public InvalidLanguageException(string message) : base(message) { }
}

/// <summary>
/// Content was read or written before any language exists. Create one first — the first
/// language created becomes the default.
/// </summary>
public sealed class NoLanguageConfiguredException : MultiLingualException
{
    /// <summary>Creates the exception with the standard guidance.</summary>
    public NoLanguageConfiguredException()
        : base("No language is configured yet. Create at least one language before reading or " +
               "writing translatable content; the first language created becomes the default.") { }
}

/// <summary>
/// An entity was created while the current language was not the base language. In
/// base-language mode the base value lives in the entity's own table and every translation
/// hangs off it, so it has to exist first. Create the entity in the base language, then add
/// other languages with a follow-up update.
/// </summary>
public sealed class BaseLanguageRequiredException : MultiLingualException
{
    /// <summary>The configured base language.</summary>
    public string BaseLanguage { get; }

    /// <summary>The language the create was attempted in.</summary>
    public string RequestedLanguage { get; }

    /// <summary>Creates the exception for an entity created in the wrong language.</summary>
    public BaseLanguageRequiredException(string entityName, string baseLanguage, string requestedLanguage)
        : base($"A new '{entityName}' must be created in the base language '{baseLanguage}', but the " +
               $"current language is '{requestedLanguage}'. Create it with '{baseLanguage}', then add " +
               $"'{requestedLanguage}' with an update.")
    {
        BaseLanguage      = baseLanguage;
        RequestedLanguage = requestedLanguage;
    }
}
