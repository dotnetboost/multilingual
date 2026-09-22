import { createSharedComposable } from '@vueuse/core'
import type { Language, LanguageUpdate, NewLanguage, PurgeResult } from '~/types'

/**
 * The language list from `GET api/languages`, shared by every page, and the administration calls
 * that `MapLanguageEndpoints()` maps. Each call reloads the list afterwards: setting a default
 * demotes another language, so patching one row locally would be wrong.
 *
 * Refusals come back as problem details — the default cannot be deleted or deactivated, a
 * language with content cannot be deleted, and so on — and are shown as the API words them.
 */
const _useLanguages = () => {
  const toast = useToast()
  const { baseLanguage } = useRuntimeConfig().public

  const languages = ref<Language[]>([])
  const pending = ref(false)
  const loaded = ref(false)
  const loadError = ref('')
  /** Code of the language an action is running on, for per-row loading states. */
  const busy = ref<string | null>(null)

  const active = computed(() => languages.value.filter(language => language.isActive))
  const defaultLanguage = computed(() => languages.value.find(language => language.isDefault))

  async function load() {
    pending.value = true
    loadError.value = ''

    try {
      languages.value = await $fetch<Language[]>('/api/languages')
      loaded.value = true
    } catch (error) {
      loadError.value = problemMessage(error)
    } finally {
      pending.value = false
    }
  }

  /** Loads the list the first time a page needs it. */
  async function ensureLoaded() {
    if (!loaded.value && !pending.value) {
      await load()
    }
  }

  function find(code: string) {
    return languages.value.find(language => language.code.toLowerCase() === code.toLowerCase())
  }

  /** Whether `code` is `MultiLingualOptions.BaseLanguage`: stored in the entity's own columns. */
  function isBase(code: string) {
    return !!baseLanguage && code.toLowerCase() === baseLanguage.toLowerCase()
  }

  /** Runs one call, reports the outcome, and reloads. Resolves to the call's result, or `undefined` if it failed. */
  async function run<T>(code: string, call: () => Promise<T>, success: (result: T) => { title: string, description?: string }) {
    busy.value = code

    try {
      const result = await call()
      toast.add({ ...success(result), icon: 'i-lucide-check', color: 'success' })
      return result
    } catch (error) {
      toast.add({
        title: 'The API refused the change',
        description: problemMessage(error),
        icon: 'i-lucide-triangle-alert',
        color: 'error'
      })
      return undefined
    } finally {
      busy.value = null
      await load()
    }
  }

  const create = (input: NewLanguage) => run(input.code, () =>
    $fetch<Language>('/api/languages', { method: 'POST', body: input }),
  created => ({
    title: `${created.name} added`,
    description: created.isDefault
      ? `${created.code} is the default language.`
      : `Content can now be written in ${created.code}.`
  }))

  const update = (code: string, input: LanguageUpdate) => run(code, () =>
    $fetch<Language>(`/api/languages/${code}`, { method: 'PUT', body: input }),
  updated => ({
    title: `${updated.name} saved`,
    description: updated.isActive ? undefined : 'Inactive: its content is kept, but never served.'
  }))

  const setDefault = (code: string) => run(code, () =>
    $fetch<Language>(`/api/languages/${code}/set-default`, { method: 'POST' }),
  promoted => ({
    title: `${promoted.name} is now the default`,
    description: 'Reads without a translation in the requested language fall back to it.'
  }))

  const remove = (code: string) => run(code, () =>
    $fetch(`/api/languages/${code}`, { method: 'DELETE' }),
  () => ({ title: `${code} deleted` }))

  const purge = (code: string) => run(code, () =>
    $fetch<PurgeResult>(`/api/languages/${code}/translations`, { method: 'DELETE' }),
  result => ({
    title: `${result.deletedTranslations} ${result.deletedTranslations === 1 ? 'translation' : 'translations'} deleted`,
    description: `Reads in ${result.code} now fall back until it is written again.`
  }))

  return {
    languages, active, defaultLanguage, pending, loaded, loadError, busy,
    load, ensureLoaded, find, isBase, create, update, setDefault, remove, purge
  }
}

export const useLanguages = createSharedComposable(_useLanguages)
