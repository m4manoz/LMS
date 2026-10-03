import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '../lib/api'
import { useAuth } from '../lib/auth'

type Focus = 'recommendations' | 'gamification' | 'virtual-labs' | 'offline'
type Recommendation = { courseId: string; courseCode: string; title: string; description?: string | null; categoryName?: string | null; score: number; reason: string; explanation: string; variant: string }
type Badge = { code: string; name: string; description: string; awardedAtUtc?: string }
type Profile = { totalPoints: number; currentStreakDays: number; longestStreakDays: number; lastActivityDateAd?: string | null; badges: Badge[]; recentEvents: { type: string; points: number; description: string; occurredAtUtc: string }[] }
type Lab = { id: string; code: string; name: string; description?: string | null; providerType: string; launchUrl?: string | null; status: string; healthStatus: string; lastHealthCheckUtc?: string | null; lastHealthError?: string | null; updatedAtUtc: string }
type Settings = { isEnabled: boolean; courseCompletionPoints: number; dailyPointCap: number }
type Enrollment = { courseId: string; courseTitle: string; status: string; progressPercent: number }
type OfflineDeviceCredentials = { id: string; name: string; deviceSecret: string; maxActivePackages: number }
type EncryptedOfflinePackage = { deviceId: string; itemId: string; expiresAtUtc: string; algorithm: string; nonceBase64: string; ciphertextBase64: string; tagBase64: string }

