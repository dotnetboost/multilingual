import type { FormError } from '@nuxt/ui'
import type { ContentType, DetailField } from '~/types'

/**
 * The content types the dashboard edits, one tab each. The labels and descriptions here are the
 * English ones; `types.{route}` in a locale translates them (see `useI18n().localizeType`).
 *
 * Unlike the language endpoints, these endpoints belong to the application — here, the ones in
 * samples/SampleApp. Each entry says where the type lives and which of its properties are
 * `[MultiLingual]`. The dashboard assumes the shape the sample uses: `GET api/{route}` and
 * `GET api/{route}/{id}` read in the `Accept-Language` language, `PUT api/{route}/{id}` writes the
 * supplied translatable fields in it (and the supplied `details`, which are the same in every
 * language), and `GET api/{route}/{id}/translations` returns every stored language.
 */
export const contentTypes: ContentType[] = [
  {
    route: 'products',
    name: 'Product',
    label: 'Products',
    description: 'Property-level [MultiLingual]: Name and Description are translated, Sku and Price are ordinary columns.',
    icon: 'i-lucide-package',
    to: '/content',
    identifier: 'sku',
    fields: [
      { key: 'name', label: 'Name' },
      { key: 'description', label: 'Description', multiline: true }
    ],
    clearable: true,
    details: [
      { key: 'sku', label: 'SKU', description: 'Unique, up to 64 characters. The same in every language.', type: 'text', required: true, maxLength: 64 },
      { key: 'price', label: 'Price', type: 'number', min: 0, step: 0.01 },
      { key: 'categoryId', label: 'Category', type: 'reference', reference: 'categories' }
    ]
  },
  {
    route: 'categories',
    name: 'Category',
    label: 'Categories',
    description: 'Class-level [MultiLingual]: every string property is translated, except Slug, which opts out with [NotMultiLingual].',
    icon: 'i-lucide-folder-tree',
    to: '/content/categories',
    identifier: 'slug',
    fields: [
      { key: 'name', label: 'Name' },
      { key: 'description', label: 'Description', multiline: true }
    ],
    clearable: false,
    details: [
      { key: 'slug', label: 'Slug', description: 'Unique, up to 128 characters. Opted out with [NotMultiLingual], so it is the same in every language.', type: 'text', required: true, maxLength: 128 },
      { key: 'displayOrder', label: 'Display order', type: 'number', step: 1 }
    ],
    deleteNote: 'Its products are kept, with no category.'
  }
]

/** The value a `reference` picker holds for "none": Select items cannot carry an empty value. */
export const NO_REFERENCE = 0

export function findContentType(route: string): ContentType | undefined {
  return contentTypes.find(type => type.route === route)
}

/**
 * The stored value of `key` in one language's entry of a translations response, whose property
 * names are the C# ones. `undefined` when nothing is stored — reads then fall back.
 */
export function storedValue(entry: Record<string, string | null> | undefined, key: string) {
  if (!entry) {
    return undefined
  }

  const property = Object.keys(entry).find(name => name.toLowerCase() === key.toLowerCase())
  return property === undefined ? undefined : entry[property]
}

/**
 * Checks the text details a form holds. The API validates too, and its word is final: these only
 * save a round trip for the obvious.
 */
export function validateDetails(fields: DetailField[], values: Record<string, unknown>): FormError[] {
  const { t } = useI18n()
  const errors: FormError[] = []

  for (const field of fields.filter(candidate => candidate.type === 'text')) {
    const value = String(values[field.key] ?? '').trim()

    if (field.required && !value) {
      errors.push({ name: field.key, message: t('common.required') })
    } else if (field.maxLength && value.length > field.maxLength) {
      errors.push({ name: field.key, message: t('common.maxLength', { max: field.maxLength }) })
    }
  }

  return errors
}

/** `displayOrder` -> `Display Order`. Column header for untranslated properties. */
export function humanizeKey(key: string): string {
  const spaced = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  return spaced.charAt(0).toUpperCase() + spaced.slice(1)
}
