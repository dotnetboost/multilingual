/**
 * Proxies every `/api/**` call through to the API — the language endpoints and the content
 * endpoints alike. `Accept-Language` travels with the request, which is what picks the language
 * a read resolves to and a write is stored in.
 *
 * The browser therefore only ever talks to this app's own origin, which keeps the API free of
 * CORS configuration and lets the target move between environments by changing
 * `NUXT_MULTILINGUAL_API_URL` alone.
 */
export default defineEventHandler(async (event) => {
  const { multilingualApiUrl } = useRuntimeConfig(event)
  const path = getRouterParam(event, 'path') ?? ''
  const { search } = getRequestURL(event)

  const target = `${multilingualApiUrl.replace(/\/$/, '')}/api/${path}${search}`

  return proxyRequest(event, target)
})
