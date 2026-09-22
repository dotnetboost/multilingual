<script setup lang="ts">
const type = findContentType('categories')!
</script>

<template>
  <ContentBrowser :type="type" />
</template>
