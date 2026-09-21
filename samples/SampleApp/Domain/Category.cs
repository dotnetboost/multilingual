using DotNetBoost.MultiLingual.Core.Attributes;

namespace SampleApp.Domain;

/// <summary>
/// Demonstrates CLASS-LEVEL <c>[MultiLingual]</c>. Every string property with a public
/// get/set becomes translatable with no per-property annotation:
/// <see cref="Name"/> and <see cref="Description"/> are translated;
/// <see cref="Id"/> (key) and <see cref="Slug"/> (opted out) are not, and
/// <see cref="DisplayOrder"/> is skipped because it is not a string.
/// </summary>
[MultiLingual]
public class Category
{
    public int Id { get; set; }

    /// <summary>Translatable via the class-level attribute.</summary>
    public string? Name { get; set; }

    /// <summary>Translatable via the class-level attribute.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// A string property explicitly opted OUT of the class-level attribute, so it stays an
    /// ordinary column. Without <c>[NotMultiLingual]</c> this would be translated too.
    /// </summary>
    [NotMultiLingual]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Not a string, so never translatable even under class-level application.</summary>
    public int DisplayOrder { get; set; }

    public ICollection<Product> Products { get; set; } = new List<Product>();
}
