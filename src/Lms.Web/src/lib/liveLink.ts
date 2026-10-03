export type LiveLink = { sessionId: string; recording: boolean }

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** Reads the links the live-class provider hands out: `?liveSession=<id>` to join a class, `?recording=<id>` to watch one. */
export function parseLiveLink(search: string): LiveLink | null {
  const params = new URLSearchParams(search)
  const recording = params.get('recording')?.trim()
  const session = params.get('liveSession')?.trim()
  if (recording && guid.test(recording)) return { sessionId: recording, recording: true }
  if (session && guid.test(session)) return { sessionId: session, recording: false }
  return null
}

/** Takes the live-class parameters out of the address bar once they have been read, keeping anything else. */
export function clearLiveLink() {
  try {
    const params = new URLSearchParams(window.location.search)
    params.delete('liveSession'); params.delete('recording'); params.delete('host')
    const query = params.toString()
    history.replaceState(null, '', window.location.pathname + (query ? `?${query}` : '') + window.location.hash)
  } catch { /* not available in some embedded views */ }
}
