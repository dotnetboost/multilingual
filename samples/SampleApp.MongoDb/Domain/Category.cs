using DotNetBoost.MultiLingual.Core.Attributes;

namespace SampleApp.MongoDb.Domain;

/// <summary>
/// The EF Core sample's <c>Category</c>, as a MongoDB document, and the same demonstration:
/// CLASS-LEVEL <c>[MultiLingual]</c>, so every string property with a public get/set is
/// translatable unless it opts out. <see cref="Name"/> and <see cref="Description"/> are
/// translated; <see cref="Slug"/> opts out; <see cref="DisplayOrder"/> is not a string.
/// <para>
/// No <c>Products</c> collection, unlike the EF Core version: mapped, it would serialize every
/// product into the category's own document.
/// </para>
/// </summary>
[MultiLingual]
public sealed class Category
{
    public int Id { get; set; }

    /// <summary>Translatable via the class-level attribute.</summary>
    public string? Name { get; set; }

    /// <summary>Translatable via the class-level attribute.</summary>
    public string? Description { get; set; }

    /// <summary>Explicitly opted OUT, so it stays an ordinary field.</summary>
    [NotMultiLingual]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Not a string, so never translatable.</summary>
    public int DisplayOrder { get; set; }
}
