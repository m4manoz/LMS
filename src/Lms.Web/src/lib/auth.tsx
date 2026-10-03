import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { apiRequest, setStoredSession, getStoredSession, SESSION_CHANGED, SESSION_EXPIRED, type StoredSession } from './api'

type AuthContextValue = {
  session: StoredSession | null
  login: (tenantSlug: string, email: string, password: string) => Promise<void>
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<StoredSession | null>(() => getStoredSession())
  // The API client renews the access token by itself, or ends the session when it cannot.
  useEffect(() => {
    const changed = () => setSession(getStoredSession())
    const expired = () => setSession(null)
    window.addEventListener(SESSION_CHANGED, changed)
    window.addEventListener(SESSION_EXPIRED, expired)
    return () => { window.removeEventListener(SESSION_CHANGED, changed); window.removeEventListener(SESSION_EXPIRED, expired) }
  }, [])
  const value = useMemo(() => ({
    session,
    async login(tenantSlug: string, email: string, password: string) {
      const next = await apiRequest<StoredSession>('/api/v1/auth/login', { method: 'POST', body: JSON.stringify({ tenantSlug, email, password }) })
      setStoredSession(next); setSession(next)
    },
    logout() { setStoredSession(null); setSession(null) },
  }), [session])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth must be used inside AuthProvider')
  return value
}

