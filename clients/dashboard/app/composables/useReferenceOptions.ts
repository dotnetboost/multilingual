import type { ContentItem, DetailField } from '~/types'

export interface ReferenceOption {
  label: string
  value: number
  disabled?: boolean
}

/**
 * The choices of every `reference` detail — a product's category, say — read in `language`, so
 * each item is labelled the way a client in that language sees it. "None" comes first.
 */
export function useReferenceOptions(fields: MaybeRefOrGetter<DetailField[]>, language: MaybeRefOrGetter<string>) {
  /** Reference field key → the items it can point at. */
  const options = ref<Record<string, ReferenceOption[]>>({})
  const { t } = useI18n()

  async function load() {
    for (const field of toValue(fields).filter(candidate => candidate.type === 'reference')) {
      const target = findContentType(field.reference!)
      const code = toValue(language)

      try {
        const items = await $fetch<ContentItem[]>(`/api/${field.reference}`, { headers: code ? inLanguage(code) : undefined })

        options.value[field.key] = [
          { label: t('common.none'), value: NO_REFERENCE },
          ...items.map(item => ({
            label: String(item.name ?? (target ? item[target.identifier] : item.id)),
            value: item.id
          }))
        ]
      } catch {
        options.value[field.key] = [{ label: t('common.none'), value: NO_REFERENCE }]
      }
    }
  }

  return { options, load }
}
