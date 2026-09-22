import { plural, type ContentTypeMessages } from './types'

/**
 * The dashboard's own strings, in English. This file is the source: its shape is `Messages`, so
 * every other locale must have the same keys. `{name}` placeholders are filled in by `t()`, and a
 * `plural()` entry is picked by `count` with the language's plural rules.
 */
const en = {
  app: {
    title: 'DotNetBoost.MultiLingual — Dashboard',
    description: 'Manage languages and translate content through the DotNetBoost.MultiLingual REST API.'
  },

  common: {
    refresh: 'Refresh',
    cancel: 'Cancel',
    save: 'Save',
    reset: 'Reset',
    tryAgain: 'Try again',
    none: 'None',
    required: 'Required.',
    maxLength: 'At most {max} characters.',
    apiUnreachable: 'The API could not be reached.'
  },

  nav: {
    overview: 'Overview',
    languages: 'Languages',
    content: 'Content',
    apiReference: 'API reference',
    documentation: 'Documentation',
    goTo: 'Go to'
  },

  header: {
    language: 'Dashboard language'
  },

  appearance: {
    label: 'Appearance',
    theme: 'Theme',
    primary: 'Primary',
    neutral: 'Neutral',
    light: 'Light',
    dark: 'Dark',
    uiDocs: 'Nuxt UI documentation',
    github: 'GitHub repository'
  },

  overview: {
    connecting: 'Connecting…',
    connected: 'API connected',
    unreachable: 'API unreachable',
    introTitle: 'Content in every language, from one attribute',
    introDescription: 'This dashboard manages the languages that DotNetBoost.MultiLingual.API maps with MapLanguageEndpoints(), and edits the [MultiLingual] properties of the sample\'s entities one language at a time — each write goes out with that language in Accept-Language.',
    openApiReference: 'Open API reference',
    servedIn: 'Served in',
    items: plural({ one: '{count} item', other: '{count} items' }),
    stats: {
      languages: 'Languages',
      activeCount: '{count} active',
      totalCount: '{count} in total',
      defaultLanguage: 'Default language',
      defaultDetail: '{code} — reads fall back to it',
      noneConfigured: 'None configured',
      storageMode: 'Storage mode',
      baseLanguage: 'Base language: {name}',
      translationsTable: 'Translations table',
      baseDetail: '{code} lives in each entity\'s own columns; every other language in Translations',
      tableDetail: 'Every language lives in the shared Translations table'
    }
  },

  languages: {
    add: 'Add language',
    cardTitle: 'Languages content is served in',
    cardDescription: 'Exactly one is the default: reads fall back to it when the requested language has no translation. The code is a language\'s identity and never changes; its content is kept while it is inactive.',
    loadError: 'Could not load the languages',
    empty: 'No languages yet. The first one added becomes the default.',
    columns: {
      code: 'Code',
      name: 'Name',
      direction: 'Direction',
      role: 'Role',
      active: 'Active'
    },
    roles: {
      default: 'default',
      base: 'base'
    },
    baseTooltip: 'MultiLingualOptions.BaseLanguage: stored in each entity\'s own columns',
    activate: 'Activate {name}',
    deactivate: 'Deactivate {name}',
    actionsFor: 'Actions for {name}',
    actions: {
      edit: 'Edit',
      makeDefault: 'Make default',
      purge: 'Delete all translations',
      delete: 'Delete language'
    },
    confirmPurge: {
      title: 'Delete every {name} translation?',
      description: 'Removes every translation written in {code}, across all entities. Reads in it then fall back to the default language. The language itself stays.',
      baseNote: 'It is the base language, so its values are columns and are not affected.',
      label: 'Delete translations'
    },
    confirmDelete: {
      title: 'Delete {name}?',
      description: 'The API refuses while translations in it exist — delete those first, or deactivate the language to keep them.',
      label: 'Delete language'
    },
    form: {
      addTitle: 'Add a language',
      editTitle: 'Edit {name}',
      addDescription: 'Content can be written in it as soon as it exists; reads fall back until it is.',
      editDescription: 'A full replacement of the name, direction and active flag. The code cannot change.',
      codeDescription: 'ISO 639-1, optionally with a region. It is the language\'s identity and never changes.',
      codeRequired: 'Required, e.g. en, ar or pt-BR.',
      nameDescription: 'Usually in the language itself, as a language picker shows it: Español, العربية.',
      directionAddDescription: 'Derived from the runtime culture data when left alone: ar, he, fa and ur are rtl.',
      directionEditDescription: 'Choosing “Derive” re-derives it from the code.',
      directions: {
        auto: 'Derive from the code',
        ltr: 'Left to right (ltr)',
        rtl: 'Right to left (rtl)'
      },
      activeDescription: 'An inactive language keeps its content but is never served.',
      makeDefault: 'Make it the default',
      firstIsDefault: 'The first language is always the default.',
      demotesDefault: 'Demotes the current default.'
    },
    toasts: {
      refused: 'The API refused the change',
      added: '{name} added',
      addedDefault: '{code} is the default language.',
      addedWritable: 'Content can now be written in {code}.',
      saved: '{name} saved',
      inactive: 'Inactive: its content is kept, but never served.',
      promoted: '{name} is now the default',
      promotedDescription: 'Reads without a translation in the requested language fall back to it.',
      deleted: '{code} deleted',
      purged: plural({ one: '{count} translation deleted', other: '{count} translations deleted' }),
      purgedDescription: 'Reads in {code} now fall back until it is written again.'
    }
  },

  content: {
    selectRow: 'Select a row to edit it.',
    resolvedTo: 'resolved to',
    translatedInto: 'Translated into',
    coverageStored: '{name}: stored',
    coverageFallsBack: '{name}: falls back',
    deleteAria: 'Delete {id}',
    deleteDescription: 'It is removed in every language, with all of its translations.',
    deleteFailed: 'Delete failed',
    editorDescription: 'Edited one language at a time: PUT api/{route}/{id} with that language in Accept-Language.',

    /**
     * Phrases that name a content type. `types.{route}.{key}` overrides one for a single type — a
     * locale whose nouns have gender needs that — and these fill in for the rest. `{name}` is the
     * type's name (“Product”), `{noun}` the same in lower case, `{label}` its plural (“products”).
     */
    generic: {
      new: 'New {noun}',
      create: 'Create {noun}',
      created: '{name} created',
      deleted: '{name} deleted',
      deleteTitle: 'Delete {noun} {id}?',
      deleteLabel: 'Delete {noun}',
      empty: 'No {label} yet.',
      loadError: 'Could not load {label}',
      itemLoadError: 'Could not load this {noun}',
      itemTitle: '{name} {id}'
    }
  },

  create: {
    description: 'POST api/{route}. The translatable fields are written in one language; add the others afterwards from the editor.',
    writtenIn: 'Written in',
    baseDescription: 'The base language. The API only creates in it — every other language hangs off the base value.',
    acceptDescription: 'Sent as Accept-Language. Reads in the other languages fall back to this one until they are written.'
  },

  editor: {
    editingIn: 'Editing in',
    status: {
      base: 'base column',
      translated: 'translated',
      fallsBack: 'falls back'
    },
    detailsSeparator: 'Details — the same in every language',
    translatedSeparator: 'Translated — {name}',
    notStored: 'not stored',
    referenceLocked: 'Can be changed, not removed',
    clear: 'Clear translation',
    saveIn: 'Save in {name}',
    languagesTitle: 'Languages',
    languagesDescription: 'Pick the language to edit the translated fields in. Unsaved edits in each are kept until you save or reset them.',
    unsaved: 'unsaved',
    editing: 'Editing',
    editIn: 'Edit in {name}',
    detailsPending: 'Unsaved detail changes are saved with whichever language you save next.',
    footnote: 'Placeholders show what a read falls back to while a language has nothing stored. Inactive languages are not listed: nothing is served in them.',
    confirmClear: {
      title: 'Clear the {code} translation?',
      description: 'Every translated field of this item in that language is removed. Reads in it then fall back, as for a language never written.',
      label: 'Clear translation'
    },
    toasts: {
      saved: 'Saved in {code}',
      saveFailed: 'Save in {code} failed',
      cleared: '{code} translation cleared',
      clearFailed: 'Clearing {code} failed'
    }
  },

  error: {
    title: 'Page not found',
    description: 'We are sorry but this page could not be found.'
  },

  // English is what `utils/content.ts` says already, so nothing is overridden here.
  types: {} as Record<string, ContentTypeMessages>
}

export default en

export type Messages = typeof en
