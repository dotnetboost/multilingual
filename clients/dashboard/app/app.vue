<script setup lang="ts">
const colorMode = useColorMode()
const { code, dir, uiLocale, t } = useI18n()

const color = computed(() => colorMode.value === 'dark' ? '#1b1718' : 'white')

useHead({
  meta: [
    { charset: 'utf-8' },
    { name: 'viewport', content: 'width=device-width, initial-scale=1' },
    { key: 'theme-color', name: 'theme-color', content: color }
  ],
  link: [
    { rel: 'icon', href: '/favicon.ico' }
  ],
  // The dashboard's language sets the page's: an rtl language flips the whole layout.
  htmlAttrs: {
    lang: code,
    dir
  }
})

const title = computed(() => t('app.title'))
const description = computed(() => t('app.description'))

useSeoMeta({
  title,
  description,
  ogTitle: title,
  ogDescription: description
})
</script>

<template>
  <UApp :locale="uiLocale">
    <NuxtLoadingIndicator />

    <NuxtLayout>
      <NuxtPage />
    </NuxtLayout>
  </UApp>
</template>
