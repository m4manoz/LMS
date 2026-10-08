import { getStoredSession, refreshSession } from './api'

export type MessageSignal = { type: 'message' | 'edited' | 'deleted'; conversationId: string; messageId: string | null }
type Listener = (signal: MessageSignal) => void
type StatusListener = (connected: boolean) => void

/**
 * One shared live connection that tells the app the moment a message is sent, edited or deleted in a conversation the person can reach.
 * The browser's EventSource cannot send the sign-in header, so this reads the event stream with fetch. It reconnects by itself (the server
 * ends each connection after a few minutes), waits longer after failures, and does nothing at all when nobody is signed in.
 * The signal carries no text: the page then fetches what changed through its usual calls. If the connection cannot be made, pages keep polling.
 */
const listeners = new Set<Listener>()
const statusListeners = new Set<StatusListener>()
let controller: AbortController | null = null
let connected = false

function setConnected(value: boolean) {
  if (connected === value) return
  connected = value
  statusListeners.forEach((listener) => listener(value))
}

export const isLive = () => connected

/** Pulls complete events out of the text received so far and returns what is left over. */
export function parseSignals(buffer: string, emit: (signal: MessageSignal) => void): string {
  let rest = buffer
  let end = rest.indexOf('\n\n')
  while (end >= 0) {
    const block = rest.slice(0, end)
    rest = rest.slice(end + 2)
    const data = block.split('\n').find((line) => line.startsWith('data: '))
    if (data) {
      try { emit(JSON.parse(data.slice(6)) as MessageSignal) } catch { /* a damaged event is skipped */ }
    }
    end = rest.indexOf('\n\n')
  }
  return rest
}

async function connectOnce(signal: AbortSignal): Promise<'ended' | 'refused'> {
  const session = getStoredSession()
  if (!session?.accessToken) return 'refused'
  const headers = new Headers({ Authorization: `Bearer ${session.accessToken}` })
  if (session.tenant.slug) headers.set('X-Tenant-Slug', session.tenant.slug)
  const response = await fetch('/api/v1/tenant/messages/stream', { headers, signal })
  if (response.status === 401) { await refreshSession(); return 'refused' }
  if (!response.ok || !response.body) return 'refused'
  setConnected(true)
  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  for (;;) {
    const { done, value } = await reader.read()
    if (done) return 'ended'
    buffer = parseSignals(buffer + decoder.decode(value, { stream: true }).replace(/\r\n/g, '\n'), (event) => listeners.forEach((listener) => listener(event)))
  }
}

async function run(signal: AbortSignal) {
  let failures = 0
  while (!signal.aborted) {
    try {
      const outcome = await connectOnce(signal)
      failures = outcome === 'ended' ? 0 : failures + 1
    } catch {
      if (signal.aborted) break
      failures += 1
    }
    setConnected(false)
    if (signal.aborted) break
    // Straight back after a normal end; otherwise 2, 4, 8 … up to 30 seconds.
    const wait = failures === 0 ? 250 : Math.min(30_000, 1000 * 2 ** failures)
    await new Promise<void>((resolve) => { const timer = window.setTimeout(resolve, wait); signal.addEventListener('abort', () => { window.clearTimeout(timer); resolve() }, { once: true }) })
  }
  setConnected(false)
}

/** Starts listening (opening the shared connection if this is the first listener). Call the returned function to stop. */
export function subscribeMessages(listener: Listener, onStatus?: StatusListener): () => void {
  listeners.add(listener)
  if (onStatus) { statusListeners.add(onStatus); onStatus(connected) }
  if (!controller && typeof fetch === 'function') {
    controller = new AbortController()
    void run(controller.signal)
  }
  return () => {
    listeners.delete(listener)
    if (onStatus) statusListeners.delete(onStatus)
    if (listeners.size === 0 && controller) { controller.abort(); controller = null; setConnected(false) }
  }
}