export default function AdvancedLearningPage({ focus }: { focus: Focus }) {
  const { session } = useAuth()
  const canManageLabs = session?.permissions.includes('virtuallab.manage') ?? false
  const canManageGamification = session?.permissions.includes('gamification.manage') ?? false
  const [recommendations, setRecommendations] = useState<Recommendation[]>([])
  const [profile, setProfile] = useState<Profile | null>(null)
  const [labs, setLabs] = useState<Lab[]>([])
  const [settings, setSettings] = useState<Settings | null>(null)
  const [enrollments, setEnrollments] = useState<Enrollment[]>([])
  const [selectedCourseId, setSelectedCourseId] = useState('')
  const [offlinePackage, setOfflinePackage] = useState<EncryptedOfflinePackage | null>(null)
  const [form, setForm] = useState({ code: '', name: '', providerType: 'external-simulation', description: '', launchUrl: '' })
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [registering, setRegistering] = useState(false)
  const [busy, setBusy] = useState(false)

  async function refresh() {
    try {
      setError(null)
      if (focus === 'recommendations') setRecommendations(await apiRequest<Recommendation[]>('/api/v1/tenant/learning/recommendations'))
      if (focus === 'gamification') {
        setProfile(await apiRequest<Profile>('/api/v1/tenant/gamification/me'))
        if (canManageGamification) setSettings(await apiRequest<Settings>('/api/v1/tenant/gamification/settings'))
      }
      if (focus === 'virtual-labs') setLabs(await apiRequest<Lab[]>('/api/v1/tenant/virtual-labs'))
      if (focus === 'offline') {
        const items = await apiRequest<Enrollment[]>('/api/v1/tenant/enrollments')
        const active = items.filter(item => item.status === 'Active' || item.status === 'Completed')
        setEnrollments(active)
        if (!selectedCourseId && active[0]) setSelectedCourseId(active[0].courseId)
      }
    } catch (exception) { setError(readError(exception, 'Unable to load this advanced learning module.')) }
  }

  useEffect(() => { void refresh() }, [focus])

  function openRegister() {
    setForm({ code: '', name: '', providerType: 'external-simulation', description: '', launchUrl: '' }); setProblem(null); setNotice(null); setRegistering(true)
  }

  async function createLab(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form.code.trim()) { setProblem('Enter a lab code.'); return }
    if (!form.name.trim()) { setProblem('Enter a lab name.'); return }
    if (!form.providerType.trim()) { setProblem('Enter a provider type.'); return }
    if (form.launchUrl.trim() && !isHttpUrl(form.launchUrl.trim())) { setProblem('Enter the launch URL as a full web address, for example https://provider.example/lab.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      await apiRequest('/api/v1/tenant/virtual-labs', { method: 'POST', body: JSON.stringify({ ...form, description: form.description || null, launchUrl: form.launchUrl || null }) })
      setRegistering(false); setNotice(`Lab “${form.name.trim()}” registered.`)
      setForm({ code: '', name: '', providerType: 'external-simulation', description: '', launchUrl: '' }); await refresh()
    } catch (exception) { setProblem(readError(exception, 'Unable to create the virtual lab.')) }
    finally { setBusy(false) }
  }

  async function dismissRecommendation(courseId: string) {
    try {
      await apiRequest(`/api/v1/tenant/learning/recommendations/${courseId}/dismiss`, { method: 'POST', body: '{}' })
      setRecommendations(items => items.filter(item => item.courseId !== courseId))
    } catch (exception) { setError(readError(exception, 'Unable to dismiss this recommendation.')) }
  }

  async function updateGamificationSettings(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!settings) return
    if (!Number.isInteger(settings.courseCompletionPoints) || settings.courseCompletionPoints < 0 || settings.courseCompletionPoints > 10000) { setError('Course completion points must be a whole number from 0 to 10000.'); return }
    if (!Number.isInteger(settings.dailyPointCap) || settings.dailyPointCap < 1 || settings.dailyPointCap > 100000) { setError('Daily point cap must be a whole number from 1 to 100000.'); return }
    setBusy(true); setError(null); setNotice(null)
    try { setSettings(await apiRequest<Settings>('/api/v1/tenant/gamification/settings', { method: 'PUT', body: JSON.stringify(settings) })); setNotice('Policy saved.') }
    catch (exception) { setError(readError(exception, 'Unable to update gamification settings.')) }
    finally { setBusy(false) }
  }

  async function checkLabHealth(labId: string) {
    setBusy(true); setError(null)
    try { await apiRequest(`/api/v1/tenant/virtual-labs/${labId}/health`, { method: 'POST', body: '{}' }); await refresh() }
    catch (exception) { setError(readError(exception, 'Unable to check the provider health.')) }
    finally { setBusy(false) }
  }

  async function launchLab(lab: Lab) {
    if (!lab.launchUrl) return
    setBusy(true); setError(null)
    const popup = window.open('about:blank', '_blank')
    try {
      const response = await apiRequest<{ launchUrl: string | null; token: string }>(`/api/v1/tenant/virtual-labs/${lab.id}/launch-token`)
      if (!response.launchUrl) throw new Error('This lab does not have a launch URL.')
      const url = new URL(response.launchUrl)
      url.searchParams.set('launchToken', response.token)
      if (popup) popup.location.href = url.toString()
      else window.location.assign(url.toString())
    } catch (exception) { popup?.close(); setError(readError(exception, 'Unable to create a secure lab launch.')) }
    finally { setBusy(false) }
  }

  async function prepareOffline() {
    if (!selectedCourseId) { setError('Choose an enrolled course first.'); return }
    if (!session) return
    setBusy(true); setError(null)
    try {
      const credentials = await ensureOfflineDevice(session)
      const encryptedPackage = await apiRequest<EncryptedOfflinePackage>(`/api/v1/tenant/offline/courses/${selectedCourseId}/package`, { headers: { 'X-Offline-Device-Id': credentials.id, 'X-Offline-Device-Secret': credentials.deviceSecret } })
      if (new Date(encryptedPackage.expiresAtUtc).getTime() <= Date.now()) throw new Error('The encrypted offline package has already expired.')
      const cache = await caches.open(`lms-offline-${session.tenant.id}-${session.user.id}`)
      await cache.put(`/api/v1/tenant/offline/courses/${selectedCourseId}/package?offline=1`, new Response(JSON.stringify(encryptedPackage), { headers: { 'Content-Type': 'application/json' } }))
      localStorage.setItem(`lms-offline-package:${session.tenant.id}:${session.user.id}:${selectedCourseId}`, JSON.stringify(encryptedPackage))
      setOfflinePackage(encryptedPackage)
      void apiRequest('/api/v1/tenant/telemetry/events', { method: 'POST', body: JSON.stringify({ name: 'pwa.offline_package_prepared', offlineDeviceId: credentials.id, propertiesJson: JSON.stringify({ courseId: selectedCourseId, expiresAtUtc: encryptedPackage.expiresAtUtc }) }) })
    } catch (exception) { setError(readError(exception, 'Unable to prepare offline learning content.')) }
    finally { setBusy(false) }
  }

  async function ensureOfflineDevice(currentSession: NonNullable<typeof session>): Promise<OfflineDeviceCredentials> {
    const storageKey = `lms-offline-device:${currentSession.tenant.id}:${currentSession.user.id}`
    const stored = localStorage.getItem(storageKey)
    if (stored) return JSON.parse(stored) as OfflineDeviceCredentials
    const fingerprintKey = `lms-offline-fingerprint:${currentSession.tenant.id}:${currentSession.user.id}`
    const fingerprint = localStorage.getItem(fingerprintKey) || crypto.randomUUID()
    localStorage.setItem(fingerprintKey, fingerprint)
    const device = await apiRequest<OfflineDeviceCredentials>('/api/v1/tenant/offline/devices', { method: 'POST', body: JSON.stringify({ name: `${navigator.platform || 'Browser'} offline device`, fingerprint }) })
    localStorage.setItem(storageKey, JSON.stringify(device))
    return device
  }

  const title = focus === 'recommendations' ? 'Learning recommendations' : focus === 'gamification' ? 'Learning achievements' : focus === 'virtual-labs' ? 'Virtual labs' : 'Offline learning'
  const description = focus === 'recommendations'
    ? 'Recommendations include assessment evidence, an explanation, an experiment variant and dismissal controls.'
    : focus === 'gamification'
      ? 'Progress points, streaks, badges and tenant policy controls are auditable.'
      : focus === 'virtual-labs'
        ? 'Virtual labs use short-lived signed launches, provider health checks and idempotent result callbacks.'
        : 'Prepare enrolled course metadata and approved assets for a 24-hour offline package.'
  return (
    <section className="col-span-full flex flex-col gap-4 text-foreground">
      <PageHeader title={title} description={description}
        actions={<><Button variant="outline" onClick={() => void refresh()}>Refresh</Button>{focus === 'virtual-labs' && canManageLabs ? <Button onClick={openRegister}><Plus className="mr-1 h-4 w-4" aria-hidden />Register lab</Button> : null}</>} />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />

      {focus === 'recommendations' ? (
        recommendations.length === 0 ? <EmptyState>No new published course recommendations are available yet.</EmptyState> : (
          <RowList label="Recommendations">
            {recommendations.map(item => (
              <ListRow key={item.courseId} columns="sm:grid-cols-[minmax(0,1fr)_auto_auto] md:grid-cols-[minmax(0,2fr)_110px_minmax(0,2fr)_auto]">
                <div className="min-w-0"><strong className="block truncate">{item.title}</strong><small className="text-muted-foreground">{item.courseCode}{item.categoryName ? ` · ${item.categoryName}` : ''} · {item.description || item.reason}</small></div>
                <div><Badge variant="default">{Math.round(item.score * 100)}% match</Badge></div>
                <div className="hidden text-muted-foreground md:block"><small className="block">Why</small>{item.explanation} · Experiment: {item.variant}</div>
                <div className="flex justify-end"><Button variant="soft" size="sm" aria-label={`Dismiss recommendation ${item.title}`} onClick={() => void dismissRecommendation(item.courseId)}>Dismiss</Button></div>
              </ListRow>
            ))}
          </RowList>
        )
      ) : null}

      {focus === 'gamification' && profile ? (
        <>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Metric label="Total points" value={profile.totalPoints} />
            <Metric label="Current streak" value={`${profile.currentStreakDays} days`} />
            <Metric label="Longest streak" value={`${profile.longestStreakDays} days`} />
            <Metric label="Badges" value={profile.badges.length} />
          </div>
          {canManageGamification && settings ? (
            <FormLayout onSubmit={updateGamificationSettings}>
              <FormSection title="Tenant gamification policy" description="Points and daily caps apply to every learner in your organization.">
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={settings.isEnabled} onChange={event => setSettings({ ...settings, isEnabled: event.target.checked })} />
                  Enabled
                </label>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field id="completion-points" label="Course completion points" required>
                    <Input id="completion-points" type="number" min="0" max="10000" value={settings.courseCompletionPoints} onChange={event => setSettings({ ...settings, courseCompletionPoints: Number(event.target.value) })} />
                  </Field>
                  <Field id="daily-cap" label="Daily point cap" required>
                    <Input id="daily-cap" type="number" min="1" max="100000" value={settings.dailyPointCap} onChange={event => setSettings({ ...settings, dailyPointCap: Number(event.target.value) })} />
                  </Field>
                </div>
              </FormSection>
              <FormActions busy={busy} submitLabel="Save policy" />
            </FormLayout>
          ) : null}
          <div className="grid gap-6 lg:grid-cols-2">
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-semibold">Badges earned</h3>
              {profile.badges.length === 0 ? <EmptyState>Complete learning activities to earn badges.</EmptyState> : (
                <RowList label="Badges earned">
                  {profile.badges.map(item => (
                    <ListRow key={item.code} columns="sm:grid-cols-[minmax(0,1fr)_auto]">
                      <div className="min-w-0"><strong className="block truncate">{item.name}</strong><small className="text-muted-foreground">{item.description}</small></div>
                      <small className="text-muted-foreground">{item.awardedAtUtc ? new Date(item.awardedAtUtc).toLocaleDateString() : ''}</small>
                    </ListRow>
                  ))}
                </RowList>
              )}
            </div>
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-semibold">Recent activity</h3>
              {profile.recentEvents.length === 0 ? <EmptyState>Your earned points will appear here.</EmptyState> : (
                <RowList label="Recent activity">
                  {profile.recentEvents.map((item, index) => (
                    <ListRow key={`${item.occurredAtUtc}-${index}`} columns="sm:grid-cols-[minmax(0,1fr)_auto]">
                      <div className="min-w-0"><strong className="block truncate">{item.description}</strong><small className="text-muted-foreground">{new Date(item.occurredAtUtc).toLocaleString()}</small></div>
                      <div><Badge variant="default">+{item.points}</Badge></div>
                    </ListRow>
                  ))}
                </RowList>
              )}
            </div>
          </div>
        </>
      ) : null}

      {focus === 'virtual-labs' ? (
        <>
          {labs.length === 0 ? <EmptyState>No virtual labs are configured for this tenant.</EmptyState> : (
            <RowList label="Virtual labs">
              {labs.map(lab => (
                <ListRow key={lab.id} columns="sm:grid-cols-[minmax(0,1fr)_auto] md:grid-cols-[minmax(0,2fr)_110px_minmax(0,2fr)_auto]">
                  <div className="min-w-0"><strong className="block truncate">{lab.name}</strong><small className="text-muted-foreground">{lab.code} · {lab.providerType} · {lab.description || 'External simulation integration.'}</small></div>
                  <div><Badge variant={lab.status === 'Active' ? 'default' : 'secondary'}>{lab.status}</Badge></div>
                  <div className="hidden text-muted-foreground md:block">
                    <small className="block">Provider health</small>
                    {lab.healthStatus}{lab.lastHealthCheckUtc ? ` · checked ${new Date(lab.lastHealthCheckUtc).toLocaleString()}` : ''}{lab.lastHealthError ? ` · ${lab.lastHealthError}` : ''}
                  </div>
                  <div className="flex justify-end gap-2">
                    {lab.launchUrl ? <Button variant="soft" size="sm" disabled={busy} aria-label={`Secure launch for ${lab.name}`} onClick={() => void launchLab(lab)}>Secure launch</Button> : null}
                    {canManageLabs ? <Button variant="secondary" size="sm" disabled={busy} aria-label={`Check health of ${lab.name}`} onClick={() => void checkLabHealth(lab.id)}>Check health</Button> : null}
                  </div>
                </ListRow>
              ))}
            </RowList>
          )}
          <SidePanel open={registering} label="Register a lab integration" onClose={() => setRegistering(false)}>
            <FormLayout onSubmit={createLab}>
              <ErrorBanner message={problem} />
              <FormSection title="About the lab">
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field id="lab-code" label="Lab code" required><Input id="lab-code" value={form.code} onChange={event => setForm({ ...form, code: event.target.value })} placeholder="PHYSICS-LAB" /></Field>
                  <Field id="lab-name" label="Lab name" required><Input id="lab-name" value={form.name} onChange={event => setForm({ ...form, name: event.target.value })} placeholder="Physics simulation" /></Field>
                </div>
                <Field id="lab-description" label="Description"><Textarea id="lab-description" value={form.description} onChange={event => setForm({ ...form, description: event.target.value })} placeholder="Description" rows={3} /></Field>
              </FormSection>
              <FormSection title="Provider">
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field id="lab-provider" label="Provider type" required><Input id="lab-provider" value={form.providerType} onChange={event => setForm({ ...form, providerType: event.target.value })} placeholder="Provider type" /></Field>
                  <Field id="lab-url" label="Launch URL"><Input id="lab-url" type="url" value={form.launchUrl} onChange={event => setForm({ ...form, launchUrl: event.target.value })} placeholder="https://provider.example/lab" /></Field>
                </div>
              </FormSection>
              <FormActions busy={busy} submitLabel="Register lab" onCancel={() => setRegistering(false)} />
            </FormLayout>
          </SidePanel>
        </>
      ) : null}

      {focus === 'offline' ? (
        <FormLayout onSubmit={event => { event.preventDefault(); void prepareOffline() }}>
          <FormSection title="Encrypted offline package" description="Packages are device-bound, encrypted, limited to three registered devices and expire after 24 hours.">
            <Field id="offline-course" label="Choose enrolled course" required>
              <Select id="offline-course" value={selectedCourseId} onChange={event => { setSelectedCourseId(event.target.value); setOfflinePackage(null) }}>
                <option value="">Select a course</option>
                {enrollments.map(item => <option value={item.courseId} key={item.courseId}>{item.courseTitle} · {item.progressPercent}%</option>)}
              </Select>
            </Field>
            {offlinePackage ? (
              <div className="flex items-start justify-between gap-3 rounded-md border border-border p-3 text-sm">
                <span>
                  <strong className="block">Encrypted offline learning content</strong>
                  <span className="text-muted-foreground">{offlinePackage.algorithm} package cached for this learner device.</span>
                  <small className="block text-muted-foreground">License expires {new Date(offlinePackage.expiresAtUtc).toLocaleString()}.</small>
                </span>
                <Badge>Protected offline</Badge>
              </div>
            ) : null}
          </FormSection>
          <FormActions busy={busy} submitLabel="Prepare encrypted offline package" busyLabel="Preparing encrypted package…" />
        </FormLayout>
      ) : null}
    </section>
  )
}

function Metric({ label, value }: { label: string; value: string | number }) {
  return (
    <Card>
      <CardHeader>
        <CardDescription>{label}</CardDescription>
        <CardTitle className="text-3xl">{value}</CardTitle>
      </CardHeader>
    </Card>
  )
}
function isHttpUrl(value: string) { try { const url = new URL(value); return url.protocol === 'http:' || url.protocol === 'https:' } catch { return false } }
function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
