import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, RowList } from '@/components/form'
import { ApiError, apiRequest } from '@/lib/api'

type Accommodation = { learnerUserId: string; learnerName: string; extraTimePercent: number; extraAttempts: number; note?: string | null }
type Learner = { learnerUserId: string; name: string; status: string }

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)
const empty = { learnerUserId: '', extraTimePercent: '0', extraAttempts: '0', note: '' }

/** Extra time and extra attempts for individual learners (for example a learning support plan). They apply to every assessment in the course. */
export default function AssessmentAccommodationsPanel({ courseId }: { courseId: string }) {
  const [rows, setRows] = useState<Accommodation[] | null>(null)
  const [learners, setLearners] = useState<Learner[]>([])
  const [form, setForm] = useState(empty)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const base = `/api/v1/tenant/courses/${courseId}/accommodations`

  const load = useCallback(async () => {
    try {
      const list = await apiRequest<Accommodation[]>(base)
      setRows(Array.isArray(list) ? list : [])
      const roster = await apiRequest<Learner[]>(`/api/v1/tenant/courses/${courseId}/enrollments`)
      setLearners(Array.isArray(roster) ? roster.filter((entry) => entry.status !== 'Withdrawn') : [])
    } catch (exception) { setError(readError(exception, 'Unable to load the accommodations.')) }
  }, [base, courseId])
  useEffect(() => { setRows(null); setForm(empty); setEditingId(null); void load() }, [load])

  const available = learners.filter((learner) => editingId === learner.learnerUserId || !rows?.some((row) => row.learnerUserId === learner.learnerUserId))

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const time = Number(form.extraTimePercent)
    const attempts = Number(form.extraAttempts)
    if (!form.learnerUserId) return setProblem('Choose a learner.')
    if (!Number.isInteger(time) || time < 0 || time > 200) return setProblem('Extra time is a whole number from 0 to 200 percent.')
    if (!Number.isInteger(attempts) || attempts < 0 || attempts > 10) return setProblem('Extra attempts are a whole number from 0 to 10.')
    setBusy(true); setProblem(null); setNotice(null)
    try {
      await apiRequest(`${base}/${form.learnerUserId}`, { method: 'PUT', body: JSON.stringify({ extraTimePercent: time, extraAttempts: attempts, note: form.note.trim() || null }) })
      setForm(empty); setEditingId(null); setNotice('Accommodation saved. It applies to attempts started from now on.'); await load()
    } catch (exception) { setProblem(readError(exception, 'Unable to save the accommodation.')) } finally { setBusy(false) }
  }

  async function remove(row: Accommodation) {
    if (!window.confirm(`Remove the accommodation for ${row.learnerName}?`)) return
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(`${base}/${row.learnerUserId}`, { method: 'DELETE' }); setNotice('Accommodation removed.'); await load() }
    catch (exception) { setError(readError(exception, 'Unable to remove the accommodation.')) } finally { setBusy(false) }
  }

  const edit = (row: Accommodation) => { setEditingId(row.learnerUserId); setForm({ learnerUserId: row.learnerUserId, extraTimePercent: String(row.extraTimePercent), extraAttempts: String(row.extraAttempts), note: row.note ?? '' }); setProblem(null) }

  return (
    <section className="flex flex-col gap-4">
      <p className="text-sm text-muted-foreground">Give a learner extra time on timed assessments and extra attempts in this course. The note is only for staff.</p>
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      {rows === null ? <p role="status" className="text-sm text-muted-foreground">Loading…</p>
        : rows.length === 0 ? <EmptyState>No learner has an accommodation in this course.</EmptyState>
          : (
            <RowList label="Accommodations">
              {rows.map((row) => (
                <ListRow key={row.learnerUserId} columns="md:grid-cols-[minmax(0,1.5fr)_110px_110px_minmax(0,1.5fr)_auto]">
                  <strong className="truncate">{row.learnerName}</strong>
                  <span className="text-xs text-muted-foreground">+{row.extraTimePercent}% time</span>
                  <span className="text-xs text-muted-foreground">+{row.extraAttempts} attempt{row.extraAttempts === 1 ? '' : 's'}</span>
                  <span className="truncate text-xs text-muted-foreground">{row.note ?? ''}</span>
                  <div className="flex gap-2">
                    <Button size="sm" variant="outline" aria-label={`Edit accommodation for ${row.learnerName}`} onClick={() => edit(row)}>Edit</Button>
                    <Button size="sm" variant="outline" disabled={busy} aria-label={`Remove accommodation for ${row.learnerName}`} onClick={() => void remove(row)}>Remove</Button>
                  </div>
                </ListRow>
              ))}
            </RowList>
          )}

      <FormLayout onSubmit={(event) => void save(event)} noValidate>
        <ErrorBanner message={problem} />
        <FormSection title={editingId ? 'Change accommodation' : 'Add accommodation'} divider={false}>
          <Field id="accommodation-learner" label="Learner" required>
            <Select id="accommodation-learner" value={form.learnerUserId} disabled={!!editingId} onChange={(event) => setForm({ ...form, learnerUserId: event.target.value })}>
              <option value="">Choose an enrolled learner</option>
              {available.map((learner) => <option key={learner.learnerUserId} value={learner.learnerUserId}>{learner.name}</option>)}
            </Select>
          </Field>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field id="accommodation-time" label="Extra time (%)" hint="50 turns a 60 minute limit into 90 minutes."><Input id="accommodation-time" type="number" min="0" max="200" value={form.extraTimePercent} onChange={(event) => setForm({ ...form, extraTimePercent: event.target.value })} /></Field>
            <Field id="accommodation-attempts" label="Extra attempts"><Input id="accommodation-attempts" type="number" min="0" max="10" value={form.extraAttempts} onChange={(event) => setForm({ ...form, extraAttempts: event.target.value })} /></Field>
          </div>
          <Field id="accommodation-note" label="Note for staff"><Input id="accommodation-note" maxLength={1000} value={form.note} onChange={(event) => setForm({ ...form, note: event.target.value })} /></Field>
        </FormSection>
        <FormActions busy={busy} submitLabel="Save accommodation" onCancel={editingId ? () => { setEditingId(null); setForm(empty); setProblem(null) } : undefined} />
      </FormLayout>
    </section>
  )
}
