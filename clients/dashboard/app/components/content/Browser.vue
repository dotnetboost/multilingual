<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { ContentItem, ContentTranslations, ContentType } from '~/types'

/**
 * Lists one content type, read in a language of the user's choosing — the list is exactly what a
 * client sending that `Accept-Language` receives — and shows which languages each item is
 * actually translated into. Selecting a row opens the editor.
 */
const props = defineProps<{
  type: ContentType
}>()

const toast = useToast()
const { active, defaultLanguage, isBase, ensureLoaded, loadError: languagesError } = useLanguages()

const readIn = ref('')
const items = ref<ContentItem[]>([])
/** Item id → codes of the languages it has something stored in. */
const coverage = ref<Record<number, string[]>>({})
const pending = ref(true)
const loadError = ref('')
const selected = ref<number | null>(null)
const creating = ref(false)
/** The item awaiting confirmation of its delete. */
const deleting = ref<ContentItem | null>(null)
const removing = ref(false)

/** The language the API actually answered in — not necessarily the one asked for. */
const resolved = computed(() => items.value[0]?.language)

const languageItems = computed(() => active.value.map(language => ({
  label: `${language.name} (${language.code})`,
  value: language.code
})))

async function load() {
  pending.value = true
  loadError.value = ''

  try {
    await ensureLoaded()
    if (languagesError.value) {
      throw new Error(languagesError.value)
    }

    readIn.value ||= defaultLanguage.value?.code ?? ''

    items.value = await $fetch<ContentItem[]>(`/api/${props.type.route}`, {
      headers: readIn.value ? inLanguage(readIn.value) : undefined
    })

    // One request per item. Fine for an admin screen over a sample's worth of rows; an
    // application with thousands would expose coverage from the API instead.
    const entries = await Promise.all(items.value.map(async (item) => {
      const { translations } = await $fetch<ContentTranslations>(`/api/${props.type.route}/${item.id}/translations`)
      const codes = Object.keys(translations).filter(code => Object.values(translations[code] ?? {}).some(value => value !== null))
      return [item.id, codes] as const
    }))

    coverage.value = Object.fromEntries(entries)
  } catch (error) {
    loadError.value = problemMessage(error)
  } finally {
    pending.value = false
  }
}

function covered(id: number, code: string) {
  return isBase(code) || !!coverage.value[id]?.includes(code)
}

const columns = computed<TableColumn<ContentItem>[]>(() => [
  { accessorKey: 'id', header: '#' },
  { accessorKey: props.type.identifier, header: humanizeKey(props.type.identifier) },
  ...props.type.fields.map(field => ({ accessorKey: field.key, header: field.label })),
  { id: 'coverage', header: 'Translated into' },
  { id: 'actions', header: '' }
])

const selectedItem = computed(() => items.value.find(item => item.id === selected.value))

/**
 * Deletes the item awaiting confirmation. Its translations go with it: the save interceptor
 * removes them in the same transaction, so the application's endpoint never mentions them.
 */
async function onDelete() {
  const item = deleting.value
  if (!item) {
    return
  }

  removing.value = true

  try {
    const result = await $fetch<{ message?: string }>(`/api/${props.type.route}/${item.id}`, { method: 'DELETE' })

    toast.add({ title: `${props.type.name} deleted`, description: result.message, icon: 'i-lucide-trash-2', color: 'success' })

    if (selected.value === item.id) {
      selected.value = null
    }
    deleting.value = null
    await load()
  } catch (error) {
    toast.add({ title: 'Delete failed', description: problemMessage(error), icon: 'i-lucide-triangle-alert', color: 'error' })
  } finally {
    removing.value = false
  }
}

/** Opens the new item in the editor, where its other languages are added. */
async function onCreated(id: number) {
  await load()
  selected.value = id
}

watch(readIn, (next, previous) => {
  if (previous) {
    load()
  }
})

onMounted(load)
</script>

