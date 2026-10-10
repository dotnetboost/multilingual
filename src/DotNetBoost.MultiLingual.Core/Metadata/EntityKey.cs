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
    /// resolved, how a key is discovered and formatted, when a key counts as unset, and the two
    /// length limits.
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
    /// <b>Bump it for ANY change</b> to entity-name resolution
    /// (<see cref="MultiLingualMetadata.EntityNameFor"/>), to key discovery (the
    /// <c>[Key]</c> → <c>Id</c> → <c>{TypeName}Id</c> order the convention accessor applies), to
    /// key formatting (<see cref="ToKeyString"/>), to unset-key detection
    /// (<see cref="IsUnset"/>), or to either length limit (<see cref="MaxLength"/>,
    /// <see cref="MultiLingualMetadata.MaxEntityNameLength"/>). Nothing else may change it —
    /// and a rule left off this list is how two libraries drift while both still claim the same
    /// version.
    /// This library's own suite runs the full rule set for the version named here, so the version
    /// is one it is held to rather than one it merely claims.
    /// </para>
    /// </summary>
    public const string AddressingVersion = "1.0";

    /// <summary>
    /// The longest key <c>Translation.EntityId</c> can hold. Every numeric key and a Guid (36)
    /// fit; a string key longer than this cannot have translations, and writing one is refused.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Formats a key value invariantly, refusing one that cannot address a row at all.
    /// <para>
    /// Empty and whitespace are refused here rather than at the write, for the same reason an
    /// entity name's length is checked early: the error names the call that made the mistake. It is
    /// also where DotNetBoost.Media refuses them, so both libraries reject the same keys at the
    /// same point and the conformance suite can compare them through one hook.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="keyValue"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The formatted key is empty or whitespace.</exception>
    public static string ToKeyString(object keyValue)
    {
        var text = keyValue switch
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

        // Whitespace is the one that mattered: it is not unset — "   " has a length — so it used to
        // pass as a key, and rows filed under it could never be joined to a library that refuses it.
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("An entity key cannot be empty or whitespace.");

        // The LENGTH limit is deliberately NOT checked here. TranslationManager refuses an
        // over-long key at the write, where it can name the entity as well as the key and its
        // length — a better error than this method could give, and the one its tests protect.
        // Enforcement layer is each library's own business; the limit itself is the shared rule,
        // and MaxLength is what the conformance suite compares.
        return text;
    }

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
