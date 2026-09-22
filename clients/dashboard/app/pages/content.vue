<script setup lang="ts">
import type { NavigationMenuItem } from '@nuxt/ui'

const { apiReferenceUrl } = useRuntimeConfig().public

// One top-menu entry per content type. Adding a type to `contentTypes` (and a page under
// `pages/content/`) is all it takes to get another tab.
const links = [
  contentTypes.map(type => ({
    label: type.label,
    icon: type.icon,
    to: type.to,
    exact: type.to === '/content'
  })),
  [{
    label: 'API reference',
    icon: 'i-lucide-book-open',
    to: apiReferenceUrl,
    target: '_blank'
  }]
] satisfies NavigationMenuItem[][]
</script>

<template>
  <UDashboardPanel id="content">
    <template #header>
      <UDashboardNavbar title="Content">
        <template #leading>
          <UDashboardSidebarCollapse />
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
