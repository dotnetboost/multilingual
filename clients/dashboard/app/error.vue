<script setup lang="ts">
import type { NuxtError } from '#app'

defineProps<{
  error: NuxtError
}>()

const { code, dir, uiLocale, t } = useI18n()

useSeoMeta({
  title: () => t('error.title'),
  description: () => t('error.description')
})

useHead({
  htmlAttrs: {
    lang: code,
    dir
  }
})
</script>

<template>
  <UApp :locale="uiLocale">
    <UError :error="error" />
  </UApp>
</template>
