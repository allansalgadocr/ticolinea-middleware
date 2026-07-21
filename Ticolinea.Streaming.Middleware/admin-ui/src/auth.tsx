import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { api, ApiError } from './api'
import type { AdminUser } from './types'

interface AuthState {
  user: AdminUser | null
  /** True until the initial /auth/me has settled — prevents a login-screen flash on refresh. */
  loading: boolean
  login: (username: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

const Ctx = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AdminUser | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    // A 401 here is the normal "not logged in" case, not an error to surface.
    api
      .me()
      .then(setUser)
      .catch(() => setUser(null))
      .finally(() => setLoading(false))
  }, [])

  const login = useCallback(async (username: string, password: string) => {
    setUser(await api.login(username, password))
  }, [])

  const logout = useCallback(async () => {
    try {
      await api.logout()
    } finally {
      // Drop the local session even if the round trip failed — the cookie may
      // already be gone server-side.
      setUser(null)
    }
  }, [])

  const value = useMemo(() => ({ user, loading, login, logout }), [user, loading, login, logout])
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useAuth() {
  const ctx = useContext(Ctx)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}

export function RequireAuth({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth()
  const location = useLocation()

  if (loading) return <FullPageSpinner />
  if (!user) return <Navigate to="/" replace state={{ from: location.pathname }} />
  return <>{children}</>
}

/** Owner-only areas (user administration). Operators are redirected, not shown a dead link. */
export function RequireOwner({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth()

  if (loading) return <FullPageSpinner />
  if (!user) return <Navigate to="/" replace />
  if (user.role !== 'owner') return <Navigate to="/canales" replace />
  return <>{children}</>
}

export function FullPageSpinner() {
  return (
    <div className="relative z-10 grid min-h-screen place-items-center">
      <Spinner />
    </div>
  )
}

export function Spinner({ size = 22 }: { size?: number }) {
  return (
    <span
      role="status"
      aria-label="Cargando"
      className="inline-block animate-spin rounded-full border-2 border-line border-t-mint"
      style={{ width: size, height: size }}
    />
  )
}

export function errorMessage(e: unknown): string {
  if (e instanceof ApiError) return e.message
  if (e instanceof Error) return e.message
  return 'Ocurrió un error inesperado.'
}
