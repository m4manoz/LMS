import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '../lib/api'
import { useAuth } from '../lib/auth'

type Course = { id: string; title: string; status: string }
type Job = { id: string; feature: string; status: string; courseId: string | null; provider: string | null; model: string | null; attemptCount: number; lastError: string | null; createdAtUtc: string; completedAtUtc: string | null; outputCount: number }
type Output = { id: string; feature: string; title: string; content: string; provider: string; model: string; status: string; createdAtUtc: string; approvedAtUtc: string | null; citations: Citation[]; reviews: Review[] }
type Citation = { id: string; sourceType: string; sourceId: string; sourceTitle: string; locator: string | null; excerpt: string | null }
type Review = { id: string; decision: string; notes: string | null; reviewerUserId: string; createdAtUtc: string; outputStatus: string }
type JobDetail = Job & { requestedByUserId: string; instruction: string; outputLanguage: string; outputs: Output[] }

const features = [
  ['LessonSummary', 'Lesson summary'],
  ['QuestionDraft', 'Question draft'],
  ['FlashcardDraft', 'Flashcard draft'],
  ['TranslationDraft', 'Translation draft'],
  ['TutorExplanation', 'Tutor explanation']
]

/** The job list is always shown; pass initialTab="new" to open the "New draft" panel straight away. */
export default function AiWorkspacePage({ initialTab = 'jobs' }: { initialTab?: 'new' | 'jobs' }) {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('ai.manage') ?? false
  const [courses, setCourses] = useState<Course[]>([])
  const [jobs, setJobs] = useState<Job[]>([])
  const [selected, setSelected] = useState<JobDetail | null>(null)
  const [feature, setFeature] = useState(features[0][0])
  const [courseId, setCourseId] = useState('')
  const [instruction, setInstruction] = useState('Create a concise, learner-friendly draft.')
  const [language, setLanguage] = useState('en')
  const [reviewNotes, setReviewNotes] = useState('')
  const [busy, setBusy] = useState(false)
  const [creating, setCreating] = useState(initialTab === 'new')
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  async function refresh() {
    try {
      const [courseResponse, jobResponse] = await Promise.all([
        apiRequest<Course[]>('/api/v1/tenant/courses'),
        apiRequest<Job[]>('/api/v1/tenant/ai/jobs')
      ])
      setCourses(courseResponse.filter(course => course.status === 'Published'))
      setJobs(jobResponse)
      if (selected?.id) {
        const detail = await apiRequest<JobDetail>(`/api/v1/tenant/ai/jobs/${selected.id}`)
        setSelected(detail)
      }
    } catch (exception) { setError(readError(exception, 'Unable to load the AI workspace.')) }
  }

  useEffect(() => { void refresh() }, [])

  function openCreate() {
    setSelected(null); setProblem(null); setError(null); setCreating(true)
  }

  async function createJob(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!courseId) { setProblem('Choose a published course.'); return }
    if (!instruction.trim()) { setProblem('Enter an instruction for the draft.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      const job = await apiRequest<Job>('/api/v1/tenant/ai/jobs', {
        method: 'POST', body: JSON.stringify({ feature, courseId: courseId || null, instruction, outputLanguage: language })
      })
      setCreating(false)
      await refresh()
      await openJob(job.id)
    } catch (exception) { setProblem(readError(exception, 'Unable to queue the AI draft.')) }
    finally { setBusy(false) }
  }

  async function openJob(jobId: string) {
    setError(null)
    try { setSelected(await apiRequest<JobDetail>(`/api/v1/tenant/ai/jobs/${jobId}`)); setCreating(false) }
    catch (exception) { setError(readError(exception, 'Unable to open the AI job.')) }
  }

  async function review(outputId: string, decision: 'Approved' | 'Rejected') {
    setBusy(true); setError(null)
    try {
      await apiRequest(`/api/v1/tenant/ai/outputs/${outputId}/reviews`, { method: 'POST', body: JSON.stringify({ decision, notes: reviewNotes }) })
      if (selected) await openJob(selected.id)
    } catch (exception) { setError(readError(exception, 'Unable to save the review.')) }
    finally { setBusy(false) }
  }

  async function regenerate(outputId: string) {
    setBusy(true); setError(null)
    try {
      const job = await apiRequest<Job>(`/api/v1/tenant/ai/outputs/${outputId}/regenerate`, { method: 'POST' })
      await refresh(); await openJob(job.id)
    } catch (exception) { setError(readError(exception, 'Unable to queue regeneration.')) }
    finally { setBusy(false) }
  }

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="AI learning workspace" description="Drafts are grounded only in published course lessons, stored with citations, and stay unpublished until a teacher approves them."
        actions={<><Button variant="outline" onClick={() => void refresh()}>Refresh</Button><Button onClick={openCreate}><Plus className="mr-1 h-4 w-4" aria-hidden />New draft</Button></>} />
      {!selected ? <ErrorBanner message={error} /> : null}

      <h3 className="text-sm font-semibold">Jobs and review ({jobs.length})</h3>
      {jobs.length === 0 ? <EmptyState>No AI jobs yet. Generate the first draft with New draft.</EmptyState> : (
        <RowList label="AI jobs">
          {jobs.map(job => (
            <ListRow key={job.id} selected={job.id === selected?.id} columns="sm:grid-cols-[minmax(0,1fr)_auto] md:grid-cols-[minmax(0,2fr)_110px_minmax(0,1fr)_auto]">
              <div className="min-w-0"><strong className="block truncate">{labelFor(job.feature)}</strong><small className="text-muted-foreground">{job.status} · {job.outputCount} output{job.outputCount === 1 ? '' : 's'}</small></div>
              <div><Badge variant={job.status === 'Completed' ? 'default' : 'secondary'}>{job.status}</Badge></div>
              <div className="hidden text-muted-foreground md:block"><small className="block">Provider</small>{job.provider ?? 'queued'}</div>
              <div className="flex justify-end">
                <Button type="button" size="sm" variant="secondary" aria-label={`View details for ${labelFor(job.feature)} from ${new Date(job.createdAtUtc).toLocaleString()}`} aria-expanded={job.id === selected?.id} onClick={() => void openJob(job.id)}>View details</Button>
              </div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={creating} label="New draft" onClose={() => setCreating(false)}>
        <FormLayout onSubmit={createJob}>
          <ErrorBanner message={problem} />
          <FormSection title="About the draft" description="The draft is queued and appears under Jobs and review.">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field id="ai-feature" label="Draft type">
                <Select id="ai-feature" value={feature} onChange={event => setFeature(event.target.value)}>
                  {features.map(item => <option key={item[0]} value={item[0]}>{item[1]}</option>)}
                </Select>
              </Field>
              <Field id="ai-course" label="Published course" required>
                <Select id="ai-course" value={courseId} onChange={event => setCourseId(event.target.value)}>
                  <option value="">Choose a course</option>
                  {courses.map(course => <option key={course.id} value={course.id}>{course.title}</option>)}
                </Select>
              </Field>
            </div>
            <Field id="ai-language" label="Output language" className="max-w-xs"><Input id="ai-language" value={language} onChange={event => setLanguage(event.target.value)} maxLength={20} /></Field>
            <Field id="ai-instruction" label="Instruction" required><Textarea id="ai-instruction" value={instruction} onChange={event => setInstruction(event.target.value)} maxLength={4000} rows={4} /></Field>
          </FormSection>
          <p className="rounded-md border border-border p-3 text-xs text-muted-foreground">
            <strong>Safety boundary.</strong> The local provider is deterministic for development. Production providers should be added behind the same interface with tenant budgets, redaction and evaluation gates.
          </p>
          <FormActions busy={busy} submitLabel="Generate draft" busyLabel="Queueing…" onCancel={() => setCreating(false)} />
        </FormLayout>
      </SidePanel>

      <SidePanel open={selected !== null} label={selected ? `${labelFor(selected.feature)} job` : 'AI job'} onClose={() => { setSelected(null); setError(null) }}>
        {selected ? (
          <div className="flex flex-col gap-5">
            <ErrorBanner message={error} />
            <div className="flex flex-col gap-1">
              <p className="text-xs text-muted-foreground">{labelFor(selected.feature)} · {selected.provider ?? 'waiting for provider'}{selected.model ? ` · ${selected.model}` : ''}</p>
              <div><Badge variant={selected.status === 'Completed' ? 'default' : 'secondary'}>{selected.status}</Badge></div>
              <p className="text-sm text-muted-foreground">{selected.instruction}</p>
            </div>
            {selected.lastError ? <ErrorBanner message={selected.lastError} /> : null}
            {selected.outputs.map(output => (
              <section key={output.id} className="flex flex-col gap-3 rounded-md border border-border p-4" aria-label={output.title}>
                <div className="flex items-start justify-between gap-3">
                  <div><h3 className="text-base font-semibold">{output.title}</h3><small className="text-muted-foreground">{output.provider} · {output.model}</small></div>
                  <Badge>{output.status}</Badge>
                </div>
                <pre className="whitespace-pre-wrap rounded-md border border-border bg-muted p-3 text-sm">{output.content}</pre>
                <div className="flex flex-col gap-1 text-sm">
                  <strong>Grounding citations</strong>
                  {output.citations.length === 0 ? <span className="text-muted-foreground">No citations returned.</span> : output.citations.map(citation => <span key={citation.id} className="text-muted-foreground">[{citation.sourceTitle}] {citation.excerpt}</span>)}
                </div>
                {canManage ? (
                  <>
                    <Field id={`review-${output.id}`} label="Review note"><Textarea id={`review-${output.id}`} value={reviewNotes} onChange={event => setReviewNotes(event.target.value)} rows={2} placeholder="Optional review note" /></Field>
                    <div className="flex flex-wrap gap-2">
                      <Button disabled={busy} onClick={() => void review(output.id, 'Approved')}>Approve</Button>
                      <Button variant="softDestructive" disabled={busy} onClick={() => void review(output.id, 'Rejected')}>Reject</Button>
                      <Button variant="secondary" disabled={busy} onClick={() => void regenerate(output.id)}>Regenerate</Button>
                    </div>
                  </>
                ) : null}
                {output.reviews.length > 0 ? <p className="text-sm text-muted-foreground">Latest review: {output.reviews[0].decision}{output.reviews[0].notes ? ` — ${output.reviews[0].notes}` : ''}</p> : null}
              </section>
            ))}
          </div>
        ) : null}
      </SidePanel>
    </section>
  )
}

function labelFor(value: string) { return features.find(item => item[0].toLowerCase() === value.toLowerCase())?.[1] ?? value }
function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
