import { createSharedComposable } from '@vueuse/core'

const _useDashboard = () => {
  const router = useRouter()

  defineShortcuts({
    'g-h': () => router.push('/'),
    'g-l': () => router.push('/languages'),
    'g-c': () => router.push('/content')
  })
}

export const useDashboard = createSharedComposable(_useDashboard)
