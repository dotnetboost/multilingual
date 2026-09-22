<script setup lang="ts">
import type { NavigationMenuItem } from '@nuxt/ui'

const { apiReferenceUrl } = useRuntimeConfig().public
const { t, localizeType } = useI18n()

// One top-menu entry per content type. Adding a type to `contentTypes` (and a page under
// `pages/content/`) is all it takes to get another tab.
const links = computed(() => [
  contentTypes.map(type => ({
    label: localizeType(type).label,
    icon: type.icon,
    to: type.to,
    exact: type.to === '/content'
  })),
  [{
    label: t('nav.apiReference'),
    icon: 'i-lucide-book-open',
    to: apiReferenceUrl,
    target: '_blank'
  }]
] satisfies NavigationMenuItem[][])
</script>

<template>
  <UDashboardPanel id="content">
    <template #header>
      <UDashboardNavbar :title="t('nav.content')">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <LanguageSelect />
        </template>
      </UDashboardNavbar>

      <UDashboardToolbar>
        <!-- NOTE: The `-mx-1` class is used to align with the `DashboardSidebarCollapse` button here. -->
        <UNavigationMenu :items="links" highlight class="-mx-1 flex-1" />
      </UDashboardToolbar>
    </template>

    <template #body>
      <NuxtPage />
    </template>
  </UDashboardPanel>
</template>
