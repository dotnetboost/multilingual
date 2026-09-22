<script setup lang="ts">
import type { ContentType } from '~/types'

/**
 * One item, edited one language at a time: its details, the same in every language, and its
 * translatable fields in the language being edited. The list below switches languages; unsaved
 * edits in each are kept until saved or reset.
 */
const props = defineProps<{
  type: ContentType
  id: number
}>()

const emit = defineEmits<{
  saved: []
}>()

const { active, defaultLanguage, find, isBase } = useLanguages()
const { baseLanguage } = useRuntimeConfig().public

const {
  state, stored, storedDetails, details, loadError, saving,
  load, save, reset, clear, dirty, translationDirty, detailsDirty, hasContent, fallback
} = useContentItem(props.type, () => props.id)

/** The language picked from the list; until then, the base language or else the default. */
const chosen = ref<string | null>(null)

const editing = computed(() => {
  const codes = active.value.map(language => language.code)
  const candidates = [chosen.value, baseLanguage, defaultLanguage.value?.code, codes[0]]
  return candidates.find(code => code && codes.includes(code)) ?? ''
})

const language = computed(() => find(editing.value))

const { options, load: loadOptions } = useReferenceOptions(() => props.type.details, editing)

const clearing = ref(false)

const validate = (values: Record<string, unknown>) => validateDetails(props.type.details, values)

async function onSave() {
  if (await save(editing.value)) {
    emit('saved')
  }
}

async function onClear() {
  if (await clear(editing.value)) {
    emit('saved')
  }
  clearing.value = false
}

