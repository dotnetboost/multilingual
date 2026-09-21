namespace DotNetBoost.MultiLingual.Core.Models;

/// <summary>
/// The translations to persist for one entity instance: languageCode → (propertyName → value).
/// Produced by <see cref="Interfaces.ITranslationManager.CaptureValues"/> before the entity
/// is saved, and handed to <see cref="Interfaces.ITranslationManager.WriteAsync"/> once its
/// key is known.
/// </summary>
/// <param name="EntityName">Logical entity name, from <see cref="Metadata.MultiLingualMetadata.EntityNameFor"/>.</param>
/// <param name="EntityId">The entity's key, formatted by <see cref="Metadata.EntityKey.ToKeyString"/>.</param>
/// <param name="ValuesByLanguage">languageCode → (propertyName → value).</param>
public sealed record TranslationWrite(
    string EntityName,
    string EntityId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> ValuesByLanguage)
{
    /// <summary>
    /// The entity instance the values were captured from, when there is one. Lets the manager
    /// discard values staged for it and remember what was persisted, so a later save of the
    /// same, unchanged instance does not write them again.
    /// </summary>
    public object? Source { get; init; }
}
