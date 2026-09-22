using System.Data.Common;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Puts the request's languages into a translated query just before it runs. The compiled SQL is
/// shared by every request and holds <see cref="LanguageTokens"/> where the languages go; this
/// replaces them, in the text of the one command about to execute, with the languages of the
/// scope that runs it — which EF hands over as <see cref="DbContextEventData.Context"/>.
/// <para>
/// The replacements are language codes the library validated and normalised (letters, digits and
/// hyphens) or empty — an empty code matches no translation, which is how a lookup is switched
/// off: the fallback when there is none, both lookups when the base language is served.
/// </para>
/// <para>
/// Stateless and shared. Runs for every command of a context configured with
/// <c>UseMultiLingual(sp)</c>, and leaves any command without a token untouched.
/// </para>
/// </summary>
internal sealed class LanguageTokenInterceptor : DbCommandInterceptor
{
    /// <summary>The shared instance <see cref="MultiLingualDbContextOptionsExtensions.UseMultiLingual"/> attaches.</summary>
    public static LanguageTokenInterceptor Instance { get; } = new();

    private LanguageTokenInterceptor() { }

    /// <inheritdoc/>
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Apply(command, eventData);
        return result;
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Apply(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc/>
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Apply(command, eventData);
        return result;
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Apply(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Apply(command, eventData);
        return result;
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(command, eventData);
        return ValueTask.FromResult(result);
    }

    private static void Apply(DbCommand command, CommandEventData eventData)
    {
        var text = command.CommandText;
        if (eventData.Context is not { } context || !text.Contains(LanguageTokens.Prefix, StringComparison.Ordinal))
            return;

        var services = MultiLingualServices.For(context);
        var engine   = services.GetRequiredService<TranslationManager>();
        var options  = services.GetRequiredService<MultiLingualOptions>();

        var resolved = new Dictionary<string, (string Language, string? Fallback)>(StringComparer.OrdinalIgnoreCase);

        (string Language, string? Fallback) Languages(string? code)
        {
            var key = code ?? "";
            if (!resolved.TryGetValue(key, out var languages))
            {
                languages = engine.ReadLanguages(code);
                resolved[key] = languages;
            }

            return languages;
        }

        string Sql(string? code) => code is null || options.IsBaseLanguage(code) ? "" : code;

        command.CommandText = LanguageTokens.Pattern().Replace(text, match =>
        {
            var explicitLanguage = match.Groups["code"].Success ? match.Groups["code"].Value : null;
            var languages        = Languages(explicitLanguage);

            return match.Groups["kind"].Value == "L" ? Sql(languages.Language) : Sql(languages.Fallback);
        });
    }
}
