<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { ContentItem, ContentType } from '~/types'

/**
 * Creates one item: its untranslated properties, and its translatable ones in a single language —
 * `POST api/{route}` with that language in `Accept-Language`. The other languages are added
 * afterwards, from the editor.
 *
 * With a base language configured the API only accepts a create in that language (every
 * translation hangs off the base value), so it is fixed; without one, any active language will do.
 */
const props = defineProps<{
  type: ContentType
}>()

const open = defineModel<boolean>('open', { default: false })

const emit = defineEmits<{
  created: [id: number]
}>()

const toast = useToast()
const { baseLanguage } = useRuntimeConfig().public
const { active, defaultLanguage, find } = useLanguages()

const form = useTemplateRef('form')

/** The sentinel a `reference` picker holds for "none": Select items cannot carry an empty value. */
const NONE = 0

// Values differ per content type, and each control narrows its own at the point of use.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const state = reactive<Record<string, any>>({})
const language = ref('')
const saving = ref(false)
const problem = ref('')
/** Reference field key → the items it can point at. */
const options = ref<Record<string, { label: string, value: number }[]>>({})

const direction = computed(() => find(language.value)?.direction ?? 'ltr')

const languageItems = computed(() => active.value.map(candidate => ({
  label: `${candidate.name} (${candidate.code})`,
  value: candidate.code
})))

function initialValue(field: ContentType['create'][number]) {
  return field.type === 'number' ? 0 : field.type === 'reference' ? NONE : ''
}

async function loadOptions() {
  for (const field of props.type.create.filter(candidate => candidate.type === 'reference')) {
    const target = findContentType(field.reference!)

    try {
      const items = await $fetch<ContentItem[]>(`/api/${field.reference}`, { headers: inLanguage(language.value) })

      options.value[field.key] = [
        { label: 'None', value: NONE },
        ...items.map(item => ({
          label: String(item.name ?? (target ? item[target.identifier] : item.id)),
          value: item.id
        }))
      ]
    } catch {
      options.value[field.key] = [{ label: 'None', value: NONE }]
    }
  }
}

watch(open, (isOpen) => {
  if (!isOpen) {
    return
  }

  problem.value = ''
  language.value = baseLanguage || defaultLanguage.value?.code || ''

  for (const field of props.type.create) {
    state[field.key] = initialValue(field)
  }
  for (const field of props.type.fields) {
    state[field.key] = ''
  }

  loadOptions()
})

// The API validates too, and its word is final. These only save a round trip for the obvious.
function validate(values: Record<string, unknown>): FormError[] {
  const errors: FormError[] = []

  for (const field of props.type.create.filter(candidate => candidate.type === 'text')) {
    const value = String(values[field.key] ?? '').trim()

    if (field.required && !value) {
      errors.push({ name: field.key, message: 'Required.' })
    } else if (field.maxLength && value.length > field.maxLength) {
      errors.push({ name: field.key, message: `At most ${field.maxLength} characters.` })
    }
  }

  return errors
}

/** Enter submits from a single-line input; in a textarea it is a new line, and mid-composition it is the IME's. */
function onEnter(event: KeyboardEvent) {
  if (event.isComposing || event.target instanceof HTMLTextAreaElement) {
    return
  }

  event.preventDefault()
  form.value?.submit()
}

async function submit() {
  saving.value = true
  problem.value = ''

  const body: Record<string, unknown> = {}
  for (const field of props.type.create) {
    const value = state[field.key]

    if (field.type === 'reference') {
      if (value !== NONE) {
        body[field.key] = value
      }
    } else {
      body[field.key] = field.type === 'text' ? String(value).trim() : value
    }
  }
  // An empty field is left out rather than stored as an empty value: reads then fall back.
  for (const field of props.type.fields) {
    if (state[field.key]) {
      body[field.key] = state[field.key]
    }
  }

  try {
    const created = await $fetch<ContentItem>(`/api/${props.type.route}`, {
      method: 'POST',
      body,
      headers: inLanguage(language.value)
    })

    toast.add({
      title: `${props.type.name} created`,
      // The sample's confirmation, in the language it was created in.
      description: created.message,
      icon: 'i-lucide-check',
      color: 'success'
    })

    open.value = false
    emit('created', created.id)
  } catch (error) {
    problem.value = problemMessage(error)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UModal
    v-model:open="open"
    :title="`New ${type.name.toLowerCase()}`"
    :description="`POST api/${type.route}. The translatable fields are written in one language; add the others afterwards from the editor.`"
  >
    <template #body>
      <UForm
        :id="`create-${type.route}`"
        ref="form"
        :state="state"
        :validate="validate"
        class="flex flex-col gap-4"
        @submit="submit"
        @keydown.enter="onEnter"
      >
        <UAlert
          v-if="problem"
          color="error"
          variant="subtle"
          icon="i-lucide-triangle-alert"
          :description="problem"
        />

        <UFormField
          v-for="field in type.create"
          :key="field.key"
          :name="field.key"
          :label="field.label"
          :description="field.description"
          :required="field.required"
        >
          <UInputNumber
            v-if="field.type === 'number'"
            v-model="state[field.key]"
            :min="field.min"
            :step="field.step"
            class="w-full sm:w-56"
          />

          <USelect
            v-else-if="field.type === 'reference'"
            v-model="state[field.key]"
            :items="options[field.key] ?? []"
            class="w-full"
          />

          <UInput
            v-else
            v-model="state[field.key]"
            autocomplete="off"
            class="w-full font-mono"
          />
        </UFormField>

        <USeparator />

        <UFormField
          name="language"
          label="Written in"
          :description="baseLanguage
            ? `The base language. The API only creates in it — every other language hangs off the base value.`
            : 'Sent as Accept-Language. Reads in the other languages fall back to this one until they are written.'"
        >
          <USelect
            v-model="language"
            :items="languageItems"
            :disabled="!!baseLanguage"
            icon="i-lucide-languages"
            class="w-full"
            @update:model-value="loadOptions"
          />
        </UFormField>

        <UFormField
          v-for="field in type.fields"
          :key="field.key"
          :name="field.key"
          :label="field.label"
        >
          <UTextarea
            v-if="field.multiline"
            v-model="state[field.key]"
            :dir="direction"
            :rows="2"
            autoresize
            class="w-full"
          />
          <UInput
            v-else
            v-model="state[field.key]"
            :dir="direction"
            autocomplete="off"
            class="w-full"
          />
        </UFormField>
      </UForm>
    </template>

    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton
          label="Cancel"
          color="neutral"
          variant="ghost"
          @click="open = false"
        />
        <UButton
          :form="`create-${type.route}`"
          type="submit"
          :label="`Create ${type.name.toLowerCase()}`"
          :loading="saving"
        />
      </div>
    </template>
  </UModal>
</template>
