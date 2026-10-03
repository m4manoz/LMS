import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import SidePanel from '@/components/SidePanel'
import { ApiError, apiRequest, downloadFile } from '@/lib/api'
import { useAuth } from '@/lib/auth'

type Submission = {
  id: string; assignmentId: string; learnerUserId: string; learnerName: string | null; textResponse: string | null
  fileName: string | null; fileSizeBytes: number | null; submissionCount: number; isLate: boolean; status: string
  scorePoints: number | null; finalPoints: number | null; feedback: string | null; submittedAtUtc: string; gradedAtUtc: string | null
}
type Assignment = {
  id: string; courseId: string; courseTitle: string; title: string; instructions: string | null; maxPoints: number
  dueAtUtc: string | null; allowLate: boolean; latePenaltyPercent: number; status: string
  submissionCount: number; gradedCount: number; mySubmission: Submission | null
}
type Course = { id: string; code: string; title: string; status: string }

const emptyForm = { title: '', instructions: '', maxPoints: '100', dueLocal: '', allowLate: false, latePenaltyPercent: '10' }

/** Returns the first problem with the new-assignment form, or null when it can be sent. */
function validateForm(courseId: string, form: typeof emptyForm): string | null {
  if (!courseId) return 'Choose a course first.'
  if (!form.title.trim()) return 'Enter a title.'
  const points = Number(form.maxPoints)
  if (!form.maxPoints.trim() || !Number.isFinite(points) || points < 1 || points > 1000) return 'Maximum points must be a number from 1 to 1000.'
  if (form.dueLocal && Number.isNaN(new Date(form.dueLocal).getTime())) return 'Enter a valid due date and time.'
  if (form.allowLate) {
    const penalty = Number(form.latePenaltyPercent)
    if (!form.latePenaltyPercent.trim() || !Number.isFinite(penalty) || penalty < 0 || penalty > 100) return 'Late penalty must be a number from 0 to 100.'
  }
  return null
}

