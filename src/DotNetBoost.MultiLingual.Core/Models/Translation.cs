namespace DotNetBoost.MultiLingual.Core.Models;

/// <summary>
/// One translated value, for one property, of one entity instance, in one language.
/// A single store of these rows backs every <c>[MultiLingual]</c> entity in the system —
/// adding translation support to a new entity adds no tables, collections or migrations.
/// <para>
/// There is no surrogate key: the natural key
/// (<see cref="EntityName"/>, <see cref="EntityId"/>, <see cref="PropertyName"/>,
/// <see cref="LanguageCode"/>) is unique by definition, maps directly onto a relational
/// composite key or a MongoDB compound index, and leads with the columns every read filters on.
/// </para>
/// <para>
/// Not sealed, on purpose: it is an entity type in the application's own context, and EF Core's
/// lazy-loading proxies refuse a model that contains a sealed entity type.
/// </para>
/// </summary>
public class Translation
{
    /// <summary>Logical name of the owning entity type, e.g. <c>Product</c>.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// The owning entity's primary key, stored as a string so the same store serves
    /// <c>int</c>, <c>long</c>, <c>Guid</c> and <c>string</c> keys without a schema change.
    /// Values are formatted invariantly by <see cref="Metadata.EntityKey.ToKeyString"/>.
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Name of the translatable property, e.g. <c>Name</c>.</summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Canonical code of the language this value is written in (<c>en</c>, <c>pt-BR</c>).
    /// A code rather than a numeric id, so every provider can store it as-is and no read or
    /// write needs a lookup to translate between the two.
    /// </summary>
    public string LanguageCode { get; set; } = string.Empty;

    /// <summary>The translated text. <c>null</c> is a stored "no value".</summary>
    public string? Value { get; set; }
}
