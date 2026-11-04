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

    /// <summary>True when <paramref name="currentValue"/> is still exactly what a read served to the property.</summary>
    public bool IsServedValue(object entity, string propertyName, string? currentValue) =>
        _values.TryGetValue(entity, out var byProperty)
        && byProperty.TryGetValue(propertyName, out var hydrated)
        && string.Equals(hydrated.Value, currentValue, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="currentValue"/> is still what a read served to the property, from
    /// a language other than <paramref name="baseLanguage"/> — a value that must never reach the
    /// base language's column.
    /// </summary>
    public bool IsServedFromAnotherLanguage(object entity, string propertyName, string? currentValue, string baseLanguage) =>
        _values.TryGetValue(entity, out var byProperty)
        && byProperty.TryGetValue(propertyName, out var hydrated)
        && string.Equals(hydrated.Value, currentValue, StringComparison.Ordinal)
        && !string.Equals(hydrated.LanguageCode, baseLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when writing <paramref name="currentValue"/> in <paramref name="authoringLanguage"/> would be redundant or wrong.</summary>
    public bool IsUnchanged(object entity, string propertyName, string? currentValue, string authoringLanguage) =>
        _values.TryGetValue(entity, out var byProperty)
        && byProperty.TryGetValue(propertyName, out var hydrated)
        && string.Equals(hydrated.Value, currentValue, StringComparison.Ordinal)
        && (hydrated.IsFallback
            || string.Equals(hydrated.LanguageCode, authoringLanguage, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ base-language mode

    private readonly ConditionalWeakTable<object, Dictionary<string, string?>> _baseValues = new();
    private readonly ConditionalWeakTable<object, Dictionary<string, string?>> _baseAuthored = new();

    /// <summary>
    /// Remembers <paramref name="value"/> as what the base language's column holds for the
    /// property. Without <paramref name="replace"/>, only the first value counts: the one a read
    /// found in the property before it served another language there.
    /// </summary>
    public void RememberBaseValue(object entity, string propertyName, string? value, bool replace)
    {
        var byProperty = _baseValues.GetValue(entity, static _ => new Dictionary<string, string?>(StringComparer.Ordinal));
        if (replace || !byProperty.ContainsKey(propertyName))
            byProperty[propertyName] = value;
    }

    /// <summary>The base-language value remembered for the property, if any.</summary>
    public bool TryGetBaseValue(object entity, string propertyName, out string? value)
    {
        value = null;
        return _baseValues.TryGetValue(entity, out var byProperty) && byProperty.TryGetValue(propertyName, out value);
    }

    /// <summary>
    /// Declares <paramref name="value"/>, just assigned to the property, as the base language's —
    /// <c>SetAll</c> — so a save writes it to the column whatever language is current, and never
    /// as that language's translation.
    /// </summary>
    public void MarkBaseAuthored(object entity, string propertyName, string? value, string baseLanguage)
    {
        var byProperty = _baseAuthored.GetValue(entity, static _ => new Dictionary<string, string?>(StringComparer.Ordinal));
        byProperty[propertyName] = value;

        // On loan from the base language, as far as every other language is concerned: what
        // keeps CaptureValues from filing it as the current language's text.
        Mark(entity, propertyName, value, baseLanguage, isFallback: true);
        RememberBaseValue(entity, propertyName, value, replace: true);
    }

    /// <summary>True when the property still holds the base-language value <c>SetAll</c> assigned it.</summary>
    public bool IsBaseAuthored(object entity, string propertyName, string? currentValue) =>
        _baseAuthored.TryGetValue(entity, out var byProperty)
        && byProperty.TryGetValue(propertyName, out var authored)
        && string.Equals(authored, currentValue, StringComparison.Ordinal);

    private sealed record Hydrated(string? Value, string LanguageCode, bool IsFallback);
}
