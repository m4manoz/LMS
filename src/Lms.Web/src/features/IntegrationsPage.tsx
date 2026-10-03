import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ApiError, apiRequest } from '@/lib/api'
import LiveClassSettingsPanel from './LiveClassSettingsPanel'
import VideoAiSettingsPanel from './VideoAiSettingsPanel'

type EmailSettings = {
  provider: 'Smtp' | 'Log'; enabled: boolean; fromAddress: string; fromName: string; smtpHost: string | null; smtpPort: number
  smtpUseSsl: boolean; smtpUsername: string | null; hasPassword: boolean; smtpPasswordReference: string | null; updatedAtUtc: string | null
}
type OutboxItem = { id: string; recipient: string; subject: string; status: string; attemptCount: number; lastError: string | null; createdAtUtc: string; sentAtUtc: string | null }

type Form = {
  provider: 'Smtp' | 'Log'; enabled: boolean; fromAddress: string; fromName: string; smtpHost: string; smtpPort: string
  smtpUseSsl: boolean; smtpUsername: string; smtpPassword: string; clearPassword: boolean; smtpPasswordReference: string
}

export const toForm = (settings: EmailSettings): Form => ({
  provider: settings.provider, enabled: settings.enabled, fromAddress: settings.fromAddress, fromName: settings.fromName,
  smtpHost: settings.smtpHost ?? '', smtpPort: String(settings.smtpPort), smtpUseSsl: settings.smtpUseSsl,
  smtpUsername: settings.smtpUsername ?? '', smtpPassword: '', clearPassword: false, smtpPasswordReference: settings.smtpPasswordReference ?? '',
})

/**
 * Builds the save request. The password follows the API contract:
 * omitted keeps the stored one, "" clears it, anything else replaces it.
 */
export function toRequest(form: Form) {
  const body: Record<string, unknown> = {
    provider: form.provider, enabled: form.enabled, fromAddress: form.fromAddress, fromName: form.fromName,
    smtpHost: form.provider === 'Smtp' ? form.smtpHost : null, smtpPort: Number(form.smtpPort) || 587, smtpUseSsl: form.smtpUseSsl,
    smtpUsername: form.smtpUsername || null, smtpPasswordReference: form.smtpPasswordReference || null,
  }
  if (form.clearPassword) body.smtpPassword = ''
  else if (form.smtpPassword) body.smtpPassword = form.smtpPassword
  return body
}

