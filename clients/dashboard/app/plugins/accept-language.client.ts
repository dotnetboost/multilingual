/**
 * Sends the dashboard's language as `Accept-Language` on every API call that does not pick one
 * itself, so what the API words — problem details, delete confirmations — comes back in the
 * language the dashboard is shown in. Calls that read or write content in a particular language
 * set the header explicitly, and those are left alone.
 */
export default defineNuxtPlugin(() => {
  const { code } = useI18n()

  globalThis.$fetch = globalThis.$fetch.create({
    onRequest({ options }) {
      // ofetch has normalised the headers into a Headers instance by the time hooks run.
      const headers = options.headers as Headers

      if (!headers.has('Accept-Language')) {
        headers.set('Accept-Language', code.value)
      }
    }
  }) as typeof globalThis.$fetch
})
