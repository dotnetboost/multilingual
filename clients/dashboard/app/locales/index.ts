import type { Locale, Messages as UiMessages } from '@nuxt/ui'
import { ar as uiAr, en as uiEn, fr as uiFr } from '@nuxt/ui/locale'
import type { TextDirection } from '~/types'
import ar from './ar'
import en, { type Messages } from './en'
import fr from './fr'

export type { Messages } from './en'
export type { ContentTypeMessages, Plural } from './types'

/**
 * The languages the dashboard's own text is written in. Any language the API serves can be picked
 * in the header; one without an entry here shows English text, laid out in its own direction.
 * Adding a language is one file with the shape of `en.ts`, listed here with Nuxt UI's own locale
 * for it (`@nuxt/ui/locale`), which words the components' built-in labels.
 */
export const locales: Record<string, { name: string, direction: TextDirection, messages: Messages, ui: Locale<UiMessages> }> = {
  en: { name: 'English', direction: 'ltr', messages: en, ui: uiEn },
  ar: { name: 'العربية', direction: 'rtl', messages: ar, ui: uiAr },
  fr: { name: 'Français', direction: 'ltr', messages: fr, ui: uiFr }
}

export const fallbackLocale = 'en'

/** The bundled locale for `code`: an exact match, else its base language (`fr-CA` → `fr`). */
export function findLocale(code: string) {
  const lower = code.toLowerCase()
  return locales[lower] ?? locales[lower.split('-')[0]!]
}
