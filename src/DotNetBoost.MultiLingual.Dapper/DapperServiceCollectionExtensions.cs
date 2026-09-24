using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Dapper;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the Dapper storage provider.</summary>
    public static class DapperBuilderExtensions
    {
        /// <summary>
        /// Configures Dapper as the provider, over a connection created per scope:
        /// <code>
        /// services.AddMultiLingual()
        ///         .UseDapper(sp =&gt; new SqlConnection(connectionString), migrateSchema: true)
        ///         .Build();
        /// </code>
        /// <para>
        /// The connection is registered as <see cref="IDbConnection"/> in the scope, so the
        /// container disposes it and application code can inject the same one — begin a
        /// transaction on it, write your entity, and hand the transaction to
        /// <see cref="DapperTranslationStore.WithTransaction"/> so both commit together.
        /// </para>
        /// <para>
        /// There is no change tracker to hook, so application code localizes after a query and
        /// saves translations itself through <see cref="IMultiLingualManager"/>. See docs/dapper.md.
        /// </para>
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="connectionFactory">
        /// Creates the connection for each scope. Its runtime type selects the SQL dialect —
        /// SQL Server, PostgreSQL or SQLite — unless <see cref="DapperOptions.Dialect"/> says otherwise.
        /// </param>
        /// <param name="migrateSchema">
        /// When <c>true</c>, a hosted service creates the <c>Languages</c> and <c>Translations</c>
        /// tables at startup if they are absent, in the shape the EF Core provider maps. Leave it
        /// off — the default — when the schema is owned by migrations or a deployment step; it
        /// only ever creates, and will not migrate a table whose shape has changed.
        /// </param>
        /// <param name="configure">Table and schema naming, and an explicit dialect.</param>
        /// <exception cref="InvalidOperationException">A provider is already configured on this builder.</exception>
        public static MultiLingualBuilder UseDapper(
            this MultiLingualBuilder builder,
            Func<IServiceProvider, IDbConnection> connectionFactory,
            bool migrateSchema = false,
            Action<DapperOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(connectionFactory);
            MultiLingualBuilderGuard.EnsureProviderNotConfigured(builder, "Dapper");

            var options = new DapperOptions();
            configure?.Invoke(options);
            builder.Services.TryAddSingleton(options);

            // Scoped and container-owned: the connection is disposed with the scope, and anything
            // else in the scope that asks for IDbConnection gets the very one the stores use.
            builder.Services.AddScoped(connectionFactory);

            // Scoped too, so a transaction the application puts in the scope is the one the stores
            // enlist in — an entity and its translations then commit or roll back together.
            builder.Services.AddScoped<ITranslationStore>(sp => new DapperTranslationStore(
                sp.GetRequiredService<IDbConnection>(), options, sp.GetService<IDbTransaction>()));
            builder.Services.AddScoped<ILanguageStore>(sp => new DapperLanguageStore(
                sp.GetRequiredService<IDbConnection>(), options, sp.GetService<IDbTransaction>()));

            // IEntityKeyAccessor is deliberately left alone: Dapper maps plain objects, so the
            // Core convention ([Key], Id, {Type}Id) is already the best available answer.

            if (migrateSchema)
            {
                builder.Services.AddSingleton<IHostedService>(sp => new DapperMigrationHostedService(
                    sp,
                    options,
                    sp.GetService<ILogger<DapperMigrationHostedService>>()
                        ?? NullLogger<DapperMigrationHostedService>.Instance));
            }

            return builder;
        }
    }
}
