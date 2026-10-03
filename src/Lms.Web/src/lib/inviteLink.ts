export type InviteLink = { token: string; tenant: string }

/** Reads an invitation link of the form `#invite=CODE&tenant=SLUG`. The code travels in the fragment so it is never sent to a server by the browser. */
export function parseInviteHash(hash: string): InviteLink | null {
  const params = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash)
  const token = params.get('invite')?.trim()
  const tenant = params.get('tenant')?.trim()
  return token && tenant ? { token, tenant } : null
}

/** Reads a password reset link of the form `#reset=CODE&tenant=SLUG`. */
export function parseResetHash(hash: string): InviteLink | null {
  const params = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash)
  const token = params.get('reset')?.trim()
  const tenant = params.get('tenant')?.trim()
  return token && tenant ? { token, tenant } : null
}

/** Removes the code from the address bar once it has been used or abandoned. */
export function clearInviteHash() {
  try { history.replaceState(null, '', window.location.pathname + window.location.search) } catch { /* not available in some embedded views */ }
}
