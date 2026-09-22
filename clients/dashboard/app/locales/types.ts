/**
 * A message that depends on a number: one form per plural category the language uses, picked with
 * `Intl.PluralRules`. `other` is required — every language has it.
 */
export type Plural = { other: string } & Partial<Record<Intl.LDMLPluralRule, string>>

/** Marks a message as plural. `t()` then needs a `count`. */
export function plural(forms: Plural): Plural {
  return forms
}

/**
 * The text of one content type in one locale, under `types.{route}`. Everything is optional: what
 * is missing falls back to the English in `utils/content.ts`, and a phrase to `content.generic`.
 */
export interface ContentTypeMessages {
  label?: string
  name?: string
  description?: string
  deleteNote?: string
  /** Translatable field key → label. */
  fields?: Record<string, string>
  /** Detail key → label and description. */
  details?: Record<string, { label?: string, description?: string }>
  new?: string
  create?: string
  created?: string
  deleted?: string
  deleteTitle?: string
  deleteLabel?: string
  empty?: string
  loadError?: string
  itemLoadError?: string
  itemTitle?: string
}
