<script setup lang="ts">
import type { ContentItem } from '~/types'

const { apiReferenceUrl, baseLanguage } = useRuntimeConfig().public
const { languages, active, defaultLanguage, pending: languagesPending, loadError, load: loadLanguages, find } = useLanguages()

interface TypeStatus {
  online: boolean
  summary: string
}

const status = reactive<Record<string, TypeStatus>>({})
const probing = ref(true)

async function probe() {
  probing.value = true

  await Promise.all([
    loadLanguages(),
    ...contentTypes.map(async (type) => {
      try {
        const items = await $fetch<ContentItem[]>(`/api/${type.route}`)

        status[type.route] = {
          online: true,
          summary: `${items.length} ${items.length === 1 ? 'item' : 'items'}`
        }
      } catch (error) {
        status[type.route] = { online: false, summary: problemMessage(error) }
      }
    })
  ])

  probing.value = false
}

const loading = computed(() => probing.value || languagesPending.value)
const reachable = computed(() => !loadError.value && contentTypes.every(type => status[type.route]?.online))

const stats = computed(() => [{
  title: 'Languages',
  icon: 'i-lucide-languages',
  value: `${active.value.length} active`,
  detail: `${languages.value.length} in total`,
  to: '/languages'
}, {
  title: 'Default language',
  icon: 'i-lucide-star',
  value: defaultLanguage.value?.name ?? '—',
  detail: defaultLanguage.value ? `${defaultLanguage.value.code} — reads fall back to it` : 'None configured',
  to: '/languages'
}, {
  title: 'Storage mode',
  icon: 'i-lucide-database',
  value: baseLanguage ? `Base language: ${find(baseLanguage)?.name ?? baseLanguage}` : 'Translations table',
  detail: baseLanguage
    ? `${baseLanguage} lives in each entity's own columns; every other language in Translations`
    : 'Every language lives in the shared Translations table',
  to: undefined
}])

onMounted(probe)
</script>

<template>
  <UDashboardPanel id="home">
    <template #header>
      <UDashboardNavbar title="Overview" :ui="{ right: 'gap-3' }">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UBadge
            :color="loading ? 'neutral' : reachable ? 'success' : 'error'"
            variant="subtle"
            :icon="loading ? 'i-lucide-loader-circle' : reachable ? 'i-lucide-plug' : 'i-lucide-plug-zap'"
            :label="loading ? 'Connecting…' : reachable ? 'API connected' : 'API unreachable'"
          />

          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="ghost"
            :loading="loading"
            aria-label="Refresh"
            @click="probe"
          />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UPageCard
        title="Content in every language, from one attribute"
        description="This dashboard manages the languages that DotNetBoost.MultiLingual.API maps with MapLanguageEndpoints(), and edits the [MultiLingual] properties of the sample's entities one language at a time — each write goes out with that language in Accept-Language."
        variant="naked"
        class="mb-2"
      >
        <UButton
          label="Open API reference"
          icon="i-lucide-book-open"
          color="neutral"
          variant="outline"
          :to="apiReferenceUrl"
          target="_blank"
          class="w-fit lg:ms-auto"
        />
      </UPageCard>

      <UAlert
        v-if="loadError"
        color="error"
        variant="subtle"
        icon="i-lucide-plug-zap"
        title="Could not load the languages"
        :description="loadError"
      />

      <UPageGrid class="lg:grid-cols-3 gap-4 sm:gap-6">
        <UPageCard
          v-for="stat in stats"
          :key="stat.title"
          :icon="stat.icon"
          :title="stat.title"
          :to="stat.to"
          variant="subtle"
          :spotlight="!!stat.to"
          :ui="{ leading: 'p-2.5 rounded-full bg-primary/10 ring ring-inset ring-primary/25', title: 'font-normal text-muted text-xs uppercase' }"
        >
          <USkeleton v-if="loading" class="h-7 w-32" />
          <div v-else>
            <p class="text-xl font-semibold text-highlighted">
              {{ stat.value }}
            </p>
            <p class="text-sm text-muted">
              {{ stat.detail }}
            </p>
          </div>
        </UPageCard>
      </UPageGrid>

      <div v-if="active.length" class="flex flex-wrap items-center gap-2">
        <span class="text-sm text-muted">Served in</span>
        <UBadge
          v-for="language in active"
          :key="language.code"
          :color="language.isDefault ? 'primary' : 'neutral'"
          variant="subtle"
        >
          <span :dir="language.direction">{{ language.name }}</span>
          <span class="font-mono opacity-70">{{ language.code }}</span>
        </UBadge>
      </div>

      <UPageGrid class="lg:grid-cols-2">
        <UPageCard
          v-for="type in contentTypes"
          :key="type.route"
          :title="type.label"
          :description="type.description"
          :icon="type.icon"
          :to="type.to"
          variant="subtle"
          spotlight
        >
          <template #footer>
            <div class="flex flex-wrap items-center gap-2">
              <UBadge
                color="neutral"
                variant="subtle"
                class="font-mono"
                :label="`api/${type.route}`"
              />

              <USkeleton v-if="loading" class="h-5 w-24" />
              <UBadge
                v-else
                :color="status[type.route]?.online ? 'success' : 'error'"
                variant="subtle"
                :label="status[type.route]?.summary"
              />
            </div>
          </template>
        </UPageCard>
      </UPageGrid>
    </template>
  </UDashboardPanel>
</template>