<template>
  <div class="flex flex-col gap-4">
    <UPageCard
      :title="type.label"
      :description="type.description"
      variant="naked"
      orientation="horizontal"
    >
      <div class="flex items-center gap-2 lg:ms-auto">
        <USelect
          v-model="readIn"
          :items="languageItems"
          icon="i-lucide-languages"
          class="w-52"
          aria-label="Read in"
        />
        <UButton
          icon="i-lucide-refresh-cw"
          color="neutral"
          variant="ghost"
          :loading="pending"
          aria-label="Refresh"
          @click="load"
        />
        <UButton
          icon="i-lucide-plus"
          :label="`New ${type.name.toLowerCase()}`"
          @click="creating = true"
        />
      </div>
    </UPageCard>

    <UAlert
      v-if="loadError"
      color="error"
      variant="subtle"
      icon="i-lucide-plug-zap"
      :title="`Could not load ${type.label.toLowerCase()}`"
      :description="loadError"
      :actions="[{ label: 'Try again', color: 'neutral', variant: 'outline', onClick: load }]"
    />

    <div v-else class="flex flex-wrap items-center gap-2 text-sm text-muted">
      <UBadge
        color="neutral"
        variant="subtle"
        class="font-mono"
        :label="`GET api/${type.route}`"
      />
      <UBadge
        color="neutral"
        variant="outline"
        class="font-mono"
        :label="`Accept-Language: ${readIn}`"
      />
      <template v-if="resolved && resolved !== readIn">
        <UIcon name="i-lucide-arrow-right" />
        <span>resolved to <span class="font-mono">{{ resolved }}</span></span>
      </template>
      <span class="ms-auto">Select a row to edit it in every language.</span>
    </div>

    <UTable
      :data="items"
      :columns="columns"
      :loading="pending"
      :empty="`No ${type.label.toLowerCase()} yet.`"
      class="rounded-lg border border-default"
      :ui="{ tr: 'cursor-pointer', td: 'align-top' }"
      @select="(_, row) => selected = row.original.id"
    >
      <template #[`${type.identifier}-cell`]="{ row }">
        <span class="font-mono text-highlighted">{{ row.original[type.identifier] }}</span>
      </template>

      <template v-for="field in type.fields" :key="field.key" #[`${field.key}-cell`]="{ row }">
        <span dir="auto" class="block max-w-sm whitespace-normal" :class="field.multiline ? 'text-muted line-clamp-2' : 'text-highlighted'">
          {{ row.original[field.key] ?? '—' }}
        </span>
      </template>

      <template #coverage-cell="{ row }">
        <div class="flex flex-wrap gap-1">
          <UTooltip
            v-for="language in active"
            :key="language.code"
            :text="covered(row.original.id, language.code) ? `${language.name}: stored` : `${language.name}: falls back`"
          >
            <UBadge
              :color="covered(row.original.id, language.code) ? 'success' : 'neutral'"
              :variant="covered(row.original.id, language.code) ? 'subtle' : 'outline'"
              size="sm"
              class="font-mono"
              :label="language.code"
            />
          </UTooltip>
        </div>
      </template>

      <template #actions-cell="{ row }">
        <div class="flex justify-end">
          <UButton
            icon="i-lucide-trash-2"
            color="neutral"
            variant="ghost"
            :aria-label="`Delete ${row.original[type.identifier]}`"
            @click.stop="deleting = row.original"
          />
        </div>
      </template>
    </UTable>

    <ContentCreateModal v-model:open="creating" :type="type" @created="onCreated" />

    <ConfirmModal
      :open="deleting !== null"
      :title="`Delete ${type.name.toLowerCase()} ${deleting?.[type.identifier] ?? ''}?`"
      :description="['It is removed in every language, with all of its translations.', type.deleteNote].filter(Boolean).join(' ')"
      :confirm-label="`Delete ${type.name.toLowerCase()}`"
      :loading="removing"
      @update:open="!$event && (deleting = null)"
      @confirm="onDelete"
    />

    <USlideover
      :open="selected !== null"
      :title="selectedItem ? `${type.name} ${selectedItem[type.identifier]}` : type.name"
      :description="`Each language is saved on its own: PUT api/${type.route}/${selected} with that language in Accept-Language.`"
      :ui="{ content: 'sm:max-w-xl' }"
      @update:open="!$event && (selected = null)"
    >
      <template #body>
        <ContentEditor
          v-if="selected !== null"
          :id="selected"
          :type="type"
          @saved="load"
        />
      </template>
    </USlideover>
  </div>
</template>
