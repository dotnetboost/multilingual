using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Localization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for JSON-backed <see cref="IStringLocalizer"/>.</summary>
    public static class JsonLocalizationBuilderExtensions
    {
        /// <summary>
        /// Serves <see cref="IStringLocalizer{T}"/> from JSON files — one per language, named after
        /// its code — in the same language as translated content:
        /// <code>
        /// services.AddMultiLingual()
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .AddJsonLocalization()          // Resources/en.json, Resources/ar.json, …
        ///         .Build();
        /// </code>
        /// Nested objects become dotted keys (<c>localizer["Product.Created"]</c>), and a message
        /// missing in the requested language falls back exactly as content does. Replaces any
        /// <see cref="IStringLocalizerFactory"/> registered before or after it, including the
        /// <c>.resx</c> one from <c>AddLocalization()</c>.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">Changes the folder, the file provider, or reloading.</param>
        public static MultiLingualBuilder AddJsonLocalization(
            this MultiLingualBuilder builder,
            Action<JsonLocalizationOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            var options = new JsonLocalizationOptions();
            configure?.Invoke(options);

            var services = builder.Services;
            services.Replace(ServiceDescriptor.Singleton(options));
            services.Replace(ServiceDescriptor.Singleton(sp => CreateStore(sp, options)));
            services.TryAddSingleton<LanguageChain>();

            // Replace rather than TryAdd, so this wins whichever order it and AddLocalization()
            // are called in: AddLocalization() only ever TryAdds.
            services.Replace(ServiceDescriptor.Singleton<IStringLocalizerFactory, JsonStringLocalizerFactory>());
            services.Replace(ServiceDescriptor.Transient(typeof(IStringLocalizer<>), typeof(JsonStringLocalizer<>)));

            if (options.ValidateFilesAtStartup)
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JsonLocalizationValidationHostedService>());

            return builder;
        }

        private static JsonResourceStore CreateStore(IServiceProvider sp, JsonLocalizationOptions options)
        {
            var logger = sp.GetService<ILogger<JsonResourceStore>>() ?? NullLogger<JsonResourceStore>.Instance;

            if (options.FileProvider is { } configured)
                return new JsonResourceStore(configured, options, logger);

            if (sp.GetService<IHostEnvironment>()?.ContentRootFileProvider is { } contentRoot)
                return new JsonResourceStore(contentRoot, options, logger);

            return new JsonResourceStore(new PhysicalFileProvider(AppContext.BaseDirectory), options, logger, ownsProvider: true);
        }
    }
}

namespace DotNetBoost.MultiLingual.Localization
{
    /// <summary>Parses every message file at startup, so a malformed one fails the start.</summary>
    internal sealed class JsonLocalizationValidationHostedService(JsonResourceStore store) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            store.LoadAll();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
