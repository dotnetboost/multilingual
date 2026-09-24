using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.Core;

/// <summary>
/// Checks that the engine's configuration matches what is actually stored. Today that means one
/// thing: in base-language mode, <see cref="MultiLingualOptions.BaseLanguage"/> has to name a
/// language that exists and is active, because every entity's own table holds that language and
/// every other language falls back to it.
/// </summary>
public static partial class MultiLingualValidation
{
    private const string LoggerCategory = "DotNetBoost.MultiLingual.Validation";

    /// <summary>
    /// Verifies the configured base language against the language store. A no-op when no base
    /// language is configured.
    ///
    /// <para><b>What it deliberately tolerates.</b> An unreachable store (a database that has not
    /// been migrated yet, a provider registered later in startup) and an empty language list are
    /// both logged and allowed. Neither is the mistake this looks for, and failing the host for
    /// the empty case would be a deadlock: creating that first language is usually what the
    /// application's own endpoints are for.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Languages exist but the base language is not among them, or it exists and is inactive.
    /// </exception>
    public static async Task ValidateBaseLanguageAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<MultiLingualOptions>();
        if (!options.HasBaseLanguage)
            return;

        var baseLanguage = options.BaseLanguage!;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory) ?? NullLogger.Instance;

        // Its own scope: the store is scoped, and this runs from the root provider at startup.
        // An ASYNC scope, because disposing a synchronous one throws as soon as anything it created
        // implements only IAsyncDisposable — which a provider's connection or unit of work may well
        // do, and the failure would be at startup, in library code, with nothing pointing at the
        // application's own registration.
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

        IReadOnlyList<Language> languages;
        try
        {
            languages = await scope.ServiceProvider.GetRequiredService<ILanguageStore>().GetAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.StoreUnreachable(logger, baseLanguage, ex);
            return;
        }

        if (languages.Count == 0)
        {
            Log.NoLanguagesYet(logger, baseLanguage);
            return;
        }

        var match = languages.FirstOrDefault(l =>
            string.Equals(l.Code, baseLanguage, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var known = string.Join(", ", languages.Select(l => l.Code).OrderBy(c => c, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"BaseLanguage is '{baseLanguage}', but no language with that code exists. Configured " +
                $"languages: {known}. Every entity's own table holds the base language and every other " +
                "language falls back to it, so it has to exist. Create it, or correct BaseLanguage.");
        }

        if (!match.IsActive)
        {
            throw new InvalidOperationException(
                $"BaseLanguage '{match.Code}' exists but is inactive. Content in every other language " +
                "falls back to it, so it has to stay active. Activate it, or correct BaseLanguage.");
        }

        Log.BaseLanguageConfirmed(logger, match.Code);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 3000, Level = LogLevel.Debug,
            Message = "Base language '{BaseLanguage}' is configured and active.")]
        public static partial void BaseLanguageConfirmed(ILogger logger, string baseLanguage);

        [LoggerMessage(EventId = 3001, Level = LogLevel.Warning,
            Message = "Could not read the language list to confirm base language '{BaseLanguage}'. " +
                      "If the database has not been created yet this is expected; otherwise the base " +
                      "language is unverified.")]
        public static partial void StoreUnreachable(ILogger logger, string baseLanguage, Exception exception);

        [LoggerMessage(EventId = 3002, Level = LogLevel.Warning,
            Message = "No languages exist yet, so base language '{BaseLanguage}' could not be confirmed. " +
                      "Create it before reading or writing translatable content.")]
        public static partial void NoLanguagesYet(ILogger logger, string baseLanguage);
    }
}

/// <summary>
/// Runs <see cref="MultiLingualValidation.ValidateBaseLanguageAsync"/> as the host starts.
/// Registered by <c>Build()</c> when a base language is configured and
/// <see cref="MultiLingualOptions.ValidateBaseLanguageAtStartup"/> is on.
/// </summary>
internal sealed class MultiLingualValidationHostedService(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        MultiLingualValidation.ValidateBaseLanguageAsync(services, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
