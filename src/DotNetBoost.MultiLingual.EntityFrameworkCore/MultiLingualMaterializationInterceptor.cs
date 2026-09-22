using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Remembers which context materialized each entity that can hold translated values (or leads to
/// some), and what its translatable properties held at that moment. <see cref="EntityFill"/> —
/// called from the query's own projection, which is compiled once and shared by every request —
/// finds the request's scope through it, and knows which properties the caller has edited since.
///
/// <para><b>One shared instance.</b> EF Core treats materialization interceptors as singleton
/// interceptors: the instance is part of the key EF caches its internal service provider under, so
/// a new instance per request would build a new internal provider per request (and fail after
/// twenty). This one is stateless and shared.</para>
/// </summary>
internal sealed class MultiLingualMaterializationInterceptor : IMaterializationInterceptor
{
    /// <summary>The shared instance <see cref="MultiLingualDbContextOptionsExtensions.UseMultiLingual"/> attaches.</summary>
    public static MultiLingualMaterializationInterceptor Instance { get; } = new();

    private MultiLingualMaterializationInterceptor() { }

    /// <inheritdoc/>
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (materializationData.EntityType is { } entityType && QueryTranslation.ReachesTranslations(entityType))
            EntityFill.Record(entity, materializationData.Context);

        return entity;
    }
}