function edit(code: string) {
  chosen.value = code
  document.getElementById(`content-editor-${props.type.route}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

/** What a read falls back to, shown in an empty field of a language that falls back at all. */
function placeholder(key: string) {
  return language.value?.isDefault || isBase(editing.value) ? undefined : fallback(key)
}

function status(code: string) {
  if (isBase(code)) {
    return { label: 'base column', color: 'info' as const, icon: 'i-lucide-database' }
  }

  return hasContent(code)
    ? { label: 'translated', color: 'success' as const, icon: 'i-lucide-check' }
    : { label: 'falls back', color: 'warning' as const, icon: 'i-lucide-corner-down-left' }
}

watch(editing, loadOptions, { immediate: true })
</script>

<template>
  <div class="flex flex-col gap-4">
    <UAlert
      v-if="loadError"
      color="error"
      variant="subtle"
      icon="i-lucide-plug-zap"
      :title="`Could not load this ${type.name.toLowerCase()}`"
      :description="loadError"
      :actions="[{ label: 'Try again', color: 'neutral', variant: 'outline', onClick: load }]"
    />

    <template v-else-if="language && state[editing]">
      <UForm
        :id="`content-editor-${type.route}`"
        :state="details"
        :validate="validate"
        class="scroll-mt-4"
        @submit="onSave"
      >
        <UPageCard variant="subtle" :ui="{ container: 'gap-y-4' }">
          <template #header>
            <div class="flex flex-col gap-1">
              <span class="text-xs font-medium uppercase tracking-wide text-muted">Editing in</span>
              <div class="flex flex-wrap items-center gap-2">
                <span class="text-lg font-semibold text-highlighted" :dir="language.direction">{{ language.name }}</span>
                <UBadge
                  color="neutral"
                  variant="subtle"
                  class="font-mono"
                  :label="language.code"
                />
                <UBadge
                  v-if="language.isDefault"
                  color="primary"
                  variant="subtle"
                  label="default"
                />
                <UBadge variant="subtle" v-bind="status(language.code)" />
              </div>
            </div>
          </template>

          <USeparator label="Details — the same in every language" :ui="{ label: 'text-muted' }" />

          <ContentDetailFields
            :fields="type.details"
            :state="details"
            :options="options"
            :stored="storedDetails"
          />

          <USeparator :label="`Translated — ${language.name}`" :ui="{ label: 'text-muted' }" />

          <UFormField
            v-for="field in type.fields"
            :key="field.key"
            :name="field.key"
            :label="field.label"
            :hint="stored[editing]?.[field.key] === undefined && !isBase(editing) ? 'not stored' : undefined"
          >
            <UTextarea
              v-if="field.multiline"
              v-model="state[editing]![field.key]"
              :dir="language.direction"
              :placeholder="placeholder(field.key)"
              :rows="2"
              autoresize
              class="w-full"
            />
            <UInput
              v-else
              v-model="state[editing]![field.key]"
              :dir="language.direction"
              :placeholder="placeholder(field.key)"
              autocomplete="off"
              class="w-full"
            />
          </UFormField>

          <div class="flex items-center gap-2">
            <UButton
              v-if="type.clearable && !isBase(editing) && hasContent(editing)"
              label="Clear translation"
              icon="i-lucide-eraser"
              color="error"
              variant="ghost"
              :disabled="saving !== null"
              @click="clearing = true"
            />
            <div class="ms-auto flex items-center gap-2">
              <UButton
                label="Reset"
                color="neutral"
                variant="ghost"
                :disabled="!dirty(editing) || saving !== null"
                @click="reset(editing)"
              />
              <UButton
                :form="`content-editor-${type.route}`"
                type="submit"
                :label="`Save in ${language.name}`"
                :loading="saving === editing"
                :disabled="!dirty(editing) || (saving !== null && saving !== editing)"
              />
            </div>
          </div>
        </UPageCard>
      </UForm>

      <UPageCard
        title="Languages"
        description="Pick the language to edit the translated fields in. Unsaved edits in each are kept until you save or reset them."
        variant="naked"
      >
        <ul class="divide-y divide-default rounded-lg border border-default">
          <li
            v-for="candidate in active"
            :key="candidate.code"
            class="flex flex-wrap items-center gap-2 px-3 py-2"
            :class="candidate.code === editing ? 'bg-elevated/50' : undefined"
          >
            <span class="font-medium text-highlighted" :dir="candidate.direction">{{ candidate.name }}</span>
            <UBadge
              color="neutral"
              variant="subtle"
              size="sm"
              class="font-mono"
              :label="candidate.code"
            />
            <UBadge
              v-if="candidate.isDefault"
              color="primary"
              variant="subtle"
              size="sm"
              label="default"
            />
            <UBadge variant="subtle" size="sm" v-bind="status(candidate.code)" />
            <UBadge
              v-if="translationDirty(candidate.code)"
              color="warning"
              variant="outline"
              size="sm"
              label="unsaved"
            />

            <UButton
              v-if="candidate.code === editing"
              class="ms-auto"
              label="Editing"
              icon="i-lucide-pencil"
              color="neutral"
              variant="soft"
              size="sm"
              disabled
            />
            <UButton
              v-else
              class="ms-auto"
              :label="`Edit in ${candidate.name}`"
              icon="i-lucide-languages"
              color="neutral"
              variant="outline"
              size="sm"
              @click="edit(candidate.code)"
            />
          </li>
        </ul>
      </UPageCard>

      <p class="text-sm text-muted">
        <template v-if="detailsDirty">
          Unsaved detail changes are saved with whichever language you save next.
        </template>
        Placeholders show what a read falls back to while a language has nothing stored. Inactive
        languages are not listed: nothing is served in them.
      </p>
    </template>

    <template v-else>
      <USkeleton v-for="n in 3" :key="n" class="h-32 w-full" />
    </template>

    <ConfirmModal
      :open="clearing"
      :title="`Clear the ${editing} translation?`"
      description="Every translated field of this item in that language is removed. Reads in it then fall back, as for a language never written."
      confirm-label="Clear translation"
      :loading="saving !== null"
      @update:open="clearing = $event"
      @confirm="onClear"
    />
  </div>
</template>
