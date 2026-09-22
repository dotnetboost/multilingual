<script setup lang="ts">
/**
 * The dashboard's language, in the header of every page. It sets the text, the direction and the
 * language content is read in — see `useI18n`.
 */
const { available, code, t, setLanguage } = useI18n()

const items = computed(() => available.value.map(language => ({
  label: language.name,
  value: language.code,
  direction: language.direction
})))
</script>

<template>
  <USelect
    :model-value="code"
    :items="items"
    icon="i-lucide-languages"
    color="neutral"
    variant="outline"
    class="w-32 sm:w-40"
    :content="{ align: 'end' }"
    :aria-label="t('header.language')"
    @update:model-value="setLanguage($event as string)"
  >
    <template #item-label="{ item }">
      <span :dir="item.direction">{{ item.label }}</span>
    </template>

    <template #item-trailing="{ item }">
      <span class="font-mono text-xs text-muted">{{ item.value }}</span>
    </template>
  </USelect>
</template>
