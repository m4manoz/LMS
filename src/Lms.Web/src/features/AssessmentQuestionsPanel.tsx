import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, NoticeBanner } from '@/components/form'
import { ApiError, apiRequest } from '@/lib/api'
import { choiceTypes, fromLocalInput, readableType, rubricTypes, splitCsv, toLocalInput, type AssessmentDetail, type Question, type Rubric } from '@/lib/assessments'

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)
const emptyForm = { type: 'MultipleChoice', prompt: '', options: '', correct: '', points: '1', pool: '', rubricId: '' }

/**
 * Building an assessment: its questions (added, edited, removed, pooled, marked by a rubric), its settings, and its versions.
 * A draft is edited directly. A published assessment is changed through a new version that learners do not see until it is published.
 */
export default function AssessmentQuestionsPanel({ detail, onChanged }: { detail: AssessmentDetail; onChanged: () => Promise<void> }) {
  const { assessment } = detail
  const editable = assessment.status === 'Draft' || detail.editingDraftVersion === true
  const published = assessment.status === 'Published'
  const base = `/api/v1/tenant/assessments/${assessment.id}`
  const [rubrics, setRubrics] = useState<Rubric[]>([])
  const [form, setForm] = useState(emptyForm)
  const [editing, setEditing] = useState<Question | null>(null)
  const [settings, setSettings] = useState({ title: assessment.title, instructions: assessment.instructions ?? '', minutes: assessment.timeLimitMinutes ? String(assessment.timeLimitMinutes) : '', attempts: String(assessment.attemptLimit), shuffleQuestions: !!assessment.shuffleQuestions, shuffleOptions: !!assessment.shuffleOptions, opensAt: toLocalInput(assessment.opensAtUtc), dueAt: toLocalInput(assessment.dueAtUtc) })
  const [draws, setDraws] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [settingsError, setSettingsError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    apiRequest<Rubric[]>(`/api/v1/tenant/courses/${assessment.courseId}/rubrics`).then((list) => setRubrics(Array.isArray(list) ? list : [])).catch(() => setRubrics([]))
  }, [assessment.courseId])
  useEffect(() => { setDraws(Object.fromEntries((detail.pools ?? []).map((pool) => [pool.name, String(pool.drawCount)]))) }, [detail.pools])

  async function run(action: () => Promise<void>, failure: string, report: (message: string) => void = setError) {
    setBusy(true); setError(null); setNotice(null)
    try { await action() } catch (exception) { report(readError(exception, failure)) } finally { setBusy(false) }
  }

  const isChoice = choiceTypes.includes(form.type)
  const rubric = rubrics.find((item) => item.id === form.rubricId)
  const startEdit = (question: Question) => {
    setEditing(question); setFormError(null)
    setForm({ type: question.type, prompt: question.prompt, options: question.options.join(', '), correct: question.correctAnswers.join(', '), points: String(question.points), pool: question.pool ?? '', rubricId: question.rubricId ?? '' })
  }
  const cancelEdit = () => { setEditing(null); setForm(emptyForm); setFormError(null) }

  const saveQuestion = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const prompt = form.prompt.trim()
    const options = splitCsv(form.options)
    const correct = splitCsv(form.correct)
    const points = rubric ? rubric.totalPoints : Number(form.points)
    if (!prompt) return setFormError('Question prompt is required.')
    if (isChoice && options.length < 2) return setFormError('At least two options are required for choice questions.')
    if (isChoice && correct.length === 0) return setFormError('Please indicate the correct answer(s) for choice questions.')
    if (form.type === 'ShortAnswer' && correct.length === 0) return setFormError('Give at least one accepted answer.')
    if (!Number.isFinite(points) || points <= 0) return setFormError('Points must be a positive number.')
    void run(async () => {
      const body = JSON.stringify({ type: form.type, prompt, options, correctAnswers: correct, points, pool: form.pool.trim() || undefined, rubricId: rubricTypes.includes(form.type) && form.rubricId ? form.rubricId : undefined })
      if (editing) await apiRequest(`${base}/questions/${editing.id}`, { method: 'PUT', body })
      else await apiRequest(`${base}/questions`, { method: 'POST', body })
      cancelEdit()
      await onChanged()
    }, editing ? 'Unable to save the question.' : 'Unable to add question.', setFormError)
  }

  const remove = (question: Question) => {
    if (!window.confirm(`Delete question ${question.displayOrder}?`)) return
    void run(async () => { await apiRequest(`${base}/questions/${question.id}`, { method: 'DELETE' }); if (editing?.id === question.id) cancelEdit(); await onChanged() }, 'Unable to delete the question.')
  }

  const saveDraw = (name: string) => void run(async () => {
    await apiRequest(`${base}/pools/${encodeURIComponent(name)}`, { method: 'PUT', body: JSON.stringify({ drawCount: Number(draws[name]) }) })
    setNotice(`Pool “${name}” saved.`); await onChanged()
  }, 'Unable to save the pool.')

  const publish = () => void run(async () => { await apiRequest(`${base}/publish`, { method: 'POST' }); setNotice(published ? 'The new version is now live.' : 'Published.'); await onChanged() }, 'Unable to publish assessment.')
  const startVersion = () => void run(async () => { await apiRequest(`${base}/versions`, { method: 'POST' }); setNotice('A new version was started. Learners keep the current one until you publish.'); await onChanged() }, 'Unable to start a new version.')
  const discard = () => {
    if (!window.confirm('Discard the new version? Its changes are lost; the current version stays as it is.')) return
    void run(async () => { await apiRequest(`${base}/versions/draft`, { method: 'DELETE' }); cancelEdit(); await onChanged() }, 'Unable to discard the new version.')
  }

  const saveSettings = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!settings.title.trim()) return setSettingsError('Enter a title.')
    if (settings.minutes.trim() && (!Number.isInteger(Number(settings.minutes)) || Number(settings.minutes) < 1)) return setSettingsError('Time limit must be a whole number of minutes, 1 or more.')
    const attempts = Number(settings.attempts)
    if (!Number.isInteger(attempts) || attempts < 1 || attempts > 20) return setSettingsError('Attempts allowed must be a whole number from 1 to 20.')
    const opens = fromLocalInput(settings.opensAt)
    const due = fromLocalInput(settings.dueAt)
    if (opens && due && new Date(due) <= new Date(opens)) return setSettingsError('The deadline must be after the opening time.')
    setSettingsError(null)
    void run(async () => {
      await apiRequest(base, { method: 'PUT', body: JSON.stringify({ title: settings.title.trim(), instructions: settings.instructions.trim() || null, timeLimitMinutes: settings.minutes.trim() ? Number(settings.minutes) : null, attemptLimit: attempts, shuffleQuestions: settings.shuffleQuestions, shuffleOptions: settings.shuffleOptions, opensAtUtc: opens, dueAtUtc: due }) })
      setNotice('Settings saved. They apply to attempts started from now on.'); await onChanged()
    }, 'Unable to save the settings.', setSettingsError)
  }

  const pools = detail.pools ?? []
  return (
    <div className="flex flex-col gap-4">
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      <Card>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <CardTitle>{assessment.title}</CardTitle>
            <div className="flex flex-col items-end gap-1">
              <Badge>{assessment.status}</Badge>
              {published ? <small className="text-muted-foreground">Version {assessment.currentVersion ?? 1} is live{detail.editingDraftVersion ? ` · editing version ${assessment.draftVersion}` : ''}</small> : null}
            </div>
          </div>
          <CardDescription>
            {detail.questions.length} question{detail.questions.length === 1 ? '' : 's'} in {detail.editingDraftVersion ? 'the new version' : 'this assessment'}; each attempt gets {assessment.questionCount} of the live version.
            {!published ? ' Publish the assessment once it is complete.' : null}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          {assessment.status === 'Draft' ? <Button disabled={busy || detail.questions.length === 0} onClick={publish}>Publish assessment</Button> : null}
          {published && !detail.editingDraftVersion ? <Button variant="outline" disabled={busy} onClick={startVersion}>Start a new version</Button> : null}
          {detail.editingDraftVersion ? <>
            <Button disabled={busy || detail.questions.length === 0} onClick={publish}>Publish new version</Button>
            <Button variant="outline" disabled={busy} onClick={discard}>Discard new version</Button>
          </> : null}
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Settings</CardTitle></CardHeader>
        <CardContent>
          <FormLayout onSubmit={saveSettings} noValidate>
            <ErrorBanner message={settingsError} />
            <FormSection title="Basics" divider={false}>
              <Field id="settings-title" label="Assessment title" required><Input id="settings-title" value={settings.title} onChange={(event) => setSettings({ ...settings, title: event.target.value })} /></Field>
              <Field id="settings-instructions" label="Assessment instructions"><Textarea id="settings-instructions" rows={2} value={settings.instructions} onChange={(event) => setSettings({ ...settings, instructions: event.target.value })} /></Field>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field id="settings-minutes" label="Time limit in minutes" hint="Empty for no limit."><Input id="settings-minutes" type="number" min="1" value={settings.minutes} onChange={(event) => setSettings({ ...settings, minutes: event.target.value })} /></Field>
                <Field id="settings-attempts" label="Attempts allowed per learner" required><Input id="settings-attempts" type="number" min="1" max="20" value={settings.attempts} onChange={(event) => setSettings({ ...settings, attempts: event.target.value })} /></Field>
              </div>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field id="settings-opens" label="Opens at" hint="Empty: open as soon as it is published."><Input id="settings-opens" type="datetime-local" value={settings.opensAt} onChange={(event) => setSettings({ ...settings, opensAt: event.target.value })} /></Field>
                <Field id="settings-due" label="Deadline" hint="No attempt can start after it, and one under way ends at it."><Input id="settings-due" type="datetime-local" value={settings.dueAt} onChange={(event) => setSettings({ ...settings, dueAt: event.target.value })} /></Field>
              </div>
              <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={settings.shuffleQuestions} onChange={(event) => setSettings({ ...settings, shuffleQuestions: event.target.checked })} />Shuffle the order of questions for each learner</label>
              <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={settings.shuffleOptions} onChange={(event) => setSettings({ ...settings, shuffleOptions: event.target.checked })} />Shuffle the options of choice questions</label>
            </FormSection>
            <FormActions busy={busy} submitLabel="Save settings" />
          </FormLayout>
        </CardContent>
      </Card>

      {pools.length > 0 ? (
        <Card>
          <CardHeader>
            <CardTitle>Question pools</CardTitle>
            <CardDescription>Each learner is dealt only this many questions from a pool, chosen at random. Questions in a pool must be worth the same points.</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-col gap-2">
            {pools.map((pool) => (
              <div key={pool.name} className="flex flex-wrap items-center gap-2 text-sm">
                <strong className="min-w-32">{pool.name}</strong>
                <label className="flex items-center gap-2">Draw
                  <Input className="w-20" type="number" min="1" max={pool.questionCount} aria-label={`Questions to draw from ${pool.name}`} disabled={!editable} value={draws[pool.name] ?? ''} onChange={(event) => setDraws({ ...draws, [pool.name]: event.target.value })} />
                </label>
                <span className="text-muted-foreground">of {pool.questionCount}</span>
                {editable ? <Button size="sm" variant="outline" disabled={busy || Number(draws[pool.name]) === pool.drawCount} onClick={() => saveDraw(pool.name)}>Save</Button> : null}
              </div>
            ))}
          </CardContent>
        </Card>
      ) : null}

      <Card>
        <CardHeader><CardTitle>Questions</CardTitle></CardHeader>
        <CardContent className="flex flex-col gap-3">
          {detail.questions.length === 0 ? <EmptyState>Add questions before publishing.</EmptyState> : detail.questions.map((question) => (
            <div key={question.id} className="rounded-md border border-border p-3 text-sm">
              <div className="mb-1 flex items-center justify-between gap-2 text-xs text-muted-foreground">
                <span>Question {question.displayOrder}{question.pool ? ` · pool ${question.pool}` : ''}</span>
                <span>{readableType[question.type] ?? question.type} · {question.points} point{question.points === 1 ? '' : 's'}</span>
              </div>
              <strong>{question.prompt}</strong>
              {question.options.length > 0 ? <p className="text-muted-foreground">Options: {question.options.join(' · ')}</p> : null}
              {question.correctAnswers.length > 0 ? <small className="text-primary">Correct: {question.correctAnswers.join(', ')}</small> : null}
              {question.rubricName ? <small className="block text-muted-foreground">Marked with rubric: {question.rubricName}</small> : null}
              {editable ? (
                <div className="mt-2 flex gap-2">
                  <Button size="sm" variant="outline" disabled={busy} aria-label={`Edit question ${question.displayOrder}`} onClick={() => startEdit(question)}>Edit</Button>
                  <Button size="sm" variant="outline" disabled={busy} aria-label={`Delete question ${question.displayOrder}`} onClick={() => remove(question)}>Delete</Button>
                </div>
              ) : null}
            </div>
          ))}
        </CardContent>
      </Card>

      {editable ? (
        <Card>
          <CardHeader><CardTitle>{editing ? `Edit question ${editing.displayOrder}` : 'Add question'}</CardTitle></CardHeader>
          <CardContent>
            <FormLayout onSubmit={saveQuestion}>
              <ErrorBanner message={formError} />
              <FormSection title="Question">
                <Field id="question-type" label="Type" required>
                  <Select id="question-type" value={form.type} onChange={(event) => setForm({ ...form, type: event.target.value, rubricId: rubricTypes.includes(event.target.value) ? form.rubricId : '' })}>
                    {Object.keys(readableType).map((type) => <option key={type} value={type}>{type}</option>)}
                  </Select>
                </Field>
                <Field id="question-prompt" label="Prompt" required>
                  <Textarea id="question-prompt" value={form.prompt} onChange={(event) => setForm({ ...form, prompt: event.target.value })} rows={3} placeholder="Question prompt" />
                </Field>
                <Field id="question-points" label="Points" required className="max-w-xs" hint={rubric ? `Worth the rubric's total: ${rubric.totalPoints}.` : undefined}>
                  <Input id="question-points" value={rubric ? String(rubric.totalPoints) : form.points} disabled={!!rubric} onChange={(event) => setForm({ ...form, points: event.target.value })} type="number" min="1" max="100" />
                </Field>
              </FormSection>
              <FormSection title="Answers" description="Needed for choice and short-answer questions. Separate entries with commas.">
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field id="question-options" label="Options" required={isChoice}>
                    <Input id="question-options" value={form.options} onChange={(event) => setForm({ ...form, options: event.target.value })} placeholder="Comma separated" />
                  </Field>
                  <Field id="question-correct" label="Correct answers" required={isChoice} hint={form.type === 'ShortAnswer' ? 'Any one of these counts as right.' : undefined}>
                    <Input id="question-correct" value={form.correct} onChange={(event) => setForm({ ...form, correct: event.target.value })} placeholder="Comma separated" />
                  </Field>
                </div>
              </FormSection>
              <FormSection title="Options for this question" description="Optional.">
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field id="question-pool" label="Pool" hint="Questions with the same pool name are alternatives; each learner gets only some of them.">
                    <Input id="question-pool" value={form.pool} maxLength={100} onChange={(event) => setForm({ ...form, pool: event.target.value })} placeholder="No pool" />
                  </Field>
                  {rubricTypes.includes(form.type) ? (
                    <Field id="question-rubric" label="Rubric" hint="Mark by criteria instead of one score.">
                      <Select id="question-rubric" value={form.rubricId} onChange={(event) => setForm({ ...form, rubricId: event.target.value })}>
                        <option value="">No rubric</option>
                        {rubrics.map((item) => <option key={item.id} value={item.id}>{item.name} ({item.totalPoints} points)</option>)}
                      </Select>
                    </Field>
                  ) : null}
                </div>
              </FormSection>
              <FormActions busy={busy} busyLabel={editing ? 'Saving…' : 'Adding…'} submitLabel={editing ? 'Save question' : 'Add question'} onCancel={editing ? cancelEdit : undefined} />
            </FormLayout>
          </CardContent>
        </Card>
      ) : published ? (
        <p className="text-sm text-muted-foreground">This assessment is published, so its questions cannot be changed directly. Start a new version to edit them; learners keep the current version until you publish the new one.</p>
      ) : null}
    </div>
  )
}
