using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.TestSupport;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>
/// DotNetBoost Entity Addressing — the spec this library and <c>DotNetBoost.Media</c> both
/// implement so that rows filed by one can be joined with rows filed by the other. These are the
/// tests a consumer would otherwise have to write by hand, which is why the data and the
/// assertions live in <c>TestSupport</c> and this file only runs them.
/// </summary>
public sealed class EntityAddressingTests
{
    [Theory]
    [MemberData(nameof(EntityAddressing.KeyCases), MemberType = typeof(EntityAddressing))]
    public void EveryKeyForm_IsStoredAsTheSpecSaysItMustBe(object keyValue, string expected) =>
        EntityAddressing.AssertKey(keyValue, expected);

    [Fact]
    public void TheSpecsOwnInvariants_StillHold() => EntityAddressing.AssertSpecIsIntact();

    [Fact]
    public void ThePublishedVersion_IsWhatTestSupportRunsTheSuiteFor() =>
        Assert.Equal(EntityKey.AddressingVersion, EntityAddressing.Version);

    /// <summary>
    /// The cross-library assertion as a consumer writes it. Standing in for Media's constants with
    /// this library's own values is the point: the test proves the comparison catches a
    /// disagreement, and a real consumer passes <c>MediaKey</c>'s.
    /// </summary>
    [Fact]
    public void ALibraryPublishingTheSameConstants_Agrees()
    {
        EntityAddressing.AssertAgreesWith(
            "DotNetBoost.Media",
            EntityKey.AddressingVersion,
            EntityKey.MaxLength,
            MultiLingualMetadata.MaxEntityNameLength);
    }

    [Theory]
    [InlineData("0.9", 64, 128, "spec version")]
    [InlineData("1.0", 32, 128, "entity id length")]
    [InlineData("1.0", 64, 64, "entity name length")]
    public void ALibraryThatDrifted_FailsLoudly_NamingTheDifference(
        string version, int keyMaxLength, int nameMaxLength, string expectedInMessage)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EntityAddressing.AssertAgreesWith("DotNetBoost.Media", version, keyMaxLength, nameMaxLength));

        Assert.Contains(expectedInMessage, error.Message, StringComparison.Ordinal);
        Assert.Contains("DotNetBoost.Media", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyFormattedSomeOtherWay_FailsNamingBothForms()
    {
        var error = Assert.Throws<InvalidOperationException>(() => EntityAddressing.AssertKey(42, "forty-two"));

        Assert.Contains("'forty-two'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'42'", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the name half of the spec

    [Fact]
    public void AnEntityName_IsTheFixedNameOrTheTypeName_NotTheFullName()
    {
        Assert.Equal("Product", MultiLingualMetadata.EntityNameFor(typeof(Product)));
        Assert.Equal("Branding", MultiLingualMetadata.EntityNameFor(typeof(AddressedByAFixedName)));
    }

    [Fact]
    public void AProxy_IsAddressedAsTheClassItStandsFor()
    {
        // Both halves of the spec say "proxy-unwrapped": a lazy-loading proxy must not file rows
        // under a name nothing else can find.
        Assert.Equal(
            MultiLingualMetadata.EntityNameFor(typeof(Product)),
            MultiLingualMetadata.EntityNameFor(RuntimeProxy.TypeOf<Product>()));
    }
}

[MultiLingualEntity("Branding")]
public sealed class AddressedByAFixedName
{
    public int Id { get; set; }

    [MultiLingual] public string? Tagline { get; set; }
}
