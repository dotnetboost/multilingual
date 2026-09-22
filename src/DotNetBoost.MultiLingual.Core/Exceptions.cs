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

/// <summary>
/// No language with the given code exists, where the language itself was being addressed —
/// read, updated or deleted. The API package answers it with a 404.
/// </summary>
public class LanguageNotFoundException : MultiLingualException
{
    /// <summary>The code that was looked up.</summary>
    public string Code { get; }

    /// <summary>Creates the exception for <paramref name="code"/>.</summary>
    public LanguageNotFoundException(string code)
        : this(code, $"Language '{code}' was not found.") { }

    /// <summary>Creates the exception for <paramref name="code"/> with a specific message.</summary>
    protected LanguageNotFoundException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// A request USED a language that does not exist — for values being written, or as the language
/// to read or query in — rather than addressing the language itself. The resource the request
/// was about exists; the request is what is wrong, so the API package answers with a 400, not
/// the 404 of its base type.
/// <para>
/// Derives from <see cref="LanguageNotFoundException"/>, so code catching that keeps working.
/// </para>
/// </summary>
public sealed class UnknownLanguageException : LanguageNotFoundException
{
    /// <summary>What the code was used for, e.g. "the values being written".</summary>
    public string Usage { get; }

    /// <summary>Creates the exception for <paramref name="code"/>, saying what it was used for.</summary>
    /// <param name="code">The unknown code.</param>
    /// <param name="usage">What the code was used for, e.g. "the values being written".</param>
    public UnknownLanguageException(string code, string usage)
        : base(code, $"Language '{code}' does not exist, so it cannot be used for {usage}. Create it first.")
    {
        Usage = usage;
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
    /// <summary>The name translations of the entity are stored under.</summary>
    public string EntityName { get; }

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
        EntityName        = entityName;
        BaseLanguage      = baseLanguage;
        RequestedLanguage = requestedLanguage;
    }
}

/// <summary>
/// A translation was set or cleared in the base language. Its values are the entity's own
/// columns, not a translation, so they are changed by assigning the properties and saving the
/// entity. Thrown by <see cref="Interfaces.ITranslationAccessor{T}"/> before anything is queued.
/// </summary>
public sealed class BaseLanguageNotTranslatableException : MultiLingualException
{
    /// <summary>The name translations of the entity are stored under.</summary>
    public string EntityName { get; }

    /// <summary>The base language that was named.</summary>
    public string BaseLanguage { get; }

    /// <summary>Creates the exception for a translation named in the base language.</summary>
    public BaseLanguageNotTranslatableException(string entityName, string baseLanguage)
        : base($"'{baseLanguage}' is the base language: '{entityName}' stores its values in its own " +
               "columns, so they are not a translation. Assign the properties and save the entity instead.")
    {
        EntityName   = entityName;
        BaseLanguage = baseLanguage;
    }
}
