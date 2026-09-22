using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System.Globalization;

namespace DotNetBoost.MultiLingual.API;

/// <summary>
/// Resolves the language of every request from the <c>Accept-Language</c> header:
/// <list type="number">
///   <item>The best quality-weighted match among ACTIVE languages — exact (<c>en-US</c>) first,
///   then the bare language part (<c>en</c>).</item>
///   <item>The system default language.</item>
/// </list>
/// The result is applied to the scoped <see cref="ILanguageContext"/> and reported on the
/// <c>Content-Language</c> response header, so callers can tell what they got. <c>Vary:
/// Accept-Language</c> is added too, so caches keep one copy per language. It is also set as
/// <see cref="CultureInfo.CurrentUICulture"/> for the rest of the pipeline — see
/// <see cref="MultiLingualRequestOptions"/>.
/// </summary>
/// <param name="next">The rest of the pipeline.</param>
/// <param name="catalog">The language list.</param>
/// <param name="options">What else the resolved language is applied to.</param>
public sealed class LanguageResolutionMiddleware(RequestDelegate next, ILanguageCatalog catalog, MultiLingualRequestOptions options)
{
    /// <summary>Resolves the language, then runs the rest of the pipeline.</summary>
    public async Task InvokeAsync(HttpContext context, ILanguageContext languageContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(languageContext);

        var all             = await catalog.GetAllAsync(context.RequestAborted);
        var defaultLanguage = all.FirstOrDefault(l => l.IsDefault);

        // Bootstrapping: with no languages configured yet, leave the request unresolved so the
        // language endpoints still work. Anything that needs a language throws a clear 409.
        if (defaultLanguage is null)
        {
            await next(context);
            return;
        }

        var (resolved, honoured) = ReadHeaderLanguage(context.Request, all);
        languageContext.Apply(resolved ?? defaultLanguage, defaultLanguage, honoured);

        context.Response.Headers[HeaderNames.ContentLanguage] = languageContext.Current.Code;
        context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptLanguage);

        // Set here, in the method that awaits the rest of the pipeline, so everything downstream
        // sees it. It does not reach an exception handler registered further out: that runs after
        // this method has returned, and with it the culture it set.
        if (options.SetCurrentUICulture)
            languageContext.ApplyUiCulture(options.SetCurrentCulture);

        await next(context);
    }

    /// <summary>
    /// Picks the highest-quality <c>Accept-Language</c> entry that matches an active language.
    /// <c>*</c> never selects a language — it means "anything", which is what the default already
    /// is — but it does make falling back to the default count as honoured.
    /// </summary>
    internal static (LanguageInfo? Match, bool Honoured) ReadHeaderLanguage(
        HttpRequest request, IReadOnlyList<LanguageInfo> languages)
    {
        if (!request.Headers.TryGetValue(HeaderNames.AcceptLanguage, out var raw) || StringValues.IsNullOrEmpty(raw))
            return (null, true);

        if (!StringWithQualityHeaderValue.TryParseList(raw.OfType<string>().ToList(), out var parsed) || parsed is null)
            return (null, true);

        // OrderByDescending is stable, so equal weights keep the caller's order.
        var ordered = parsed
            .Where(v => v.Quality is null or > 0)
            .OrderByDescending(v => v.Quality ?? 1.0);

        var namedSpecificLanguage = false;
        var acceptsAnything       = false;

        foreach (var candidate in ordered)
        {
            var value = candidate.Value.Value;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (value == "*")
            {
                acceptsAnything = true;
                continue;
            }

            namedSpecificLanguage = true;

            var exact = languages.FirstOrDefault(l =>
                l.IsActive && string.Equals(l.Code, value, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return (exact, true);

            var separator = value.IndexOf('-', StringComparison.Ordinal);
            var bare      = separator > 0 ? value[..separator] : value;

            var partial = languages.FirstOrDefault(l =>
                l.IsActive && string.Equals(l.Code, bare, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
                return (partial, true);
        }

        return (null, !namedSpecificLanguage || acceptsAnything);
    }
}

/// <summary>Pipeline registration.</summary>
public static class MultiLingualApplicationBuilderExtensions
{
    /// <summary>
    /// Adds <c>Accept-Language</c> resolution to the pipeline. Place it before the endpoints
    /// that serve translatable content — after <c>UseRouting</c> keeps the intent obvious.
    /// </summary>
    public static IApplicationBuilder UseMultiLingual(this IApplicationBuilder app) =>
        app.UseMultiLingual(static _ => { });

    /// <summary>
    /// Adds <c>Accept-Language</c> resolution to the pipeline, applying <paramref name="configure"/>
    /// to what the resolved language is carried over to:
    /// <code>
    /// app.UseMultiLingual(o =&gt; o.SetCurrentCulture = true);
    /// </code>
    /// </summary>
    public static IApplicationBuilder UseMultiLingual(this IApplicationBuilder app, Action<MultiLingualRequestOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MultiLingualRequestOptions();
        configure(options);
        return app.UseMiddleware<LanguageResolutionMiddleware>(options);
    }
}

/// <summary>What <c>UseMultiLingual()</c> carries the resolved language over to, besides <see cref="ILanguageContext"/>.</summary>
public sealed class MultiLingualRequestOptions
{
    /// <summary>
    /// Set <see cref="CultureInfo.CurrentUICulture"/> to the resolved language, so localizers that
    /// read it — <c>.resx</c> ones, and framework validation messages — answer in the same language
    /// as translated content. Default: <c>true</c>.
    /// </summary>
    public bool SetCurrentUICulture { get; set; } = true;

    /// <summary>
    /// Also set <see cref="CultureInfo.CurrentCulture"/>, which changes how numbers and dates are
    /// parsed and formatted — <c>12,5</c> rather than <c>12.5</c> for a German caller, in form and
    /// route values as well as in output. Default: <c>false</c>. Only applies when
    /// <see cref="SetCurrentUICulture"/> is on.
    /// </summary>
    public bool SetCurrentCulture { get; set; }
}
