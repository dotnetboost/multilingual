using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Services;
using System.ComponentModel.DataAnnotations;

namespace DotNetBoost.MultiLingual.UnitTests;

public sealed class MetadataTests
{
    // Invalid declarations are private: MultiLingualBuilder.Build() validates every PUBLIC
    // decorated type it can see, and would reject these for every other test in the assembly.
    private sealed class NonStringProperty
    {
        public int Id { get; set; }
        [MultiLingual] public int Count { get; set; }
    }

    private sealed class InitOnlyProperty
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; init; }
    }

    private sealed class PrivateSetter
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; private set; }
    }

    private sealed class Annotated
    {
        [Key] public int Code { get; set; }
    }

    private sealed class Composite
    {
        [Key] public int A { get; set; }
        [Key] public int B { get; set; }
    }

    private sealed class NoKey
    {
        public string? Name { get; set; }
    }

    private sealed class Order
    {
        public long OrderId { get; set; }
    }

    [Fact]
    public void PropertyLevel_SelectsOnlyDecoratedProperties()
    {
        var names = MultiLingualMetadata.For(typeof(Product)).Select(p => p.Name);
        Assert.Equal(["Name", "Description"], names);
    }

    [Fact]
    public void ClassLevel_SelectsStrings_ExceptKeyAndOptedOut()
    {
        var names = MultiLingualMetadata.For(typeof(Category)).Select(p => p.Name);
        Assert.Equal(["Name"], names);
    }

    [Fact]
    public void Undecorated_IsNotMultiLingual()
    {
        Assert.False(MultiLingualMetadata.IsMultiLingual(typeof(Plain)));
        Assert.Empty(MultiLingualMetadata.For(typeof(Plain)));
    }

    [Fact]
    public void Accessors_ReadAndWriteThroughCompiledDelegates()
    {
        var property = MultiLingualMetadata.For(typeof(Product)).Single(p => p.Name == "Name");
        var product  = new Product();

        property.SetValue(product, "Laptop");

        Assert.Equal("Laptop", product.Name);
        Assert.Equal("Laptop", property.GetValue(product));
    }

    [Theory]
    [InlineData(typeof(NonStringProperty), "only string properties")]
    [InlineData(typeof(InitOnlyProperty), "'init' setter")]
    [InlineData(typeof(PrivateSetter), "public setter")]
    public void InvalidDeclaration_Throws(Type type, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MultiLingualMetadata.For(type));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_ReportsEveryOffender_InOneMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MultiLingualModelValidator.Validate([typeof(Product), typeof(NonStringProperty), typeof(InitOnlyProperty)]));

        Assert.Contains(nameof(NonStringProperty), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InitOnlyProperty), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_AcceptsValidTypes()
    {
        MultiLingualModelValidator.Validate([typeof(Product), typeof(Category), typeof(Plain)]);
    }

    [Theory]
    [InlineData(42, "42")]
    [InlineData(42L, "42")]
    [InlineData("abc", "abc")]
    public void EntityKey_FormatsInvariantly(object key, string expected) =>
        Assert.Equal(expected, EntityKey.ToKeyString(key));

    [Fact]
    public void EntityKey_FormatsGuidInDFormat()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", EntityKey.ToKeyString(id));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void EntityKey_DetectsUnsetKeys(object? key, bool unset) =>
        Assert.Equal(unset, EntityKey.IsUnset(key));

    [Fact]
    public void ConventionAccessor_FindsKeyByAttributeIdOrTypeNameId()
    {
        var accessor = new ConventionEntityKeyAccessor();

        Assert.Equal("5", accessor.GetKey(new Product { Id = 5 }));
        Assert.Equal("9", accessor.GetKey(new Annotated { Code = 9 }));
        Assert.Equal("12", accessor.GetKey(new Order { OrderId = 12 }));
    }

    [Fact]
    public void ConventionAccessor_ReturnsNull_ForUnassignedKey() =>
        Assert.Null(new ConventionEntityKeyAccessor().GetKey(new Product()));

    [Fact]
    public void ConventionAccessor_RejectsCompositeAndMissingKeys()
    {
        var accessor = new ConventionEntityKeyAccessor();
        Assert.Throws<NotSupportedException>(() => accessor.GetKey(new Composite()));
        Assert.Throws<InvalidOperationException>(() => accessor.GetKey(new NoKey()));
    }
}
