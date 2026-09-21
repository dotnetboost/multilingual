using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Metadata;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>What <c>Translation.EntityName</c> holds for a type, and the clashes <c>Build()</c> refuses.</summary>
public sealed class EntityNameTests
{
    // Private for the same reason as in MetadataTests: Build() validates every PUBLIC decorated
    // type it can see, and the clashing ones below would fail it for every other test.

    private sealed class Unnamed
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }

    [MultiLingual(EntityName = "catalog-item")]
    private sealed class NamedAtClassLevel
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [MultiLingualEntity("article")]
    private sealed class NamedForPropertyLevel
    {
        public int Id { get; set; }
        [MultiLingual] public string? Title { get; set; }
    }

    [MultiLingualEntity("vehicle")]
    private class Vehicle
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }

    private sealed class Car : Vehicle;

    private sealed class Truck : Vehicle;

    [MultiLingualEntity("twice")]
    [MultiLingual(EntityName = "twice-again")]
    private sealed class NamedTwice
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [MultiLingualEntity("  ")]
    private sealed class BlankName
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }

    [MultiLingualEntity(" padded")]
    private sealed class PaddedName
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }

    private sealed class NamedOnAProperty
    {
        public int Id { get; set; }
        [MultiLingual(EntityName = "nope")] public string? Name { get; set; }
    }

    [MultiLingualEntity("Shared")]
    private sealed class FirstShared
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }

    [MultiLingualEntity("shared")]
    private sealed class SecondShared
    {
        public int Id { get; set; }
        [MultiLingual] public string? Name { get; set; }
    }

    private static class Sales
    {
        public sealed class Item
        {
            public int Id { get; set; }
            [MultiLingual] public string? Name { get; set; }
        }
    }

    private static class Library
    {
        public sealed class Item
        {
            public int Id { get; set; }
            [MultiLingual] public string? Name { get; set; }
        }

        [MultiLingualEntity("library-item")]
        public sealed class RenamedItem
        {
            public int Id { get; set; }
            [MultiLingual] public string? Name { get; set; }
        }
    }

    // ================================================================== resolution

    [Fact]
    public void WithoutAName_IsTheClassName()
    {
        Assert.Equal(nameof(Unnamed), MultiLingualMetadata.EntityNameFor(typeof(Unnamed)));
    }

    [Fact]
    public void ClassLevelAttribute_FixesTheName()
    {
        Assert.Equal("catalog-item", MultiLingualMetadata.EntityNameFor(typeof(NamedAtClassLevel)));
        Assert.Equal(["Name"], MultiLingualMetadata.For(typeof(NamedAtClassLevel)).Select(p => p.Name));
    }

    [Fact]
    public void EntityAttribute_FixesTheName_ForPropertyLevelDeclarations()
    {
        Assert.Equal("article", MultiLingualMetadata.EntityNameFor(typeof(NamedForPropertyLevel)));
    }

    [Fact]
    public void DerivedTypes_InheritTheirBasesName()
    {
        Assert.Equal("vehicle", MultiLingualMetadata.EntityNameFor(typeof(Car)));
        Assert.Equal("vehicle", MultiLingualMetadata.EntityNameFor(typeof(Truck)));
    }

    [Theory]
    [InlineData(typeof(NamedTwice), "Keep one")]
    [InlineData(typeof(BlankName), "non-blank")]
    [InlineData(typeof(PaddedName), "leading")]
    public void InvalidName_Throws(Type type, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MultiLingualMetadata.EntityNameFor(type));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NameOnAProperty_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MultiLingualMetadata.For(typeof(NamedOnAProperty)));
        Assert.Contains("[MultiLingualEntity", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================== clashes

    [Fact]
    public void Validator_RejectsSameShortName_InDifferentNamespaces()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MultiLingualModelValidator.Validate([typeof(Sales.Item), typeof(Library.Item)]));

        Assert.Contains("'Item'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(Sales.Item).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(Library.Item).FullName!, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_RejectsNamesDifferingOnlyInCase()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MultiLingualModelValidator.Validate([typeof(FirstShared), typeof(SecondShared)]));

        Assert.Contains(nameof(FirstShared), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SecondShared), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_AcceptsAFixedName_ThatResolvesTheClash()
    {
        MultiLingualModelValidator.Validate([typeof(Sales.Item), typeof(Library.RenamedItem)]);
    }

    [Fact]
    public void Validator_AcceptsAHierarchy_SharingItsBasesName()
    {
        MultiLingualModelValidator.Validate([typeof(Vehicle), typeof(Car), typeof(Truck)]);
    }

    [Fact]
    public void Validator_AcceptsSiblings_EvenWhenTheBaseIsNotListed()
    {
        MultiLingualModelValidator.Validate([typeof(Car), typeof(Truck)]);
    }

    [Fact]
    public void Validator_RejectsAnInvalidName_AlongsideOtherErrors()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MultiLingualModelValidator.Validate([typeof(NamedTwice), typeof(NamedOnAProperty)]));

        Assert.Contains(nameof(NamedTwice), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NamedOnAProperty), ex.Message, StringComparison.Ordinal);
    }
}
