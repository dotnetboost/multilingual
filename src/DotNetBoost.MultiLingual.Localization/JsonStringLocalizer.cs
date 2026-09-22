using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace DotNetBoost.MultiLingual.Localization;

/// <summary>
/// Looks messages up along a <see cref="LanguageChain"/>. Every localizer shares one set of
/// files: the type argument of <see cref="IStringLocalizer{T}"/> only names the consumer.
/// </summary>
internal class JsonStringLocalizer(JsonResourceStore store, Func<IReadOnlyList<string>> chain) : IStringLocalizer
{
    public LocalizedString this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);

            var languages = chain();
            return Find(name, languages) is var (value, location)
                ? new LocalizedString(name, value, resourceNotFound: false, location)
                : NotFound(name, languages);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var found = this[name];
            try
            {
                // Formatted like string.Format and ResourceManagerStringLocalizer: with the ambient
                // culture, and the key itself as the format when there is no message.
                var value = string.Format(CultureInfo.CurrentCulture, found.Value, arguments);
                return new LocalizedString(name, value, found.ResourceNotFound, found.SearchedLocation);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"'{name}' in {found.SearchedLocation} is not a valid format string: {ex.Message}", ex);
            }
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var languages = chain();
        var seen      = new HashSet<string>(StringComparer.Ordinal);

        foreach (var code in includeParentCultures ? languages : languages.Take(1))
        {
            if (store.Get(code) is not { } messages)
                continue;

            var location = store.Describe(code);
            foreach (var (key, value) in messages)
            {
                if (seen.Add(key))
                    yield return new LocalizedString(key, value, resourceNotFound: false, location);
            }
        }
    }

    private (string Value, string Location)? Find(string name, IReadOnlyList<string> languages)
    {
        foreach (var code in languages)
        {
            if (store.Get(code) is { } messages && messages.TryGetValue(name, out var value))
                return (value, store.Describe(code));
        }

        return null;
    }

    private LocalizedString NotFound(string name, IReadOnlyList<string> languages) =>
        new(name, name, resourceNotFound: true,
            searchedLocation: languages.Count == 0 ? null : string.Join(", ", languages.Select(store.Describe)));
}

/// <summary>
/// The <see cref="IStringLocalizer{T}"/> applications inject. It reads the language from the
/// <see cref="ILanguageContext"/> of the scope it was resolved from — the one the request
/// middleware resolved, or the one a background job set with <c>UseAsync</c> — so it follows
/// the language even where <see cref="CultureInfo.CurrentUICulture"/> does not, such as in an
/// exception handler. Resolved from the root provider it has no language, and falls back to
/// the ambient culture.
/// </summary>
internal sealed class JsonStringLocalizer<T>(JsonResourceStore store, LanguageChain chain, ILanguageContext context)
    : JsonStringLocalizer(store, () => chain.For(context)), IStringLocalizer<T>;

/// <summary>
/// Creates localizers for code that asks the factory rather than DI — for example DataAnnotations
/// validation messages, which MVC caches for the application's lifetime. Those cannot hold a
/// scoped <see cref="ILanguageContext"/>, so they read <see cref="CultureInfo.CurrentUICulture"/>,
/// which the <c>UseMultiLingual()</c> middleware sets to the request's language.
/// </summary>
internal sealed class JsonStringLocalizerFactory(JsonResourceStore store, LanguageChain chain) : IStringLocalizerFactory
{
    private readonly JsonStringLocalizer _shared = new(store, chain.ForCurrentCulture);

    public IStringLocalizer Create(Type resourceSource) => _shared;

    public IStringLocalizer Create(string baseName, string location) => _shared;
}
