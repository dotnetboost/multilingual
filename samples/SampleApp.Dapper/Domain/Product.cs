using DotNetBoost.MultiLingual.Core.Attributes;

namespace SampleApp.Dapper.Domain;

/// <summary>
/// The EF Core sample's <c>Product</c>, as a plain object Dapper maps. Identical in every way that
/// matters: PROPERTY-LEVEL <c>[MultiLingual]</c> on <see cref="Name"/> and
/// <see cref="Description"/>, <see cref="Sku"/> and <see cref="Price"/> ordinary columns. The
/// attribute is provider-neutral — nothing here knows which provider will store it.
/// <para>
/// One difference from the EF Core version, and it is about Dapper rather than about this library:
/// there is no <c>Category</c> navigation. Dapper does not track or lazy-load, so a related entity
/// is something a query asks for explicitly — see <c>ProductsController.GetSummaries</c>.
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
    /// Translatable. In base-language mode this column holds the base language's text and the other
    /// languages are rows in <c>Translations</c>. Without a base language there is no column for it
    /// at all, and the SELECT below simply never mentions it — with Dapper, "unmapped" is just a
    /// column your SQL does not name.
    /// </summary>
    [MultiLingual]
    public string? Name { get; set; }

    /// <summary>Translatable.</summary>
    [MultiLingual]
    public string? Description { get; set; }

    public int? CategoryId { get; set; }
}
