using DotNetBoost.MultiLingual.Core.Attributes;

namespace SampleApp.Domain;

/// <summary>
/// Demonstrates PROPERTY-LEVEL <c>[MultiLingual]</c>.
/// <para>
/// <see cref="Sku"/> and <see cref="Price"/> are ordinary mapped columns on the
/// <c>Products</c> table. <see cref="Name"/> and <see cref="Description"/> are translatable,
/// and where they live depends on configuration: this sample sets <c>BaseLanguage = "en"</c>,
/// so they keep a column holding the English text while Arabic and French are rows in
/// <c>Translations</c>. Without a base language they would have no column at all.
/// </para>
/// </summary>
public class Product
{
    public int Id { get; set; }

    /// <summary>Not translatable — a SKU is the same in every language.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>Not translatable.</summary>
    public decimal Price { get; set; }

    /// <summary>
    /// Translatable. Nullable because a value missing in every language of the fallback chain
    /// resolves to <c>null</c>.
    /// </summary>
    [MultiLingual]
    public string? Name { get; set; }

    /// <summary>Translatable.</summary>
    [MultiLingual]
    public string? Description { get; set; }

    public int? CategoryId { get; set; }

    public Category? Category { get; set; }
}
