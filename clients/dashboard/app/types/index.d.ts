/** `ltr` or `rtl` — the values of the HTML `dir` attribute, as the API sends them. */
export type TextDirection = 'ltr' | 'rtl'

/** A language as `GET api/languages` returns it. */
export interface Language {
  /** Canonical code, e.g. `en`, `pt-BR`. The identity — it never changes. */
  code: string
  name: string
  direction: TextDirection
  isDefault: boolean
  isActive: boolean
}

/** Body of `POST api/languages`. Leaving `direction` out derives it from the code. */
export interface NewLanguage {
  code: string
  name: string
  direction?: TextDirection
  isActive: boolean
  isDefault: boolean
}

/** Body of `PUT api/languages/{code}` — a full replacement of everything but the code. */
export interface LanguageUpdate {
  name: string
  direction?: TextDirection
  isActive: boolean
}

/** Result of `DELETE api/languages/{code}/translations`. */
export interface PurgeResult {
  code: string
  deletedTranslations: number
}

/** A translatable property of a content type. */
export interface ContentField {
  /** camelCase name, as it appears in the entity's JSON. */
  key: string
  label: string
  /** Renders a textarea rather than a single-line input. */
  multiline?: boolean
}

/**
 * An entity with `[MultiLingual]` properties, as surfaced by the dashboard. The endpoints are
 * the application's own, not the library's, so each content type names the shape it has.
 */
export interface ContentType {
  /** Path under `api/`, e.g. `products`. */
  route: string
  /** C# class name, shown as the type's identity. */
  name: string
  label: string
  description: string
  icon: string
  /** Dashboard route for this type's tab. */
  to: string
  /** Untranslated property that identifies an item to a person, e.g. `sku`. */
  identifier: string
  /** The `[MultiLingual]` properties. */
  fields: ContentField[]
  /** Whether the API offers `DELETE api/{route}/{id}/translations/{code}`. */
  clearable: boolean
}

/** One entity as the content endpoints return it, resolved to one language. */
export interface ContentItem {
  id: number
  /** The language the API resolved the request to. */
  language: string
  /** Set by writes: a confirmation in the request's language. */
  message?: string
  [key: string]: unknown
}

/**
 * `GET api/{route}/{id}/translations`: language code → property name → value. Property names are
 * the C# ones (PascalCase). The base language, if there is one, is not in it: it is the row.
 */
export interface ContentTranslations {
  id: number
  translations: Record<string, Record<string, string | null>>
}

/** RFC 7807 problem details, with ASP.NET Core's validation `errors` when there are any. */
export interface ProblemDetails {
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
}
