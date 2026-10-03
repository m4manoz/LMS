import { useCallback, useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { parseInviteHash } from '@/lib/inviteLink'

type Mine = { id: string; courseId: string; courseCode: string; courseTitle: string; invitedBy: string; message: string | null; createdAtUtc: string; expiresAtUtc: string }
type Sent = { id: string; courseId: string; courseTitle: string; email: string; status: string; invitedBy: string; createdAtUtc: string; expiresAtUtc: string; emailStatus?: string | null }
type Created = { token: string; link: string | null; hasAccount: boolean; emailStatus: string; emailError: string | null; email: string }
type Course = { id: string; code: string; title: string; status: string }
type Accepted = { outcome: string; courseTitle: string }

/** What happened to the email, in words for the person who sent the invitation. */
export function emailOutcome(created: Pick<Created, 'email' | 'emailStatus' | 'emailError' | 'hasAccount'>): string {
  switch (created.emailStatus) {
    case 'Sent': return `We emailed the invitation, with a sign-up link, to ${created.email}.`
    case 'Failed': return `The email to ${created.email} could not be sent${created.emailError ? ` (${created.emailError})` : ''}. Share the link or code yourself.`
    case 'NotNeeded': return `${created.email} already has an account, so they were notified in the app. They can accept from Invitations.`
    default: return 'Email is not set up for this organization, so nothing was emailed. Share the link or code yourself.'
  }
}

export const acceptedMessage = (accepted: Accepted) =>
  accepted.outcome === 'Waitlisted' ? `${accepted.courseTitle} is full, so you are on the waitlist and will be enrolled when a place opens.`
    : accepted.outcome === 'AlreadyEnrolled' ? `You are already enrolled in ${accepted.courseTitle}.` : `You are enrolled in ${accepted.courseTitle}.`

/** "3 days left", "today", or "expired". */
export function expiresIn(iso: string, now = new Date()): string {
  const ms = new Date(iso).getTime() - now.getTime()
  if (ms <= 0) return 'expired'
  const days = Math.ceil(ms / 86400000)
  return days <= 1 ? 'expires today' : `${days} days left`
}

const statusVariant = (status: string) => (status === 'Pending' ? 'default' : status === 'Accepted' ? 'secondary' : status === 'Expired' || status === 'Revoked' || status === 'Declined' ? 'outline' : 'secondary') as 'default' | 'secondary' | 'outline'

const emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
const emptyForm = { courseId: '', email: '', message: '', days: '14' }

export default function InvitationsPage() {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('enrollment.manage') ?? false
  const [mine, setMine] = useState<Mine[]>([])
  const [sent, setSent] = useState<Sent[]>([])
  const [courses, setCourses] = useState<Course[]>([])
  const [filter, setFilter] = useState('')
  const [code, setCode] = useState(() => parseInviteHash(window.location.hash)?.token ?? '')
  const [form, setForm] = useState(emptyForm)
  const [creating, setCreating] = useState(false)
  const [created, setCreated] = useState<Created | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState(false)

  const loadMine = useCallback(async () => {
    try { setMine(await apiRequest<Mine[]>('/api/v1/tenant/invitations/mine')) }
    catch (exception) { setError(readError(exception, 'Unable to load your invitations.')) }
  }, [])
  const loadSent = useCallback(async (status = filter) => {
    if (!canManage) return
    try { setSent(await apiRequest<Sent[]>(`/api/v1/tenant/invitations${status ? `?status=${status}` : ''}`)) }
    catch (exception) { setError(readError(exception, 'Unable to load the invitations you sent.')) }
  }, [canManage, filter])
  useEffect(() => {
    void loadMine(); void loadSent()
    if (canManage) apiRequest<Course[]>('/api/v1/tenant/courses').then((items) => setCourses(items.filter((item) => item.status === 'Published'))).catch(() => setCourses([]))
  }, [])

  async function run(action: () => Promise<void>, failure: string, report: (message: string) => void = setError) {
    setBusy(true); setError(null); setNotice(null); setProblem(null)
    try { await action() } catch (exception) { report(readError(exception, failure)) } finally { setBusy(false) }
  }

  const respond = (path: string, body?: object) => run(async () => {
    const accepted = await apiRequest<Accepted | undefined>(path, { method: 'POST', ...(body ? { body: JSON.stringify(body) } : {}) })
    setNotice(accepted && 'outcome' in accepted ? acceptedMessage(accepted) : 'Invitation declined.')
    await loadMine()
  }, 'That did not work.')

  const startCreate = () => { setForm(emptyForm); setCreated(null); setProblem(null); setCopied(false); setCreating(true) }
  const close = () => { setCreating(false); setCreated(null); setProblem(null) }

  const send = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const days = Number(form.days)
    if (!form.courseId) { setProblem('Choose a course.'); return Promise.resolve() }
    if (!emailPattern.test(form.email.trim())) { setProblem('Enter a valid email address.'); return Promise.resolve() }
    if (!Number.isInteger(days) || days < 1 || days > 90) { setProblem('Enter a validity of 1 to 90 days.'); return Promise.resolve() }
    return run(async () => {
      const result = await apiRequest<Omit<Created, 'email'>>('/api/v1/tenant/invitations', { method: 'POST', body: JSON.stringify({ courseId: form.courseId, email: form.email, message: form.message, expiresInDays: days }) })
      setCreated({ ...result, email: form.email }); setCopied(false)
      setForm({ ...form, email: '', message: '' }); await loadSent()
    }, 'Unable to send the invitation.', setProblem)
  }

  const copy = async () => { if (created) { try { await navigator.clipboard.writeText(created.link ?? created.token); setCopied(true) } catch { setCopied(false) } } }

  const useCode = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!code.trim()) { setError('Paste the invitation code first.'); return }
    void respond('/api/v1/tenant/invitations/accept', { token: code.trim() }).then(() => setCode(''))
  }

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Invitations" description={`Accept an invitation to join a course${canManage ? ', or invite people to yours' : ''}.`}
        actions={canManage ? <Button onClick={startCreate}><Plus className="mr-1 h-4 w-4" aria-hidden />New invitation</Button> : undefined} />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />

      <Tabs defaultValue="mine">
        <TabsList>
          <TabsTrigger value="mine">My invitations ({mine.length})</TabsTrigger>
          {canManage ? <TabsTrigger value="sent">Sent</TabsTrigger> : null}
        </TabsList>

        <TabsContent value="mine">
          <div className="flex flex-col gap-6">
            {mine.length === 0 ? <EmptyState>You have no pending invitations.</EmptyState> : (
              <RowList label="My invitations">
                {mine.map((invitation) => (
                  <ListRow key={invitation.id} columns="md:grid-cols-[minmax(0,1fr)_auto]">
                    <div className="min-w-0">
                      <small className="block text-muted-foreground">{invitation.courseCode}</small>
                      <strong className="block truncate">{invitation.courseTitle}</strong>
                      <small className="block text-muted-foreground">From {invitation.invitedBy} · {expiresIn(invitation.expiresAtUtc)}</small>
                      {invitation.message ? <p className="mt-1 whitespace-pre-wrap">{invitation.message}</p> : null}
                    </div>
                    <div className="flex gap-2">
                      <Button disabled={busy} aria-label={`Accept invitation to ${invitation.courseTitle}`} onClick={() => void respond(`/api/v1/tenant/invitations/${invitation.id}/accept`)}>Accept</Button>
                      <Button variant="outline" disabled={busy} aria-label={`Decline invitation to ${invitation.courseTitle}`} onClick={() => void respond(`/api/v1/tenant/invitations/${invitation.id}/decline`)}>Decline</Button>
                    </div>
                  </ListRow>
                ))}
              </RowList>
            )}

            <FormLayout onSubmit={useCode} className="max-w-xl">
              <FormSection title="Have an invitation code?" description="Paste the code you were sent. You must be signed in with the email address it was sent to.">
                <div className="flex items-end gap-2">
                  <Field id="invite-code" label="Invitation code" className="flex-1"><Input id="invite-code" autoComplete="off" value={code} onChange={(e) => setCode(e.target.value)} /></Field>
                  <Button type="submit" disabled={busy}>Use code</Button>
                </div>
              </FormSection>
            </FormLayout>
          </div>
        </TabsContent>

        {canManage ? (
          <TabsContent value="sent">
            <div className="flex flex-col gap-4">
              <Field id="sent-filter" label="Show" className="max-w-xs">
                <Select id="sent-filter" value={filter} onChange={(e) => { setFilter(e.target.value); void loadSent(e.target.value) }}>
                  <option value="">All</option><option value="pending">Pending</option><option value="accepted">Accepted</option><option value="declined">Declined</option><option value="expired">Expired</option><option value="revoked">Revoked</option>
                </Select>
              </Field>
              {sent.length === 0 ? <EmptyState>No invitations.</EmptyState> : (
                <RowList label="Sent invitations">
                  {sent.map((item) => (
                    <ListRow key={item.id} columns="md:grid-cols-[minmax(0,1fr)_110px_90px]">
                      <div className="min-w-0"><strong className="block truncate">{item.email}</strong><small className="text-muted-foreground">{item.courseTitle} · sent {new Date(item.createdAtUtc).toLocaleDateString()} by {item.invitedBy}{item.emailStatus === 'Sent' ? ' · emailed' : item.emailStatus === 'Failed' ? ' · email failed' : ''}</small></div>
                      <div><Badge variant={statusVariant(item.status)}>{item.status}</Badge></div>
                      <div className="flex justify-end">
                        {item.status === 'Pending' ? <Button variant="softDestructive" size="sm" disabled={busy} aria-label={`Revoke invitation for ${item.email}`} onClick={() => { if (window.confirm(`Revoke the invitation for ${item.email}?`)) void run(async () => { await apiRequest(`/api/v1/tenant/invitations/${item.id}/revoke`, { method: 'POST' }); await loadSent() }, 'Unable to revoke the invitation.') }}>Revoke</Button> : null}
                      </div>
                    </ListRow>
                  ))}
                </RowList>
              )}
            </div>
          </TabsContent>
        ) : null}
      </Tabs>

      <SidePanel open={creating} label="New invitation" onClose={close}>
        <div className="flex flex-col gap-6">
          {created ? (
            <div role="status" className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm">
              <strong>Invitation created for {created.email}</strong>
              <span className={created.emailStatus === 'Failed' ? 'text-destructive' : 'text-muted-foreground'}>{emailOutcome(created)}</span>
              <span className="text-muted-foreground">The code is shown only once. Whoever holds it can join as that address, so send it only to them.</span>
              <code className="break-all rounded bg-muted px-2 py-1">{created.link ?? created.token}</code>
              <div><Button type="button" variant="outline" size="sm" onClick={() => void copy()}>{copied ? 'Copied' : created.link ? 'Copy link' : 'Copy code'}</Button></div>
            </div>
          ) : null}
          <FormLayout onSubmit={send}>
            <ErrorBanner message={problem} />
            <FormSection title="Who and what" description="People with an account are notified in the app. People without one are emailed a link to create their account and join. You also get the one-time code and link to send yourself.">
              <Field id="invite-course" label="Course" required>
                <Select id="invite-course" value={form.courseId} onChange={(e) => setForm({ ...form, courseId: e.target.value })}>
                  <option value="">Choose a published course</option>
                  {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
                </Select>
              </Field>
              <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_140px]">
                <Field id="invite-email" label="Email address" required><Input id="invite-email" type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} /></Field>
                <Field id="invite-days" label="Valid for (days)"><Input id="invite-days" type="number" min="1" max="90" value={form.days} onChange={(e) => setForm({ ...form, days: e.target.value })} /></Field>
              </div>
              <Field id="invite-message" label="Message (optional)"><Textarea id="invite-message" rows={2} maxLength={1000} value={form.message} onChange={(e) => setForm({ ...form, message: e.target.value })} /></Field>
            </FormSection>
            <FormActions busy={busy} submitLabel="Send invitation" busyLabel="Sending…" onCancel={close} />
          </FormLayout>
        </div>
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
