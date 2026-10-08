export class ApiError extends Error {
  status: number
  constructor(message: string, status: number) { super(message); this.status = status }
}

export type StoredSession = {
  accessToken: string
  refreshToken: string
  expiresAtUtc: string
  tenant: { id: string; slug: string; name: string }
  user: { id: string; email: string; displayName: string }
  role: string
  roles: string[]
  permissions: string[]
}

const sessionKey = 'lms-auth-session'

export function getStoredSession(): StoredSession | null {
  try {
    const raw = localStorage.getItem(sessionKey)
    return raw ? JSON.parse(raw) as StoredSession : null
  } catch { return null }
}

export function setStoredSession(session: StoredSession | null) {
  if (session) localStorage.setItem(sessionKey, JSON.stringify(session))
  else localStorage.removeItem(sessionKey)
}

/** Sent when the session ends for good (the refresh token no longer works), so the app can show the sign-in page. */
export const SESSION_CHANGED = 'lms-session-changed'
export const SESSION_EXPIRED = 'lms-session-expired'

let refreshing: Promise<boolean> | null = null

/**
 * Gets a new access token with the refresh token. Refresh tokens work once, so every request that finds the token expired
 * waits for the same attempt instead of starting its own. Returns false when the person has to sign in again.
 */
export function refreshSession(): Promise<boolean> {
  refreshing ??= (async () => {
    const current = getStoredSession()
    if (!current?.refreshToken) return false
    try {
      const response = await fetch('/api/v1/auth/refresh', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tenantSlug: current.tenant.slug, refreshToken: current.refreshToken }) })
      if (response.ok) {
        setStoredSession(await response.json() as StoredSession)
        window.dispatchEvent(new Event(SESSION_CHANGED))
        return true
      }
      if (response.status === 401 || response.status === 403) { setStoredSession(null); window.dispatchEvent(new Event(SESSION_EXPIRED)) }
      return false
    } catch { return false }   // no connection: keep the session, the request will fail with its own message
  })().finally(() => { refreshing = null })
  return refreshing
}

/** Renews the access token a little before it runs out, so requests do not fail on the way. */
async function ensureFreshSession() {
  const session = getStoredSession()
  if (!session?.refreshToken || !session.expiresAtUtc) return
  if (new Date(session.expiresAtUtc).getTime() - Date.now() < 30_000) await refreshSession()
}

const isAuthPath = (path: string) => path.startsWith('/api/v1/auth/')

export async function apiRequest<T>(path: string, options: RequestInit = {}): Promise<T> {
  if (!isAuthPath(path)) await ensureFreshSession()
  let response = await send(path, options)
  // An expired or revoked access token: renew it once and try again. If that cannot be done the person is asked to sign in.
  if (response.status === 401 && !isAuthPath(path) && getStoredSession()?.accessToken && await refreshSession()) response = await send(path, options)
  const body = await response.text()
  let parsed: unknown
  try { parsed = body ? JSON.parse(body) : null } catch { parsed = body }
  if (!response.ok) {
    const expired = response.status === 401 && !isAuthPath(path) && getStoredSession() === null
    const message = expired ? 'Your session has ended. Please sign in again.'
      : typeof parsed === 'object' && parsed !== null && 'message' in parsed
        ? String((parsed as { message: unknown }).message)
        : `Request failed with status ${response.status}.`
    throw new ApiError(message, response.status)
  }
  return parsed as T
}

function send(path: string, options: RequestInit) {
  const session = getStoredSession()
  const headers = new Headers(options.headers)
  // Multipart bodies need the browser to set the boundary, and a raw piece of a file says what it is; everything else is JSON.
  if (options.body instanceof Blob) headers.set('Content-Type', 'application/octet-stream')
  else if (!(options.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  if (session?.accessToken) headers.set('Authorization', `Bearer ${session.accessToken}`)
  if (session?.tenant.slug) headers.set('X-Tenant-Slug', session.tenant.slug)
  return fetch(path, { ...options, headers })
}

/** Downloads an authenticated file and saves it with the server-provided or fallback name. */
export async function downloadFile(path: string, fallbackName: string) {
  const session = getStoredSession()
  const headers = new Headers()
  if (session?.accessToken) headers.set('Authorization', `Bearer ${session.accessToken}`)
  if (session?.tenant.slug) headers.set('X-Tenant-Slug', session.tenant.slug)
  const response = await fetch(path, { headers })
  if (!response.ok) throw new ApiError(`Download failed with status ${response.status}.`, response.status)
  const blob = await response.blob()
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fallbackName
  anchor.click()
  URL.revokeObjectURL(url)
}

/** Loads a private file with the user's credentials and returns a temporary object URL (revoke it when done). */
export async function fetchBlobUrl(path: string): Promise<{ url: string; contentType: string }> {
  const session = getStoredSession()
  const headers = new Headers()
  if (session?.accessToken) headers.set('Authorization', `Bearer ${session.accessToken}`)
  if (session?.tenant.slug) headers.set('X-Tenant-Slug', session.tenant.slug)
  const response = await fetch(path, { headers })
  if (!response.ok) throw new ApiError(`The file could not be loaded (status ${response.status}).`, response.status)
  const blob = await response.blob()
  return { url: URL.createObjectURL(blob), contentType: blob.type }
}

export { sessionKey }

