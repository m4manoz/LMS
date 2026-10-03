import { useCallback, useEffect, useState } from 'react'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, apiRequest } from '@/lib/api'

type WaitlistEntry = { enrollmentId: string; learnerUserId: string; name: string; email: string; position: number; joinedAtUtc: string }
type RosterEntry = { enrollmentId: string; learnerUserId: string; name: string; email: string; status: string; source: string; progressPercent: number; enrolledAtUtc: string }
type Candidate = { userId: string; name: string; email: string; role: string }
type EnrollResponse = { enrolled: number; waitlisted: number; results: { learnerUserId: string; name: string; outcome: string; message: string | null }[] }
type Summary = { courseId: string; capacity: number | null; active: number; completed: number; freeSeats: number | null; waitlist: WaitlistEntry[]; justPromoted: number }

/** What happened when several people were enrolled, in words: counts first, then the reason for each one that did not get in. */
export function describeEnrollment(response: EnrollResponse): string {
  const parts: string[] = []
  if (response.enrolled > 0) parts.push(`${response.enrolled} enrolled.`)
  if (response.waitlisted > 0) parts.push(`${response.waitlisted} put on the waitlist because the course is full.`)
  for (const item of response.results) if (item.outcome !== 'Enrolled' && item.outcome !== 'Waitlisted') parts.push(`${item.name}: ${item.message ?? item.outcome}`)
  return parts.length > 0 ? parts.join(' ') : 'Nobody was enrolled.'
}

/** Capacity as text for the input: empty means no limit. Returns null when the text is not a usable capacity. */
export function parseCapacity(text: string): { ok: true; value: number | null } | { ok: false } {
  const trimmed = text.trim()
  if (trimmed === '') return { ok: true, value: null }
  const value = Number(trimmed)
  return Number.isInteger(value) && value >= 1 && value <= 1_000_000 ? { ok: true, value } : { ok: false }
}

