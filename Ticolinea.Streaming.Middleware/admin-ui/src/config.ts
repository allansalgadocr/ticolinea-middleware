import { useEffect, useState } from 'react'

export interface NodeInfo {
  provider: string
  displayName: string
}

// Fetched once and cached at module scope: the node's identity cannot change
// while the page is open, so remounting a route must not refetch it.
let cached: NodeInfo | null = null

const FALLBACK: NodeInfo = { provider: 'nodo', displayName: 'Nodo' }

export function useNodeInfo(): NodeInfo {
  const [info, setInfo] = useState<NodeInfo>(cached ?? FALLBACK)

  useEffect(() => {
    if (cached) return
    let alive = true
    fetch('/api/console/node', { credentials: 'same-origin' })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
      .then((data: NodeInfo) => {
        cached = data
        if (alive) setInfo(data)
      })
      // Identity is cosmetic; a failure must not block the login form.
      .catch(() => undefined)
    return () => {
      alive = false
    }
  }, [])

  return info
}
