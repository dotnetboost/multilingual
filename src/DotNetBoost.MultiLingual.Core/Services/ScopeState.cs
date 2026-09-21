using System.Runtime.CompilerServices;

namespace DotNetBoost.MultiLingual.Core.Services;

/// <summary>
/// Values queued with <c>ITranslationManager.Stage</c>, waiting for the next save of their
/// entity. Scoped; keyed weakly by instance, so an entity that is never saved does not pin
/// its staged values for the life of the scope.
/// </summary>
internal sealed class TranslationWriteBuffer
{
    private readonly ConditionalWeakTable<object, Dictionary<string, Dictionary<string, string?>>> _entries = new();

    public void Stage(object entity, string languageCode, IReadOnlyDictionary<string, string?> values)
    {
        var byLanguage = _entries.GetValue(entity,
            static _ => new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase));

        if (!byLanguage.TryGetValue(languageCode, out var byProperty))
        {
            byProperty = new Dictionary<string, string?>(StringComparer.Ordinal);
            byLanguage[languageCode] = byProperty;
        }

        foreach (var (propertyName, value) in values)
            byProperty[propertyName] = value;
    }

    public IReadOnlyDictionary<string, Dictionary<string, string?>>? Get(object entity) =>
        _entries.TryGetValue(entity, out var byLanguage) ? byLanguage : null;

    public void Remove(object entity) => _entries.Remove(entity);
}

/// <summary>
/// Remembers what the read path put into each translatable property, and in which language —
/// so the write path can tell an untouched value from an edited one.
///
/// <para>
/// Without this, a read-then-write round trip would silently promote a fallback into a real
/// translation: load a product in French (Name falls back to the English "Laptop"), change
/// only the price, save — and the French name would become "Laptop". A value still equal to
/// what was hydrated is skipped: always when it was a fallback, and when it was a real
/// translation in the language now being written (rewriting it would change nothing).
/// </para>
/// </summary>
internal sealed class HydratedValueTracker
{
    private readonly ConditionalWeakTable<object, Dictionary<string, Hydrated>> _values = new();

    public void Mark(object entity, string propertyName, string? value, string languageCode, bool isFallback)
    {
        var byProperty = _values.GetValue(entity, static _ => new Dictionary<string, Hydrated>(StringComparer.Ordinal));
        byProperty[propertyName] = new Hydrated(value, languageCode, isFallback);
    }

    public void Forget(object entity, string propertyName)
    {
        if (_values.TryGetValue(entity, out var byProperty))
            byProperty.Remove(propertyName);
    }

    /// <summary>True when writing <paramref name="currentValue"/> in <paramref name="authoringLanguage"/> would be redundant or wrong.</summary>
    public bool IsUnchanged(object entity, string propertyName, string? currentValue, string authoringLanguage) =>
        _values.TryGetValue(entity, out var byProperty)
        && byProperty.TryGetValue(propertyName, out var hydrated)
        && string.Equals(hydrated.Value, currentValue, StringComparison.Ordinal)
        && (hydrated.IsFallback
            || string.Equals(hydrated.LanguageCode, authoringLanguage, StringComparison.OrdinalIgnoreCase));

    private sealed record Hydrated(string? Value, string LanguageCode, bool IsFallback);
}
