using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Data;

namespace DotNetBoost.MultiLingual.Dapper;

/// <summary>
/// Creates the provider's tables once, when the host starts. Registered by
/// <c>UseDapper(..., migrateSchema: true)</c>; the DDL is conditional, so running it on every
/// boot costs one round trip and changes nothing after the first.
/// </summary>
/// <param name="services">Resolves the connection, in a scope of its own because it is scoped.</param>
/// <param name="options">Table naming.</param>
/// <param name="logger">Records what was created, or why it could not be.</param>
internal sealed partial class DapperMigrationHostedService(
    IServiceProvider services,
    DapperOptions options,
    ILogger<DapperMigrationHostedService> logger) : IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        try
        {
            var connection = scope.ServiceProvider.GetRequiredService<IDbConnection>();
            await DapperSchema.EnsureCreatedAsync(connection, options, cancellationToken).ConfigureAwait(false);
            LogSchemaEnsured(logger, options.LanguagesTable, options.TranslationsTable);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Deliberately fatal. Every read and write this provider makes needs these two tables,
            // so a host that starts without them only fails later, on a request, and less legibly.
            // Pass migrateSchema: false when the schema is managed elsewhere.
            LogSchemaFailed(logger, options.LanguagesTable, options.TranslationsTable, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 6100, Level = LogLevel.Debug,
        Message = "MultiLingual tables ensured ('{languages}', '{translations}').")]
    private static partial void LogSchemaEnsured(ILogger logger, string languages, string translations);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Error,
        Message = "Could not create the MultiLingual tables ('{languages}', '{translations}').")]
    private static partial void LogSchemaFailed(ILogger logger, string languages, string translations, Exception exception);
}
