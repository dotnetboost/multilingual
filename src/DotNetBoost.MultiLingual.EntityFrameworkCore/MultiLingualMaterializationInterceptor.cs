using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// The read path, first half. Translatable properties have no column, so a freshly
/// materialized entity has them all <c>null</c>. This interceptor does NOT query for their
/// values inline — a query per row from inside materialization would both re-enter the
/// context mid-query and be a textbook N+1. It only records the instance;
/// <see cref="ITranslationHydrator"/> resolves them all in one round trip afterwards, and it
/// tells an active <see cref="LocalizedRead"/> which context that happened on.
///
/// <para><b>One shared instance.</b> EF Core treats materialization interceptors as
/// singleton interceptors: the instance is part of the key EF caches its internal service
/// provider under, so a new instance per request would build a new internal provider per
/// request (and fail after twenty). This one is stateless and shared; it finds the request's
/// <see cref="MaterializedEntityRegistry"/> through the context's application service provider
/// — the scoped provider <c>AddDbContext</c> builds the options from.</para>
/// </summary>
internal sealed class MultiLingualMaterializationInterceptor : IMaterializationInterceptor
{
    /// <summary>The shared instance <see cref="MultiLingualDbContextOptionsExtensions.UseMultiLingual"/> attaches.</summary>
    public static MultiLingualMaterializationInterceptor Instance { get; } = new();

    // A context belongs to exactly one scope for its whole life, so its registry is looked up once.
    private readonly ConditionalWeakTable<DbContext, MaterializedEntityRegistry> _registries = new();

    private MultiLingualMaterializationInterceptor() { }

    /// <inheritdoc/>
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (MultiLingualMetadata.IsMultiLingual(entity.GetType()))
        {
            _registries.GetValue(materializationData.Context, ResolveRegistry).Register(entity);
            LocalizedRead.Record(materializationData.Context);
        }

        return entity;
    }

    private static MaterializedEntityRegistry ResolveRegistry(DbContext context)
    {
        var applicationServices = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;

        return applicationServices?.GetService<MaterializedEntityRegistry>()
               ?? throw new InvalidOperationException(
                   $"'{context.GetType().Name}' materialized a [MultiLingual] entity but has no scoped " +
                   "MultiLingual services. Register the context with AddDbContext((sp, options) => " +
                   "options.UseMultiLingual(sp)) and a scoped options lifetime (the default).");
    }
}
