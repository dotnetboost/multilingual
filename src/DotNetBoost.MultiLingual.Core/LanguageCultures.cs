using DotNetBoost.MultiLingual.Core.Interfaces;
using System.Globalization;

namespace DotNetBoost.MultiLingual.Core;

/// <summary>
/// Carries the resolved language over to .NET's ambient cultures, for code that reads them
/// rather than <see cref="ILanguageContext"/> — <c>.resx</c> localizers, framework validation
/// messages, culture-aware formatting.
/// </summary>
public static class LanguageCultures
{
    /// <summary>
    /// Sets <see cref="CultureInfo.CurrentUICulture"/> — and, when
    /// <paramref name="includeFormatting"/> is true, <see cref="CultureInfo.CurrentCulture"/> — to
    /// the current language of <paramref name="context"/>.
    /// <para>
    /// Deliberately synchronous. An ambient culture set inside an <c>async</c> method is undone
    /// when that method returns, so a helper that awaited anything could not set it for its
    /// caller; this one can. Call it after <see cref="ILanguageContext.UseAsync"/> in a
    /// background job, or at the top of an exception handler, which runs outside the request
    /// middleware that set it the first time.
    /// </para>
    /// <para>
    /// The DotNetBoost JSON localizer does not need it: it reads the language from
    /// <see cref="ILanguageContext"/> directly.
    /// </para>
    /// </summary>
    /// <param name="context">The language context.</param>
    /// <param name="includeFormatting">
    /// Also set <see cref="CultureInfo.CurrentCulture"/>, which changes how numbers and dates are
    /// parsed and formatted. Off by default for that reason.
    /// </param>
    /// <returns>
    /// False when nothing was changed: no language is resolved, or the runtime has no culture
    /// for its code (invariant globalization mode).
    /// </returns>
    public static bool ApplyUiCulture(this ILanguageContext context, bool includeFormatting = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsResolved || !TryGetCulture(context.Current.Code, out var culture))
            return false;

        CultureInfo.CurrentUICulture = culture;
        if (includeFormatting)
            CultureInfo.CurrentCulture = culture;

        return true;
    }

    /// <summary>The runtime's culture for <paramref name="code"/>, if it has one.</summary>
    public static bool TryGetCulture(string? code, out CultureInfo culture)
    {
        culture = CultureInfo.InvariantCulture;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        try
        {
            culture = CultureInfo.GetCultureInfo(code);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
