import { useEffect, useState } from 'react'
import { Bookmark, Pencil, Trash2 } from 'lucide-react'
import { ErrorBanner } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { formatDuration, type VideoItem } from '@/lib/video'
import { currentChapter, formatChapters, parseChapters, type Chapter, type VideoNoteItem } from '@/lib/videoStudy'

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

/** The outline of a video: each part with its start, the part playing now marked, and a click to go there. Shows nothing for a video with no chapters. */
export function ChapterList({ videoId, time, onSeek }: { videoId: string; time: number; onSeek: (seconds: number) => void }) {
  const [chapters, setChapters] = useState<Chapter[]>([])
  useEffect(() => {
    let active = true
    apiRequest<Chapter[]>(`/api/v1/tenant/videos/${videoId}/chapters`).then((list) => { if (active) setChapters(Array.isArray(list) ? list : []) }).catch(() => { if (active) setChapters([]) })
    return () => { active = false }
  }, [videoId])
  if (chapters.length === 0) return null
  const now = currentChapter(chapters, time)
  return (
    <section aria-label="Chapters" className="flex flex-col gap-1 rounded-md border border-border p-3">
      <h4 className="text-sm font-semibold">Chapters</h4>
      <ol className="flex flex-col">
        {chapters.map((chapter) => (
          <li key={chapter.startSeconds}>
            <button type="button" aria-current={now?.startSeconds === chapter.startSeconds ? 'true' : undefined} onClick={() => onSeek(chapter.startSeconds)}
              className={`flex w-full items-baseline gap-3 rounded px-2 py-1 text-left text-sm hover:bg-muted ${now?.startSeconds === chapter.startSeconds ? 'bg-muted font-medium' : ''}`}>
              <span className="w-12 shrink-0 tabular-nums text-muted-foreground">{formatDuration(chapter.startSeconds)}</span>{chapter.title}
            </button>
          </li>
        ))}
      </ol>
    </section>
  )
}

