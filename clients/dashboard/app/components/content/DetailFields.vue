<script setup lang="ts">
import type { DetailField } from '~/types'

/**
 * The untranslated properties of an item — SKU, price, category; slug, display order — as form
 * fields over `state`. Shared by the create form and the editor, so both ask for them alike.
 */
const props = defineProps<{
  fields: DetailField[]
  // Values differ per content type, and each control narrows its own at the point of use.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  state: Record<string, any>
  options: Record<string, ReferenceOption[]>
  /**
   * The values as stored, when editing. A reference the API cannot unset then offers "None" only
   * while nothing is set.
   */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  stored?: Record<string, any>
}>()

function referenceItems(field: DetailField) {
  const items = props.options[field.key] ?? []
  const locked = props.stored && !field.unsettable && props.stored[field.key] !== NO_REFERENCE

  return locked
    ? items.map(item => item.value === NO_REFERENCE ? { ...item, disabled: true } : item)
    : items
}

function hint(field: DetailField) {
  return field.type === 'reference' && props.stored && !field.unsettable && props.stored[field.key] !== NO_REFERENCE
    ? 'Can be changed, not removed'
    : undefined
}
</script>

<template>
  <!-- `state` is the parent form's reactive state, written in place like any field of it. -->
  <!-- eslint-disable vue/no-mutating-props -->
  <UFormField
    v-for="field in fields"
    :key="field.key"
    :name="field.key"
    :label="field.label"
    :description="field.description"
    :hint="hint(field)"
    :required="field.required"
  >
    <UInputNumber
      v-if="field.type === 'number'"
      v-model="state[field.key]"
      :min="field.min"
      :step="field.step"
      class="w-full sm:w-56"
    />

    <USelect
      v-else-if="field.type === 'reference'"
      v-model="state[field.key]"
      :items="referenceItems(field)"
      class="w-full"
    />

    <UInput
      v-else
      v-model="state[field.key]"
      autocomplete="off"
      class="w-full font-mono"
    />
  </UFormField>
</template>
