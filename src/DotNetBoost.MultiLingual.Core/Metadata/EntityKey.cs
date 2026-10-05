using System.Globalization;

namespace DotNetBoost.MultiLingual.Core.Metadata;

/// <summary>
/// Converts an entity's primary key to the canonical string stored in
/// <c>Translation.EntityId</c>. Supporting int, long, Guid and string keys is what lets a
/// single translations store serve every entity in the system. Every provider must format keys
/// through here, or the same entity would be addressed by two different strings.
/// </summary>
public static class EntityKey
{
    /// <summary>
    /// The version of <b>DotNetBoost Entity Addressing</b> — the spec in the README under
    /// "Entity addressing" — that this implementation conforms to: how an entity name is
    /// resolved, how a key is formatted, and the two length limits.
    ///
    /// <para>
    /// Every DotNetBoost library that files rows against the same (entity name, entity id) pair
    /// publishes this constant with the same value, and <c>DotNetBoost.Media</c> does. An
    /// application joining rows across two of them can assert that they agree:
    /// <code>
    /// Assert.Equal(
    ///     DotNetBoost.Media.Core.Metadata.EntityKey.AddressingVersion,
    ///     DotNetBoost.MultiLingual.Core.Metadata.EntityKey.AddressingVersion);
    /// </code>
    /// which is a one-line replacement for hand-written tests that compare key formats, and makes
    /// a deliberate format change fail loudly in every consumer rather than quietly stranding
    /// stored rows.
    /// </para>
    ///
    /// <para>
    /// <b>Bump it for ANY change</b> to <see cref="ToKeyString"/>, to
    /// <see cref="MultiLingualMetadata.EntityNameFor"/>, to <see cref="MaxLength"/> or to
    /// <see cref="MultiLingualMetadata.MaxEntityNameLength"/>. Nothing else may change it.
    /// <c>DotNetBoost.MultiLingual.TestSupport</c>'s <c>EntityAddressing</c> runs the conformance
    /// suite for the version named here.
    /// </para>
    /// </summary>
    public const string AddressingVersion = "1.0";

    /// <summary>
    /// The longest key <c>Translation.EntityId</c> can hold. Every numeric key and a Guid (36)
    /// fit; a string key longer than this cannot have translations, and writing one is refused.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>Formats a key value invariantly.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="keyValue"/> is null.</exception>
    public static string ToKeyString(object keyValue) => keyValue switch
    {
        null           => throw new ArgumentNullException(nameof(keyValue)),
        Guid guid      => guid.ToString("D", CultureInfo.InvariantCulture),
        string s       => s,
        int i          => i.ToString(CultureInfo.InvariantCulture),
        long l         => l.ToString(CultureInfo.InvariantCulture),
        short sh       => sh.ToString(CultureInfo.InvariantCulture),
        byte b         => b.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _              => keyValue.ToString() ?? throw new InvalidOperationException(
            $"Cannot convert key of type '{keyValue.GetType().Name}' to a translation key.")
    };

    /// <summary>
    /// True when <paramref name="value"/> is the CLR default for its key type — an entity whose
    /// store-generated key has not been assigned yet, and so cannot be addressed.
    /// </summary>
    /// <remarks>
    /// Only the key types listed below are recognised. A value of any OTHER type reads as SET,
    /// even at its own default — a strongly-typed id struct, or <c>ObjectId.Empty</c> — because
    /// Core cannot reference a provider's types to know better.
    /// <para>
    /// A provider whose keys use such a type must therefore return <c>null</c> from its
    /// <see cref="DotNetBoost.MultiLingual.Core.Interfaces.IEntityKeyAccessor"/> for the
    /// unassigned value: the accessors are the only callers of this method, so that is the one
    /// place the knowledge exists. Left to this method, an unsaved entity looks addressable and
    /// its translations are stored under the default value's string form.
    /// </para>
    /// </remarks>
    public static bool IsUnset(object? value) => value switch
    {
        null     => true,
        int i    => i == 0,
        long l   => l == 0,
        short s  => s == 0,
        byte b   => b == 0,
        Guid g   => g == Guid.Empty,
        string s => s.Length == 0,
        _        => false
    };
}