/** A learner's own notes on a video: a word or a bookmark at the moment they are at, listed in the order of the video. */
export function VideoNotes({ videoId, time, onSeek }: { videoId: string; time: number; onSeek: (seconds: number) => void }) {
  const [notes, setNotes] = useState<VideoNoteItem[]>([])
  const [text, setText] = useState('')
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const base = `/api/v1/tenant/videos/${videoId}/notes`
  const sorted = (list: VideoNoteItem[]) => [...list].sort((a, b) => a.positionSeconds - b.positionSeconds || a.createdAtUtc.localeCompare(b.createdAtUtc))

  useEffect(() => {
    let active = true
    apiRequest<VideoNoteItem[]>(base).then((list) => { if (active) setNotes(Array.isArray(list) ? sorted(list) : []) }).catch(() => { if (active) setNotes([]) })
    return () => { active = false }
  }, [base])

  async function run(action: () => Promise<void>, failure: string) {
    setBusy(true); setProblem(null)
    try { await action() } catch (exception) { setProblem(readError(exception, failure)) } finally { setBusy(false) }
  }
  const add = (event: React.FormEvent) => {
    event.preventDefault()
    return run(async () => {
      const note = await apiRequest<VideoNoteItem>(base, { method: 'POST', body: JSON.stringify({ positionSeconds: Math.floor(time), text: text.trim() }) })
      setNotes((current) => sorted([...current, note])); setText('')
    }, 'Unable to save the note.')
  }
  const save = (note: VideoNoteItem) => run(async () => {
    const updated = await apiRequest<VideoNoteItem>(`${base}/${note.id}`, { method: 'PUT', body: JSON.stringify({ positionSeconds: note.positionSeconds, text: editing?.text ?? '' }) })
    setNotes((current) => sorted(current.map((item) => (item.id === note.id ? updated : item)))); setEditing(null)
  }, 'Unable to save the note.')
  const remove = (note: VideoNoteItem) => run(async () => { await apiRequest(`${base}/${note.id}`, { method: 'DELETE' }); setNotes((current) => current.filter((item) => item.id !== note.id)) }, 'Unable to delete the note.')

  return (
    <section aria-label="My notes" className="flex flex-col gap-2 rounded-md border border-border p-3">
      <h4 className="text-sm font-semibold">My notes <small className="font-normal text-muted-foreground">Only you can see them</small></h4>
      <ErrorBanner message={problem} />
      <form className="flex flex-col gap-2" onSubmit={(event) => void add(event)}>
        <Textarea aria-label="Note" rows={2} maxLength={1000} placeholder="Write a note, or leave it empty to bookmark this moment" value={text} onChange={(event) => setText(event.target.value)} />
        <div><Button type="submit" size="sm" variant="secondary" disabled={busy}><Bookmark className="mr-1.5 h-4 w-4" aria-hidden />Add note at {formatDuration(Math.floor(time))}</Button></div>
      </form>
      {notes.length === 0 ? <p className="text-sm text-muted-foreground">No notes yet.</p> : (
        <ul className="flex flex-col gap-1">
          {notes.map((note) => (
            <li key={note.id} className="flex flex-col gap-1 rounded px-2 py-1 hover:bg-muted/50">
              {editing?.id === note.id ? (
                <div className="flex flex-col gap-2">
                  <Textarea aria-label="Edit note" rows={2} maxLength={1000} value={editing.text} onChange={(event) => setEditing({ id: note.id, text: event.target.value })} />
                  <div className="flex gap-2"><Button type="button" size="sm" disabled={busy} onClick={() => void save(note)}>Save</Button><Button type="button" size="sm" variant="outline" onClick={() => setEditing(null)}>Cancel</Button></div>
                </div>
              ) : (
                <div className="flex items-start justify-between gap-2">
                  <button type="button" aria-label={`Go to ${formatDuration(note.positionSeconds)}`} onClick={() => onSeek(note.positionSeconds)} className="flex min-w-0 flex-1 items-baseline gap-3 text-left text-sm">
                    <span className="w-12 shrink-0 tabular-nums text-primary">{formatDuration(note.positionSeconds)}</span>
                    <span className="whitespace-pre-wrap break-words">{note.text || <em className="text-muted-foreground">Bookmark</em>}</span>
                  </button>
                  <span className="flex shrink-0 gap-1">
                    <Button type="button" size="icon" variant="ghost" aria-label={`Edit the note at ${formatDuration(note.positionSeconds)}`} onClick={() => setEditing({ id: note.id, text: note.text })}><Pencil className="h-4 w-4" aria-hidden /></Button>
                    <Button type="button" size="icon" variant="ghost" aria-label={`Delete the note at ${formatDuration(note.positionSeconds)}`} disabled={busy} onClick={() => void remove(note)}><Trash2 className="h-4 w-4" aria-hidden /></Button>
                  </span>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

/** Staff write the outline as lines of "time title", the way chapters are written in a video description. */
export function ChaptersEditor({ video }: { video: VideoItem }) {
  const [text, setText] = useState('')
  const [loaded, setLoaded] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    if (video.type === 'External') return undefined
    let active = true
    apiRequest<Chapter[]>(`/api/v1/tenant/videos/${video.id}/chapters`).then((list) => { if (active) { setText(formatChapters(Array.isArray(list) ? list : [])); setLoaded(true) } }).catch(() => { if (active) setLoaded(true) })
    return () => { active = false }
  }, [video.id, video.type])

  if (video.type === 'External') return <p className="text-sm text-muted-foreground">Chapters can only be set on videos held here, not on linked videos.</p>

  async function save(event: React.FormEvent) {
    event.preventDefault()
    setNotice(null)
    const parsed = parseChapters(text)
    setProblem(parsed.error)
    if (parsed.error) return
    if (video.durationSeconds && parsed.chapters.some((chapter) => chapter.startSeconds > video.durationSeconds!)) return setProblem(`A chapter cannot start after the video ends (${formatDuration(video.durationSeconds)}).`)
    setBusy(true)
    try {
      const saved = await apiRequest<Chapter[]>(`/api/v1/tenant/videos/${video.id}/chapters`, { method: 'PUT', body: JSON.stringify({ chapters: parsed.chapters }) })
      setText(formatChapters(saved)); setNotice(saved.length === 0 ? 'The chapters were cleared.' : `Saved ${saved.length} chapter${saved.length === 1 ? '' : 's'}.`)
    } catch (exception) { setProblem(readError(exception, 'Unable to save the chapters.')) } finally { setBusy(false) }
  }

  return (
    <form className="flex max-w-xl flex-col gap-3" onSubmit={(event) => void save(event)}>
      <ErrorBanner message={problem} />
      {notice ? <p role="status" className="text-sm text-muted-foreground">{notice}</p> : null}
      <label className="flex flex-col gap-1 text-sm font-medium">Chapters
        <Textarea rows={8} disabled={!loaded} placeholder={'0:00 Introduction\n2:30 The main idea\n9:45 Summary'} value={text} onChange={(event) => setText(event.target.value)} />
        <small className="font-normal text-muted-foreground">One chapter a line: its start time, then its title. The first starts at 0:00. Learners see them as an outline they can click.</small>
      </label>
      <div><Button type="submit" disabled={busy || !loaded}>Save chapters</Button></div>
    </form>
  )
}
