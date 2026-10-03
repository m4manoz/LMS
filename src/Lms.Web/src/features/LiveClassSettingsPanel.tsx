import { useEffect, useState } from 'react'
import { ErrorBanner, Field, FormActions, FormLayout, FormSection, NoticeBanner } from '@/components/form'
import { Input } from '@/components/ui/input'
import { ApiError, apiRequest } from '@/lib/api'
import { cn } from '@/lib/utils'

type Settings = { provider: string; jitsiBaseUrl: string; liveKitUrl?: string | null; liveKitApiKey?: string | null; liveKitSecretSet?: boolean; updatedAtUtc?: string | null }

export const liveProviderChoices = [
  { value: 'Manual', title: 'Your own meeting link', text: 'The teacher makes the meeting in Zoom, Google Meet, Microsoft Teams or similar and pastes its link when scheduling. Learners are sent there. Recordings are made in that tool and attached by link.' },
  { value: 'Jitsi', title: 'Jitsi Meet', text: 'A free, open-source video room is created for every class, on the public server or your own. Camera, microphone and screen sharing work in the browser. Recordings are made in the meeting and attached by link.' },
  { value: 'LiveKit', title: 'LiveKit (inside this app)', text: 'The class is held in a room inside this app: camera, microphone and screen sharing, with attendance taken automatically. Needs a LiveKit server (LiveKit Cloud or your own). Recordings are attached by link.' },
  { value: 'Local', title: 'Placeholder (no video)', text: 'For trying things out: the class link opens this app and no video is carried. Do not use it for real classes.' },
] as const

/** A usable Jitsi address: https, no sign-in details, nothing after the path. Mirrors the server's check. */
export function validateJitsiAddress(value: string): string | null {
  const text = value.trim()
  if (!text) return null // blank means the public server
  try {
    const url = new URL(text)
    if (url.protocol !== 'https:') return 'Use an https address, for example https://meet.jit.si.'
    if (url.username || url.password) return 'The address must not contain a user name or password.'
    if (url.search || url.hash) return 'The address must not contain a query or fragment.'
    return null
  } catch { return 'Enter the server address as a link, for example https://meet.jit.si.' }
}

/** A usable LiveKit address: wss (or https), no sign-in details, nothing after the host. Mirrors the server's check. */
export function validateLiveKitAddress(value: string): string | null {
  const text = value.trim()
  if (!text) return 'Enter the LiveKit server address, for example wss://your-project.livekit.cloud.'
  try {
    const url = new URL(text)
    const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)       // a server on this machine, for trying things out
    if (url.protocol !== 'wss:' && url.protocol !== 'https:' && !(local && (url.protocol === 'ws:' || url.protocol === 'http:'))) return 'Use a wss:// address, for example wss://your-project.livekit.cloud. (ws://localhost is allowed for a test server on this machine.)'
    if (url.username || url.password) return 'The address must not contain a user name or password.'
    if (url.search || url.hash) return 'The address must not contain a query or fragment.'
    return null
  } catch { return 'Enter the server address as a link, for example wss://your-project.livekit.cloud.' }
}

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

