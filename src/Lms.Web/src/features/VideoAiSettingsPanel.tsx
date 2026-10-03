import { useEffect, useState } from 'react'
import { ErrorBanner, Field, FormActions, FormLayout, FormSection, NoticeBanner } from '@/components/form'
import { Input } from '@/components/ui/input'
import { ApiError, apiRequest } from '@/lib/api'
import { cn } from '@/lib/utils'

type Settings = { provider: string; baseUrl: string; apiKeySet: boolean; transcriptionModel: string; chatModel: string; autoTranscribe: boolean; conversionAvailable: boolean }

export const videoAiChoices = [
  { value: 'Local', title: 'Built in (nothing leaves the system)', text: 'Summaries and practice questions are picked from the transcript itself. Transcripts are added by hand (WebVTT or SRT). Simple, private and free.' },
  { value: 'OpenAiCompatible', title: 'An AI service (OpenAI or compatible)', text: 'Transcripts are made from the sound of the video, and summaries and questions are written by a language model. Works with OpenAI and with services or self-hosted servers that copy its API. The transcript and the sound are sent to that service.' },
] as const

/** A usable service address: https, no sign-in details, nothing after the path. Mirrors the server's check. */
export function validateServiceAddress(value: string): string | null {
  const text = value.trim()
  if (!text) return null // blank means OpenAI
  try {
    const url = new URL(text)
    if (url.protocol !== 'https:') return 'Use an https address, for example https://api.openai.com/v1.'
    if (url.username || url.password) return 'The address must not contain a user name or password.'
    if (url.search || url.hash) return 'The address must not contain a query or fragment.'
    return null
  } catch { return 'Enter the service address as a link, for example https://api.openai.com/v1.' }
}

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

/** The administrator's choice of who listens to videos and writes about them. */
export default function VideoAiSettingsPanel() {
  const [loaded, setLoaded] = useState<Settings | null>(null)
  const [provider, setProvider] = useState('Local')
  const [baseUrl, setBaseUrl] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [keySet, setKeySet] = useState(false)
  const [transcriptionModel, setTranscriptionModel] = useState('')
  const [chatModel, setChatModel] = useState('')
  const [auto, setAuto] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const apply = (settings: Settings) => {
    setLoaded(settings); setProvider(settings.provider); setBaseUrl(settings.baseUrl); setKeySet(settings.apiKeySet)
    setTranscriptionModel(settings.transcriptionModel); setChatModel(settings.chatModel); setAuto(settings.autoTranscribe); setApiKey('')
  }
  useEffect(() => {
    apiRequest<Settings>('/api/v1/tenant/integrations/video-ai').then(apply).catch((exception) => setError(readError(exception, 'Unable to load the video AI settings.')))
  }, [])

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setNotice(null); setError(null)
    let message: string | null = null
    if (provider === 'OpenAiCompatible') {
      message = validateServiceAddress(baseUrl)
      if (!message && !keySet && !apiKey.trim()) message = 'Enter the API key.'
    }
    setProblem(message)
    if (message) return
    setBusy(true)
    try {
      const body = provider === 'OpenAiCompatible'
        ? { provider, baseUrl: baseUrl.trim() || null, apiKey: apiKey.trim() || null, transcriptionModel: transcriptionModel.trim() || null, chatModel: chatModel.trim() || null, autoTranscribe: auto }
        : { provider }
      apply(await apiRequest<Settings>('/api/v1/tenant/integrations/video-ai', { method: 'PUT', body: JSON.stringify(body) }))
      setNotice('Saved. New transcripts, summaries and questions will use this.')
    } catch (exception) { setError(readError(exception, 'Unable to save the video AI settings.')) }
    finally { setBusy(false) }
  }

  if (loaded === null) return error ? <ErrorBanner message={error} /> : <p className="text-sm text-muted-foreground">Loading…</p>

  return (
    <FormLayout onSubmit={save}>
      <ErrorBanner message={error ?? problem} />
      <NoticeBanner message={notice} />
      <FormSection title="Who listens to your videos and writes about them?" description="Transcripts make videos searchable. Summaries and practice questions are drafts that teachers check before learners see them.">
        <div role="radiogroup" aria-label="Video AI provider" className="flex flex-col gap-2">
          {videoAiChoices.map((choice) => (
            <label key={choice.value} className={cn('flex cursor-pointer gap-3 rounded-md border border-border p-3 text-sm', provider === choice.value && 'border-primary bg-muted')}>
              <input type="radio" name="video-ai-provider" className="mt-1" value={choice.value} checked={provider === choice.value} onChange={() => { setProvider(choice.value); setProblem(null) }} />
              <span><strong className="block">{choice.title}</strong><span className="text-muted-foreground">{choice.text}</span></span>
            </label>
          ))}
        </div>
      </FormSection>
      {provider === 'OpenAiCompatible' ? (
        <FormSection title="The service" description="The key is stored encrypted and is never shown again.">
          <Field id="ai-url" label="Service address" className="max-w-md" hint="Leave blank for OpenAI (https://api.openai.com/v1). Enter another address for a compatible service.">
            <Input id="ai-url" type="url" placeholder="https://api.openai.com/v1" value={baseUrl} onChange={(event) => setBaseUrl(event.target.value)} />
          </Field>
          <Field id="ai-key" label="API key" required={!keySet} className="max-w-md" hint={keySet ? 'A key is saved. Leave blank to keep it, or type a new one to replace it.' : undefined}>
            <Input id="ai-key" type="password" autoComplete="new-password" value={apiKey} onChange={(event) => setApiKey(event.target.value)} />
          </Field>
          <Field id="ai-transcription" label="Speech-to-text model" className="max-w-md"><Input id="ai-transcription" value={transcriptionModel} onChange={(event) => setTranscriptionModel(event.target.value)} /></Field>
          <Field id="ai-chat" label="Writing model" className="max-w-md" hint="Used for summaries and practice questions."><Input id="ai-chat" value={chatModel} onChange={(event) => setChatModel(event.target.value)} /></Field>
          <label className="flex items-start gap-2 text-sm">
            <input type="checkbox" className="mt-1" checked={auto} onChange={(event) => setAuto(event.target.checked)} />
            <span><strong className="block">Transcribe every new video automatically</strong><span className="text-muted-foreground">Once an uploaded video has been converted, its sound is sent for a transcript. Otherwise teachers ask for one per video.</span></span>
          </label>
          {loaded.conversionAvailable ? null : <p role="note" className="text-sm text-muted-foreground">Video conversion (FFmpeg) is not set up on this server, so the sound cannot be taken from uploaded videos yet. Pasted transcripts, summaries and questions still work.</p>}
        </FormSection>
      ) : null}
      <FormActions busy={busy} submitLabel="Save settings" />
    </FormLayout>
  )
}
