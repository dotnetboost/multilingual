import type { ContentItem, ContentTranslations, ContentType, DetailField } from '~/types'

type Values = Record<string, string | undefined>
// Values differ per detail, and each control narrows its own at the point of use.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Details = Record<string, any>

/**
 * One item for editing: its details, the same in every language, and its translatable fields in
 * every active language.
 *
 * What is stored comes from `GET api/{route}/{id}/translations`. That leaves out the base
 * language, which is the entity's own row, so the item is also read — in the base language when
 * there is one, else in the default — which gives the details too. A field with nothing stored in
 * a language is `undefined` there: reads in that language fall back to the default.
 *
 * Saving writes one language — the API stores a `PUT` in the request's `Accept-Language` — along
 * with any changed details, and sends only what changed, since a missing field means "leave it
 * alone". Unsaved edits in the other languages survive the reload that follows.
 */
export function useContentItem(type: ContentType, id: MaybeRefOrGetter<number>) {
  const toast = useToast()
  const { active, defaultLanguage, isBase, ensureLoaded } = useLanguages()
  const { baseLanguage } = useRuntimeConfig().public
  const { t } = useI18n()

  const endpoint = computed(() => `/api/${type.route}/${toValue(id)}`)

  /** Language code → field key → stored value. The baseline for dirty checking. */
  const stored = ref<Record<string, Values>>({})
  /** Language code → field key → what the form holds. */
  const state = reactive<Record<string, Record<string, string>>>({})
  /** Detail key → stored value. */
  const storedDetails = ref<Details>({})
  /** Detail key → what the form holds. */
  const details = reactive<Details>({})
  const identifier = ref('')

  const pending = ref(true)
  const loadError = ref('')
  /** Code of the language being saved or cleared. */
  const saving = ref<string | null>(null)

  /** A detail as the form holds it: a reference to nothing is the "None" sentinel. */
  function detailValue(field: DetailField, value: unknown) {
    return field.type === 'reference' ? (value as number | null) ?? NO_REFERENCE : value
  }

  async function load() {
    pending.value = true
    loadError.value = ''

    // Edits not yet saved, to put back over what is reloaded.
    const edits = Object.fromEntries(Object.keys(state).map(code => [
      code,
      Object.fromEntries(changed(code).map(key => [key, state[code]![key]]))
    ]))
    const detailEdits = Object.fromEntries(changedDetails().map(key => [key, details[key]]))

    try {
      await ensureLoaded()

      const readIn = baseLanguage || defaultLanguage.value?.code
      const [translations, item] = await Promise.all([
        $fetch<ContentTranslations>(`${endpoint.value}/translations`),
        $fetch<ContentItem>(endpoint.value, { headers: readIn ? inLanguage(readIn) : undefined })
      ])

      const next: Record<string, Values> = {}
      for (const language of active.value) {
        next[language.code] = Object.fromEntries(type.fields.map(field => [
          field.key,
          isBase(language.code)
            ? (item[field.key] as string | null) ?? undefined
            : storedValue(translations.translations[language.code], field.key) ?? undefined
        ]))
      }

      identifier.value = String(item[type.identifier] ?? '')
      stored.value = next
      storedDetails.value = Object.fromEntries(type.details.map(field => [field.key, detailValue(field, item[field.key])]))

      for (const code of Object.keys(next)) {
        state[code] = Object.fromEntries(type.fields.map(field => [
          field.key,
          edits[code]?.[field.key] ?? next[code]![field.key] ?? ''
        ]))
      }
      for (const field of type.details) {
        details[field.key] = field.key in detailEdits ? detailEdits[field.key] : storedDetails.value[field.key]
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

  function changedDetails() {
    return type.details
      .filter(field => details[field.key] !== undefined && details[field.key] !== storedDetails.value[field.key])
      .map(field => field.key)
  }

  /** Whether the translatable fields in `code` hold unsaved changes. */
  const translationDirty = (code: string) => changed(code).length > 0
  const detailsDirty = computed(() => changedDetails().length > 0)
  /** Whether saving in `code` has anything to send. */
  const dirty = (code: string) => detailsDirty.value || translationDirty(code)

  /** Whether anything at all is stored in `code` — otherwise every read in it falls back. */
  const hasContent = (code: string) => type.fields.some(field => stored.value[code]?.[field.key] !== undefined)

  /** What a read in a language with nothing stored for `key` returns instead. */
  function fallback(key: string) {
    const code = defaultLanguage.value?.code
    return (code && stored.value[code]?.[key]) || (baseLanguage && stored.value[baseLanguage]?.[key]) || undefined
  }

  /** The changed details as the API takes them: text trimmed, and an emptied number or "None" left out. */
  function detailBody() {
    const body: Record<string, unknown> = {}

    for (const key of changedDetails()) {
      const field = type.details.find(candidate => candidate.key === key)!
      const value = details[key]

      if (field.type === 'text') {
        body[key] = String(value).trim()
      } else if (value !== null && value !== NO_REFERENCE) {
        body[key] = value
      }
    }

    return body
  }

  async function save(code: string) {
    const body = {
      ...detailBody(),
      ...Object.fromEntries(changed(code).map(key => [key, state[code]![key]]))
    }
    saving.value = code

    try {
      const saved = await $fetch<ContentItem>(endpoint.value, {
        method: 'PUT',
        body,
        headers: inLanguage(code)
      })

      toast.add({
        title: t('editor.toasts.saved', { code }),
        // The sample's confirmation, already in the language that was written.
        description: saved.message,
        icon: 'i-lucide-check',
        color: 'success'
      })

      // What was just sent is stored now: reload it rather than carry it over as an edit.
      for (const field of type.details) {
        details[field.key] = storedDetails.value[field.key]
      }
      state[code] = { ...stored.value[code] } as Record<string, string>
      await load()
      return true
    } catch (error) {
      toast.add({ title: t('editor.toasts.saveFailed', { code }), description: problemMessage(error), icon: 'i-lucide-triangle-alert', color: 'error' })
      return false
    } finally {
      saving.value = null
    }
  }

  /** Puts back what is stored, in `code` and in the details. */
  function reset(code: string) {
    for (const field of type.fields) {
      state[code]![field.key] = stored.value[code]?.[field.key] ?? ''
    }
    for (const field of type.details) {
      details[field.key] = storedDetails.value[field.key]
    }
  }

  /** Removes every stored field in `code`, so reads in it fall back. Not offered for the base language. */
  async function clear(code: string) {
    saving.value = code

    try {
      const result = await $fetch<{ message?: string }>(`${endpoint.value}/translations/${code}`, { method: 'DELETE' })

      toast.add({ title: t('editor.toasts.cleared', { code }), description: result.message, icon: 'i-lucide-eraser', color: 'success' })
      state[code] = { ...stored.value[code] } as Record<string, string>
      await load()
      return true
    } catch (error) {
      toast.add({ title: t('editor.toasts.clearFailed', { code }), description: problemMessage(error), icon: 'i-lucide-triangle-alert', color: 'error' })
      return false
    } finally {
      saving.value = null
    }
  }

  // Another item: no edit carries over to it.
  watch(endpoint, () => {
    for (const code of Object.keys(state)) {
      reset(code)
    }
    load()
  })
  onMounted(load)

  return {
    identifier, stored, state, storedDetails, details, pending, loadError, saving,
    load, save, reset, clear, dirty, translationDirty, detailsDirty, hasContent, fallback
  }
}
