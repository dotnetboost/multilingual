using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.MultiLingual.EntityFrameworkCore;

/// <summary>
/// Collects every <c>[MultiLingual]</c> entity EF materializes during the scope, so the
/// hydrator can fill them all in — including ones reached through <c>Include</c> — with one
/// query per entity type.
///
/// <para><b>Edited entities are left alone.</b> Each entity's translatable values are
/// remembered as they were materialized. One that no longer matches by the time it is drained
/// was assigned by the caller — typically an entity loaded for update, which a write endpoint
/// never hydrates, left queued until some later read in the same scope drains the registry.
/// Hydrating it would overwrite the caller's edit with the stored value, and the save that
/// follows would then see nothing to write.</para>
/// </summary>
/// <param name="logger">Reports the registry filling up.</param>
public sealed partial class MaterializedEntityRegistry(ILogger<MaterializedEntityRegistry> logger)
{
    private readonly List<Pending> _pending = [];
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    private bool _overflowed;

    /// <summary>
    /// Safety valve. If a caller streams a huge result set and never hydrates, stop growing
    /// rather than pinning every row for the life of the scope. Going past it is harmless until
    /// something tries to hydrate: <see cref="Drain"/> then throws, rather than hand back a
    /// partial set and leave the rest silently <c>null</c>.
    /// </summary>
    public const int Capacity = 20_000;

    /// <summary>Queues <paramref name="entity"/> for the next hydration.</summary>
    public void Register(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (_seen.Contains(entity))
            return;

        if (_pending.Count >= Capacity)
        {
            if (!_overflowed)
            {
                _overflowed = true;
                LogCapacityReached(Capacity);
            }

            return;
        }

        _seen.Add(entity);
        _pending.Add(new Pending(entity, Snapshot(entity)));
    }

    /// <summary>
    /// Takes everything queued so far that is still as it was materialized, and empties the queue.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// More than <see cref="Capacity"/> entities were materialized since the last drain, so some
    /// were never recorded. The queue is emptied before throwing, so the scope can carry on.
    /// </exception>
    public IReadOnlyList<object> Drain()
    {
        var overflowed = _overflowed;
        var pending    = _pending.ToArray();

        _pending.Clear();
        _seen.Clear();
        _overflowed = false;

        if (overflowed)
        {
            throw new InvalidOperationException(
                $"More than {Capacity} [MultiLingual] entities were materialized since the last hydration, " +
                "so not all of them can be hydrated. Hydrate in smaller pages, or pass the entities to " +
                "ITranslationManager.HydrateAsync directly — it has no limit.");
        }

        if (pending.Length == 0)
            return [];

        var drained = new List<object>(pending.Length);
        foreach (var item in pending)
        {
            if (IsUntouched(item))
                drained.Add(item.Entity);
        }

        return drained;
    }

    /// <summary>Number of entities waiting for hydration.</summary>
    public int PendingCount => _pending.Count;

    private static string?[] Snapshot(object entity)
    {
        var properties = MultiLingualMetadata.For(entity.GetType());
        var values = new string?[properties.Count];

        for (var i = 0; i < values.Length; i++)
            values[i] = properties[i].GetValue(entity);

        return values;
    }

    private static bool IsUntouched(Pending item)
    {
        var properties = MultiLingualMetadata.For(item.Entity.GetType());

        for (var i = 0; i < properties.Count; i++)
        {
            if (!string.Equals(properties[i].GetValue(item.Entity), item.Materialized[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning,
        Message = "More than {Capacity} [MultiLingual] entities were materialized in one scope without being " +
                  "hydrated. Further entities are not recorded, and the next hydration will fail.")]
    private partial void LogCapacityReached(int capacity);

    private sealed record Pending(object Entity, string?[] Materialized);
}
