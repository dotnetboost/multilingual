using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using System.Globalization;

namespace DotNetBoost.MultiLingual.Localization;

/// <summary>
/// The languages a message is looked up in, in order — the same fallback translated content
/// uses: the requested language, then the default language (unless
/// <see cref="MultiLingualOptions.FallbackToDefaultLanguage"/> is off), then the base language
/// in base-language mode, which is always the last resort. Each is followed by its parent, so
/// <c>ar-EG</c> falls back to <c>ar</c> before it falls back to another language.
/// </summary>
internal sealed class LanguageChain(MultiLingualOptions options, ILanguageCatalog catalog)
{
    /// <summary>
    /// The chain for <paramref name="context"/>, or for the ambient
    /// <see cref="CultureInfo.CurrentUICulture"/> when it has no language — outside a request, or
    /// in the root scope.
    /// </summary>
    public IReadOnlyList<string> For(ILanguageContext? context) =>
        context is { IsResolved: true }
            ? Build(context.Current.Code, context.DefaultLanguage.Code)
            : ForCurrentCulture();

    /// <summary>The chain for <see cref="CultureInfo.CurrentUICulture"/>.</summary>
    public IReadOnlyList<string> ForCurrentCulture()
    {
        var culture = CultureInfo.CurrentUICulture.Name;
        var @default = catalog.GetAll().FirstOrDefault(l => l.IsDefault)?.Code;
        return Build(string.IsNullOrEmpty(culture) ? null : culture, @default);
    }

    internal IReadOnlyList<string> Build(string? current, string? @default)
    {
        var chain = new List<string>(6);

        // Nothing asked for: the default is the answer, not a fallback.
        AddWithParents(chain, current ?? @default);

        if (options.FallbackToDefaultLanguage)
            AddWithParents(chain, @default);

        AddWithParents(chain, options.BaseLanguage);
        return chain;
    }

    private static void AddWithParents(List<string> chain, string? code)
    {
        while (!string.IsNullOrEmpty(code))
        {
            if (!chain.Contains(code, StringComparer.OrdinalIgnoreCase))
                chain.Add(code);

            var separator = code.LastIndexOf('-');
            code = separator > 0 ? code[..separator] : null;
        }
    }
}
