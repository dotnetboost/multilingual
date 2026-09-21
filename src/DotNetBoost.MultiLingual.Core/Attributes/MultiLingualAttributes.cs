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
public sealed class MultiLingualAttribute : Attribute
{
    /// <summary>
    /// The name stored in <c>Translation.EntityName</c> for this type's rows, in place of the
    /// class name. Class level only; for property-level declarations use
    /// <see cref="MultiLingualEntityAttribute"/>. See that attribute for why you would set it.
    /// </summary>
    public string? EntityName { get; set; }
}

/// <summary>
/// Fixes the name stored in <c>Translation.EntityName</c> for a type's translation rows.
///
/// <para>
/// <b>Why it matters.</b> Without it the stored name is the class name, so the class name
/// becomes part of the database: renaming the class leaves every existing translation filed
/// under the old name, where nothing reads it any more. Setting a name here decouples the two —
/// rename the class freely, and the rows still match. To rename a class that has no fixed name
/// yet, set this to its <i>old</i> name.
/// </para>
///
/// <para>
/// Two types may not share a name, compared case-insensitively (a case-insensitive database
/// collation would mix their rows). <c>Build()</c> fails on a clash. The exception is an
/// inheritance hierarchy: a derived type inherits its base's name, and shares its rows, which
/// is correct when they share a key space, as EF Core's TPH, TPT and TPC mappings all do.
/// </para>
///
/// <para>
/// Equivalent to <c>[MultiLingual(EntityName = "...")]</c> at class level. Use this form when
/// the translatable properties are marked individually and the class has no
/// <see cref="MultiLingualAttribute"/> of its own.
/// </para>
/// </summary>
/// <param name="name">The stored name. Non-blank, at most 128 characters.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class MultiLingualEntityAttribute(string name) : Attribute
{
    /// <summary>The name stored in <c>Translation.EntityName</c>.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// Escape hatch for class-level <see cref="MultiLingualAttribute"/>: a string property
/// marked with this stays an ordinary, untranslated member.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NotMultiLingualAttribute : Attribute;
