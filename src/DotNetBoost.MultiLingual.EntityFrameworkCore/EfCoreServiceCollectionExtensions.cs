using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the EF Core storage provider.</summary>
    public static class EntityFrameworkBuilderExtensions
    {
        /// <summary>
        /// Configures Entity Framework Core as the provider. <typeparamref name="TContext"/> holds
        /// the translations and languages; attach the interceptors to every context that saves
        /// <c>[MultiLingual]</c> entities with <see cref="MultiLingualDbContextOptionsExtensions.UseMultiLingual"/>.
        /// <para>
        /// The context needs nothing but <c>ApplyMultiLingual()</c> in <c>OnModelCreating</c>: the
        /// provider reaches the two tables through <c>context.Set&lt;T&gt;()</c>, so declaring
        /// <c>DbSet&lt;Translation&gt;</c> and <c>DbSet&lt;Language&gt;</c> properties is optional.
        /// </para>
        /// </summary>
        public static MultiLingualBuilder UseEntityFrameworkCore<TContext>(this MultiLingualBuilder builder)
            where TContext : DbContext
        {
            ArgumentNullException.ThrowIfNull(builder);
            MultiLingualBuilderGuard.EnsureProviderNotConfigured(builder, "EntityFrameworkCore");

            var services = builder.Services;

            services.AddScoped<ITranslationStore>(sp => new EfCoreTranslationStore(sp.GetRequiredService<TContext>()));
            services.AddScoped<ILanguageStore>(sp => new EfCoreLanguageStore(sp.GetRequiredService<TContext>()));
            services.Replace(ServiceDescriptor.Scoped<IEntityKeyAccessor>(
                sp => new EfCoreEntityKeyAccessor(sp.GetRequiredService<TContext>())));

            services.TryAddScoped<MultiLingualSaveChangesInterceptor>();

            return builder;
        }
    }

    /// <summary>Attaches the MultiLingual interceptors to a context.</summary>
    public static class MultiLingualDbContextOptionsExtensions
    {
        /// <summary>
        /// Adds the save, materialization, query and command interceptors, which translate every
        /// query on the context and read and write translations for the scope's language. Use the
        /// <c>(sp, options)</c> overload of
        /// <c>AddDbContext</c>:
        /// <code>
        /// services.AddDbContext&lt;AppDbContext&gt;((sp, o) =&gt; o.UseSqlite(cs).UseMultiLingual(sp));
        /// </code>
        /// Not compatible with <c>AddDbContextPool</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The provider has not been registered.</exception>
        public static DbContextOptionsBuilder UseMultiLingual(this DbContextOptionsBuilder options, IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(serviceProvider);

            var save = serviceProvider.GetService<MultiLingualSaveChangesInterceptor>()
                ?? throw new InvalidOperationException(
                    "The MultiLingual EF Core provider is not registered. Call " +
                    "services.AddMultiLingual().UseEntityFrameworkCore<TContext>().Build() first.");

            return options.AddInterceptors(
                save,
                MultiLingualMaterializationInterceptor.Instance,
                MultiLingualQueryInterceptor.Instance,
                LanguageTokenInterceptor.Instance);
        }
    }
}
