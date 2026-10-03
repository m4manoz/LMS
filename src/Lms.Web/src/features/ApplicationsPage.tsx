import { useCallback, useEffect, useState } from 'react'
import { Check, Copy, X } from 'lucide-react'
import { EmptyState, ErrorBanner, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { ApiError, apiRequest } from '@/lib/api'

type Application = { id: string; courseId: string; courseTitle: string; fullName: string; email: string; phone: string | null; message: string | null; status: string; createdAtUtc: string; decidedAtUtc: string | null; invitationId: string | null }
type Approval = { application: Application; token: string | null; link: string | null; expiresAtUtc: string | null; emailStatus: string; emailError: string | null }

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

/** What to tell staff after approving: where the invitation went and what to share when email did not do it. */
export function describeApproval(approval: Approval): string {
  const name = approval.application.fullName
  if (approval.emailStatus === 'AlreadyEnrolled') return `${name} is already in the course, so no invitation was needed.`
  if (approval.emailStatus === 'NotNeeded') return `${name} already has an account and was told about the invitation in the app.`
  if (approval.emailStatus === 'Sent') return `An invitation was emailed to ${approval.application.email}.`
  return `The email could not be sent${approval.emailError ? ` (${approval.emailError})` : ''}. Share the link or code below with ${name}.`
}

/** People who applied for a course from the public page. Approving sends them an invitation to create an account and join. */
export default function ApplicationsPage() {
  const [items, setItems] = useState<Application[] | null>(null)
  const [filter, setFilter] = useState('Pending')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [shared, setShared] = useState<Approval | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { const list = await apiRequest<Application[]>(`/api/v1/tenant/applications${filter === 'All' ? '' : `?status=${filter}`}`); setItems(Array.isArray(list) ? list : []) }
    catch (exception) { setItems([]); setError(readError(exception, 'Unable to load the applications.')) }
  }, [filter])
  useEffect(() => { void load() }, [load])

  async function approve(item: Application) {
    setBusy(item.id); setError(null); setNotice(null); setShared(null)
    try {
      const approval = await apiRequest<Approval>(`/api/v1/tenant/applications/${item.id}/approve`, { method: 'POST', body: JSON.stringify({}) })
      setNotice(describeApproval(approval))
      if (approval.emailStatus !== 'Sent' && approval.token) setShared(approval)
      await load()
    } catch (exception) { setError(readError(exception, 'Unable to approve the application.')) }
    finally { setBusy(null) }
  }

  async function decline(item: Application) {
    if (!window.confirm(`Decline the application of ${item.fullName}? They are not told automatically.`)) return
    setBusy(item.id); setError(null); setNotice(null); setShared(null)
    try { await apiRequest(`/api/v1/tenant/applications/${item.id}/decline`, { method: 'POST' }); setNotice(`${item.fullName}’s application was declined.`); await load() }
    catch (exception) { setError(readError(exception, 'Unable to decline the application.')) }
    finally { setBusy(null) }
  }

  const copy = (value: string) => void navigator.clipboard?.writeText(value).then(() => setNotice('Copied.')).catch(() => undefined)

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Applications" description="People who applied for a course from your public page. Approve one to send an invitation to create an account and join the course."
        actions={<Select aria-label="Show applications" className="w-40" value={filter} onChange={(event) => setFilter(event.target.value)}>{['Pending', 'Approved', 'Declined', 'All'].map((value) => <option key={value} value={value}>{value}</option>)}</Select>} />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      {shared ? (
        <section aria-label="Invitation to share" className="flex flex-col gap-2 rounded-md border border-border p-4 text-sm">
          <strong>Share this with {shared.application.fullName}</strong>
          {shared.link ? <div className="flex items-center gap-2"><code className="min-w-0 flex-1 truncate rounded bg-muted px-2 py-1">{shared.link}</code><Button type="button" size="sm" variant="outline" onClick={() => copy(shared.link!)}><Copy className="mr-1 h-4 w-4" aria-hidden />Copy link</Button></div> : null}
          {shared.token ? <div className="flex items-center gap-2"><span className="text-muted-foreground">Code</span><code className="rounded bg-muted px-2 py-1">{shared.token}</code><Button type="button" size="sm" variant="outline" onClick={() => copy(shared.token!)}><Copy className="mr-1 h-4 w-4" aria-hidden />Copy code</Button></div> : null}
          <small className="text-muted-foreground">It is shown only once. The person opens the link, or uses the code on the “I have an invitation” page.</small>
        </section>
      ) : null}
      {items === null ? <p className="text-sm text-muted-foreground">Loading…</p> : items.length === 0 ? <EmptyState>{filter === 'Pending' ? 'No applications are waiting.' : 'No applications here.'}</EmptyState> : (
        <RowList label="Applications">
          {items.map((item) => (
            <ListRow key={item.id} columns="sm:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)_100px_auto]">
              <div className="min-w-0"><strong className="block truncate">{item.fullName}</strong><small className="text-muted-foreground">{item.email}{item.phone ? ` · ${item.phone}` : ''}</small>{item.message ? <p className="mt-1 line-clamp-2 text-sm text-muted-foreground">“{item.message}”</p> : null}</div>
              <div className="min-w-0"><small className="block text-muted-foreground">Applied for</small><span className="block truncate">{item.courseTitle}</span><small className="text-muted-foreground">{new Date(item.createdAtUtc).toLocaleDateString()}</small></div>
              <div><Badge variant={item.status === 'Approved' ? 'default' : item.status === 'Declined' ? 'outline' : 'secondary'}>{item.status}</Badge></div>
              <div className="flex justify-end gap-1.5">
                {item.status !== 'Approved' ? <Button type="button" size="sm" disabled={busy !== null} aria-label={`Approve ${item.fullName}`} onClick={() => void approve(item)}><Check className="mr-1 h-4 w-4" aria-hidden />Approve</Button> : null}
                {item.status === 'Pending' ? <Button type="button" size="sm" variant="outline" disabled={busy !== null} aria-label={`Decline ${item.fullName}`} onClick={() => void decline(item)}><X className="mr-1 h-4 w-4" aria-hidden />Decline</Button> : null}
              </div>
            </ListRow>
          ))}
        </RowList>
      )}
    </section>
  )
}
