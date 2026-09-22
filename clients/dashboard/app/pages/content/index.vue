<script setup lang="ts">
const type = findContentType('products')!
</script>

<template>
  <ContentBrowser :type="type" />
</template>
