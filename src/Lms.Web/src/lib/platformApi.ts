import { ApiError } from './api'

export type PlatformTenant = { id: string; slug: string; name: string; status: 'Active' | 'Suspended' | 'Archived'; createdAtUtc: string; updatedAtUtc: string; members: number; courses: number }
export type PlatformAdmin = { userId: string; email: string; displayName: string; status: string }
export type PlatformTenantDetail = PlatformTenant & { enrollments: number; admins: PlatformAdmin[]; domains: string[]; storageBucket?: string | null; ownBucket?: boolean }
export type NewTenant = { name: string; slug: string; adminEmail?: string; adminName?: string; adminPassword?: string }

/** The platform key stays in this browser tab only, never in local storage. */
const keyStore = 'lms-platform-key'
export const getPlatformKey = () => { try { return sessionStorage.getItem(keyStore) ?? '' } catch { return '' } }
export const setPlatformKey = (key: string | null) => { try { if (key) sessionStorage.setItem(keyStore, key); else sessionStorage.removeItem(keyStore) } catch { /* private window: the key is asked for again */ } }

/** True when the address asks for the operator console (#/platform). */
export const isPlatformAddress = (hash: string) => hash === '#/platform' || hash === '#platform'

async function call<T>(key: string, path: string, method = 'GET', body?: unknown): Promise<T> {
  const response = await fetch(path, { method, headers: { 'X-Platform-Key': key, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: body === undefined ? undefined : JSON.stringify(body) })
  const text = await response.text()
  let parsed: unknown
  try { parsed = text ? JSON.parse(text) : null } catch { parsed = text }
  if (!response.ok) {
    if (response.status === 401) throw new ApiError('The platform key was not accepted.', 401)
    const problem = parsed as { message?: string; errors?: Record<string, string[]> } | null
    const detail = problem?.message ?? (problem?.errors ? Object.values(problem.errors).flat()[0] : undefined)
    throw new ApiError(detail ?? `Request failed with status ${response.status}.`, response.status)
  }
  return parsed as T
}

const base = '/api/v1/platform/tenants'

export const listTenants = (key: string, q = '', status = '') => {
  const query = new URLSearchParams()
  if (q.trim()) query.set('q', q.trim())
  if (status) query.set('status', status)
  const suffix = query.toString()
  return call<PlatformTenant[]>(key, suffix ? `${base}?${suffix}` : base)
}
export const getTenant = (key: string, slug: string) => call<PlatformTenantDetail>(key, `${base}/${encodeURIComponent(slug)}`)
export const renameTenant = (key: string, slug: string, name: string) => call<PlatformTenant>(key, `${base}/${encodeURIComponent(slug)}`, 'PUT', { name })
export const changeTenantStatus = (key: string, slug: string, action: 'suspend' | 'activate' | 'archive') => call<{ status: string }>(key, `${base}/${encodeURIComponent(slug)}/${action}`, 'POST')

/** Creates the organization and, when an administrator is given, that person too. */
export async function createTenant(key: string, tenant: NewTenant) {
  await call(key, base, 'POST', { name: tenant.name, slug: tenant.slug })
  if (tenant.adminEmail) {
    await call(key, `${base}/${encodeURIComponent(tenant.slug)}/bootstrap-admin`, 'POST', { email: tenant.adminEmail, displayName: tenant.adminName || 'Administrator', password: tenant.adminPassword })
  }
}
