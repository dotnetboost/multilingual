<script setup lang="ts">
import type { DropdownMenuItem, TableColumn } from '@nuxt/ui'
import type { Language } from '~/types'

const { languages, pending, loadError, busy, load, isBase, update, setDefault, remove, purge } = useLanguages()

const formOpen = ref(false)
const editing = ref<Language>()

/** The destructive action awaiting confirmation. */
const confirming = ref<{ kind: 'delete' | 'purge', language: Language } | null>(null)

const columns: TableColumn<Language>[] = [
  { accessorKey: 'code', header: 'Code' },
  { accessorKey: 'name', header: 'Name' },
  { accessorKey: 'direction', header: 'Direction' },
  { id: 'role', header: 'Role' },
  { accessorKey: 'isActive', header: 'Active' },
  { id: 'actions', header: '' }
]

function add() {
  editing.value = undefined
  formOpen.value = true
}

function edit(language: Language) {
  editing.value = language
  formOpen.value = true
}

function toggleActive(language: Language, isActive: boolean) {
  // PUT is a full replacement, so the name and direction go back as they are.
  update(language.code, { name: language.name, direction: language.direction, isActive })
}

function actions(language: Language): DropdownMenuItem[][] {
  return [[{
    label: 'Edit',
    icon: 'i-lucide-pencil',
    onSelect: () => edit(language)
  }, {
    label: 'Make default',
    icon: 'i-lucide-star',
    disabled: language.isDefault || !language.isActive,
    onSelect: () => setDefault(language.code)
  }], [{
    label: 'Delete all translations',
    icon: 'i-lucide-eraser',
    color: 'error',
    onSelect: () => confirming.value = { kind: 'purge', language }
  }, {
    label: 'Delete language',
    icon: 'i-lucide-trash-2',
    color: 'error',
    disabled: language.isDefault,
    onSelect: () => confirming.value = { kind: 'delete', language }
  }]]
}

const confirmation = computed(() => {
  const target = confirming.value
  if (!target) {
    return { title: '', description: '', label: '' }
  }

  return target.kind === 'purge'
    ? {
        title: `Delete every ${target.language.name} translation?`,
        description: `Removes every translation written in ${target.language.code}, across all entities. Reads in it then fall back to the default language. The language itself stays.`
          + (isBase(target.language.code) ? ' It is the base language, so its values are columns and are not affected.' : ''),
        label: 'Delete translations'
      }
    : {
        title: `Delete ${target.language.name}?`,
        description: 'The API refuses while translations in it exist — delete those first, or deactivate the language to keep them.',
        label: 'Delete language'
      }
})

async function onConfirm() {
  const target = confirming.value
  if (!target) {
    return
  }

  await (target.kind === 'purge' ? purge(target.language.code) : remove(target.language.code))
  confirming.value = null
}

onMounted(load)
</script>

<template>
  <UDashboardPanel id="languages">
    <template #header>
      <UDashboardNavbar title="Languages" :ui="{ right: 'gap-3' }">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="ghost"
            :loading="pending"
            aria-label="Refresh"
            @click="load"
          />
          <UButton icon="i-lucide-plus" label="Add language" @click="add" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UPageCard
        title="Languages content is served in"
        description="Exactly one is the default: reads fall back to it when the requested language has no translation. The code is a language's identity and never changes; its content is kept while it is inactive."
        variant="naked"
      >
        <UBadge
          color="neutral"
          variant="subtle"
          class="w-fit font-mono"
          label="api/languages"
        />
      </UPageCard>

      <UAlert
        v-if="loadError"
        color="error"
        variant="subtle"
        icon="i-lucide-plug-zap"
        title="Could not load the languages"
        :description="loadError"
        :actions="[{ label: 'Try again', color: 'neutral', variant: 'outline', onClick: load }]"
      />

      <UTable
        v-else
        :data="languages"
        :columns="columns"
        :loading="pending && !languages.length"
        empty="No languages yet. The first one added becomes the default."
        class="rounded-lg border border-default"
      >
        <template #code-cell="{ row }">
          <span class="font-mono text-highlighted">{{ row.original.code }}</span>
        </template>

        <template #name-cell="{ row }">
          <span :dir="row.original.direction" class="text-highlighted">{{ row.original.name }}</span>
        </template>

        <template #direction-cell="{ row }">
          <UBadge
            color="neutral"
            variant="outline"
            class="font-mono"
            :icon="row.original.direction === 'rtl' ? 'i-lucide-pilcrow-left' : 'i-lucide-pilcrow-right'"
            :label="row.original.direction"
          />
        </template>

        <template #role-cell="{ row }">
          <div class="flex flex-wrap gap-1">
            <UBadge
              v-if="row.original.isDefault"
              color="primary"
              variant="subtle"
              icon="i-lucide-star"
              label="default"
            />
            <UTooltip v-if="isBase(row.original.code)" text="MultiLingualOptions.BaseLanguage: stored in each entity's own columns">
              <UBadge
                color="info"
                variant="subtle"
                icon="i-lucide-database"
                label="base"
              />
            </UTooltip>
          </div>
        </template>

        <template #isActive-cell="{ row }">
          <USwitch
            :model-value="row.original.isActive"
            :loading="busy === row.original.code"
            :disabled="busy !== null"
            :aria-label="`${row.original.isActive ? 'Deactivate' : 'Activate'} ${row.original.name}`"
            @update:model-value="toggleActive(row.original, $event)"
          />
        </template>

        <template #actions-cell="{ row }">
          <div class="flex justify-end">
            <UDropdownMenu :items="actions(row.original)" :content="{ align: 'end' }">
              <UButton
                icon="i-lucide-ellipsis-vertical"
                color="neutral"
                variant="ghost"
                :aria-label="`Actions for ${row.original.name}`"
              />
            </UDropdownMenu>
          </div>
        </template>
      </UTable>

      <LanguagesFormModal v-model:open="formOpen" :language="editing" />

      <ConfirmModal
        :open="confirming !== null"
        :title="confirmation.title"
        :description="confirmation.description"
        :confirm-label="confirmation.label"
        :loading="busy !== null"
        @update:open="!$event && (confirming = null)"
        @confirm="onConfirm"
      />
    </template>
  </UDashboardPanel>
</template>
