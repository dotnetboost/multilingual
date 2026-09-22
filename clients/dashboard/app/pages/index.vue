<script setup lang="ts">
import type { ContentItem } from '~/types'

const { apiReferenceUrl, baseLanguage } = useRuntimeConfig().public
const { languages, active, defaultLanguage, pending: languagesPending, loadError, load: loadLanguages, find } = useLanguages()
const { t, localizeType } = useI18n()

/** A content type's endpoint: how many items it returned, or why it failed. */
interface TypeStatus {
  online: boolean
  count?: number
  error?: string
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

        status[type.route] = { online: true, count: items.length }
      } catch (error) {
        status[type.route] = { online: false, error: problemMessage(error) }
      }
    })
  ])

  probing.value = false
}

const loading = computed(() => probing.value || languagesPending.value)
const reachable = computed(() => !loadError.value && contentTypes.every(type => status[type.route]?.online))

function summary(route: string) {
  const entry = status[route]
  return entry?.online ? t('overview.items', { count: entry.count }) : entry?.error
}

const stats = computed(() => [{
  title: t('overview.stats.languages'),
  icon: 'i-lucide-languages',
  value: t('overview.stats.activeCount', { count: active.value.length }),
  detail: t('overview.stats.totalCount', { count: languages.value.length }),
  to: '/languages'
}, {
  title: t('overview.stats.defaultLanguage'),
  icon: 'i-lucide-star',
  value: defaultLanguage.value?.name ?? '—',
  detail: defaultLanguage.value ? t('overview.stats.defaultDetail', { code: defaultLanguage.value.code }) : t('overview.stats.noneConfigured'),
  to: '/languages'
}, {
  title: t('overview.stats.storageMode'),
  icon: 'i-lucide-database',
  value: baseLanguage ? t('overview.stats.baseLanguage', { name: find(baseLanguage)?.name ?? baseLanguage }) : t('overview.stats.translationsTable'),
  detail: baseLanguage
    ? t('overview.stats.baseDetail', { code: baseLanguage })
    : t('overview.stats.tableDetail'),
  to: undefined
}])

onMounted(probe)
</script>

<template>
  <UDashboardPanel id="home">
    <template #header>
      <UDashboardNavbar :title="t('nav.overview')" :ui="{ right: 'gap-3' }">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UBadge
            :color="loading ? 'neutral' : reachable ? 'success' : 'error'"
            variant="subtle"
            :icon="loading ? 'i-lucide-loader-circle' : reachable ? 'i-lucide-plug' : 'i-lucide-plug-zap'"
            :label="loading ? t('overview.connecting') : reachable ? t('overview.connected') : t('overview.unreachable')"
            class="hidden sm:inline-flex"
          />

          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="ghost"
            :loading="loading"
            :aria-label="t('common.refresh')"
            @click="probe"
          />

          <LanguageSelect />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UPageCard
        :title="t('overview.introTitle')"
        :description="t('overview.introDescription')"
        variant="naked"
        class="mb-2"
      >
        <UButton
          :label="t('overview.openApiReference')"
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
        :title="t('languages.loadError')"
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
        <span class="text-sm text-muted">{{ t('overview.servedIn') }}</span>
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
          v-for="type in contentTypes.map(localizeType)"
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
                :label="summary(type.route)"
              />
            </div>
          </template>
        </UPageCard>
      </UPageGrid>
    </template>
  </UDashboardPanel>
</template>
