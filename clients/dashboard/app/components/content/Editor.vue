<script setup lang="ts">
import type { ContentType } from '~/types'

/** One item in every active language, each saved on its own. */
const props = defineProps<{
  type: ContentType
  id: number
}>()

const emit = defineEmits<{
  saved: []
}>()

const { active, isBase } = useLanguages()

const {
  state, stored, pending, loadError, saving,
  load, save, reset, clear, dirty, hasContent, fallback
} = useContentItem(props.type, () => props.id)

const clearing = ref<string | null>(null)

async function onSave(code: string) {
  if (await save(code)) {
    emit('saved')
  }
}

async function onClear() {
  if (clearing.value && await clear(clearing.value)) {
    emit('saved')
  }
  clearing.value = null
}

/** What a read falls back to, shown in an empty field of a language that falls back at all. */
function placeholder(code: string, key: string) {
  const isDefault = active.value.find(candidate => candidate.code === code)?.isDefault
  return isDefault || isBase(code) ? undefined : fallback(key)
}

function status(code: string) {
  if (isBase(code)) {
    return { label: 'base column', color: 'info' as const, icon: 'i-lucide-database' }
  }

  return hasContent(code)
    ? { label: 'translated', color: 'success' as const, icon: 'i-lucide-check' }
    : { label: 'falls back', color: 'warning' as const, icon: 'i-lucide-corner-down-left' }
}
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

    <template v-else-if="pending">
      <USkeleton v-for="n in 3" :key="n" class="h-48 w-full" />
    </template>

    <UForm
      v-for="language in active"
      v-else
      :id="`content-${language.code}`"
      :key="language.code"
      :state="state[language.code] ?? {}"
      @submit="onSave(language.code)"
    >
      <UPageCard variant="subtle" :ui="{ container: 'gap-y-4' }">
        <template #header>
          <div class="flex flex-wrap items-center gap-2">
            <span class="font-semibold text-highlighted" :dir="language.direction">{{ language.name }}</span>
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
        </template>

        <UFormField
          v-for="field in type.fields"
          :key="field.key"
          :name="field.key"
          :label="field.label"
          :hint="stored[language.code]?.[field.key] === undefined && !isBase(language.code) ? 'not stored' : undefined"
        >
          <UTextarea
            v-if="field.multiline"
            v-model="state[language.code]![field.key]"
            :dir="language.direction"
            :placeholder="placeholder(language.code, field.key)"
            :rows="2"
            autoresize
            class="w-full"
          />
          <UInput
            v-else
            v-model="state[language.code]![field.key]"
            :dir="language.direction"
            :placeholder="placeholder(language.code, field.key)"
            autocomplete="off"
            class="w-full"
          />
        </UFormField>

        <div class="flex items-center gap-2">
          <UButton
            v-if="type.clearable && !isBase(language.code) && hasContent(language.code)"
            label="Clear translation"
            icon="i-lucide-eraser"
            color="error"
            variant="ghost"
            :disabled="saving !== null"
            @click="clearing = language.code"
          />
          <div class="ms-auto flex items-center gap-2">
            <UButton
              label="Reset"
              color="neutral"
              variant="ghost"
              :disabled="!dirty(language.code) || saving !== null"
              @click="reset(language.code)"
            />
            <UButton
              :form="`content-${language.code}`"
              type="submit"
              :label="`Save ${language.code}`"
              color="neutral"
              :loading="saving === language.code"
              :disabled="!dirty(language.code) || (saving !== null && saving !== language.code)"
            />
          </div>
        </div>
      </UPageCard>
    </UForm>

    <p v-if="!pending && !loadError" class="text-sm text-muted">
      Placeholders show what a read falls back to while a language has nothing stored. Inactive
      languages are not listed: nothing is served in them.
    </p>

    <ConfirmModal
      :open="clearing !== null"
      :title="`Clear the ${clearing} translation?`"
      description="Every translated field of this item in that language is removed. Reads in it then fall back, as for a language never written."
      confirm-label="Clear translation"
      :loading="saving !== null"
      @update:open="!$event && (clearing = null)"
      @confirm="onClear"
    />
  </div>
</template>
