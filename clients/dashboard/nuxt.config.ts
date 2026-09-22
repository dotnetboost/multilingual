// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  modules: [
    '@nuxt/eslint',
    '@nuxt/ui',
    '@vueuse/nuxt'
  ],

  // Client-rendered single-page app. Nitro is still used, but only to proxy the
  // API (see server/api/[...path].ts) — never to render pages.
  ssr: false,

  devtools: {
    enabled: true
  },

  css: ['~/assets/css/main.css'],

  runtimeConfig: {
    // Base address of the API: the language endpoints and the sample's content endpoints. Read
    // by the proxy handler only, so the browser never sees it. Override with NUXT_MULTILINGUAL_API_URL.
    multilingualApiUrl: 'http://localhost:5160',

    public: {
      // Linked from the sidebar — the API's own Scalar reference.
      // Override with NUXT_PUBLIC_API_REFERENCE_URL.
      apiReferenceUrl: 'http://localhost:5160/scalar',

      // MultiLingualOptions.BaseLanguage on the API. That language lives in the entity's own
      // columns, so `GET {id}/translations` leaves it out and it cannot be cleared. Empty for an
      // API without one. Override with NUXT_PUBLIC_BASE_LANGUAGE.
      baseLanguage: 'en'
    }
  },

  compatibilityDate: '2026-06-30',

  eslint: {
    config: {
      stylistic: {
        commaDangle: 'never',
        braceStyle: '1tbs'
      }
    }
  }
})
