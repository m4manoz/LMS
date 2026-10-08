import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { EmptyState, ErrorBanner, NoticeBanner } from '@/components/form'
import { ApiError, apiRequest, downloadFile } from '@/lib/api'
import { formatBytes, manualTypes, type Attempt, type AttemptQuestion, type AttemptSummary } from '@/lib/assessments'

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

type Marks = {
  points: string
  feedback: string
  /** criterion id -> points chosen */
  criteria: Record<string, number | undefined>
}

/** Teacher review: attempts for one assessment, and marking each written answer (by rubric when the question has one). */
export default function AssessmentGradingPanel({ assessmentId, title }: { assessmentId: string; title: string }) {
  const [attempts, setAttempts] = useState<AttemptSummary[] | null>(null)
  const [open, setOpen] = useState<Attempt | null>(null)
  const [marks, setMarks] = useState<Record<string, Marks>>({})
  const [overall, setOverall] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { setAttempts(await apiRequest<AttemptSummary[]>(`/api/v1/tenant/assessments/${assessmentId}/attempts`)) }
    catch (exception) { setError(readError(exception, 'Unable to load the attempts.')) }
  }, [assessmentId])
  useEffect(() => { setOpen(null); setAttempts(null); void load() }, [load])

  async function review(item: AttemptSummary) {
    setBusy(true); setError(null); setNotice(null)
    try {
      const attempt = await apiRequest<Attempt>(`/api/v1/tenant/assessment-attempts/${item.id}`)
      setOpen(attempt)
      setOverall(attempt.teacherFeedback ?? '')
      setMarks(Object.fromEntries(attempt.questions.filter((question) => manualTypes.includes(question.type)).map((question) => [question.id, {
        points: attempt.attempt.status === 'Graded' ? String(question.scorePoints) : '',
        feedback: question.feedback ?? '',
        criteria: Object.fromEntries((question.rubricScores ?? []).map((score) => [score.criterionId, score.points])),
      }])))
    } catch (exception) { setError(readError(exception, 'Unable to open the attempt.')) } finally { setBusy(false) }
  }

  const patch = (id: string, change: Partial<Marks>) => setMarks((current) => ({ ...current, [id]: { ...current[id], ...change } }))
  const total = (question: AttemptQuestion) => question.rubric
    ? question.rubric.criteria.reduce((sum, criterion) => sum + (marks[question.id]?.criteria[criterion.id] ?? 0), 0)
    : Number(marks[question.id]?.points || 0)

  async function grade() {
    if (!open) return
    const manual = open.questions.filter((question) => manualTypes.includes(question.type))
    for (const question of manual) {
      const mark = marks[question.id]
      if (question.rubric) {
        const missing = question.rubric.criteria.find((criterion) => mark?.criteria[criterion.id] === undefined)
        if (missing) return setError(`Choose a level for “${missing.name}” in question ${question.displayOrder}.`)
      } else {
        const points = Number(mark?.points)
        if (!mark?.points.trim() || !Number.isFinite(points) || points < 0 || points > question.points)
          return setError(`Question ${question.displayOrder} is scored from 0 to ${question.points}.`)
      }
    }
    setBusy(true); setError(null)
    try {
      await apiRequest(`/api/v1/tenant/assessment-attempts/${open.attempt.id}/grade`, {
        method: 'POST',
        body: JSON.stringify({
          scorePoints: 0,
          feedback: overall.trim() || null,
          answers: manual.map((question) => question.rubric
            ? { questionId: question.id, criterionScores: question.rubric.criteria.map((criterion) => ({ criterionId: criterion.id, points: marks[question.id].criteria[criterion.id] })), feedback: marks[question.id].feedback.trim() || null }
            : { questionId: question.id, scorePoints: Number(marks[question.id].points), feedback: marks[question.id].feedback.trim() || null }),
        }),
      })
      setOpen(null); setNotice('The attempt was graded and the learner was notified.')
      await load()
    } catch (exception) { setError(readError(exception, 'Unable to grade the attempt.')) } finally { setBusy(false) }
  }

  if (open) {
    const graded = open.attempt.status === 'Graded'
    return (
      <Card>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <div>
              <p className="text-xs text-muted-foreground">{open.attempt.learnerName ?? 'Learner'} · attempt {open.attempt.attemptNumber}</p>
              <CardTitle className="text-xl">{open.assessmentTitle}</CardTitle>
            </div>
            <Badge>{open.attempt.status}</Badge>
          </div>
          <CardDescription>{graded ? 'Already graded. You can read the marks below.' : 'Mark each written answer, then save the grade.'}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-5">
          <ErrorBanner message={error} />
          {open.questions.map((question) => {
            const manual = manualTypes.includes(question.type)
            const mark = marks[question.id]
            return (
              <section key={question.id} aria-label={`Question ${question.displayOrder}`} className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm">
                <strong>{question.displayOrder}. {question.prompt}</strong>
                {manual ? (
                  <>
                    {question.text ? <p className="whitespace-pre-wrap rounded-md bg-muted px-3 py-2">{question.text}</p> : null}
                    {question.type === 'FileUpload' ? (
                      question.file ? (
                        <div className="flex flex-wrap items-center gap-2">
                          <span>{question.file.fileName} <span className="text-muted-foreground">({formatBytes(question.file.sizeBytes)})</span></span>
                          <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => void downloadFile(`/api/v1/tenant/assessment-attempts/${open.attempt.id}/answers/${question.id}/file`, question.file!.fileName).catch((exception) => setError(readError(exception, 'Unable to download the file.')))}>Download</Button>
                        </div>
                      ) : <p className="text-muted-foreground">No file was attached.</p>
                    ) : !question.text ? <p className="text-muted-foreground">No answer.</p> : null}

                    {question.rubric ? (
                      <div className="flex flex-col gap-3">
                        {question.rubric.criteria.map((criterion) => (
                          <div key={criterion.id} role="radiogroup" aria-label={criterion.name} className="flex flex-col gap-1">
                            <span className="font-medium">{criterion.name}{criterion.description ? <span className="font-normal text-muted-foreground"> — {criterion.description}</span> : null}</span>
                            <div className="flex flex-wrap gap-3">
                              {criterion.levels.map((level) => (
                                <label key={level.label} className="flex items-center gap-1.5">
                                  <input type="radio" name={`${question.id}-${criterion.id}`} disabled={graded} checked={mark?.criteria[criterion.id] === level.points}
                                    onChange={() => patch(question.id, { criteria: { ...mark?.criteria, [criterion.id]: level.points } })} />
                                  {level.label} ({level.points})
                                </label>
                              ))}
                            </div>
                          </div>
                        ))}
                        <span className="text-muted-foreground">Score: {total(question)} of {question.rubric.totalPoints}</span>
                      </div>
                    ) : (
                      <label className="flex items-center gap-2">
                        Score (0–{question.points})
                        <Input className="w-24" type="number" min="0" max={question.points} disabled={graded} aria-label={`Score for question ${question.displayOrder}`} value={mark?.points ?? ''} onChange={(event) => patch(question.id, { points: event.target.value })} />
                      </label>
                    )}
                    <Textarea aria-label={`Feedback for question ${question.displayOrder}`} disabled={graded} rows={2} placeholder="Feedback for this answer (optional)" value={mark?.feedback ?? ''} onChange={(event) => patch(question.id, { feedback: event.target.value })} />
                  </>
                ) : (
                  <p className="text-muted-foreground">
                    {question.answers.length > 0 ? question.answers.join(', ') : question.text || 'No answer'}
                    {` · ${question.scorePoints} of ${question.points}`}{question.isCorrect === true ? ' · correct' : question.isCorrect === false ? ' · not correct' : ''}
                  </p>
                )}
              </section>
            )
          })}
          <Textarea aria-label="Overall feedback" disabled={graded} rows={2} placeholder="Overall feedback for the learner (optional)" value={overall} onChange={(event) => setOverall(event.target.value)} />
          <div className="flex gap-2">
            {graded ? null : <Button disabled={busy} onClick={() => void grade()}>Save grade</Button>}
            <Button variant="outline" disabled={busy} onClick={() => { setOpen(null); setError(null) }}>Back to attempts</Button>
          </div>
        </CardContent>
      </Card>
    )
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Teacher review</CardTitle>
        <CardDescription>Attempts for {title}.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <ErrorBanner message={error} />
        <NoticeBanner message={notice} />
        {attempts === null ? <p role="status" className="text-sm text-muted-foreground">Loading…</p>
          : attempts.length === 0 ? <EmptyState>No learner attempts yet.</EmptyState>
            : attempts.map((item) => (
              <div key={item.id} className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm md:flex-row md:items-center md:justify-between">
                <span>
                  <strong className="block">{item.learnerName ?? 'Learner'} · attempt {item.attemptNumber}</strong>
                  <small className="text-muted-foreground">
                    {item.status} · {item.scorePoints}/{item.possiblePoints}{item.percentage != null ? ` · ${item.percentage}%` : ''}{item.version && item.version > 1 ? ` · version ${item.version}` : ''}
                  </small>
                </span>
                <Button variant={item.status === 'Submitted' ? 'secondary' : 'outline'} size="sm" disabled={busy} aria-label={`${item.status === 'Submitted' ? 'Grade' : 'View'} attempt ${item.attemptNumber} by ${item.learnerName ?? 'learner'}`} onClick={() => void review(item)}>
                  {item.status === 'Submitted' ? 'Grade' : 'View'}
                </Button>
              </div>
            ))}
      </CardContent>
    </Card>
  )
}
