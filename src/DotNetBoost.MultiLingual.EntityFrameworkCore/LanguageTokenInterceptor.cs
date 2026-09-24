using System.Data.Common;
using System.Text.RegularExpressions;
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
/// An asynchronous execution resolves the languages asynchronously — see <see cref="ApplyAsync"/>
/// for why that is worth the extra path — and a synchronous one synchronously.
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
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, eventData, cancellationToken);
        return result;
    }

    /// <inheritdoc/>
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Apply(command, eventData);
        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, eventData, cancellationToken);
        return result;
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Apply(command, eventData);
        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, eventData, cancellationToken);
        return result;
    }

    /// <summary>
    /// Fills in the tokens of a command about to execute synchronously. The language catalog is
    /// reached synchronously too, which is what a synchronous execution asks for.
    /// </summary>
    private static void Apply(DbCommand command, CommandEventData eventData)
    {
        if (Prepare(command, eventData) is not { } pending)
            return;

        var resolved = new Dictionary<string, (string Language, string? Fallback)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, code) in pending.Tokens)
            resolved[key] = pending.Engine.ReadLanguages(code);

        command.CommandText = Substitute(command.CommandText, resolved, pending.Options);
    }

    /// <summary>
    /// Fills in the tokens of a command about to execute asynchronously, awaiting the language
    /// catalog rather than blocking on it.
    /// <para>
    /// This is why the tokens are gathered before anything is substituted: the substitution itself
    /// is a synchronous <c>Regex.Replace</c>, whose evaluator cannot await, so every language the
    /// command names is resolved first and the evaluator then only looks one up.
    /// </para>
    /// <para>
    /// It matters because refreshing the catalog's snapshot reads the store and, when one is
    /// registered, the <c>ILanguageChangeSignal</c> — for the built-in signal a round trip to
    /// <c>IDistributedCache</c>, checked every
    /// <see cref="MultiLingualOptions.LanguageChangeCheckInterval"/>. Taken synchronously, on the
    /// thread pool, behind a semaphore every concurrent query also waits on, that is how a busy
    /// application starves its thread pool a few times a minute. Requests that went through
    /// <c>UseMultiLingual()</c> never reach the catalog here at all — the language is already on the
    /// scope — so this is the path of <c>InLanguage(...)</c> queries and of work with no request
    /// language resolved, such as a background job.
    /// </para>
    /// </summary>
    private static async ValueTask ApplyAsync(DbCommand command, CommandEventData eventData, CancellationToken ct)
    {
        if (Prepare(command, eventData) is not { } pending)
            return;

        var resolved = new Dictionary<string, (string Language, string? Fallback)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, code) in pending.Tokens)
            resolved[key] = await pending.Engine.ReadLanguagesAsync(code, ct);

        command.CommandText = Substitute(command.CommandText, resolved, pending.Options);
    }

    /// <summary>
    /// The engine, the options, and the distinct languages this command's tokens name — or
    /// <c>null</c> when there is nothing to do, which is every command that carries no token. The
    /// cheap <see cref="LanguageTokens.Prefix"/> test comes first, so an ordinary query pays one
    /// substring search and neither a service resolution nor a state machine.
    /// </summary>
    private static Pending? Prepare(DbCommand command, CommandEventData eventData)
    {
        if (eventData.Context is not { } context
            || !command.CommandText.Contains(LanguageTokens.Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var tokens = new List<(string Key, string? Code)>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in LanguageTokens.Pattern().Matches(command.CommandText))
        {
            var code = match.Groups["code"].Success ? match.Groups["code"].Value : null;
            if (seen.Add(code ?? ""))
                tokens.Add((code ?? "", code));
        }

        if (tokens.Count == 0)
            return null;   // the prefix appeared without forming a token

        var services = MultiLingualServices.For(context);

        return new Pending(
            services.GetRequiredService<TranslationManager>(),
            services.GetRequiredService<MultiLingualOptions>(),
            tokens);
    }

    /// <summary>
    /// Replaces every token with the language it stands for, looked up in what
    /// <see cref="Prepare"/> gathered and the two Apply methods resolved.
    /// </summary>
    private static string Substitute(
        string text,
        Dictionary<string, (string Language, string? Fallback)> resolved,
        MultiLingualOptions options)
    {
        string Sql(string? code) => code is null || options.IsBaseLanguage(code) ? "" : code;

        return LanguageTokens.Pattern().Replace(text, match =>
        {
            // The indexer, not TryGetValue: Prepare scanned this same text with this same pattern,
            // so a miss is a bug in one of them, and emitting "" would silently answer the query
            // with no translations at all.
            var languages = resolved[match.Groups["code"].Success ? match.Groups["code"].Value : ""];

            return match.Groups["kind"].Value == "L" ? Sql(languages.Language) : Sql(languages.Fallback);
        });
    }

    /// <summary>What one command needs substituted, and what to do it with.</summary>
    /// <param name="Engine">Resolves a language and its fallback.</param>
    /// <param name="Options">Says which language, if any, is the base one.</param>
    /// <param name="Tokens">The distinct languages the command names: the key it is cached under, and the explicit code or null for the request's own language.</param>
    private sealed record Pending(
        TranslationManager Engine,
        MultiLingualOptions Options,
        List<(string Key, string? Code)> Tokens);
}
