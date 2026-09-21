using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Entry point for registering DotNetBoost.MultiLingual.</summary>
    public static class MultiLingualServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the provider-neutral engine and returns a builder for further configuration.
        /// Follow it with a provider (<c>UseEntityFrameworkCore&lt;TContext&gt;()</c>) and <c>Build()</c>.
        /// </summary>
        public static MultiLingualBuilder AddMultiLingual(this IServiceCollection services) =>
            services.AddMultiLingual(static _ => { });

        /// <summary>
        /// Registers the provider-neutral engine, applies <paramref name="configure"/> to its
        /// options, and returns a builder for further configuration. Follow it with a provider
        /// (<c>UseEntityFrameworkCore&lt;TContext&gt;()</c>) and <c>Build()</c>.
        /// <code>
        /// services.AddMultiLingual(o =&gt; o.BaseLanguage = "en")
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .Build();
        /// </code>
        /// </summary>
        public static MultiLingualBuilder AddMultiLingual(
            this IServiceCollection services,
            Action<MultiLingualOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<ILanguageCatalog, LanguageCatalog>();
            services.TryAddSingleton<IEntityKeyAccessor, ConventionEntityKeyAccessor>();

            // Everything below is scoped: the current language and the per-instance write
            // bookkeeping belong to one unit of work (an HTTP request, a job run).
            services.TryAddScoped<ILanguageContext, LanguageContext>();
            services.TryAddScoped<TranslationWriteBuffer>();
            services.TryAddScoped<HydratedValueTracker>();
            services.TryAddScoped<ITranslationManager, TranslationManager>();
            services.TryAddScoped<ILanguageManager, LanguageManager>();

            var builder = new MultiLingualBuilder(services);
            configure(builder.Options);
            return builder;
        }
    }

    /// <summary>Builder methods for engine behaviour.</summary>
    public static class MultiLingualBuilderExtensions
    {
        /// <summary>Sets how long the language list is cached (default: 5 minutes).</summary>
        public static MultiLingualBuilder WithLanguageCacheDuration(this MultiLingualBuilder builder, TimeSpan duration)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
            builder.Options.LanguageCacheDuration = duration;
            return builder;
        }

        /// <summary>
        /// Reads a value missing in the requested language as <c>null</c>, instead of taking
        /// it from the default language.
        /// </summary>
        public static MultiLingualBuilder DisableDefaultLanguageFallback(this MultiLingualBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Options.FallbackToDefaultLanguage = false;
            return builder;
        }

        /// <summary>
        /// Stores <paramref name="code"/> in each entity's own table instead of in translation
        /// rows. See <see cref="MultiLingualOptions.BaseLanguage"/> for what that changes.
        /// </summary>
        public static MultiLingualBuilder WithBaseLanguage(this MultiLingualBuilder builder, string code)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Options.BaseLanguage = code;
            return builder;
        }

        /// <summary>
        /// Stops the application checking at startup that the base language exists and is active.
        /// Run the check yourself with <c>services.ValidateMultiLingualAsync()</c> if you want it
        /// at a specific point — after your own migrations and seeding, say.
        /// </summary>
        public static MultiLingualBuilder DisableStartupValidation(this MultiLingualBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Options.ValidateBaseLanguageAtStartup = false;
            return builder;
        }

        /// <summary>Replaces how entity keys are read (default: <c>[Key]</c>, <c>Id</c>, <c>{TypeName}Id</c>).</summary>
        public static MultiLingualBuilder UseEntityKeyAccessor<TAccessor>(this MultiLingualBuilder builder)
            where TAccessor : class, IEntityKeyAccessor
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Replace(ServiceDescriptor.Scoped<IEntityKeyAccessor, TAccessor>());
            return builder;
        }
    }

    /// <summary>Running the MultiLingual configuration checks on demand.</summary>
    public static class MultiLingualValidationExtensions
    {
        /// <summary>
        /// Checks that the configured base language exists in the language store and is active.
        /// A no-op without a base language. Call it once the store is ready — after migrations
        /// and seeding — when you would rather control the moment than let the startup check
        /// pick it:
        /// <code>
        /// var app = builder.Build();
        /// await app.Services.ValidateMultiLingualAsync();
        /// </code>
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Languages exist but the base language is not among them, or it is inactive.
        /// </exception>
        public static Task ValidateMultiLingualAsync(
            this IServiceProvider services,
            CancellationToken cancellationToken = default) =>
            MultiLingualValidation.ValidateBaseLanguageAsync(services, cancellationToken);
    }

    /// <summary>Builder methods for plugging in a storage backend this library does not ship.</summary>
    public static class CustomStoreBuilderExtensions
    {
        /// <summary>Configures custom store implementations as the provider, registered scoped.</summary>
        public static MultiLingualBuilder UseCustomStores<TTranslationStore, TLanguageStore>(this MultiLingualBuilder builder)
            where TTranslationStore : class, ITranslationStore
            where TLanguageStore : class, ILanguageStore
        {
            ArgumentNullException.ThrowIfNull(builder);
            MultiLingualBuilderGuard.EnsureProviderNotConfigured(builder, "Custom");
            builder.Services.AddScoped<ITranslationStore, TTranslationStore>();
            builder.Services.AddScoped<ILanguageStore, TLanguageStore>();
            return builder;
        }

        /// <summary>Configures custom stores created by factories — e.g. to share one instance.</summary>
        public static MultiLingualBuilder UseCustomStores(
            this MultiLingualBuilder builder,
            Func<IServiceProvider, ITranslationStore> translationStore,
            Func<IServiceProvider, ILanguageStore> languageStore)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(translationStore);
            ArgumentNullException.ThrowIfNull(languageStore);
            MultiLingualBuilderGuard.EnsureProviderNotConfigured(builder, "Custom");
            builder.Services.AddScoped(translationStore);
            builder.Services.AddScoped(languageStore);
            return builder;
        }
    }
}