export default function IntegrationsPage() {
  const [settings, setSettings] = useState<EmailSettings | null>(null)
  const [form, setForm] = useState<Form | null>(null)
  const [outbox, setOutbox] = useState<OutboxItem[]>([])
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function load() {
    try {
      const current = await apiRequest<EmailSettings>('/api/v1/tenant/integrations/email')
      setSettings(current); setForm(toForm(current))
    } catch (exception) { setError(readError(exception, 'Unable to load the email settings.')) }
  }
  async function loadOutbox() {
    try { setOutbox(await apiRequest<OutboxItem[]>('/api/v1/tenant/integrations/email/outbox')) }
    catch (exception) { setError(readError(exception, 'Unable to load the delivery log.')) }
  }
  useEffect(() => { void load(); void loadOutbox() }, [])

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form) return
    setProblem(null); setSaved(false); setTestResult(null)
    if (!/^\S+@\S+\.\S+$/.test(form.fromAddress.trim())) { setProblem('Enter a valid sender address.'); return }
    if (form.provider === 'Smtp' && !form.smtpHost.trim()) { setProblem('Enter the SMTP host.'); return }
    setBusy(true); setError(null)
    try {
      const next = await apiRequest<EmailSettings>('/api/v1/tenant/integrations/email', { method: 'PUT', body: JSON.stringify(toRequest(form)) })
      setSettings(next); setForm(toForm(next)); setSaved(true)
    } catch (exception) { setProblem(readError(exception, 'Unable to save the email settings.')) }
    finally { setBusy(false) }
  }

  async function sendTest() {
    setBusy(true); setError(null); setTestResult(null)
    try { setTestResult(await apiRequest<{ success: boolean; message: string }>('/api/v1/tenant/integrations/email/test', { method: 'POST' })) }
    catch (exception) { setError(readError(exception, 'Unable to send the test email.')) }
    finally { setBusy(false); void loadOutbox() }
  }

  const set = (patch: Partial<Form>) => form && setForm({ ...form, ...patch })

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Integrations" description="Connect your organization’s own services. Only administrators can see this page." />
      <ErrorBanner message={error} />
      <Tabs defaultValue="email">
        <TabsList>
          <TabsTrigger value="email">Email</TabsTrigger>
          <TabsTrigger value="live">Live classes</TabsTrigger>
          <TabsTrigger value="videoai">Video AI</TabsTrigger>
          <TabsTrigger value="log">Delivery log</TabsTrigger>
          <TabsTrigger value="others">Other providers</TabsTrigger>
        </TabsList>

        <TabsContent value="email">
          {!form || !settings ? <p className="text-sm text-muted-foreground">Loading…</p> : (
            <FormLayout onSubmit={save}>
              <div className="flex items-start justify-between gap-3">
                <p className="text-sm text-muted-foreground">Notifications are also emailed to people who have not opted out. In-app notifications always work.</p>
                <Badge variant={settings.enabled ? 'default' : 'secondary'}>{settings.enabled ? 'On' : 'Off'}</Badge>
              </div>
              <ErrorBanner message={problem} />
              {saved ? <NoticeBanner message="Saved." /> : null}
              {testResult ? (testResult.success ? <NoticeBanner message={testResult.message} /> : <ErrorBanner message={testResult.message} />) : null}

              <FormSection title="Provider">
                <label className="flex items-center gap-2 text-sm font-medium">
                  <input type="checkbox" checked={form.enabled} onChange={(e) => set({ enabled: e.target.checked })} />
                  Send notifications by email
                </label>
                <Field id="email-provider" label="Provider">
                  <Select id="email-provider" value={form.provider} onChange={(e) => set({ provider: e.target.value as Form['provider'] })}>
                    <option value="Smtp">SMTP server</option>
                    <option value="Log">Log only (testing — nothing is delivered)</option>
                  </Select>
                </Field>
              </FormSection>

              <FormSection title="Sender">
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field id="email-from" label="Sender address" required>
                    <Input id="email-from" type="email" value={form.fromAddress} onChange={(e) => set({ fromAddress: e.target.value })} placeholder="noreply@yourschool.edu" />
                  </Field>
                  <Field id="email-name" label="Sender name">
                    <Input id="email-name" value={form.fromName} onChange={(e) => set({ fromName: e.target.value })} maxLength={150} />
                  </Field>
                </div>
              </FormSection>

              {form.provider === 'Smtp' ? (
                <FormSection title="SMTP server">
                  <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_140px]">
                    <Field id="smtp-host" label="Host" required>
                      <Input id="smtp-host" value={form.smtpHost} onChange={(e) => set({ smtpHost: e.target.value })} placeholder="smtp.example.com" />
                    </Field>
                    <Field id="smtp-port" label="Port">
                      <Select id="smtp-port" value={form.smtpPort} onChange={(e) => set({ smtpPort: e.target.value })}>
                        <option value="587">587 (STARTTLS)</option><option value="465">465 (SSL)</option><option value="25">25</option><option value="2525">2525</option>
                      </Select>
                    </Field>
                  </div>
                  <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.smtpUseSsl} onChange={(e) => set({ smtpUseSsl: e.target.checked })} />Encrypt the connection (recommended)</label>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Field id="smtp-user" label="Username">
                      <Input id="smtp-user" autoComplete="off" value={form.smtpUsername} onChange={(e) => set({ smtpUsername: e.target.value })} />
                    </Field>
                    <Field id="smtp-password" label="Password">
                      <Input id="smtp-password" type="password" autoComplete="new-password" value={form.smtpPassword} disabled={form.clearPassword}
                        placeholder={settings.hasPassword ? 'Saved — leave blank to keep' : ''} onChange={(e) => set({ smtpPassword: e.target.value })} />
                    </Field>
                  </div>
                  {settings.hasPassword ? (
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.clearPassword} onChange={(e) => set({ clearPassword: e.target.checked, smtpPassword: '' })} />Remove the saved password</label>
                  ) : null}
                  <Field id="smtp-reference" label="Secret name (optional)" hint="If set, the password is read from your secret manager instead of the saved one.">
                    <Input id="smtp-reference" value={form.smtpPasswordReference} onChange={(e) => set({ smtpPasswordReference: e.target.value })} placeholder="e.g. SMTP_PASSWORD" />
                  </Field>
                </FormSection>
              ) : null}

              <FormActions busy={busy} submitLabel="Save settings">
                <Button type="button" variant="secondary" disabled={busy || !settings.updatedAtUtc} onClick={() => void sendTest()}>Send test email</Button>
              </FormActions>
            </FormLayout>
          )}
        </TabsContent>

        <TabsContent value="live"><LiveClassSettingsPanel /></TabsContent>

        <TabsContent value="videoai"><VideoAiSettingsPanel /></TabsContent>

        <TabsContent value="log">
          <div className="flex flex-col gap-4">
            <div className="flex items-center justify-between gap-3">
              <p className="text-sm text-muted-foreground">The last 50 messages. Failed messages are retried automatically; after 5 attempts they stop.</p>
              <Button variant="outline" size="sm" onClick={() => void loadOutbox()}>Refresh</Button>
            </div>
            {outbox.length === 0 ? <EmptyState>No emails yet.</EmptyState> : (
              <RowList label="Recent emails">
                {outbox.map((item) => (
                  <ListRow key={item.id} columns="md:grid-cols-[minmax(0,1fr)_110px]">
                    <div className="min-w-0">
                      <strong className="block truncate">{item.subject}</strong>
                      <small className="text-muted-foreground">To {item.recipient} · {new Date(item.createdAtUtc).toLocaleString()} · {item.attemptCount} attempt{item.attemptCount === 1 ? '' : 's'}</small>
                      {item.lastError ? <small className="block text-destructive">{item.lastError}</small> : null}
                    </div>
                    <div><Badge variant={item.status === 'Sent' ? 'secondary' : item.status === 'DeadLetter' ? 'destructive' : 'default'}>{item.status === 'DeadLetter' ? 'Gave up' : item.status}</Badge></div>
                  </ListRow>
                ))}
              </RowList>
            )}
          </div>
        </TabsContent>

        <TabsContent value="others">
          <div className="flex flex-col gap-4">
            <p className="text-sm text-muted-foreground">These providers are planned and cannot be configured yet.</p>
            <RowList label="Other providers">
              {['Payments', 'Live-class video', 'AI model provider', 'File storage'].map((name) => (
                <ListRow key={name} columns="md:grid-cols-[minmax(0,1fr)_auto]">
                  <span>{name}</span><div><Badge variant="secondary">Coming soon</Badge></div>
                </ListRow>
              ))}
            </RowList>
          </div>
        </TabsContent>
      </Tabs>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
