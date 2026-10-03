import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiRequest, getStoredSession, SESSION_EXPIRED, setStoredSession, type StoredSession } from './api'

const stored = (over: Partial<StoredSession> = {}): StoredSession => ({
  accessToken: 'old-access', refreshToken: 'refresh-1', expiresAtUtc: new Date(Date.now() + 3600_000).toISOString(),
  tenant: { id: 't', slug: 'acme', name: 'Acme' }, user: { id: 'u', email: 'a@b.c', displayName: 'A' }, role: 'TEACHER', roles: ['TEACHER'], permissions: [], ...over,
})
const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status })

describe('an expired sign-in', () => {
  const fetchMock = vi.fn()
  beforeEach(() => { fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); setStoredSession(stored()) })
  afterEach(() => { vi.unstubAllGlobals(); setStoredSession(null) })

  it('is renewed with the refresh token and the request is tried again', async () => {
    fetchMock.mockImplementation((path: string, init: RequestInit) => {
      if (path === '/api/v1/auth/refresh') return Promise.resolve(json(200, stored({ accessToken: 'new-access', refreshToken: 'refresh-2' })))
      return Promise.resolve((init.headers as Headers).get('Authorization') === 'Bearer new-access' ? json(200, { ok: true }) : json(401, null))
    })
    expect(await apiRequest('/api/v1/tenant/videos')).toEqual({ ok: true })
    expect(getStoredSession()?.refreshToken).toBe('refresh-2')                                   // the new pair is kept
    expect(JSON.parse(fetchMock.mock.calls.find((call) => call[0] === '/api/v1/auth/refresh')![1].body)).toEqual({ tenantSlug: 'acme', refreshToken: 'refresh-1' })
  })

  it('is renewed once for several requests that fail together (a refresh token works only once)', async () => {
    fetchMock.mockImplementation((path: string, init: RequestInit) => {
      if (path === '/api/v1/auth/refresh') return Promise.resolve(json(200, stored({ accessToken: 'new-access', refreshToken: 'refresh-2' })))
      return Promise.resolve((init.headers as Headers).get('Authorization') === 'Bearer new-access' ? json(200, {}) : json(401, null))
    })
    await Promise.all([apiRequest('/a'), apiRequest('/b'), apiRequest('/c')])
    expect(fetchMock.mock.calls.filter((call) => call[0] === '/api/v1/auth/refresh')).toHaveLength(1)
  })

  it('is renewed before the request when the token is about to run out', async () => {
    setStoredSession(stored({ expiresAtUtc: new Date(Date.now() + 5_000).toISOString() }))
    fetchMock.mockImplementation((path: string) => Promise.resolve(path === '/api/v1/auth/refresh' ? json(200, stored({ accessToken: 'new-access' })) : json(200, { ok: 1 })))
    await apiRequest('/x')
    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual(['/api/v1/auth/refresh', '/x'])
  })

  it('ends the session with a clear message when the refresh token no longer works', async () => {
    const ended = vi.fn()
    window.addEventListener(SESSION_EXPIRED, ended)
    fetchMock.mockImplementation((path: string) => Promise.resolve(json(401, path === '/api/v1/auth/refresh' ? null : null)))
    await expect(apiRequest('/x')).rejects.toMatchObject({ status: 401, message: 'Your session has ended. Please sign in again.' })
    expect(getStoredSession()).toBeNull()
    expect(ended).toHaveBeenCalledTimes(1)
    window.removeEventListener(SESSION_EXPIRED, ended)
  })

  it('keeps the session when the connection is down, and does not retry sign-in calls', async () => {
    fetchMock.mockImplementation((path: string) => path === '/api/v1/auth/refresh' ? Promise.reject(new TypeError('offline')) : Promise.resolve(json(401, null)))
    await expect(apiRequest('/x')).rejects.toMatchObject({ status: 401 })
    expect(getStoredSession()?.refreshToken).toBe('refresh-1')
    fetchMock.mockReset()
    fetchMock.mockResolvedValue(json(401, { message: 'Invalid email or password.' }))
    await expect(apiRequest('/api/v1/auth/login', { method: 'POST', body: '{}' })).rejects.toMatchObject({ message: 'Invalid email or password.' })
    expect(fetchMock).toHaveBeenCalledTimes(1)                                                 // a wrong password is not an expired session
  })
})
