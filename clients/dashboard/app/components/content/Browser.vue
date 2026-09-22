<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { ContentItem, ContentTranslations, ContentType } from '~/types'

/**
 * Lists one content type, read in the dashboard's language — picked in the header, so the list is
 * exactly what a client sending that `Accept-Language` receives — and shows which languages each
 * item is actually translated into. Selecting a row opens the editor.
 */
const props = defineProps<{
  type: ContentType
}>()

const toast = useToast()
const { active, isBase, ensureLoaded, loadError: languagesError } = useLanguages()
const { code: readIn, dir, t, tType, localizeType } = useI18n()

/** The type's labels in the dashboard's language. */
const text = computed(() => localizeType(props.type))
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

async function load() {
  pending.value = true
  loadError.value = ''

  try {
    await ensureLoaded()
    if (languagesError.value) {
      throw new Error(languagesError.value)
    }

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
  {
    accessorKey: props.type.identifier,
    header: text.value.details.find(field => field.key === props.type.identifier)?.label ?? humanizeKey(props.type.identifier)
  },
  ...text.value.fields.map(field => ({ accessorKey: field.key, header: field.label })),
  { id: 'coverage', header: t('content.translatedInto') },
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

    toast.add({ title: tType(props.type, 'deleted'), description: result.message, icon: 'i-lucide-trash-2', color: 'success' })

    if (selected.value === item.id) {
      selected.value = null
    }
    deleting.value = null
    await load()
  } catch (error) {
    toast.add({ title: t('content.deleteFailed'), description: problemMessage(error), icon: 'i-lucide-triangle-alert', color: 'error' })
  } finally {
    removing.value = false
  }
}

/** Opens the new item in the editor, where its other languages are added. */
async function onCreated(id: number) {
  await load()
  selected.value = id
}

// Picking another language in the header reads the list again, in it.
watch(readIn, load)

onMounted(load)
</script>

<template>
  <div class="flex flex-col gap-4">
    <UPageCard
      :title="text.label"
      :description="text.description"
      variant="naked"
      orientation="horizontal"
    >
      <div class="flex items-center gap-2 lg:ms-auto">
        <UButton
          icon="i-lucide-refresh-cw"
          color="neutral"
          variant="ghost"
          :loading="pending"
          :aria-label="t('common.refresh')"
          @click="load"
        />
        <UButton
          icon="i-lucide-plus"
          :label="tType(type, 'new')"
          @click="creating = true"
        />
      </div>
    </UPageCard>

    <UAlert
      v-if="loadError"
      color="error"
      variant="subtle"
      icon="i-lucide-plug-zap"
      :title="tType(type, 'loadError')"
      :description="loadError"
      :actions="[{ label: t('common.tryAgain'), color: 'neutral', variant: 'outline', onClick: load }]"
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
        <UIcon name="i-lucide-arrow-right" class="rtl:rotate-180" />
        <span>{{ t('content.resolvedTo') }} <span class="font-mono">{{ resolved }}</span></span>
      </template>
      <span class="ms-auto">{{ t('content.selectRow') }}</span>
    </div>

    <UTable
      :data="items"
      :columns="columns"
      :loading="pending"
      :empty="tType(type, 'empty')"
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
            :text="t(covered(row.original.id, language.code) ? 'content.coverageStored' : 'content.coverageFallsBack', { name: language.name })"
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
            :aria-label="t('content.deleteAria', { id: String(row.original[type.identifier]) })"
            @click.stop="deleting = row.original"
          />
        </div>
      </template>
    </UTable>

    <ContentCreateModal v-model:open="creating" :type="type" @created="onCreated" />

    <ConfirmModal
      :open="deleting !== null"
      :title="tType(type, 'deleteTitle', { id: String(deleting?.[type.identifier] ?? '') })"
      :description="[t('content.deleteDescription'), text.deleteNote].filter(Boolean).join(' ')"
      :confirm-label="tType(type, 'deleteLabel')"
      :loading="removing"
      @update:open="!$event && (deleting = null)"
      @confirm="onDelete"
    />

    <USlideover
      :open="selected !== null"
      :title="selectedItem ? tType(type, 'itemTitle', { id: String(selectedItem[type.identifier]) }) : text.name"
      :description="t('content.editorDescription', { route: type.route, id: selected ?? '' })"
      :side="dir === 'rtl' ? 'left' : 'right'"
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
