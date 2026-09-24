using DotNetBoost.MultiLingual.Core.Attributes;

namespace SampleApp.MongoDb.Domain;

/// <summary>
/// The EF Core sample's <c>Product</c>, as a MongoDB document. Identical in every way that matters:
/// PROPERTY-LEVEL <c>[MultiLingual]</c> on <see cref="Name"/> and <see cref="Description"/>,
/// <see cref="Sku"/> and <see cref="Price"/> ordinary fields. The attribute is provider-neutral —
/// nothing here knows which provider will store it.
/// <para>
/// Two differences from the EF Core version, both about documents rather than about this library.
/// There is no <c>Category</c> navigation: a document has no lazy loading, and a mapped navigation
/// would serialize the whole category into every product. And the key is an <c>int</c> rather than
/// an <c>ObjectId</c>, so the dashboard and the response DTOs are unchanged — MongoDB does not
/// generate integers, so <c>Data/IdSequence</c> does.
/// </para>
/// </summary>
public sealed class Product
{
    public int Id { get; set; }

    /// <summary>Not translatable — a SKU is the same in every language.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>Not translatable.</summary>
    public decimal Price { get; set; }

    /// <summary>
    /// Translatable. In base-language mode this field holds the base language's text and the other
    /// languages are documents in the translations collection; without one it is unmapped from the
    /// class map entirely and the document carries no such field at all.
    /// </summary>
    [MultiLingual]
    public string? Name { get; set; }

    /// <summary>Translatable.</summary>
    [MultiLingual]
    public string? Description { get; set; }

    public int? CategoryId { get; set; }
}
