namespace DotNetBoost.MultiLingual.Core.Interfaces;

/// <summary>
/// Reads a <c>[MultiLingual]</c> entity's base-language columns as the database holds them, for
/// <c>GetAllAsync(includeBase: true)</c>. A provider that can — the EF Core one — registers it;
/// without it the engine answers from what it remembers of the entity.
/// </summary>
internal interface IBaseColumnReader
{
    /// <summary>
    /// propertyName → stored value for every translatable property of <paramref name="entity"/>,
    /// or <c>null</c> when this reader cannot tell: the type is not one it maps, or no row has the
    /// entity's key.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>?> ReadAsync(object entity, CancellationToken ct = default);
}
