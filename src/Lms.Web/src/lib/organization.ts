/** Organizations are identified by a short name of 3 to 63 lowercase letters, numbers and hyphens (the same rule the server uses). */
export const normalizeOrganization = (value: string) => value.trim().toLowerCase()

export function validateOrganization(value: string): string | null {
  const slug = normalizeOrganization(value)
  if (!slug) return 'Enter your organization’s short name.'
  if (!/^[a-z0-9](?:[a-z0-9-]{1,61})[a-z0-9]$/.test(slug)) return 'Use 3 to 63 letters, numbers or hyphens.'
  return null
}

const key = 'lms-organization'

/** The organization named in the address (?org=acme), if it is a valid name. */
export function organizationFromUrl(search: string): string | null {
  const value = new URLSearchParams(search).get('org')
  return value && validateOrganization(value) === null ? normalizeOrganization(value) : null
}

/** The organization this browser used last, so the front page opens on it next time. */
export function rememberedOrganization(): string | null {
  try { const value = localStorage.getItem(key); return value && validateOrganization(value) === null ? value : null } catch { return null }
}

export function rememberOrganization(slug: string) {
  try { localStorage.setItem(key, slug) } catch { /* private browsing: the page simply asks again next time */ }
}
