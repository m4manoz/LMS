import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { ErrorBanner, NoticeBanner } from '@/components/form'
import { ApiError, apiRequest, downloadFile } from '@/lib/api'
import { formatBytes, timeLeft, type Attempt, type AttemptQuestion } from '@/lib/assessments'

type Draft = { choices: string[]; text: string }

const startingDraft = (question: AttemptQuestion): Draft => ({
  choices: question.answers ?? [],
  text: question.text ?? '',
})

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

/** Taking an attempt (and, once it is marked, reading how it went). Choices, typed text and attached files are all saved on the way. */
export default function AssessmentAttemptPanel({ attempt, canAttempt, onChange }: { attempt: Attempt; canAttempt: boolean; onChange: (attempt: Attempt) => void }) {
  const [drafts, setDrafts] = useState<Record<string, Draft>>(() => Object.fromEntries(attempt.questions.map((question) => [question.id, startingDraft(question)])))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const editable = canAttempt && attempt.attempt.status === 'InProgress'
  const graded = attempt.attempt.status === 'Graded'
  const base = `/api/v1/tenant/assessment-attempts/${attempt.attempt.id}`

  useEffect(() => {
    if (!editable || !attempt.expiresAtUtc) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [editable, attempt.expiresAtUtc])
  const remaining = editable ? timeLeft(attempt.expiresAtUtc, now) : null
  const outOfTime = editable && !!attempt.expiresAtUtc && remaining === null

  const patch = (id: string, change: Partial<Draft>) => setDrafts((current) => ({ ...current, [id]: { ...current[id], ...change } }))

  async function saveAll() {
    for (const question of attempt.questions) {
      const draft = drafts[question.id] ?? { choices: [], text: '' }
      const typed = ['Essay', 'ShortAnswer', 'FileUpload'].includes(question.type)
      await apiRequest(`${base}/answers/${question.id}`, {
        method: 'PUT',
        body: JSON.stringify({ answers: typed ? [] : draft.choices, text: typed ? draft.text : null }),
      })
    }
  }

  async function run(action: () => Promise<void>, failure: string) {
    setBusy(true); setError(null); setNotice(null)
    try { await action() } catch (exception) { setError(readError(exception, failure)) } finally { setBusy(false) }
  }

  const save = () => run(async () => { await saveAll(); setNotice('Your answers are saved.') }, 'Unable to save your answers.')
  const submit = () => run(async () => {
    await saveAll()
    onChange(await apiRequest<Attempt>(`${base}/submit`, { method: 'POST' }))
  }, 'Unable to submit your attempt.')

  const attach = (question: AttemptQuestion, file: File | undefined) => {
    if (!file) return
    void run(async () => {
      const body = new FormData()
      body.append('file', file)
      const stored = await apiRequest<{ fileName: string; sizeBytes: number }>(`${base}/answers/${question.id}/file`, { method: 'POST', body })
      onChange({ ...attempt, questions: attempt.questions.map((item) => (item.id === question.id ? { ...item, file: stored } : item)) })
    }, 'Unable to attach the file.')
  }
  const removeFile = (question: AttemptQuestion) => run(async () => {
    await apiRequest(`${base}/answers/${question.id}/file`, { method: 'DELETE' })
    onChange({ ...attempt, questions: attempt.questions.map((item) => (item.id === question.id ? { ...item, file: null } : item)) })
  }, 'Unable to remove the file.')
  const downloadAnswer = (question: AttemptQuestion) => run(
    () => downloadFile(`${base}/answers/${question.id}/file`, question.file?.fileName ?? 'answer'), 'Unable to download the file.')

  return (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="text-xs text-muted-foreground">Attempt {attempt.attempt.attemptNumber}</p>
            <CardTitle className="text-xl">{attempt.assessmentTitle}</CardTitle>
          </div>
          <div className="flex flex-col items-end gap-1">
            <Badge>{attempt.attempt.status}</Badge>
            {remaining ? <span role="timer" aria-label="Time left" className="text-sm font-medium tabular-nums">{remaining} left</span> : null}
          </div>
        </div>
        <CardDescription>{attempt.instructions || 'Answer each question, then submit your attempt.'}</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-5">
        <ErrorBanner message={error} />
        <NoticeBanner message={notice} />
        {outOfTime ? <p role="alert" className="rounded-md border border-border px-3 py-2 text-sm">Your time is up. Submit now to keep the answers you have saved.</p> : null}
        {graded ? (
          <div className="rounded-md bg-muted p-4">
            <strong className="text-2xl">{attempt.attempt.percentage}%</strong>
            <span className="ml-2 text-sm text-muted-foreground">{attempt.attempt.scorePoints} of {attempt.attempt.possiblePoints} points</span>
          </div>
        ) : attempt.attempt.status === 'Submitted' ? (
          <p className="rounded-md bg-muted p-3 text-sm">Submitted. Your teacher will mark the written answers.</p>
        ) : null}

        {attempt.questions.map((question) => {
          const draft = drafts[question.id] ?? { choices: [], text: '' }
          const fieldId = `answer-${question.id}`
          return (
            <section key={question.id} className="flex flex-col gap-2" aria-label={`Question ${question.displayOrder}`}>
              <div className="flex flex-col items-start gap-0.5">
                <strong>{question.displayOrder}. {question.prompt}</strong>
                <small className="font-normal text-muted-foreground">{question.points} point{question.points === 1 ? '' : 's'}</small>
              </div>

              {question.rubric ? (
                <details className="rounded-md border border-border px-3 py-2 text-sm">
                  <summary className="cursor-pointer">How this is marked: {question.rubric.name} ({question.rubric.totalPoints} points)</summary>
                  <ul className="mt-2 flex flex-col gap-2">
                    {question.rubric.criteria.map((criterion) => (
                      <li key={criterion.id}>
                        <strong>{criterion.name}</strong>{criterion.description ? <span className="text-muted-foreground"> — {criterion.description}</span> : null}
                        <div className="text-xs text-muted-foreground">{criterion.levels.map((level) => `${level.label} (${level.points})`).join(' · ')}</div>
                      </li>
                    ))}
                  </ul>
                </details>
              ) : null}

              {question.type === 'MultipleResponse' ? (
                <div className="flex flex-col gap-1.5" role="group" aria-label={`Answer ${question.displayOrder}`}>
                  {question.options.map((option) => (
                    <label key={option} className="flex items-center gap-2 text-sm">
                      <input type="checkbox" disabled={!editable} checked={draft.choices.includes(option)} onChange={(event) => patch(question.id, { choices: event.target.checked ? [...draft.choices, option] : draft.choices.filter((item) => item !== option) })} />
                      {option}
                    </label>
                  ))}
                </div>
              ) : question.type === 'MultipleChoice' || question.type === 'TrueFalse' ? (
                <div className="flex flex-col gap-1.5" role="radiogroup" aria-label={`Answer ${question.displayOrder}`}>
                  {question.options.map((option) => (
                    <label key={option} className="flex items-center gap-2 text-sm">
                      <input type="radio" name={fieldId} disabled={!editable} checked={draft.choices[0] === option} onChange={() => patch(question.id, { choices: [option] })} />
                      {option}
                    </label>
                  ))}
                </div>
              ) : question.type === 'ShortAnswer' ? (
                <Input id={fieldId} aria-label={`Answer ${question.displayOrder}`} disabled={!editable} value={draft.text} onChange={(event) => patch(question.id, { text: event.target.value })} placeholder="Your answer" />
              ) : question.type === 'FileUpload' ? (
                <div className="flex flex-col gap-2">
                  {question.file ? (
                    <div className="flex flex-wrap items-center gap-2 text-sm">
                      <span>{question.file.fileName} <span className="text-muted-foreground">({formatBytes(question.file.sizeBytes)})</span></span>
                      <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => void downloadAnswer(question)}>Download</Button>
                      {editable ? <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => void removeFile(question)}>Remove file</Button> : null}
                    </div>
                  ) : <p className="text-sm text-muted-foreground">No file attached.</p>}
                  {editable ? (
                    <Input id={fieldId} type="file" aria-label={`Attach a file to question ${question.displayOrder}`} disabled={busy} onChange={(event) => { attach(question, event.target.files?.[0]); event.target.value = '' }} />
                  ) : null}
                  <Textarea aria-label={`Note for question ${question.displayOrder}`} disabled={!editable} value={draft.text} onChange={(event) => patch(question.id, { text: event.target.value })} rows={2} placeholder="A note for your teacher (optional)" />
                </div>
              ) : (
                <Textarea id={fieldId} aria-label={`Answer ${question.displayOrder}`} disabled={!editable} value={draft.text} onChange={(event) => patch(question.id, { text: event.target.value })} rows={5} placeholder="Your answer" />
              )}

              {graded || attempt.attempt.status === 'Submitted' ? (
                <div className="text-sm">
                  {graded ? <span className="text-muted-foreground">Score: {question.scorePoints} of {question.points}{question.isCorrect === true ? ' · correct' : question.isCorrect === false ? ' · not correct' : ''}</span> : null}
                  {question.rubricScores?.length ? (
                    <ul className="mt-1 text-xs text-muted-foreground">
                      {question.rubricScores.map((score) => <li key={score.criterionId}>{score.name}: {score.points} of {score.maxPoints}</li>)}
                    </ul>
                  ) : null}
                  {question.feedback ? <p className="mt-1 rounded-md border border-border px-3 py-2">Feedback: {question.feedback}</p> : null}
                </div>
              ) : null}
            </section>
          )
        })}

        {editable ? (
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" disabled={busy} onClick={() => void save()}>Save answers</Button>
            <Button disabled={busy} onClick={() => void submit()}>Submit attempt</Button>
          </div>
        ) : null}
        {attempt.teacherFeedback ? <p className="rounded-md border border-border px-3 py-2 text-sm">Teacher feedback: {attempt.teacherFeedback}</p> : null}
      </CardContent>
    </Card>
  )
}
