using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.MongoDb;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the MongoDB storage provider.</summary>
    public static class MongoDbBuilderExtensions
    {
        /// <summary>
        /// Configures MongoDB as the provider, over the <see cref="IMongoDatabase"/> the
        /// application has already registered:
        /// <code>
        /// services.AddSingleton&lt;IMongoClient&gt;(new MongoClient(connectionString));
        /// services.AddSingleton(sp =&gt; sp.GetRequiredService&lt;IMongoClient&gt;().GetDatabase("shop"));
        ///
        /// services.AddMultiLingual()
        ///         .UseMongoDb()
        ///         .Build();
        /// </code>
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">Collection naming and index behaviour.</param>
        public static MultiLingualBuilder UseMongoDb(
            this MultiLingualBuilder builder,
            Action<MongoDbOptions>? configure = null) =>
            builder.UseMongoDb(static sp => sp.GetRequiredService<IMongoDatabase>(), configure);

        /// <summary>
        /// Configures MongoDB as the provider over its own client, for an application that does
        /// not register one itself. The client is a singleton, as the driver intends — it owns the
        /// connection pool.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="connectionString">A MongoDB connection string.</param>
        /// <param name="databaseName">The database holding the two collections.</param>
        /// <param name="configure">Collection naming and index behaviour.</param>
        public static MultiLingualBuilder UseMongoDb(
            this MultiLingualBuilder builder,
            string connectionString,
            string databaseName,
            Action<MongoDbOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            builder.Services.TryAddSingleton<IMongoClient>(_ => new MongoClient(connectionString));

            return builder.UseMongoDb(
                sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName),
                configure);
        }

        /// <summary>
        /// Configures MongoDB as the provider over the database <paramref name="database"/>
        /// resolves — one per tenant, say, from a scoped accessor.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="database">Resolves the database holding the two collections.</param>
        /// <param name="configure">Collection naming and index behaviour.</param>
        public static MultiLingualBuilder UseMongoDb(
            this MultiLingualBuilder builder,
            Func<IServiceProvider, IMongoDatabase> database,
            Action<MongoDbOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(database);
            MultiLingualBuilderGuard.EnsureProviderNotConfigured(builder, "MongoDb");

            var options = new MongoDbOptions();
            configure?.Invoke(options);
            builder.Services.TryAddSingleton(options);

            // Class maps and the convention that keeps translatable properties out of the
            // application's own documents. Process-global, as all BSON mapping is, and read lazily
            // — so a WithBaseLanguage() later in the chain still reaches it.
            MultiLingualBsonMapping.Register(builder.Options);

            // Scoped, so a session the application puts in the scope is the one the stores use.
            builder.Services.AddScoped<ITranslationStore>(sp =>
                new MongoTranslationStore(database(sp), options, sp.GetService<IClientSessionHandle>()));
            builder.Services.AddScoped<ILanguageStore>(sp =>
                new MongoLanguageStore(database(sp), options, sp.GetService<IClientSessionHandle>()));

            // The driver knows the id better than the naming convention does — and only it can
            // tell an un-inserted document's ObjectId.Empty from a real key.
            builder.Services.Replace(ServiceDescriptor.Scoped<IEntityKeyAccessor, MongoEntityKeyAccessor>());

            if (options.CreateIndexes)
            {
                builder.Services.AddSingleton<IHostedService>(sp => new MongoIndexInitializer(
                    sp,
                    database,
                    options,
                    sp.GetService<ILogger<MongoIndexInitializer>>() ?? NullLogger<MongoIndexInitializer>.Instance));
            }

            return builder;
        }
    }
}
