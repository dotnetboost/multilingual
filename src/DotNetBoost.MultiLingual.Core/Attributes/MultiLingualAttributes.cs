namespace DotNetBoost.MultiLingual.Core.Attributes;

/// <summary>
/// Marks content as translatable. Usable at two levels:
///
/// <para>
/// <b>Class level</b> — every <see cref="string"/> property on the type that has a public
/// getter and setter becomes translatable. Non-string members (numeric, bool, date,
/// navigation properties, collections) are never translated, even under class-level
/// application. Conventional key properties (<c>Id</c>, <c>{TypeName}Id</c>) are also excluded.
/// </para>
///
/// <para>
/// <b>Property level</b> — only the decorated properties are translatable. The property
/// must be of type <see cref="string"/>; decorating anything else throws when the type is
/// first inspected, so the mistake surfaces immediately rather than silently doing nothing.
/// </para>
///
/// <para>
/// <b>Conflict rule (documented deliberately — do not "fix" this):</b> when the attribute
/// appears at BOTH class and property level on the same type, the class-level declaration
/// WINS and every eligible string property is translatable. The property-level annotations
/// are therefore redundant in that case — they neither narrow the set nor cause an error.
/// Rationale: class-level is the broader, more explicit intent; silently narrowing to the
/// annotated subset would make the class-level attribute a no-op and confuse readers.
/// </para>
///
/// <para>
/// Translatable properties are not stored on the owning entity. Their values live in the
/// translations store, one row per entity, property and language; how the owning entity's own
/// storage is told to skip them is up to the provider (the EF Core provider removes them from
/// the model).
/// </para>
///
/// <para>
/// Use <see cref="NotMultiLingualAttribute"/> to opt a single string property back out of a
/// class-level declaration.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class MultiLingualAttribute : Attribute;

/// <summary>
/// Escape hatch for class-level <see cref="MultiLingualAttribute"/>: a string property
/// marked with this stays an ordinary, untranslated member.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NotMultiLingualAttribute : Attribute;
