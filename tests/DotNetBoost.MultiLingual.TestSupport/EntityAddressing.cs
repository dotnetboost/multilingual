using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Core.Services;
using System.ComponentModel.DataAnnotations;
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
    /// <param name="isKeyUnset">
    /// Its unset-key test, if you have it. Optional only because the constants above are what a
    /// one-line assertion reaches for; pass it and the behavioural rules are checked too, which is
    /// where two libraries actually drift.
    /// </param>
    /// <param name="discoverKey">Its convention key accessor, if you have it. Optional for the same reason.</param>
    /// <exception cref="InvalidOperationException">They disagree; the message lists every difference.</exception>
    public static void AssertAgreesWith(
        string libraryName,
        string addressingVersion,
        int keyMaxLength,
        int nameMaxLength,
        Func<object?, bool>? isKeyUnset = null,
        Func<object, string?>? discoverKey = null)
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

        if (differences.Count > 0)
        {
            throw new InvalidOperationException(
                $"{libraryName} and DotNetBoost.MultiLingual do not address subjects the same way, so rows " +
                $"filed by one cannot be joined with rows filed by the other: {string.Join("; ", differences)}. " +
                "Upgrade whichever is behind.");
        }

        // Behaviour after constants, so a version mismatch is reported as a version mismatch rather
        // than as whichever rule happens to differ because of it.
        if (isKeyUnset is not null) AssertUnsetDetection(isKeyUnset, libraryName);
        if (discoverKey is not null) AssertKeyDiscovery(discoverKey, libraryName);
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
        AssertUnsetDetection(EntityKey.IsUnset, "DotNetBoost.MultiLingual");
        AssertKeyDiscovery(ConventionKey, "DotNetBoost.MultiLingual");
    }

    /// <summary>
    /// Every value the spec calls "not assigned yet". A library that disagrees on one of them
    /// addresses rows the other believes belong to nothing — <c>byte</c> was exactly that.
    /// </summary>
    public static IEnumerable<object?> UnsetCases =>
        [null, 0, 0L, (short)0, (byte)0, Guid.Empty, string.Empty];

    /// <summary>Values that must read as an assigned key, so "everything is unset" cannot pass.</summary>
    public static IEnumerable<object?> AssignedCases =>
        [1, -1, 1L, (short)1, (byte)1, SampleGuid, "k"];

    /// <summary>
    /// Checks that another library's unset-key test agrees with the spec, in both directions.
    /// </summary>
    /// <param name="isKeyUnset">Its unset test — <c>EntityKey.IsUnset</c> or equivalent.</param>
    /// <param name="libraryName">The other library, named in the failure message.</param>
    /// <exception cref="InvalidOperationException">It treats some value differently.</exception>
    public static void AssertUnsetDetection(Func<object?, bool> isKeyUnset, string libraryName)
    {
        ArgumentNullException.ThrowIfNull(isKeyUnset);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);

        foreach (var value in UnsetCases)
        {
            if (!isKeyUnset(value))
            {
                throw new InvalidOperationException(
                    $"Entity Addressing v{Version}: {Describe(value)} means \"not assigned yet\" and must address " +
                    $"nothing, but {libraryName} treats it as a key. One library would then file rows under a " +
                    "value the other reads as absent.");
            }
        }

        foreach (var value in AssignedCases)
        {
            if (isKeyUnset(value))
            {
                throw new InvalidOperationException(
                    $"Entity Addressing v{Version}: {Describe(value)} is an assigned key, but {libraryName} " +
                    "treats it as unset and would silently store nothing for it.");
            }
        }
    }

    /// <summary>
    /// Checks that another library discovers an entity's key by the spec's rule: the single
    /// <c>[Key]</c> property, else <c>Id</c>, else <c>{TypeName}Id</c>; a composite key refused;
    /// an unreadable property skipped; an unassigned key reported as <c>null</c>.
    /// </summary>
    /// <param name="discoverKey">
    /// Its CONVENTION key accessor, not a provider's model-reading one: the shared rule is the
    /// fallback used where there is no model to consult.
    /// </param>
    /// <param name="libraryName">The other library, named in the failure message.</param>
    /// <exception cref="InvalidOperationException">It discovers a key some other way.</exception>
    public static void AssertKeyDiscovery(Func<object, string?> discoverKey, string libraryName)
    {
        ArgumentNullException.ThrowIfNull(discoverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);

        Expect(discoverKey(new AnnotatedKey { Code = 7 }), "7", "the single [Key] property");
        Expect(discoverKey(new PlainId { Id = 7 }), "7", "a property named Id");
        Expect(discoverKey(new SuffixId { SuffixIdId = 7 }), "7", "a property named {TypeName}Id");

        // Order, not just membership: an entity with both must key on the annotated one, or the
        // same entity is addressed by two different values depending on which library asked.
        Expect(discoverKey(new AnnotatedAndId { Code = 7, Id = 99 }), "7", "[Key] in preference to Id");

        // Unreadable on name alone: a write-only Id is not a key, and matching by name without
        // checking would pick it and then fail to read a value from it.
        Expect(discoverKey(new WriteOnlyId { WriteOnlyIdId = 7 }), "7", "a readable property, skipping a write-only Id");

        if (discoverKey(new PlainId()) is { } unassigned)
        {
            throw new InvalidOperationException(
                $"Entity Addressing v{Version}: an unassigned key must be reported as null, but {libraryName} " +
                $"reported '{unassigned}'. An unsaved entity would then look addressable.");
        }

        try
        {
            discoverKey(new CompositeKey { Left = 1, Right = 2 });
        }
        catch (Exception)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Entity Addressing v{Version}: a composite key must be refused — one column holds one value, and " +
            $"reflection does not order properties — but {libraryName} resolved one. The entity would be keyed " +
            "on whichever property that runtime happened to return.");

        void Expect(string? actual, string expected, string rule)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Entity Addressing v{Version}: key discovery must use {rule}, giving '{expected}'. " +
                    $"{libraryName} gave '{actual ?? "null"}'.");
            }
        }
    }

    /// <summary>This library's own convention key discovery, for <see cref="AssertSpecIsIntact"/>.</summary>
    private static string? ConventionKey(object entity) => new ConventionEntityKeyAccessor().GetKey(entity);

    /// <summary>How a key value reads in a failure message, with its type and nulls spelled out.</summary>
    private static string Describe(object? value) =>
        value is null ? "null" : $"({value.GetType().Name}){(value is string s ? $"\"{s}\"" : value)}";

    /// <summary>Keyed by attribute — the first rule of key discovery.</summary>
    public sealed class AnnotatedKey
    {
        /// <summary>Its key.</summary>
        [Key]
        public int Code { get; set; }
    }

    /// <summary>Keyed by the name <c>Id</c> — the second rule.</summary>
    public sealed class PlainId
    {
        /// <summary>Its key.</summary>
        public int Id { get; set; }
    }

    /// <summary>Keyed by <c>{TypeName}Id</c> — the third rule.</summary>
    public sealed class SuffixId
    {
        /// <summary>Its key.</summary>
        public int SuffixIdId { get; set; }
    }

    /// <summary>Carries both, so the ORDER of the rules is asserted and not just their presence.</summary>
    public sealed class AnnotatedAndId
    {
        /// <summary>Its key, because <c>[Key]</c> outranks the name.</summary>
        [Key]
        public int Code { get; set; }

        /// <summary>Not its key, despite the name.</summary>
        public int Id { get; set; }
    }

    /// <summary>Two <c>[Key]</c> properties, which one column cannot hold.</summary>
    public sealed class CompositeKey
    {
        /// <summary>Half of a composite key.</summary>
        [Key]
        public int Left { get; set; }

        /// <summary>The other half.</summary>
        [Key]
        public int Right { get; set; }
    }

    /// <summary>
    /// Has an <c>Id</c> that cannot be read, so discovery must skip it and fall through to the
    /// <c>{TypeName}Id</c> rule.
    /// </summary>
    public sealed class WriteOnlyId
    {
        private int _ignored;

        /// <summary>Write-only, and therefore not a key.</summary>
        public int Id { set => _ignored = value; }

        /// <summary>Its actual key.</summary>
        public int WriteOnlyIdId { get; set; }

        /// <summary>Keeps the compiler from warning that the backing field is never read.</summary>
        public int Ignored() => _ignored;
    }
}
