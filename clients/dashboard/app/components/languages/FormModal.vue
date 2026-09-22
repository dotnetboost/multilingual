<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { Language, TextDirection } from '~/types'

/**
 * Adds a language, or edits one when `language` is given. The code is the language's identity,
 * so it can only be chosen on the way in.
 */
const props = defineProps<{
  language?: Language
}>()

const open = defineModel<boolean>('open', { default: false })

const form = useTemplateRef('form')

const { languages, create, update, busy } = useLanguages()
const { t } = useI18n()

type Direction = TextDirection | 'auto'

const state = reactive({
  code: '',
  name: '',
  direction: 'auto' as Direction,
  isActive: true,
  isDefault: false
})

const editing = computed(() => !!props.language)
const first = computed(() => !editing.value && languages.value.length === 0)

const directions = computed(() => [
  { label: t('languages.form.directions.auto'), value: 'auto' },
  { label: t('languages.form.directions.ltr'), value: 'ltr' },
  { label: t('languages.form.directions.rtl'), value: 'rtl' }
])

watch(open, (isOpen) => {
  if (!isOpen) {
    return
  }

  Object.assign(state, props.language
    ? { code: props.language.code, name: props.language.name, direction: props.language.direction, isActive: props.language.isActive, isDefault: props.language.isDefault }
    : { code: '', name: '', direction: 'auto', isActive: true, isDefault: false })
})

// The API validates too, and its word is final. These only save a round trip for the obvious.
function validate(values: typeof state): FormError[] {
  const errors: FormError[] = []

  if (!values.code.trim()) {
    errors.push({ name: 'code', message: t('languages.form.codeRequired') })
  }

  if (!values.name.trim()) {
    errors.push({ name: 'name', message: t('common.required') })
  } else if (values.name.trim().length > 128) {
    errors.push({ name: 'name', message: t('common.maxLength', { max: 128 }) })
  }

  return errors
}

/** Enter submits, except mid-composition, where it belongs to the input method. */
function onEnter(event: KeyboardEvent) {
  if (event.isComposing) {
    return
  }

  event.preventDefault()
  form.value?.submit()
}

async function submit() {
  const direction = state.direction === 'auto' ? undefined : state.direction

  const saved = props.language
    ? await update(props.language.code, { name: state.name.trim(), direction, isActive: state.isActive })
    : await create({ code: state.code.trim(), name: state.name.trim(), direction, isActive: state.isActive, isDefault: state.isDefault })

  if (saved) {
    open.value = false
  }
}
</script>

<template>
  <UModal
    v-model:open="open"
    :title="editing ? t('languages.form.editTitle', { name: language?.name }) : t('languages.form.addTitle')"
    :description="editing ? t('languages.form.editDescription') : t('languages.form.addDescription')"
  >
    <template #body>
      <!-- The submit button sits in the modal footer, outside the form, so Enter is wired up here. -->
      <UForm
        id="language-form"
        ref="form"
        :state="state"
        :validate="validate"
        class="flex flex-col gap-4"
        @submit="submit"
        @keydown.enter="onEnter"
      >
        <UFormField
          name="code"
          :label="t('languages.columns.code')"
          :description="t('languages.form.codeDescription')"
        >
          <UInput
            v-model="state.code"
            placeholder="es"
            :disabled="editing"
            autocomplete="off"
            class="w-full font-mono"
          />
        </UFormField>

        <UFormField
          name="name"
          :label="t('languages.columns.name')"
          :description="t('languages.form.nameDescription')"
        >
          <UInput
            v-model="state.name"
            :dir="state.direction === 'auto' ? 'auto' : state.direction"
            placeholder="Español"
            autocomplete="off"
            class="w-full"
          />
        </UFormField>

        <UFormField
          name="direction"
          :label="t('languages.columns.direction')"
          :description="editing ? t('languages.form.directionEditDescription') : t('languages.form.directionAddDescription')"
        >
          <USelect
            v-model="state.direction"
            :items="directions"
            class="w-full"
          />
        </UFormField>

        <USeparator />

        <UFormField
          name="isActive"
          :label="t('languages.columns.active')"
          :description="t('languages.form.activeDescription')"
          class="flex justify-between items-center gap-4"
        >
          <USwitch v-model="state.isActive" :disabled="language?.isDefault" />
        </UFormField>

        <UFormField
          v-if="!editing"
          name="isDefault"
          :label="t('languages.form.makeDefault')"
          :description="first ? t('languages.form.firstIsDefault') : t('languages.form.demotesDefault')"
          class="flex justify-between items-center gap-4"
        >
          <USwitch :model-value="first || state.isDefault" :disabled="first" @update:model-value="state.isDefault = $event" />
        </UFormField>

        <!-- The visible submit button sits in the modal footer, outside the form; this one lets Enter submit. -->
        <button
          type="submit"
          class="sr-only"
          tabindex="-1"
          aria-hidden="true"
        />
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
          form="language-form"
          type="submit"
          :label="editing ? t('common.save') : t('languages.add')"
          :loading="busy !== null"
        />
      </div>
    </template>
  </UModal>
</template>
