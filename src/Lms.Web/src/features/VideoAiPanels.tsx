import { useEffect, useRef, useState } from 'react'
import { ErrorBanner, Field, FormActions, FormLayout, FormSection, NoticeBanner } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { clock, currentLine, filterLines, validateQuestions, validateQuizForm, type Insight, type PracticeQuestion, type QuizCreated, type Transcript } from '@/lib/videoAi'
import type { VideoItem } from '@/lib/video'

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)
const base = (video: VideoItem) => `/api/v1/tenant/videos/${video.id}`

// ---------- reading along ----------
/** The spoken words with their times. Clicking a line jumps the video there; the line being said is marked. */
export function TranscriptView({ transcript, time, onSeek }: { transcript: Transcript; time: number; onSeek: (seconds: number) => void }) {
  const [search, setSearch] = useState('')
  const lines = transcript.segments ?? []
  const shown = filterLines(lines, search)
  const now = currentLine(lines, time)
  const active = useRef<HTMLLIElement>(null)
  useEffect(() => { active.current?.scrollIntoView?.({ block: 'nearest' }) }, [now])
  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h4 className="text-sm font-semibold">Transcript</h4>
        <Input type="search" className="max-w-xs" placeholder="Search this transcript" aria-label="Search this transcript" value={search} onChange={(event) => setSearch(event.target.value)} />
      </div>
      {shown.length === 0 ? <p className="text-sm text-muted-foreground">No line has those words.</p> : (
        <ol aria-label="Transcript" className="max-h-72 overflow-y-auto rounded-md border border-border">
          {shown.map((line) => (
            <li key={line.index} ref={line.index === now ? active : undefined} aria-current={line.index === now ? 'true' : undefined} className={line.index === now ? 'bg-muted' : undefined}>
              <button type="button" className="flex w-full gap-3 px-3 py-1.5 text-left text-sm hover:bg-muted/60" onClick={() => onSeek(line.startSeconds)}>
                <span className="w-12 shrink-0 tabular-nums text-muted-foreground">{clock(line.startSeconds)}</span>
                <span>{line.text}</span>
              </button>
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

/** What staff chose to share with learners: the summary, and practice questions they can answer. */
export function StudyAids({ video, onSeek }: { video: VideoItem; onSeek: (seconds: number) => void }) {
  const [items, setItems] = useState<Insight[] | null>(null)
  useEffect(() => {
    let current = true
    apiRequest<Insight[]>(`${base(video)}/insights`).then((list) => { if (current) setItems(Array.isArray(list) ? list : []) }).catch(() => { if (current) setItems([]) })
    return () => { current = false }
  }, [video.id])   
  if (!items || items.length === 0) return null
  return (
    <div className="flex flex-col gap-3">
      {items.map((item) => item.kind === 'Summary'
        ? <SummaryCard key={item.id} insight={item} onSeek={onSeek} />
        : <PracticeCard key={item.id} insight={item} onSeek={onSeek} />)}
    </div>
  )
}

/** Turns "[12:30]" in a summary into a button that jumps there. */
function WithMoments({ text, onSeek }: { text: string; onSeek: (seconds: number) => void }) {
  const parts = text.split(/(\[\d+:\d{2}(?::\d{2})?\])/g)
  return (
    <>
      {parts.map((part, index) => {
        const match = /^\[(\d+):(\d{2})(?::(\d{2}))?\]$/.exec(part)
        if (!match) return <span key={index}>{part}</span>
        const seconds = match[3] ? Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3]) : Number(match[1]) * 60 + Number(match[2])
        return <button key={index} type="button" className="mx-0.5 rounded bg-muted px-1 text-xs tabular-nums text-primary hover:underline" onClick={() => onSeek(seconds)} aria-label={`Go to ${part.slice(1, -1)}`}>{part.slice(1, -1)}</button>
      })}
    </>
  )
}

function SummaryCard({ insight, onSeek }: { insight: Insight; onSeek: (seconds: number) => void }) {
  return (
    <section aria-label="Summary" className="rounded-md border border-border p-3">
      <h4 className="mb-1 text-sm font-semibold">Summary</h4>
      <p className="whitespace-pre-wrap text-sm"><WithMoments text={insight.content ?? ''} onSeek={onSeek} /></p>
    </section>
  )
}

function PracticeCard({ insight, onSeek }: { insight: Insight; onSeek: (seconds: number) => void }) {
  const questions = insight.questions ?? []
  const [chosen, setChosen] = useState<Record<number, number>>({})
  const answered = Object.keys(chosen).length
  const correct = questions.filter((item, index) => chosen[index] === item.answerIndex).length
  return (
    <section aria-label="Practice questions" className="rounded-md border border-border p-3">
      <div className="mb-2 flex items-center justify-between gap-2">
        <h4 className="text-sm font-semibold">Practice questions</h4>
        {answered === questions.length && questions.length > 0 ? <Badge variant="secondary">{correct} of {questions.length} correct</Badge> : null}
      </div>
      <ol className="flex flex-col gap-4">
        {questions.map((item, index) => {
          const picked = chosen[index]
          return (
            <li key={index} className="flex flex-col gap-1.5 text-sm">
              <p className="font-medium">{index + 1}. {item.question}</p>
              <div role="radiogroup" aria-label={`Answers to question ${index + 1}`} className="flex flex-col gap-1">
                {item.options.map((option, optionIndex) => {
                  const mark = picked === undefined ? '' : optionIndex === item.answerIndex ? 'border-green-600 bg-green-50 dark:bg-green-950/30' : optionIndex === picked ? 'border-destructive bg-destructive/5' : ''
                  return (
                    <label key={optionIndex} className={`flex cursor-pointer items-center gap-2 rounded-md border border-border px-2 py-1 ${mark}`}>
                      <input type="radio" name={`${insight.id}-${index}`} checked={picked === optionIndex} disabled={picked !== undefined} onChange={() => setChosen((current) => ({ ...current, [index]: optionIndex }))} />
                      <span>{option}</span>
                    </label>
                  )
                })}
              </div>
              {picked !== undefined ? (
                <p role="status" className="text-muted-foreground">
                  {picked === item.answerIndex ? 'Correct. ' : `Not quite — the answer is “${item.options[item.answerIndex]}”. `}
                  {item.explanation ?? ''}
                  {item.timestampSeconds !== null ? <Button type="button" variant="soft" size="sm" className="ml-1" onClick={() => onSeek(item.timestampSeconds!)}>Watch this part ({clock(item.timestampSeconds)})</Button> : null}
                </p>
              ) : null}
            </li>
          )
        })}
      </ol>
    </section>
  )
}

// ---------- staff: transcript ----------
export function TranscriptManager({ video, transcript, onChange }: { video: VideoItem; transcript: Transcript; onChange: (transcript: Transcript) => void }) {
  const [text, setText] = useState('')
  const [language, setLanguage] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const waiting = transcript.status === 'Queued' || transcript.status === 'Processing'

  // A transcript being made finishes by itself: check back until it does.
  useEffect(() => {
    if (!waiting) return
    const timer = window.setInterval(() => { apiRequest<Transcript>(`${base(video)}/transcript`).then(onChange).catch(() => undefined) }, 4000)
    return () => window.clearInterval(timer)
  }, [waiting, video.id])   

  async function run<T>(work: () => Promise<T>, done: string | null, fallback: string) {
    setBusy(true); setProblem(null); setNotice(null)
    try { await work(); if (done) setNotice(done) }
    catch (exception) { setProblem(readError(exception, fallback)) }
    finally { setBusy(false) }
  }

  const save = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!text.trim()) return setProblem('Paste the transcript, or choose a WebVTT or SRT file.')
    void run(async () => { onChange(await apiRequest<Transcript>(`${base(video)}/transcript`, { method: 'POST', body: JSON.stringify({ text, language: language.trim() || null }) })); setText('') }, 'Transcript saved.', 'Unable to save the transcript.')
  }

  const generate = () => run(async () => onChange(await apiRequest<Transcript>(`${base(video)}/transcript/generate`, { method: 'POST' })), 'The transcript is being made. This page updates by itself.', 'Unable to start the transcript.')

  const remove = () => {
    if (!window.confirm('Delete the transcript? Search and the transcript beside the video stop working until you add one again. Summaries and questions already written are kept.')) return
    void run(async () => { await apiRequest(`${base(video)}/transcript`, { method: 'DELETE' }); onChange({ status: 'None', source: null, language: null, provider: null, statusMessage: null, segments: null }) }, 'Transcript deleted.', 'Unable to delete the transcript.')
  }

  async function chooseFile(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    if (file) setText(await file.text())
  }

  return (
    <div className="flex flex-col gap-4">
      <ErrorBanner message={problem} />
      <NoticeBanner message={notice} />
      <div className="flex flex-wrap items-center gap-2 text-sm" data-testid="transcript-state">
        {transcript.status === 'None' ? <span className="text-muted-foreground">No transcript yet.</span> : null}
        {waiting ? <span role="status" className="text-muted-foreground">The transcript is being made…</span> : null}
        {transcript.status === 'Ready' ? <><Badge variant="secondary">Ready</Badge><span className="text-muted-foreground">{transcript.segments?.length ?? 0} lines · {transcript.source === 'Generated' ? 'made from the sound' : 'added by hand'}{transcript.language ? ` · ${transcript.language}` : ''}</span></> : null}
        {transcript.status === 'Failed' ? <span role="alert" className="text-destructive">The transcript could not be made. {transcript.statusMessage}</span> : null}
        {transcript.status !== 'None' && !waiting ? <Button type="button" size="sm" variant="softDestructive" disabled={busy} onClick={remove}>Delete transcript</Button> : null}
      </div>
      {video.type !== 'External' ? (
        <FormSection title="Make it automatically" description="The sound of the video is sent to the speech-to-text service your administrator set up under Integrations → Video AI.">
          <div><Button type="button" variant="outline" disabled={busy || waiting} onClick={() => void generate()}>{transcript.status === 'Ready' || transcript.status === 'Failed' ? 'Make the transcript again' : 'Make the transcript'}</Button></div>
        </FormSection>
      ) : null}
      <FormLayout onSubmit={save}>
        <FormSection title="Or add one yourself" description="A WebVTT (.vtt) or SRT (.srt) file, as exported by most video tools and captioning services. Adding one replaces the current transcript.">
          <Field id="transcript-file" label="Choose a file"><Input id="transcript-file" type="file" accept=".vtt,.srt,text/vtt,text/plain" onChange={(event) => void chooseFile(event)} /></Field>
          <Field id="transcript-text" label="Transcript" required><Textarea id="transcript-text" rows={6} placeholder={'WEBVTT\n\n00:00:00.000 --> 00:00:04.000\nWelcome to the lesson.'} value={text} onChange={(event) => setText(event.target.value)} /></Field>
          <Field id="transcript-language" label="Language" className="max-w-xs" hint="Optional, for example en or ne."><Input id="transcript-language" maxLength={40} value={language} onChange={(event) => setLanguage(event.target.value)} /></Field>
        </FormSection>
        <FormActions busy={busy} submitLabel="Save transcript" busyLabel="Saving…" />
      </FormLayout>
    </div>
  )
}

// ---------- staff: summaries and questions ----------
export function InsightsManager({ video, hasTranscript }: { video: VideoItem; hasTranscript: boolean }) {
  const [items, setItems] = useState<Insight[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [count, setCount] = useState('5')
  const [editing, setEditing] = useState<string | null>(null)
  const [quizFor, setQuizFor] = useState<string | null>(null)

  useEffect(() => {
    apiRequest<Insight[]>(`${base(video)}/insights`).then((list) => setItems(Array.isArray(list) ? list : [])).catch((exception) => { setItems([]); setProblem(readError(exception, 'Unable to load the summaries and questions.')) })
  }, [video.id])   

  const replace = (updated: Insight) => setItems((current) => (current ?? []).map((item) => (item.id === updated.id ? updated : item)))

  async function generate(kind: 'Summary' | 'Questions') {
    setBusy(true); setProblem(null); setNotice(null)
    try {
      const created = await apiRequest<Insight>(`${base(video)}/insights`, { method: 'POST', body: JSON.stringify({ kind, count: kind === 'Questions' ? Number(count) : undefined }) })
      setItems((current) => [...(current ?? []), created])
      setNotice(`${kind === 'Summary' ? 'Summary' : 'Questions'} written as a draft. Check it, then publish it for learners.`)
    } catch (exception) { setProblem(readError(exception, 'Unable to write it.')) }
    finally { setBusy(false) }
  }

  async function publish(item: Insight) {
    setProblem(null)
    try { replace(await apiRequest<Insight>(`${base(video)}/insights/${item.id}/publish`, { method: 'POST', body: JSON.stringify({ published: !item.published }) })) }
    catch (exception) { setProblem(readError(exception, 'Unable to change who can see it.')) }
  }

  async function remove(item: Insight) {
    if (!window.confirm(`Delete this ${item.kind === 'Summary' ? 'summary' : 'set of questions'}?`)) return
    setProblem(null)
    try { await apiRequest(`${base(video)}/insights/${item.id}`, { method: 'DELETE' }); setItems((current) => (current ?? []).filter((other) => other.id !== item.id)) }
    catch (exception) { setProblem(readError(exception, 'Unable to delete it.')) }
  }

  return (
    <div className="flex flex-col gap-4">
      <ErrorBanner message={problem} />
      <NoticeBanner message={notice} />
      {hasTranscript ? null : <p className="rounded-md border border-border p-3 text-sm text-muted-foreground">Add a transcript first (on the Transcript tab): summaries and questions are written from what is said in the video.</p>}
      <FormSection title="Write something for learners" description="Written from the transcript. It stays a draft, visible only to staff, until you publish it. Check it first: automatic writing can be wrong.">
        <div className="flex flex-wrap items-end gap-3">
          <Button type="button" variant="outline" disabled={busy || !hasTranscript} onClick={() => void generate('Summary')}>Write a summary</Button>
          <Field id="question-count" label="Questions" className="w-24">
            <Select id="question-count" value={count} onChange={(event) => setCount(event.target.value)}>{['3', '5', '8', '10'].map((value) => <option key={value} value={value}>{value}</option>)}</Select>
          </Field>
          <Button type="button" variant="outline" disabled={busy || !hasTranscript} onClick={() => void generate('Questions')}>Write practice questions</Button>
        </div>
      </FormSection>
      {items === null ? <p className="text-sm text-muted-foreground">Loading…</p> : items.length === 0 ? <p className="text-sm text-muted-foreground">Nothing written yet.</p> : (
        <ul className="flex flex-col gap-3" aria-label="Summaries and questions">
          {items.map((item) => (
            <li key={item.id} className="flex flex-col gap-2 rounded-md border border-border p-3">
              <div className="flex flex-wrap items-center gap-2">
                <strong className="text-sm">{item.kind === 'Summary' ? 'Summary' : 'Practice questions'}</strong>
                <Badge variant={item.published ? 'default' : 'outline'}>{item.published ? 'Published' : 'Draft'}</Badge>
                <small className="text-muted-foreground">{item.provider === 'Local' ? 'picked from the transcript' : `written by ${item.model ?? 'an AI model'}`}</small>
              </div>
              {editing === item.id
                ? <InsightEditor video={video} insight={item} onSaved={(updated) => { replace(updated); setEditing(null) }} onCancel={() => setEditing(null)} />
                : item.kind === 'Summary'
                  ? <p className="whitespace-pre-wrap text-sm">{item.content}</p>
                  : <ol className="list-decimal pl-5 text-sm">{(item.questions ?? []).map((question, index) => <li key={index}>{question.question} <span className="text-muted-foreground">→ {question.options[question.answerIndex]}</span></li>)}</ol>}
              {item.quizAssessmentId ? <p className="text-sm text-muted-foreground">A graded quiz was made from these questions. Find it under Assessments.</p> : null}
              {quizFor === item.id ? <QuizForm video={video} insight={item} onCreated={(quiz) => { replace({ ...item, quizAssessmentId: quiz.assessmentId }); setQuizFor(null); setNotice(`Quiz “${quiz.title}” made with ${quiz.questions} questions (${quiz.totalPoints} points). It is ${quiz.status === 'Published' ? 'published, so learners can take it' : 'a draft: publish it under Assessments'}.`) }} onCancel={() => setQuizFor(null)} /> : null}
              {editing === item.id ? null : (
                <div className="flex flex-wrap gap-2">
                  <Button type="button" size="sm" variant="outline" onClick={() => setEditing(item.id)}>Edit</Button>
                  <Button type="button" size="sm" variant={item.published ? 'outline' : 'default'} onClick={() => void publish(item)}>{item.published ? 'Unpublish' : 'Publish to learners'}</Button>
                  {item.kind === 'Questions' && item.published && !item.quizAssessmentId ? <Button type="button" size="sm" variant="soft" onClick={() => setQuizFor(quizFor === item.id ? null : item.id)}>Make a graded quiz</Button> : null}
                  <Button type="button" size="sm" variant="softDestructive" onClick={() => void remove(item)}>Delete</Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/** Turns a published set of practice questions into a graded quiz in the course's assessments. */
function QuizForm({ video, insight, onCreated, onCancel }: { video: VideoItem; insight: Insight; onCreated: (quiz: QuizCreated) => void; onCancel: () => void }) {
  const [title, setTitle] = useState('Quiz: ' + video.title)
  const [points, setPoints] = useState('1')
  const [attempts, setAttempts] = useState('1')
  const [minutes, setMinutes] = useState('')
  const [now, setNow] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function create(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const message = validateQuizForm({ title, points, attempts, minutes })
    setProblem(message)
    if (message) return
    setBusy(true)
    try {
      onCreated(await apiRequest<QuizCreated>(base(video) + '/insights/' + insight.id + '/quiz', { method: 'POST', body: JSON.stringify({ title: title.trim() || null, pointsPerQuestion: Number(points), attemptLimit: Number(attempts), timeLimitMinutes: minutes.trim() ? Number(minutes) : null, publish: now }) }))
    } catch (exception) { setProblem(readError(exception, 'Unable to make the quiz.')); setBusy(false) }
  }

  return (
    <FormLayout onSubmit={create}>
      <ErrorBanner message={problem} />
      <FormSection title="Make a graded quiz" description="The same questions become multiple-choice questions in this course's assessments, marked automatically. Results go to the gradebook like any other assessment.">
        <Field id={'quiz-title-' + insight.id} label="Title"><Input id={'quiz-title-' + insight.id} maxLength={250} value={title} onChange={(event) => setTitle(event.target.value)} /></Field>
        <div className="grid gap-3 sm:grid-cols-3">
          <Field id={'quiz-points-' + insight.id} label="Points per question" required><Input id={'quiz-points-' + insight.id} inputMode="numeric" value={points} onChange={(event) => setPoints(event.target.value)} /></Field>
          <Field id={'quiz-attempts-' + insight.id} label="Attempts allowed" required><Input id={'quiz-attempts-' + insight.id} inputMode="numeric" value={attempts} onChange={(event) => setAttempts(event.target.value)} /></Field>
          <Field id={'quiz-minutes-' + insight.id} label="Time limit (minutes)" hint="Empty for no limit."><Input id={'quiz-minutes-' + insight.id} inputMode="numeric" value={minutes} onChange={(event) => setMinutes(event.target.value)} /></Field>
        </div>
        <label className="flex items-start gap-2 text-sm"><input type="checkbox" className="mt-1" checked={now} onChange={(event) => setNow(event.target.checked)} /><span><strong className="block">Publish it now</strong><span className="text-muted-foreground">Otherwise it stays a draft until you publish it under Assessments. The course must be published.</span></span></label>
      </FormSection>
      <FormActions busy={busy} submitLabel="Make the quiz" busyLabel="Making…" onCancel={onCancel} />
    </FormLayout>
  )
}

type Draft = { question: string; options: string; answerIndex: number; timestampSeconds: number | null; explanation: string }

function InsightEditor({ video, insight, onSaved, onCancel }: { video: VideoItem; insight: Insight; onSaved: (insight: Insight) => void; onCancel: () => void }) {
  const [content, setContent] = useState(insight.content ?? '')
  const [drafts, setDrafts] = useState<Draft[]>((insight.questions ?? []).map((item) => ({ question: item.question, options: item.options.join('\n'), answerIndex: item.answerIndex, timestampSeconds: item.timestampSeconds, explanation: item.explanation ?? '' })))
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const change = (index: number, patch: Partial<Draft>) => setDrafts((current) => current.map((item, at) => (at === index ? { ...item, ...patch } : item)))

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    let body: object
    if (insight.kind === 'Summary') {
      if (!content.trim()) return setProblem('Write the summary.')
      body = { content }
    } else {
      const questions: PracticeQuestion[] = drafts.map((item) => ({ question: item.question.trim(), options: item.options.split('\n').map((option) => option.trim()).filter(Boolean), answerIndex: item.answerIndex, timestampSeconds: item.timestampSeconds, explanation: item.explanation.trim() || null }))
      const message = validateQuestions(questions)
      if (message) return setProblem(message)
      body = { questions }
    }
    setProblem(null); setBusy(true)
    try { onSaved(await apiRequest<Insight>(`${base(video)}/insights/${insight.id}`, { method: 'PUT', body: JSON.stringify(body) })) }
    catch (exception) { setProblem(readError(exception, 'Unable to save.')); setBusy(false) }
  }

  return (
    <FormLayout onSubmit={save}>
      <ErrorBanner message={problem} />
      {insight.kind === 'Summary'
        ? <Field id={`edit-${insight.id}`} label="Summary" required><Textarea id={`edit-${insight.id}`} rows={8} maxLength={20000} value={content} onChange={(event) => setContent(event.target.value)} /></Field>
        : (
          <div className="flex flex-col gap-3">
            {drafts.map((item, index) => {
              const options = item.options.split('\n').map((option) => option.trim()).filter(Boolean)
              return (
                <fieldset key={index} className="flex flex-col gap-2 rounded-md border border-border p-3">
                  <legend className="px-1 text-sm font-medium">Question {index + 1}</legend>
                  <Field id={`q-${insight.id}-${index}`} label="Question" required><Input id={`q-${insight.id}-${index}`} maxLength={500} value={item.question} onChange={(event) => change(index, { question: event.target.value })} /></Field>
                  <Field id={`o-${insight.id}-${index}`} label="Answers" required hint="One answer per line."><Textarea id={`o-${insight.id}-${index}`} rows={4} value={item.options} onChange={(event) => change(index, { options: event.target.value })} /></Field>
                  <Field id={`a-${insight.id}-${index}`} label="Correct answer" required>
                    <Select id={`a-${insight.id}-${index}`} value={String(item.answerIndex)} onChange={(event) => change(index, { answerIndex: Number(event.target.value) })}>
                      {options.map((option, at) => <option key={at} value={at}>{option}</option>)}
                    </Select>
                  </Field>
                  <Field id={`e-${insight.id}-${index}`} label="Explanation"><Input id={`e-${insight.id}-${index}`} maxLength={500} value={item.explanation} onChange={(event) => change(index, { explanation: event.target.value })} /></Field>
                  <div><Button type="button" size="sm" variant="softDestructive" onClick={() => setDrafts((current) => current.filter((_, at) => at !== index))}>Remove question {index + 1}</Button></div>
                </fieldset>
              )
            })}
            <div><Button type="button" size="sm" variant="outline" onClick={() => setDrafts((current) => [...current, { question: '', options: '', answerIndex: 0, timestampSeconds: null, explanation: '' }])}>Add a question</Button></div>
          </div>
        )}
      <FormActions busy={busy} submitLabel="Save changes" busyLabel="Saving…" onCancel={onCancel} />
    </FormLayout>
  )
}
