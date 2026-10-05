using DotNetBoost.MultiLingual.Core.Metadata;
using System.Globalization;

namespace DotNetBoost.MultiLingual.TestSupport;

/// <summary>
/// The conformance suite for <b>DotNetBoost Entity Addressing</b> — the spec in the README that
/// this library and <c>DotNetBoost.Media</c> both implement so that rows filed by one can be
/// joined with rows filed by the other.
///
/// <para>
/// Two libraries agreeing by convention is what this replaces. An application that stores
/// translations for a media library's files depends on both of them formatting
/// <c>(entityName, entityId)</c> identically, and nothing in either assembly says so. Rather than
/// write its own theory table comparing key formats, a consumer runs this one:
/// </para>
///
/// <code>
/// using MediaKey   = DotNetBoost.Media.Core.Metadata.EntityKey;
/// using MediaNames = DotNetBoost.Media.Core.Metadata.MediaEntityNames;
///
/// [Theory]
/// [MemberData(nameof(EntityAddressing.KeyCases), MemberType = typeof(EntityAddressing))]
/// public void KeysAreFormattedToTheSpec(object key, string expected) =&gt;
///     EntityAddressing.AssertKey(key, expected);
///
/// [Fact]
/// public void MediaAndMultiLingualAddressSubjectsTheSameWay() =&gt;
///     EntityAddressing.AssertAgreesWith(
///         "DotNetBoost.Media",
///         MediaKey.AddressingVersion,
///         MediaKey.MaxLength,
///         MediaNames.MaxLength);
/// </code>
///
/// <para>
/// Everything here throws <see cref="InvalidOperationException"/> rather than using an assertion
/// library, so it stays usable from any test framework — and from a startup check, if an
/// application would rather fail to boot than discover the disagreement in its data.
/// </para>
/// </summary>
public static class EntityAddressing
{
    /// <summary>The spec version this library implements.</summary>
    public static string Version => EntityKey.AddressingVersion;

    /// <summary>The longest entity id the spec allows (64).</summary>
    public static int KeyMaxLength => EntityKey.MaxLength;

    /// <summary>The longest entity name the spec allows (128).</summary>
    public static int NameMaxLength => MultiLingualMetadata.MaxEntityNameLength;

    private static readonly Guid SampleGuid = new("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    /// <summary>
    /// Every key form the spec pins down, as <c>[key value, the string it must be stored as]</c> —
    /// an xunit <c>[MemberData]</c> source, and plain data for anything else.
    /// <para>
    /// A Guid is formatted <c>"D"</c> (lower case, hyphenated, no braces) and numbers invariantly,
    /// which is why an id may be handed in as either its own type or its string form and reach the
    /// same rows.
    /// </para>
    /// </summary>
    public static IEnumerable<object[]> KeyCases =>
    [
        [SampleGuid, "3f2504e0-4f89-11d3-9a0c-0305e82c3301"],
        [42, "42"],
        [-1, "-1"],
        [0, "0"],
        [long.MaxValue, "9223372036854775807"],
        [(short)7, "7"],
        [(byte)7, "7"],
        ["already-a-string", "already-a-string"],
        ["Mixed-Case-Kept", "Mixed-Case-Kept"]
    ];

    /// <summary>
    /// Checks that <paramref name="keyValue"/> is stored as <paramref name="expected"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key is formatted some other way.</exception>
    public static void AssertKey(object keyValue, string expected)
    {
        ArgumentNullException.ThrowIfNull(keyValue);

        var actual = EntityKey.ToKeyString(keyValue);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Entity Addressing v{Version}: a {keyValue.GetType().Name} key must be stored as " +
                $"'{expected}', but this implementation stores it as '{actual}'.");
        }
    }

    /// <summary>Runs <see cref="AssertKey"/> over every case in <see cref="KeyCases"/>.</summary>
    /// <exception cref="InvalidOperationException">Any case is formatted some other way.</exception>
    public static void AssertEveryKeyCase()
    {
        foreach (var @case in KeyCases)
            AssertKey(@case[0], (string)@case[1]);
    }

    /// <summary>
    /// Checks that another library addressing the same subjects implements the same spec version
    /// and the same limits. Everything a cross-library join depends on, in one call.
    /// </summary>
    /// <param name="libraryName">The other library, named in the failure message.</param>
    /// <param name="addressingVersion">Its own <c>AddressingVersion</c> constant.</param>
    /// <param name="keyMaxLength">Its own entity-id length limit.</param>
    /// <param name="nameMaxLength">Its own entity-name length limit.</param>
    /// <exception cref="InvalidOperationException">They disagree; the message lists every difference.</exception>
    public static void AssertAgreesWith(string libraryName, string addressingVersion, int keyMaxLength, int nameMaxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);

        var differences = new List<string>();

        if (!string.Equals(addressingVersion, Version, StringComparison.Ordinal))
        {
            differences.Add(
                $"spec version: {libraryName} implements v{addressingVersion}, this library v{Version}");
        }

        if (keyMaxLength != KeyMaxLength)
            differences.Add($"entity id length: {libraryName} allows {keyMaxLength}, this library {KeyMaxLength}");

        if (nameMaxLength != NameMaxLength)
            differences.Add($"entity name length: {libraryName} allows {nameMaxLength}, this library {NameMaxLength}");

        if (differences.Count == 0)
            return;

        throw new InvalidOperationException(
            $"{libraryName} and DotNetBoost.MultiLingual do not address subjects the same way, so rows " +
            $"filed by one cannot be joined with rows filed by the other: {string.Join("; ", differences)}. " +
            "Upgrade whichever is behind.");
    }

    /// <summary>
    /// Checks the spec's own invariants, independently of any other library: the limits are what
    /// the spec publishes, and every key form still round-trips.
    /// </summary>
    /// <exception cref="InvalidOperationException">An invariant no longer holds.</exception>
    public static void AssertSpecIsIntact()
    {
        if (KeyMaxLength != 64 || NameMaxLength != 128)
        {
            throw new InvalidOperationException(
                $"Entity Addressing v{Version} fixes the entity id at 64 characters and the entity name " +
                $"at 128; this implementation publishes {KeyMaxLength} and {NameMaxLength}. Changing a " +
                $"limit is a spec change: bump {nameof(EntityKey)}.{nameof(EntityKey.AddressingVersion)} " +
                "and the same constant in every library that shares subject addressing.");
        }

        // A Guid has to fit, with room to spare, or the whole single-table design fails for
        // Guid-keyed entities.
        if (SampleGuid.ToString("D", CultureInfo.InvariantCulture).Length > KeyMaxLength)
            throw new InvalidOperationException($"Entity Addressing v{Version}: a Guid key no longer fits in {KeyMaxLength} characters.");

        AssertEveryKeyCase();
    }
}