export default function CourseEnrollmentPanel({ courseId, published }: { courseId: string; published: boolean }) {
  const [summary, setSummary] = useState<Summary | null>(null)
  const [capacity, setCapacity] = useState('')
  const [count, setCount] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [roster, setRoster] = useState<RosterEntry[] | null>(null)
  const [candidates, setCandidates] = useState<Candidate[] | null>(null)
  const [search, setSearch] = useState('')
  const [chosen, setChosen] = useState<string[]>([])
  const [override, setOverride] = useState(false)

  const apply = (next: Summary) => { setSummary(next); setCapacity(next.capacity === null ? '' : String(next.capacity)) }
  const load = useCallback(async () => {
    try { apply(await apiRequest<Summary>(`/api/v1/tenant/courses/${courseId}/enrollment-summary`)) }
    catch (exception) { setError(readError(exception, 'Unable to load the enrollment summary.')) }
  }, [courseId])
  useEffect(() => { setMessage(null); setError(null); void load() }, [load])

  const loadRoster = useCallback(async () => {
    try { const list = await apiRequest<RosterEntry[]>(`/api/v1/tenant/courses/${courseId}/enrollments`); setRoster(Array.isArray(list) ? list : []) } catch { setRoster([]) }
  }, [courseId])
  useEffect(() => { void loadRoster() }, [loadRoster])

  // The people who could be added, narrowed by what is typed.
  const loadCandidates = useCallback(async (text: string) => {
    try { const list = await apiRequest<Candidate[]>(`/api/v1/tenant/courses/${courseId}/enrollable-learners?q=${encodeURIComponent(text.trim())}`); setCandidates(Array.isArray(list) ? list : []) } catch { setCandidates([]) }
  }, [courseId])
  useEffect(() => {
    const timer = window.setTimeout(() => { void loadCandidates(search) }, search ? 300 : 0)
    return () => window.clearTimeout(timer)
  }, [search, loadCandidates])

  async function enroll(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (chosen.length === 0) return setError('Choose at least one person to enroll.')
    setBusy(true); setError(null); setMessage(null)
    try {
      const response = await apiRequest<EnrollResponse>(`/api/v1/tenant/courses/${courseId}/enrollments`, { method: 'POST', body: JSON.stringify({ learnerUserIds: chosen, overridePrerequisites: override }) })
      setMessage(describeEnrollment(response))
      setChosen([])
      await Promise.all([load(), loadRoster(), loadCandidates(search)])
    } catch (exception) { setError(readError(exception, 'The learners could not be enrolled.')) }
    finally { setBusy(false) }
  }

  async function remove(entry: RosterEntry) {
    if (!window.confirm(`Remove ${entry.name} from this course? Their progress is kept if they are enrolled again.`)) return
    setBusy(true); setError(null); setMessage(null)
    try {
      await apiRequest(`/api/v1/tenant/enrollments/${entry.enrollmentId}/withdraw`, { method: 'POST' })
      setMessage(`${entry.name} was removed from the course.`)
      await Promise.all([load(), loadRoster(), loadCandidates(search)])
    } catch (exception) { setError(readError(exception, 'The learner could not be removed.')) }
    finally { setBusy(false) }
  }

  const toggle = (id: string) => setChosen((current) => (current.includes(id) ? current.filter((item) => item !== id) : [...current, id]))

  async function run(action: () => Promise<Summary>, done: (next: Summary) => string) {
    setBusy(true); setError(null); setMessage(null)
    try { const next = await action(); apply(next); setMessage(done(next)) }
    catch (exception) { setError(readError(exception, 'That did not work.')) }
    finally { setBusy(false) }
  }

  const parsed = parseCapacity(capacity)
  const saveCapacity = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!parsed.ok) return Promise.resolve()
    return run(() => apiRequest<Summary>(`/api/v1/tenant/courses/${courseId}/capacity`, { method: 'PUT', body: JSON.stringify({ capacity: parsed.value }) }),
      (next) => next.justPromoted > 0 ? `Capacity saved. ${next.justPromoted} learner${next.justPromoted === 1 ? '' : 's'} moved off the waitlist.` : 'Capacity saved.')
  }
  const promote = (limit: number | null) => run(() => apiRequest<Summary>(`/api/v1/tenant/courses/${courseId}/waitlist/promote`, { method: 'POST', body: JSON.stringify(limit === null ? {} : { count: limit }) }),
    (next) => next.justPromoted > 0 ? `${next.justPromoted} learner${next.justPromoted === 1 ? '' : 's'} promoted.` : 'No free seats, so nobody was promoted.')

  if (!summary) return <p className="text-sm text-muted-foreground">{error ?? 'Loading…'}</p>
  const requested = count.trim() === '' ? null : Number(count)
  const countValid = requested === null || (Number.isInteger(requested) && requested >= 1)

  return (
    <div className="flex flex-col gap-6">
      <ErrorBanner message={error} />
      <NoticeBanner message={message} />

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {[['Capacity', summary.capacity === null ? 'No limit' : String(summary.capacity)], ['Enrolled now', String(summary.active)],
          ['Free seats', summary.freeSeats === null ? '—' : String(summary.freeSeats)], ['Completed', String(summary.completed)]].map(([label, value]) => (
          <div key={label} className="rounded-md border border-border px-4 py-3"><small className="block text-muted-foreground">{label}</small><strong className="text-2xl font-semibold">{value}</strong></div>
        ))}
      </div>

      <FormLayout onSubmit={enroll}>
        <FormSection title="Add learners" description={published ? 'Choose the people to put in this course, then press Enroll. They are notified. If the course is full they join the waitlist.' : 'Publish the course first: learners can only be enrolled in a published course.'}>
          <Field id="enroll-search" label="Find people" className="max-w-sm"><Input id="enroll-search" type="search" placeholder="Name or email" value={search} onChange={(e) => setSearch(e.target.value)} disabled={!published} /></Field>
          {candidates === null ? <p className="text-sm text-muted-foreground">Loading…</p> : candidates.length === 0 ? <EmptyState>{search.trim() ? 'Nobody matches.' : 'Everyone in the organization is already in this course.'}</EmptyState> : (
            <ul aria-label="People who can be added" className="max-h-64 overflow-y-auto rounded-md border border-border">
              {candidates.map((person) => (
                <li key={person.userId} className="border-b border-border last:border-b-0">
                  <label className="flex cursor-pointer items-center gap-3 px-3 py-2 text-sm hover:bg-muted/60">
                    <input type="checkbox" disabled={!published} checked={chosen.includes(person.userId)} onChange={() => toggle(person.userId)} />
                    <span className="min-w-0 flex-1"><strong className="block truncate">{person.name}</strong><small className="text-muted-foreground">{person.email}</small></span>
                    <Badge variant="outline">{person.role}</Badge>
                  </label>
                </li>
              ))}
            </ul>
          )}
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={override} disabled={!published} onChange={(e) => setOverride(e.target.checked)} />Enroll even if they have not finished the prerequisite courses</label>
        </FormSection>
        <FormActions busy={busy} disabled={!published || chosen.length === 0} submitLabel={chosen.length > 0 ? `Enroll ${chosen.length} selected` : 'Enroll selected'} busyLabel="Enrolling…" />
      </FormLayout>

      <FormSection title={`Enrolled learners (${roster?.length ?? 0})`} description="Everyone taking the course now, and those who have finished it.">
        {roster === null ? <p className="text-sm text-muted-foreground">Loading…</p> : roster.length === 0 ? <EmptyState>Nobody is enrolled yet.</EmptyState> : (
          <RowList label="Enrolled learners">
            {roster.map((entry) => (
              <ListRow key={entry.enrollmentId} columns="sm:grid-cols-[minmax(0,1fr)_110px_90px_auto]">
                <div className="min-w-0"><strong className="block truncate">{entry.name}</strong><small className="text-muted-foreground">{entry.email}</small></div>
                <div><Badge variant={entry.status === 'Completed' ? 'default' : 'secondary'}>{entry.status}</Badge></div>
                <small className="text-muted-foreground">{entry.progressPercent}% done</small>
                <div className="flex justify-end"><Button type="button" size="sm" variant="softDestructive" disabled={busy} aria-label={`Remove ${entry.name}`} onClick={() => void remove(entry)}>Remove</Button></div>
              </ListRow>
            ))}
          </RowList>
        )}
      </FormSection>

      <FormLayout onSubmit={saveCapacity}>
        <FormSection title="Capacity" description="Raising it moves people off the waitlist straight away. Lowering it never removes anyone already enrolled.">
          <Field id="capacity-input" label="Seats (empty for no limit)" className="max-w-xs" error={!parsed.ok ? 'Enter a whole number from 1 to 1,000,000.' : null}>
            <Input id="capacity-input" inputMode="numeric" value={capacity} onChange={(e) => setCapacity(e.target.value)} placeholder="No limit" />
          </Field>
        </FormSection>
        <FormActions busy={busy} disabled={!parsed.ok} submitLabel="Save capacity" />
      </FormLayout>

      <FormSection title={`Waitlist (${summary.waitlist.length})`} description="Longest-waiting first. People move up automatically when a seat opens.">
        {published && summary.waitlist.length > 0 ? (
          <div className="flex flex-wrap items-start gap-2">
            <Field id="promote-count" label="How many" className="w-28" error={!countValid ? 'Enter a whole number, 1 or more.' : null}>
              <Input id="promote-count" inputMode="numeric" placeholder="All" value={count} onChange={(e) => setCount(e.target.value)} />
            </Field>
            <Button className="mt-6" variant="secondary" disabled={busy || !countValid} onClick={() => void promote(requested)}>Promote</Button>
          </div>
        ) : null}
        {summary.waitlist.length === 0 ? <EmptyState>Nobody is waiting.</EmptyState> : (
          <RowList label="Waitlist">
            {summary.waitlist.map((entry) => (
              <ListRow key={entry.enrollmentId} columns="sm:grid-cols-[60px_minmax(0,1fr)_auto]">
                <div><Badge variant="outline">#{entry.position}</Badge></div>
                <div className="min-w-0"><strong className="block truncate">{entry.name}</strong><small className="text-muted-foreground">{entry.email}</small></div>
                <small className="text-muted-foreground">Joined {new Date(entry.joinedAtUtc).toLocaleDateString()}</small>
              </ListRow>
            ))}
          </RowList>
        )}
      </FormSection>
    </div>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