/** The administrator's choice of how this organization holds its live classes. */
export default function LiveClassSettingsPanel() {
  const [provider, setProvider] = useState<string | null>(null)
  const [jitsi, setJitsi] = useState('')
  const [liveKitUrl, setLiveKitUrl] = useState('')
  const [liveKitKey, setLiveKitKey] = useState('')
  const [liveKitSecret, setLiveKitSecret] = useState('')
  const [secretSet, setSecretSet] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    apiRequest<Settings>('/api/v1/tenant/integrations/live-classes')
      .then((settings) => { setProvider(settings.provider); setJitsi(settings.jitsiBaseUrl ?? ''); setLiveKitUrl(settings.liveKitUrl ?? ''); setLiveKitKey(settings.liveKitApiKey ?? ''); setSecretSet(!!settings.liveKitSecretSet) })
      .catch((exception) => setError(readError(exception, 'Unable to load the live class settings.')))
  }, [])

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setNotice(null); setError(null)
    let message = provider === 'Jitsi' ? validateJitsiAddress(jitsi) : null
    if (provider === 'LiveKit') {
      message = validateLiveKitAddress(liveKitUrl)
      if (!message && !liveKitKey.trim()) message = 'Enter the LiveKit API key.'
      if (!message && !secretSet && !liveKitSecret.trim()) message = 'Enter the LiveKit API secret.'
    }
    setProblem(message)
    if (message) return
    setBusy(true)
    try {
      const saved = await apiRequest<Settings>('/api/v1/tenant/integrations/live-classes', { method: 'PUT', body: JSON.stringify({ provider, jitsiBaseUrl: provider === 'Jitsi' ? jitsi.trim() || null : null, ...(provider === 'LiveKit' ? { liveKitUrl: liveKitUrl.trim(), liveKitApiKey: liveKitKey.trim(), liveKitApiSecret: liveKitSecret.trim() || null } : {}) }) })
      setProvider(saved.provider); setJitsi(saved.jitsiBaseUrl ?? ''); setLiveKitUrl(saved.liveKitUrl ?? ''); setLiveKitKey(saved.liveKitApiKey ?? ''); setSecretSet(!!saved.liveKitSecretSet); setLiveKitSecret('')
      setNotice('Saved. New classes will use this. Classes already scheduled keep the tool they were created with.')
    } catch (exception) { setError(readError(exception, 'Unable to save the live class settings.')) }
    finally { setBusy(false) }
  }

  if (provider === null) return error ? <ErrorBanner message={error} /> : <p className="text-sm text-muted-foreground">Loading…</p>

  return (
    <FormLayout onSubmit={save}>
      <ErrorBanner message={error ?? problem} />
      <NoticeBanner message={notice} />
      <FormSection title="Where do your live classes happen?" description="Pick one. Scheduling, attendance, chat, polls and announcements stay in this system whichever you choose.">
        <div role="radiogroup" aria-label="Live class provider" className="flex flex-col gap-2">
          {liveProviderChoices.map((choice) => (
            <label key={choice.value} className={cn('flex cursor-pointer gap-3 rounded-md border border-border p-3 text-sm', provider === choice.value && 'border-primary bg-muted')}>
              <input type="radio" name="live-provider" className="mt-1" value={choice.value} checked={provider === choice.value} onChange={() => { setProvider(choice.value); setProblem(null) }} />
              <span><strong className="block">{choice.title}</strong><span className="text-muted-foreground">{choice.text}</span></span>
            </label>
          ))}
        </div>
      </FormSection>
      {provider === 'Jitsi' ? (
        <FormSection title="Jitsi server">
          <Field id="jitsi-url" label="Server address" className="max-w-md" hint="Leave blank to use the public server (https://meet.jit.si). Enter your own server’s address if you host one.">
            <Input id="jitsi-url" type="url" placeholder="https://meet.jit.si" value={jitsi} onChange={(event) => setJitsi(event.target.value)} />
          </Field>
        </FormSection>
      ) : null}
      {provider === 'LiveKit' ? (
        <FormSection title="LiveKit server" description="From your LiveKit Cloud project settings, or your own server. The secret is stored encrypted and is never shown again.">
          <Field id="livekit-url" label="Server address" required className="max-w-md">
            <Input id="livekit-url" type="url" placeholder="wss://your-project.livekit.cloud" value={liveKitUrl} onChange={(event) => setLiveKitUrl(event.target.value)} />
          </Field>
          <Field id="livekit-key" label="API key" required className="max-w-md">
            <Input id="livekit-key" autoComplete="off" value={liveKitKey} onChange={(event) => setLiveKitKey(event.target.value)} />
          </Field>
          <Field id="livekit-secret" label="API secret" required={!secretSet} className="max-w-md" hint={secretSet ? 'A secret is saved. Leave blank to keep it, or type a new one to replace it.' : undefined}>
            <Input id="livekit-secret" type="password" autoComplete="new-password" value={liveKitSecret} onChange={(event) => setLiveKitSecret(event.target.value)} />
          </Field>
        </FormSection>
      ) : null}
      <FormActions busy={busy} submitLabel="Save settings" />
    </FormLayout>
  )
}
