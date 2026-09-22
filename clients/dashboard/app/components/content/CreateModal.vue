<script setup lang="ts">
import type { ContentItem, ContentType, DetailField } from '~/types'

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
const { t, tType, localizeType } = useI18n()

/** The type's labels in the dashboard's language. */
const text = computed(() => localizeType(props.type))

const form = useTemplateRef('form')

// Values differ per content type, and each control narrows its own at the point of use.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const state = reactive<Record<string, any>>({})
const language = ref('')
const saving = ref(false)
const problem = ref('')
const { options, load: loadOptions } = useReferenceOptions(() => props.type.details, language)

const direction = computed(() => find(language.value)?.direction ?? 'ltr')

const languageItems = computed(() => active.value.map(candidate => ({
  label: `${candidate.name} (${candidate.code})`,
  value: candidate.code
})))

function initialValue(field: DetailField) {
  return field.type === 'number' ? 0 : field.type === 'reference' ? NO_REFERENCE : ''
}

watch(open, (isOpen) => {
  if (!isOpen) {
    return
  }

  problem.value = ''
  language.value = baseLanguage || defaultLanguage.value?.code || ''

  for (const field of props.type.details) {
    state[field.key] = initialValue(field)
  }
  for (const field of props.type.fields) {
    state[field.key] = ''
  }

  loadOptions()
})

const validate = (values: Record<string, unknown>) => validateDetails(props.type.details, values)

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
  for (const field of props.type.details) {
    const value = state[field.key]

    if (field.type === 'reference') {
      if (value !== NO_REFERENCE) {
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
      title: tType(props.type, 'created'),
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
    :title="tType(type, 'new')"
    :description="t('create.description', { route: type.route })"
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

        <ContentDetailFields :fields="text.details" :state="state" :options="options" />

        <USeparator />

        <UFormField
          name="language"
          :label="t('create.writtenIn')"
          :description="baseLanguage ? t('create.baseDescription') : t('create.acceptDescription')"
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
          v-for="field in text.fields"
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
          :label="t('common.cancel')"
          color="neutral"
          variant="ghost"
          @click="open = false"
        />
        <UButton
          :form="`create-${type.route}`"
          type="submit"
          :label="tType(type, 'create')"
          :loading="saving"
        />
      </div>
    </template>
  </UModal>
</template>
