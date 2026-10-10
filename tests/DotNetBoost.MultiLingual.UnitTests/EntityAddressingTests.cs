using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Services;
using System.Globalization;
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

    /// <summary>
    /// The behavioural half of the agreement, which the constants above cannot speak for. Passing
    /// this library's own implementations proves the checks run; a real consumer passes Media's
    /// <c>EntityKey.IsUnset</c> and its convention accessor.
    /// </summary>
    [Fact]
    public void ALibraryImplementingTheSameRules_Agrees()
    {
        EntityAddressing.AssertAgreesWith(
            "DotNetBoost.Media",
            EntityKey.AddressingVersion,
            EntityKey.MaxLength,
            MultiLingualMetadata.MaxEntityNameLength,
            isKeyUnset: EntityKey.IsUnset,
            discoverKey: entity => new ConventionEntityKeyAccessor().GetKey(entity));
    }

    /// <summary>
    /// The exact divergence this spec row was added for: Media's unset test omitted <c>byte</c>, so
    /// a <c>byte</c> key of 0 addressed rows in one library and nothing in the other, while both
    /// still published v1.
    /// </summary>
    [Fact]
    public void ALibraryThatMissesAnUnsetKeyType_FailsNamingIt()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            EntityAddressing.AssertUnsetDetection(
                value => value switch
                {
                    null     => true,
                    int i    => i == 0,
                    long l   => l == 0,
                    short sh => sh == 0,
                    Guid g   => g == Guid.Empty,
                    string t => t.Length == 0,
                    _        => false       // byte omitted, exactly as it was
                },
                "DotNetBoost.Media"));

        Assert.Contains("Byte", error.Message, StringComparison.Ordinal);
        Assert.Contains("DotNetBoost.Media", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryThatCallsAnAssignedKeyUnset_FailsLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EntityAddressing.AssertUnsetDetection(_ => true, "DotNetBoost.Media"));

        Assert.Contains("silently store nothing", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second divergence: resolving a composite key instead of refusing it keys the entity on
    /// whichever property reflection happened to return first.
    /// </summary>
    [Fact]
    public void ALibraryThatResolvesACompositeKey_FailsLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EntityAddressing.AssertKeyDiscovery(Resolve, "DotNetBoost.Media"));

        Assert.Contains("composite key must be refused", error.Message, StringComparison.Ordinal);

        // Correct on every rule except the one being tested, so the failure is attributable.
        static string? Resolve(object entity) => entity switch
        {
            EntityAddressing.CompositeKey   => "1",
            EntityAddressing.AnnotatedKey a => a.Code.ToString(CultureInfo.InvariantCulture),
            EntityAddressing.AnnotatedAndId a => a.Code.ToString(CultureInfo.InvariantCulture),
            EntityAddressing.SuffixId t      => t.SuffixIdId.ToString(CultureInfo.InvariantCulture),
            EntityAddressing.WriteOnlyId w   => w.WriteOnlyIdId.ToString(CultureInfo.InvariantCulture),
            EntityAddressing.PlainId { Id: 0 } => null,
            EntityAddressing.PlainId p       => p.Id.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    [Fact]
    public void ALibraryPreferringIdOverTheKeyAttribute_FailsNamingTheRule()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EntityAddressing.AssertKeyDiscovery(Resolve, "DotNetBoost.Media"));

        Assert.Contains("[Key] in preference to Id", error.Message, StringComparison.Ordinal);

        static string? Resolve(object entity) => entity switch
        {
            EntityAddressing.AnnotatedAndId a => a.Id.ToString(CultureInfo.InvariantCulture),   // wrong rule
            EntityAddressing.AnnotatedKey a   => a.Code.ToString(CultureInfo.InvariantCulture),
            EntityAddressing.SuffixId t       => t.SuffixIdId.ToString(CultureInfo.InvariantCulture),
            EntityAddressing.WriteOnlyId w    => w.WriteOnlyIdId.ToString(CultureInfo.InvariantCulture),
            EntityAddressing.PlainId { Id: 0 } => null,
            EntityAddressing.PlainId p        => p.Id.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    /// <summary>
    /// A whitespace key is not "unset" — it has a length — so it used to format as a key and file
    /// rows under it, while DotNetBoost.Media refused the same value. Rows written that way could
    /// never be joined across the two libraries.
    /// </summary>
    [Fact]
    public void AWhitespaceKey_IsRefused_NotFiledUnderBlanks()
    {
        Assert.Throws<InvalidOperationException>(() => EntityKey.ToKeyString("   "));
        Assert.Throws<InvalidOperationException>(() => EntityKey.ToKeyString(string.Empty));

        // And not by accident of being read as unset: it is refused, which is a different answer.
        Assert.False(EntityKey.IsUnset("   "));
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
