import type { ContentItem, ContentTranslations, ContentType } from '~/types'

type Values = Record<string, string | undefined>

/**
 * Every language of one item, for editing side by side.
 *
 * What is stored comes from `GET api/{route}/{id}/translations`. That leaves out the base
 * language, which is the entity's own row, so with a base language configured the item is also
 * read in it. A field with nothing stored in a language is `undefined` there: reads in that
 * language fall back to the default.
 *
 * Saving writes one language — the API stores a `PUT` in the request's `Accept-Language` — and
 * sends only the fields that changed, since a missing field means "leave it alone".
 */
export function useContentItem(type: ContentType, id: MaybeRefOrGetter<number>) {
  const toast = useToast()
  const { active, defaultLanguage, isBase, ensureLoaded } = useLanguages()
  const { baseLanguage } = useRuntimeConfig().public

  const endpoint = computed(() => `/api/${type.route}/${toValue(id)}`)

  /** Language code → field key → stored value. The baseline for dirty checking. */
  const stored = ref<Record<string, Values>>({})
  /** Language code → field key → what the form holds. */
  const state = reactive<Record<string, Record<string, string>>>({})
  const identifier = ref('')

  const pending = ref(true)
  const loadError = ref('')
  /** Code of the language being saved or cleared. */
  const saving = ref<string | null>(null)

  async function load() {
    pending.value = true
    loadError.value = ''

    try {
      await ensureLoaded()

      const [translations, base] = await Promise.all([
        $fetch<ContentTranslations>(`${endpoint.value}/translations`),
        baseLanguage ? $fetch<ContentItem>(endpoint.value, { headers: inLanguage(baseLanguage) }) : undefined
      ])

      const next: Record<string, Values> = {}
      for (const language of active.value) {
        next[language.code] = Object.fromEntries(type.fields.map(field => [
          field.key,
          isBase(language.code) && base
            ? (base[field.key] as string | null) ?? undefined
            : storedValue(translations.translations[language.code], field.key) ?? undefined
        ]))
      }

      identifier.value = String((translations as unknown as Record<string, unknown>)[type.identifier] ?? '')
      stored.value = next

      for (const code of Object.keys(next)) {
        state[code] = Object.fromEntries(type.fields.map(field => [field.key, next[code]![field.key] ?? '']))
      }
    } catch (error) {
      loadError.value = problemMessage(error)
    } finally {
      pending.value = false
    }
  }

  function changed(code: string) {
    return type.fields
      .map(field => field.key)
      .filter(key => (state[code]?.[key] ?? '') !== (stored.value[code]?.[key] ?? ''))
  }

  const dirty = (code: string) => changed(code).length > 0

  /** Whether anything at all is stored in `code` — otherwise every read in it falls back. */
  const hasContent = (code: string) => type.fields.some(field => stored.value[code]?.[field.key] !== undefined)

  /** What a read in a language with nothing stored for `key` returns instead. */
  function fallback(key: string) {
    const code = defaultLanguage.value?.code
    return (code && stored.value[code]?.[key]) || (baseLanguage && stored.value[baseLanguage]?.[key]) || undefined
  }

  async function save(code: string) {
    const body = Object.fromEntries(changed(code).map(key => [key, state[code]![key]]))
    saving.value = code

    try {
      const saved = await $fetch<ContentItem>(endpoint.value, {
        method: 'PUT',
        body,
        headers: inLanguage(code)
      })

      toast.add({
        title: `Saved in ${code}`,
        // The sample's confirmation, already in the language that was written.
        description: saved.message,
        icon: 'i-lucide-check',
        color: 'success'
      })
      await load()
      return true
    } catch (error) {
      toast.add({ title: `Save in ${code} failed`, description: problemMessage(error), icon: 'i-lucide-triangle-alert', color: 'error' })
      return false
    } finally {
      saving.value = null
    }
  }

  function reset(code: string) {
    for (const field of type.fields) {
      state[code]![field.key] = stored.value[code]?.[field.key] ?? ''
    }
  }

  /** Removes every stored field in `code`, so reads in it fall back. Not offered for the base language. */
  async function clear(code: string) {
    saving.value = code

    try {
      const result = await $fetch<{ message?: string }>(`${endpoint.value}/translations/${code}`, { method: 'DELETE' })

      toast.add({ title: `${code} translation cleared`, description: result.message, icon: 'i-lucide-eraser', color: 'success' })
      await load()
      return true
    } catch (error) {
      toast.add({ title: `Clearing ${code} failed`, description: problemMessage(error), icon: 'i-lucide-triangle-alert', color: 'error' })
      return false
    } finally {
      saving.value = null
    }
  }

  watch(endpoint, load)
  onMounted(load)

  return {
    identifier, stored, state, pending, loadError, saving,
    load, save, reset, clear, dirty, hasContent, fallback
  }
}
