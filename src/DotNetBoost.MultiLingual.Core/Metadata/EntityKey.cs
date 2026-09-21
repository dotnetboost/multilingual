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
