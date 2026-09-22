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
  // Resolved at call time: useI18n reads this list, so it cannot be created during this setup.
  const t: ReturnType<typeof useI18n>['t'] = (key, params) => useI18n().t(key, params)

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
        title: t('languages.toasts.refused'),
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
    title: t('languages.toasts.added', { name: created.name }),
    description: created.isDefault
      ? t('languages.toasts.addedDefault', { code: created.code })
      : t('languages.toasts.addedWritable', { code: created.code })
  }))

  const update = (code: string, input: LanguageUpdate) => run(code, () =>
    $fetch<Language>(`/api/languages/${code}`, { method: 'PUT', body: input }),
  updated => ({
    title: t('languages.toasts.saved', { name: updated.name }),
    description: updated.isActive ? undefined : t('languages.toasts.inactive')
  }))

  const setDefault = (code: string) => run(code, () =>
    $fetch<Language>(`/api/languages/${code}/set-default`, { method: 'POST' }),
  promoted => ({
    title: t('languages.toasts.promoted', { name: promoted.name }),
    description: t('languages.toasts.promotedDescription')
  }))

  const remove = (code: string) => run(code, () =>
    $fetch(`/api/languages/${code}`, { method: 'DELETE' }),
  () => ({ title: t('languages.toasts.deleted', { code }) }))

  const purge = (code: string) => run(code, () =>
    $fetch<PurgeResult>(`/api/languages/${code}/translations`, { method: 'DELETE' }),
  result => ({
    title: t('languages.toasts.purged', { count: result.deletedTranslations }),
    description: t('languages.toasts.purgedDescription', { code: result.code })
  }))

  return {
    languages, active, defaultLanguage, pending, loaded, loadError, busy,
    load, ensureLoaded, find, isBase, create, update, setDefault, remove, purge
  }
}

export const useLanguages = createSharedComposable(_useLanguages)
