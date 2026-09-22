import { createSharedComposable, useLocalStorage } from '@vueuse/core'
import { fallbackLocale, findLocale, locales, type ContentTypeMessages, type Messages, type Plural } from '~/locales'
import type { ContentType, TextDirection } from '~/types'

/** Every key of `Messages` that holds text, in dot notation: `languages.actions.edit`. */
type MessageKey<T = Messages> = {
  [K in keyof T & string]: T[K] extends string | Plural ? K : T[K] extends Record<string, ContentTypeMessages> ? never : `${K}.${MessageKey<T[K]>}`
}[keyof T & string]

type Params = Record<string, string | number | undefined>

type GenericPhrase = keyof Messages['content']['generic']

const english = locales[fallbackLocale]!

function isPlural(value: unknown): value is Plural {
  return typeof value === 'object' && value !== null && 'other' in value
}

function lookup(source: unknown, key: string): unknown {
  return key.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], source)
}

function interpolate(text: string, params?: Params) {
  return text.replace(/\{(\w+)\}/g, (match, name: string) => params?.[name] === undefined ? match : String(params[name]))
}

function same(a: string | undefined, b: string | undefined) {
  return !!a && !!b && a.toLowerCase() === b.toLowerCase()
}

/**
 * The dashboard's language: one choice, made in the header, that drives everything.
 *
 * - The text: `t()` reads the bundled locale for it (`~/locales`), falling back to English key by key.
 * - The layout: `dir` is the language's own direction, as the API reports it, so an `rtl` language
 *   flips the whole dashboard.
 * - The content: lists are read in it, and every API call without an explicit `Accept-Language`
 *   carries it (see `plugins/accept-language.client.ts`), so the API's own messages match.
 *
 * The choices are the API's active languages. Until they load — or when the API cannot be reached —
 * they are the bundled locales, so the dashboard can still be switched. The pick is remembered in
 * the browser; a language that is later deactivated or deleted falls back to the default.
 */
const _useI18n = () => {
  const { active, defaultLanguage } = useLanguages()

  const stored = useLocalStorage('multilingual-dashboard-language', '')

  const available = computed<{ code: string, name: string, direction: TextDirection }[]>(() => active.value.length
    ? active.value
    : Object.entries(locales).map(([code, locale]) => ({ code, name: locale.name, direction: locale.direction })))

  const language = computed(() => {
    const list = available.value
    return list.find(candidate => same(candidate.code, stored.value))
      ?? list.find(candidate => same(candidate.code, defaultLanguage.value?.code ?? fallbackLocale))
      ?? list[0]
      ?? { code: fallbackLocale, name: english.name, direction: english.direction }
  })

  const code = computed(() => language.value.code)
  const dir = computed(() => language.value.direction)
  const locale = computed(() => findLocale(code.value) ?? english)

  /**
   * Nuxt UI's locale — the components' built-in labels, and the direction they lay out in. A
   * language without a bundled locale gets the English labels, in its own direction.
   */
  const uiLocale = computed(() => extendLocale(locale.value.ui, { code: code.value, dir: dir.value }))

  const pluralRules = computed(() => {
    try {
      return new Intl.PluralRules(code.value)
    } catch {
      return new Intl.PluralRules(fallbackLocale)
    }
  })

  /** The text for `key`, or `undefined` when no locale has it. */
  function translate(key: string, params?: Params) {
    let value = lookup(locale.value.messages, key) ?? lookup(english.messages, key)

    if (isPlural(value)) {
      value = value[pluralRules.value.select(Number(params?.count ?? 0))] ?? value.other
    }

    return typeof value === 'string' ? interpolate(value, params) : undefined
  }

  /** The text for `key` in the dashboard's language, with `{placeholders}` filled from `params`. */
  function t(key: MessageKey, params?: Params) {
    return translate(key, params) ?? key
  }

  function typeMessages(type: ContentType): ContentTypeMessages {
    return locale.value.messages.types[type.route] ?? {}
  }

  /** `type` with its labels and descriptions in the dashboard's language, where the locale has them. */
  function localizeType(type: ContentType): ContentType {
    const messages = typeMessages(type)

    return {
      ...type,
      label: messages.label ?? type.label,
      name: messages.name ?? type.name,
      description: messages.description ?? type.description,
      deleteNote: messages.deleteNote ?? type.deleteNote,
      fields: type.fields.map(field => ({ ...field, label: messages.fields?.[field.key] ?? field.label })),
      details: type.details.map(field => ({
        ...field,
        label: messages.details?.[field.key]?.label ?? field.label,
        description: messages.details?.[field.key]?.description ?? field.description
      }))
    }
  }

  /**
   * A phrase that names `type` — “New product”, “Delete category {id}?”. The locale's own wording
   * for the type comes first; otherwise `content.generic` with the type's name filled in.
   */
  function tType(type: ContentType, phrase: GenericPhrase, params?: Params) {
    const localized = localizeType(type)
    const text = typeMessages(type)[phrase] ?? translate(`content.generic.${phrase}`)!

    return interpolate(text, {
      name: localized.name,
      noun: localized.name.toLocaleLowerCase(code.value),
      label: localized.label.toLocaleLowerCase(code.value),
      ...params
    })
  }

  function setLanguage(next: string) {
    stored.value = next
  }

  return { available, language, code, dir, uiLocale, t, tType, localizeType, setLanguage }
}

export const useI18n = createSharedComposable(_useI18n)
