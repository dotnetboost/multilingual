using Microsoft.Extensions.Hosting;

namespace SampleApp.Dapper.Data;

/// <summary>
/// Runs <see cref="DbInitializer"/> when the host starts.
/// <para>
/// A hosted service rather than a call before <c>app.Run()</c> — which is what the EF Core and
/// MongoDB samples do — and the reason is ordering. <c>UseDapper(..., migrateSchema: true)</c>
/// creates <c>Languages</c> and <c>Translations</c> from a hosted service of its own, and hosted
/// services do not start until <c>app.Run()</c>. Seeding before that line would therefore run
/// BEFORE the tables it needs exist. Registering this after the MultiLingual chain puts it second
/// in the queue, because hosted services start in registration order.
/// </para>
/// <para>
/// Worth knowing for any application that turns <c>migrateSchema</c> on: anything that touches the
/// library's tables at startup has to be ordered after it.
/// </para>
/// </summary>
/// <param name="services">Resolves a scope for the seeder.</param>
public sealed class DatabaseSeeder(IServiceProvider services) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) =>
        DbInitializer.InitializeAsync(services, cancellationToken);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