export default function AssignmentsPage({ initialTab = 'assignments' }: { initialTab?: 'assignments' | 'grading' }) {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('assessment.manage') ?? false
  const canSubmit = session?.permissions.includes('assessment.attempt') ?? false
  const canGrade = session?.permissions.includes('grade.manage') ?? false
  const [courses, setCourses] = useState<Course[]>([])
  const [courseId, setCourseId] = useState('')
  const [formCourseId, setFormCourseId] = useState('')
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [submissions, setSubmissions] = useState<Submission[]>([])
  const [form, setForm] = useState(emptyForm)
  const [text, setText] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [grades, setGrades] = useState<Record<string, { score: string; feedback: string }>>({})
  const [tab, setTab] = useState<string>(initialTab)
  const [error, setError] = useState<string | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const selected = assignments.find((item) => item.id === selectedId) ?? null
  const effectiveTab = tab === 'grading' && !canGrade ? 'assignments' : tab

  async function loadAssignments(nextCourse = courseId) {
    try { setAssignments(await apiRequest<Assignment[]>(`/api/v1/tenant/assignments${nextCourse ? `?courseId=${nextCourse}` : ''}`)) }
    catch (exception) { setError(readError(exception, 'Unable to load assignments.')) }
  }

  useEffect(() => {
    void loadAssignments()
    apiRequest<Course[]>('/api/v1/tenant/courses').then(setCourses).catch(() => setCourses([]))
  }, [])

  useEffect(() => {
    setSubmissions([])
    if (selectedId && canGrade) {
      apiRequest<Submission[]>(`/api/v1/tenant/assignments/${selectedId}/submissions`).then(setSubmissions).catch(() => setSubmissions([]))
    }
  }, [selectedId, canGrade])

  async function run(action: () => Promise<unknown>, failure: string, after?: () => Promise<void>) {
    setBusy(true); setError(null)
    try { await action(); if (after) await after() }
    catch (exception) { setError(readError(exception, failure)) }
    finally { setBusy(false) }
  }

  const reloadSubmissions = async () => {
    if (selectedId && canGrade) setSubmissions(await apiRequest<Submission[]>(`/api/v1/tenant/assignments/${selectedId}/submissions`))
  }

  const closePanel = () => { setCreating(false); setSelectedId(null); setFormError(null); setTab(initialTab) }
  const openNew = () => { setSelectedId(null); setNotice(null); setFormError(null); setForm(emptyForm); setFormCourseId(courseId); setCreating(true) }
  const choose = (id: string) => { setCreating(false); setNotice(null); setFormError(null); setSelectedId(id) }

  const create = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const problem = validateForm(formCourseId, form)
    if (problem) { setFormError(problem); return Promise.resolve() }
    setFormError(null)
    return run(async () => {
      await apiRequest('/api/v1/tenant/assignments', {
        method: 'POST',
        body: JSON.stringify({
          courseId: formCourseId, title: form.title, instructions: form.instructions, maxPoints: Number(form.maxPoints),
          dueAtUtc: form.dueLocal ? new Date(form.dueLocal).toISOString() : null,
          allowLate: form.allowLate, latePenaltyPercent: form.allowLate ? Number(form.latePenaltyPercent) : 0,
        }),
      })
      setForm(emptyForm)
    }, 'Unable to create the assignment.', async () => { await loadAssignments(); setCreating(false); setNotice('Assignment created as a draft. Open it to publish it when it is ready.') })
  }

  const changeStatus = (kind: 'publish' | 'close') => selected && run(
    () => apiRequest(`/api/v1/tenant/assignments/${selected.id}/${kind}`, { method: 'POST' }),
    `Unable to ${kind} the assignment.`, () => loadAssignments())

  const submit = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!selected) return Promise.resolve()
    if (!text.trim() && !file) { setFormError('Write a response or attach a file before submitting.'); return Promise.resolve() }
    setFormError(null)
    const body = new FormData()
    body.set('text', text)
    if (file) body.set('file', file)
    return run(() => apiRequest(`/api/v1/tenant/assignments/${selected.id}/submission`, { method: 'POST', body }),
      'Unable to submit your work.', async () => { setText(''); setFile(null); await loadAssignments() })
  }

  const grade = (item: Submission) => {
    const entry = grades[item.id] ?? { score: item.scorePoints?.toString() ?? '', feedback: item.feedback ?? '' }
    const score = Number(entry.score)
    if (entry.score.trim() === '' || !Number.isFinite(score) || score < 0 || (selected && score > selected.maxPoints)) {
      setError(`Score must be a number from 0 to ${selected?.maxPoints ?? 0}.`)
      return Promise.resolve()
    }
    return run(() => apiRequest(`/api/v1/tenant/assignments/submissions/${item.id}/grade`, { method: 'POST', body: JSON.stringify({ scorePoints: score, feedback: entry.feedback }) }),
      'Unable to save the grade.', async () => { await reloadSubmissions(); await loadAssignments() })
  }

  const download = (item: Submission) => run(
    () => downloadFile(`/api/v1/tenant/assignments/submissions/${item.id}/file`, item.fileName ?? 'submission'),
    'Unable to download the file.')

  const mine = selected?.mySubmission ?? null
  const overdue = selected?.dueAtUtc ? new Date(selected.dueAtUtc) < new Date() : false
  const canSubmitNow = !!selected && canSubmit && selected.status === 'Published' && mine?.status !== 'Graded' && !(overdue && !selected.allowLate)
  const statusText = (item: Assignment) => (canManage ? item.status : (item.mySubmission?.status ?? (item.status === 'Closed' ? 'Closed' : 'To do')))

  const detailsView = !selected ? null : (
    <div className="flex min-w-0 flex-col gap-4">
      <Card>
        <CardHeader>
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <p className="text-xs text-muted-foreground">{selected.courseTitle} · {selected.maxPoints} points</p>
              <CardTitle className="text-xl">{selected.title}</CardTitle>
            </div>
            <div className="flex gap-2">
              {overdue ? <Badge variant="destructive">Past due</Badge> : null}
              <Badge>{selected.status}</Badge>
            </div>
          </div>
          <CardDescription>
            {selected.dueAtUtc ? `Due ${new Date(selected.dueAtUtc).toLocaleString()}. ` : 'No deadline. '}
            {selected.allowLate ? `Late work is accepted${selected.latePenaltyPercent ? ` with a ${selected.latePenaltyPercent}% penalty` : ''}.` : 'Late work is not accepted.'}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <p className="whitespace-pre-wrap text-sm">{selected.instructions || 'No instructions were provided.'}</p>
          {canManage ? (
            <div className="flex flex-wrap gap-2">
              {selected.status === 'Draft' ? <Button disabled={busy} onClick={() => void changeStatus('publish')}>Publish</Button> : null}
              {selected.status === 'Published' ? <Button variant="secondary" disabled={busy} onClick={() => void changeStatus('close')}>Close submissions</Button> : null}
            </div>
          ) : null}
        </CardContent>
      </Card>

      {mine ? (
        <Card>
          <CardHeader>
            <div className="flex items-start justify-between gap-3">
              <CardTitle>Your submission</CardTitle>
              <div className="flex gap-2">
                {mine.isLate ? <Badge variant="destructive">Late</Badge> : null}
                <Badge>{mine.status}</Badge>
              </div>
            </div>
            <CardDescription>Submitted {new Date(mine.submittedAtUtc).toLocaleString()} · version {mine.submissionCount}</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-3 text-sm">
            {mine.textResponse ? <p className="whitespace-pre-wrap rounded-md bg-muted px-3 py-2">{mine.textResponse}</p> : null}
            {mine.fileName ? <div><Button variant="outline" size="sm" disabled={busy} onClick={() => void download(mine)}>Download {mine.fileName}</Button></div> : null}
            {mine.status === 'Graded' ? (
              <div className="rounded-md border border-border p-3">
                <strong className="text-lg">{mine.finalPoints} / {selected.maxPoints}</strong>
                {mine.finalPoints !== mine.scorePoints ? <span className="ml-2 text-muted-foreground">({mine.scorePoints} before the late penalty)</span> : null}
                {mine.feedback ? <p className="mt-2 whitespace-pre-wrap">Feedback: {mine.feedback}</p> : null}
              </div>
            ) : null}
          </CardContent>
        </Card>
      ) : null}

      {canSubmitNow ? (
        <Card>
          <CardHeader>
            <CardTitle>{mine ? 'Resubmit' : 'Submit your work'}</CardTitle>
            <CardDescription>Write a response, attach a file (up to 25 MB), or both. You can resubmit until it is graded.</CardDescription>
          </CardHeader>
          <CardContent>
            <FormLayout onSubmit={submit}>
              <ErrorBanner message={formError} />
              <FormSection title="Your work">
                <Field id="submission-text" label="Written response" hint="Required unless you attach a file.">
                  <Textarea id="submission-text" value={text} onChange={(e) => setText(e.target.value)} rows={5} maxLength={20000} />
                </Field>
                <Field id="submission-file" label="Attachment" hint="Required unless you write a response.">
                  <Input id="submission-file" type="file" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
                </Field>
              </FormSection>
              <FormActions busy={busy} busyLabel="Submitting…" submitLabel="Submit" disabled={!text.trim() && !file} />
            </FormLayout>
          </CardContent>
        </Card>
      ) : null}
    </div>
  )

  const gradingView = !selected ? null : (
    <Card>
      <CardHeader>
        <CardTitle>{selected.title}</CardTitle>
        <CardDescription>{submissions.length} submission{submissions.length === 1 ? '' : 's'} · out of {selected.maxPoints} points</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {submissions.length === 0 ? <EmptyState>No submissions yet.</EmptyState> : submissions.map((item) => {
          const entry = grades[item.id] ?? { score: item.scorePoints?.toString() ?? '', feedback: item.feedback ?? '' }
          return (
            <div key={item.id} className="flex flex-col gap-3 rounded-md border border-border p-3 text-sm">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <span>
                  <strong className="block">{item.learnerName ?? 'Learner'}</strong>
                  <small className="text-muted-foreground">Submitted {new Date(item.submittedAtUtc).toLocaleString()} · version {item.submissionCount}</small>
                </span>
                <span className="flex gap-2">
                  {item.isLate ? <Badge variant="destructive">Late</Badge> : null}
                  <Badge>{item.status}</Badge>
                </span>
              </div>
              {item.textResponse ? <p className="whitespace-pre-wrap rounded-md bg-muted px-3 py-2">{item.textResponse}</p> : null}
              {item.fileName ? <div><Button variant="outline" size="sm" disabled={busy} onClick={() => void download(item)}>Download {item.fileName}</Button></div> : null}
              <div className="flex flex-wrap items-end gap-2">
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor={`score-${item.id}`}>Score</Label>
                  <Input id={`score-${item.id}`} className="w-24" type="number" min="0" max={selected.maxPoints} value={entry.score} onChange={(e) => setGrades({ ...grades, [item.id]: { ...entry, score: e.target.value } })} />
                </div>
                <div className="flex min-w-48 flex-1 flex-col gap-1.5">
                  <Label htmlFor={`feedback-${item.id}`}>Feedback</Label>
                  <Input id={`feedback-${item.id}`} value={entry.feedback} onChange={(e) => setGrades({ ...grades, [item.id]: { ...entry, feedback: e.target.value } })} />
                </div>
                <Button variant="secondary" disabled={busy || entry.score === ''} onClick={() => void grade(item)}>{item.status === 'Graded' ? 'Update grade' : 'Grade'}</Button>
              </div>
              {item.status === 'Graded' && item.finalPoints !== item.scorePoints ? <small className="text-muted-foreground">Final after late penalty: {item.finalPoints}</small> : null}
            </div>
          )
        })}
      </CardContent>
    </Card>
  )

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader
        title="Assignments"
        description="Submit work, track deadlines and review feedback."
        actions={<>
          <span className="text-sm text-muted-foreground">{assignments.length} assignment{assignments.length === 1 ? '' : 's'}</span>
          {canManage ? <Button onClick={openNew}><Plus className="mr-1 h-4 w-4" aria-hidden />New assignment</Button> : null}
        </>}
      />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />

      <div className="flex max-w-sm flex-col gap-1.5">
        <Label htmlFor="assignment-course">Course</Label>
        <Select id="assignment-course" value={courseId} onChange={(e) => { setCourseId(e.target.value); setSelectedId(null); void loadAssignments(e.target.value) }}>
          <option value="">All courses</option>
          {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
        </Select>
      </div>

      {assignments.length === 0 ? <EmptyState>No assignments yet.</EmptyState> : (
        <RowList label="Assignments">
          {assignments.map((item) => (
            <ListRow key={item.id} selected={!creating && item.id === selectedId} columns="sm:grid-cols-[minmax(0,1fr)_auto] md:grid-cols-[minmax(0,2fr)_110px_minmax(0,1.3fr)_minmax(0,1fr)_auto]">
              <div className="min-w-0">
                <strong className="block truncate">{item.title}</strong>
                <small className="text-muted-foreground">{item.courseTitle}</small>
              </div>
              <div><Badge>{statusText(item)}</Badge></div>
              <div className="hidden text-muted-foreground md:block"><small className="block">Due</small>{item.dueAtUtc ? new Date(item.dueAtUtc).toLocaleString() : 'No deadline'}</div>
              <div className="hidden text-muted-foreground md:block">{canManage ? <><small className="block">Submissions</small>{item.submissionCount} submitted, {item.gradedCount} graded</> : <><small className="block">Points</small>{item.maxPoints}</>}</div>
              <div className="flex justify-end">
                <Button type="button" size="sm" variant="secondary" aria-label={`View details for ${item.title}`} aria-expanded={!creating && item.id === selectedId} onClick={() => choose(item.id)}>View details</Button>
              </div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={creating || selected !== null} label={creating ? 'New assignment' : 'Assignment details'} onClose={closePanel}>
        {creating ? (
          <Card>
            <CardHeader><CardTitle>New assignment</CardTitle><CardDescription>Created as a draft in the chosen course. Publish it from its details when ready.</CardDescription></CardHeader>
            <CardContent>
              <FormLayout onSubmit={create}>
                <ErrorBanner message={formError} />
                <FormSection title="Basics">
                  <Field id="new-assignment-course" label="Course" required>
                    <Select id="new-assignment-course" value={formCourseId} onChange={(e) => setFormCourseId(e.target.value)}>
                      <option value="">Choose a course</option>
                      {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
                    </Select>
                  </Field>
                  <Field id="new-assignment-title" label="Title" required>
                    <Input id="new-assignment-title" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} maxLength={250} />
                  </Field>
                  <Field id="new-assignment-instructions" label="Instructions">
                    <Textarea id="new-assignment-instructions" value={form.instructions} onChange={(e) => setForm({ ...form, instructions: e.target.value })} rows={4} />
                  </Field>
                </FormSection>
                <FormSection title="Grading and deadline">
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Field id="new-assignment-points" label="Maximum points" required>
                      <Input id="new-assignment-points" type="number" min="1" max="1000" value={form.maxPoints} onChange={(e) => setForm({ ...form, maxPoints: e.target.value })} />
                    </Field>
                    <Field id="new-assignment-due" label="Due date and time">
                      <Input id="new-assignment-due" type="datetime-local" value={form.dueLocal} onChange={(e) => setForm({ ...form, dueLocal: e.target.value })} />
                    </Field>
                  </div>
                  <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={form.allowLate} onChange={(e) => setForm({ ...form, allowLate: e.target.checked })} />
                    Accept late submissions
                  </label>
                  {form.allowLate ? (
                    <Field id="new-assignment-penalty" label="Late penalty (%)" className="max-w-xs">
                      <Input id="new-assignment-penalty" type="number" min="0" max="100" value={form.latePenaltyPercent} onChange={(e) => setForm({ ...form, latePenaltyPercent: e.target.value })} />
                    </Field>
                  ) : null}
                </FormSection>
                <FormActions busy={busy} busyLabel="Creating…" submitLabel="Create draft" onCancel={closePanel} />
              </FormLayout>
            </CardContent>
          </Card>
        ) : !selected ? null : canGrade ? (
          <Tabs value={effectiveTab} onValueChange={setTab}>
            <TabsList>
              <TabsTrigger value="assignments">Assignment</TabsTrigger>
              <TabsTrigger value="grading">Grading</TabsTrigger>
            </TabsList>
            <TabsContent value="assignments">{detailsView}</TabsContent>
            <TabsContent value="grading">{gradingView}</TabsContent>
          </Tabs>
        ) : detailsView}
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
