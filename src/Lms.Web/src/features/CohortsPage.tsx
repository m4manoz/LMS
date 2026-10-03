import { useCallback, useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'

type Cohort = { id: string; name: string; description: string | null; startDateAd: string | null; endDateAd: string | null; memberCount: number }
type Member = { userId: string; name: string; email: string }
type Detail = Cohort & { members: Member[]; skipped: number }
type Contact = { userId: string; name: string; email: string }
type Course = { id: string; code: string; title: string; status: string }
type Outcome = { userId: string; name: string; outcome: string; message: string | null; missingPrerequisites: string[] }
type Bulk = { results: Outcome[]; succeeded: number; waitlisted: number; alreadyIn: number; blocked: number }

/** Plain-language label for what happened to one person. */
export const outcomeLabel: Record<string, string> = {
  Enrolled: 'Enrolled', Invited: 'Invited', Waitlisted: 'On the waitlist', AlreadyEnrolled: 'Already in',
  MissingPrerequisites: 'Missing a prerequisite', NotAvailable: 'Not available',
}

export function summaryLine(result: Bulk, verb: 'enrolled' | 'invited'): string {
  const parts = [`${result.succeeded} ${verb}`]
  if (result.waitlisted > 0) parts.push(`${result.waitlisted} on the waitlist`)
  if (result.alreadyIn > 0) parts.push(`${result.alreadyIn} already in`)
  if (result.blocked > 0) parts.push(`${result.blocked} blocked`)
  return parts.join(' · ')
}

const emptyForm = { name: '', description: '', startDateAd: '', endDateAd: '' }

export default function CohortsPage() {
  const [cohorts, setCohorts] = useState<Cohort[]>([])
  const [detail, setDetail] = useState<Detail | null>(null)
  const [creating, setCreating] = useState(false)
  const [courses, setCourses] = useState<Course[]>([])
  const [contacts, setContacts] = useState<Contact[]>([])
  const [filter, setFilter] = useState('')
  const [search, setSearch] = useState('')
  const [courseId, setCourseId] = useState('')
  const [override, setOverride] = useState(false)
  const [result, setResult] = useState<{ kind: 'enrolled' | 'invited'; data: Bulk } | null>(null)
  const [form, setForm] = useState(emptyForm)
  const [problem, setProblem] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const loadCohorts = useCallback(async () => {
    try { setCohorts(await apiRequest<Cohort[]>('/api/v1/tenant/cohorts')) }
    catch (exception) { setError(readError(exception, 'Unable to load cohorts.')) }
  }, [])
  useEffect(() => {
    void loadCohorts()
    apiRequest<Course[]>('/api/v1/tenant/courses').then((items) => setCourses(items.filter((item) => item.status === 'Published'))).catch(() => setCourses([]))
  }, [loadCohorts])

  // The same people search used by Messages: any active member of the organization.
  useEffect(() => {
    if (!detail) return
    const timer = window.setTimeout(() => {
      apiRequest<Contact[]>(`/api/v1/tenant/messages/contacts${search.trim() ? `?q=${encodeURIComponent(search.trim())}` : ''}`).then(setContacts).catch(() => setContacts([]))
    }, 250)
    return () => window.clearTimeout(timer)
  }, [detail?.id, search])

  async function run(action: () => Promise<void>, failure: string, report: (message: string) => void = setError) {
    setBusy(true); setError(null); setProblem(null)
    try { await action() } catch (exception) { report(readError(exception, failure)) } finally { setBusy(false) }
  }

  const open = (id: string) => run(async () => { setResult(null); setCreating(false); setSearch(''); setDetail(await apiRequest<Detail>(`/api/v1/tenant/cohorts/${id}`)) }, 'Unable to open the cohort.')
  const startCreate = () => { setForm(emptyForm); setProblem(null); setError(null); setDetail(null); setCreating(true) }
  const close = () => { setCreating(false); setDetail(null); setResult(null); setProblem(null); setError(null) }

  const create = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (form.name.trim() === '') { setProblem('Enter a name for the cohort.'); return Promise.resolve() }
    if (form.startDateAd && form.endDateAd && form.endDateAd < form.startDateAd) { setProblem('The end date cannot be before the start date.'); return Promise.resolve() }
    return run(async () => {
      const created = await apiRequest<Detail>('/api/v1/tenant/cohorts', { method: 'POST', body: JSON.stringify({ name: form.name, description: form.description, startDateAd: form.startDateAd || null, endDateAd: form.endDateAd || null }) })
      setForm(emptyForm)
      await loadCohorts(); setCreating(false); setDetail(created)
    }, 'Unable to create the cohort.', setProblem)
  }

  const addMember = (userId: string) => detail && run(async () => {
    setDetail(await apiRequest<Detail>(`/api/v1/tenant/cohorts/${detail.id}/members`, { method: 'POST', body: JSON.stringify({ userIds: [userId] }) })); await loadCohorts()
  }, 'Unable to add that person.')

  const removeMember = (userId: string) => detail && run(async () => {
    await apiRequest(`/api/v1/tenant/cohorts/${detail.id}/members/${userId}`, { method: 'DELETE' })
    setDetail(await apiRequest<Detail>(`/api/v1/tenant/cohorts/${detail.id}`)); await loadCohorts()
  }, 'Unable to remove that person.')

  const deleteCohort = () => {
    if (!detail || !window.confirm(`Delete the cohort "${detail.name}"?`)) return
    return run(async () => {
      await apiRequest(`/api/v1/tenant/cohorts/${detail.id}`, { method: 'DELETE' }); setDetail(null); setResult(null); await loadCohorts()
    }, 'Unable to delete the cohort.')
  }

  const bulk = (kind: 'enroll' | 'invite') => detail && courseId && run(async () => {
    const data = await apiRequest<Bulk>(`/api/v1/tenant/cohorts/${detail.id}/${kind}`, { method: 'POST', body: JSON.stringify(kind === 'enroll' ? { courseId, overridePrerequisites: override } : { courseId }) })
    setResult({ kind: kind === 'enroll' ? 'enrolled' : 'invited', data })
  }, kind === 'enroll' ? 'Unable to enroll the cohort.' : 'Unable to send the invitations.')

  const memberIds = new Set(detail?.members.map((member) => member.userId))
  const needle = filter.trim().toLowerCase()
  const shown = cohorts.filter((cohort) => !needle || cohort.name.toLowerCase().includes(needle) || (cohort.description ?? '').toLowerCase().includes(needle))
  const panelOpen = creating || detail !== null

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Cohorts" description="Group learners into classes or batches, then enroll or invite the whole group to a course at once."
        actions={<><span className="text-sm text-muted-foreground">{cohorts.length} cohort{cohorts.length === 1 ? '' : 's'}</span><Button onClick={startCreate}><Plus className="mr-1 h-4 w-4" aria-hidden />New cohort</Button></>} />
      {!panelOpen ? <ErrorBanner message={error} /> : null}

      <Input className="max-w-sm" type="search" placeholder="Search cohorts" aria-label="Search cohorts" value={filter} onChange={(event) => setFilter(event.target.value)} />

      {cohorts.length === 0 ? <EmptyState>No cohorts yet.</EmptyState> : shown.length === 0 ? <EmptyState>No cohorts match.</EmptyState> : (
        <RowList label="Cohorts">
          {shown.map((cohort) => (
            <ListRow key={cohort.id} selected={detail?.id === cohort.id} columns="md:grid-cols-[minmax(0,2fr)_110px_minmax(0,1fr)_auto]">
              <div className="min-w-0"><strong className="block truncate">{cohort.name}</strong><small className="block truncate text-muted-foreground">{cohort.description || 'No description'}</small></div>
              <div><Badge variant="outline">{cohort.memberCount} member{cohort.memberCount === 1 ? '' : 's'}</Badge></div>
              <div className="hidden text-muted-foreground md:block">{cohort.startDateAd || cohort.endDateAd ? `${cohort.startDateAd ?? '…'} to ${cohort.endDateAd ?? '…'}` : 'No dates'}</div>
              <div className="flex justify-end"><Button variant="soft" size="sm" disabled={busy} aria-label={`View details for ${cohort.name}`} onClick={() => void open(cohort.id)}>View details</Button></div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={panelOpen} label={creating ? 'New cohort' : (detail?.name ?? 'Cohort')} onClose={close}>
        {creating ? (
          <FormLayout onSubmit={create}>
            <ErrorBanner message={problem} />
            <FormSection title="About the cohort" description="You add the people on the next screen.">
              <Field id="cohort-name" label="Name" required><Input id="cohort-name" value={form.name} maxLength={120} onChange={(e) => setForm({ ...form, name: e.target.value })} /></Field>
              <Field id="cohort-description" label="Description"><Textarea id="cohort-description" rows={2} maxLength={1000} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} /></Field>
            </FormSection>
            <FormSection title="Dates" description="Optional.">
              <div className="grid gap-3 sm:grid-cols-2">
                <Field id="cohort-start" label="Starts"><Input id="cohort-start" type="date" value={form.startDateAd} onChange={(e) => setForm({ ...form, startDateAd: e.target.value })} /></Field>
                <Field id="cohort-end" label="Ends"><Input id="cohort-end" type="date" value={form.endDateAd} onChange={(e) => setForm({ ...form, endDateAd: e.target.value })} /></Field>
              </div>
            </FormSection>
            <FormActions busy={busy} submitLabel="Create cohort" busyLabel="Creating…" onCancel={close} />
          </FormLayout>
        ) : detail ? (
          <div className="flex flex-col gap-6">
            <ErrorBanner message={error} />
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div><h3 className="text-xl font-semibold">{detail.name}</h3>{detail.description ? <p className="text-sm text-muted-foreground">{detail.description}</p> : null}</div>
              <Button variant="softDestructive" size="sm" disabled={busy} onClick={() => void deleteCohort()}>Delete cohort</Button>
            </div>

            <FormSection title={`Members (${detail.members.length})`}>
              {detail.members.length === 0 ? <EmptyState>No members yet. Search below to add people.</EmptyState> : (
                <RowList label="Members">
                  {detail.members.map((member) => (
                    <ListRow key={member.userId}>
                      <div className="min-w-0"><strong className="block truncate">{member.name}</strong><small className="text-muted-foreground">{member.email}</small></div>
                      <div className="flex justify-end"><Button variant="softDestructive" size="sm" disabled={busy} aria-label={`Remove ${member.name}`} onClick={() => void removeMember(member.userId)}>Remove</Button></div>
                    </ListRow>
                  ))}
                </RowList>
              )}
            </FormSection>

            <FormSection title="Add people">
              <Field id="member-search" label="Search by name or email" className="max-w-sm"><Input id="member-search" value={search} onChange={(e) => setSearch(e.target.value)} /></Field>
              <RowList label="People to add">
                {contacts.filter((contact) => !memberIds.has(contact.userId)).slice(0, 8).map((contact) => (
                  <ListRow key={contact.userId}>
                    <div className="min-w-0"><strong className="block truncate">{contact.name}</strong><small className="text-muted-foreground">{contact.email}</small></div>
                    <div className="flex justify-end"><Button variant="soft" size="sm" disabled={busy} aria-label={`Add ${contact.name}`} onClick={() => void addMember(contact.userId)}>Add</Button></div>
                  </ListRow>
                ))}
              </RowList>
            </FormSection>

            <FormSection title="Enroll or invite to a course" description="Enrolling is immediate. Inviting lets each person accept or decline.">
              <Field id="cohort-course" label="Course" className="max-w-md">
                <Select id="cohort-course" value={courseId} onChange={(e) => setCourseId(e.target.value)}>
                  <option value="">Choose a published course</option>
                  {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
                </Select>
              </Field>
              <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={override} onChange={(e) => setOverride(e.target.checked)} />Enroll even if prerequisites are not met</label>
              <div className="flex flex-wrap gap-2">
                <Button disabled={busy || !courseId || detail.members.length === 0} onClick={() => void bulk('enroll')}>Enroll cohort</Button>
                <Button variant="secondary" disabled={busy || !courseId || detail.members.length === 0} onClick={() => void bulk('invite')}>Send invitations</Button>
              </div>
              {result ? (
                <div role="status" className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm">
                  <strong>{summaryLine(result.data, result.kind)}</strong>
                  {result.data.results.map((item) => (
                    <div key={item.userId} className="flex flex-wrap items-center justify-between gap-2">
                      <span>{item.name}{item.missingPrerequisites.length > 0 ? <small className="ml-2 text-muted-foreground">needs {item.missingPrerequisites.join(', ')}</small> : null}</span>
                      <Badge variant={item.outcome === 'MissingPrerequisites' || item.outcome === 'NotAvailable' ? 'destructive' : item.outcome === 'AlreadyEnrolled' ? 'secondary' : 'default'}>{outcomeLabel[item.outcome] ?? item.outcome}</Badge>
                    </div>
                  ))}
                </div>
              ) : null}
            </FormSection>
          </div>
        ) : null}
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
