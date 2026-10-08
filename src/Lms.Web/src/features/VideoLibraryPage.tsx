import { useCallback, useEffect, useMemo, useState } from 'react'
import { Film, Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import VideoPlayer from '@/components/VideoPlayer'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { uploadVideoInPieces } from '@/lib/pieceUpload'
import { formatBytes, formatDuration, progressLabel, readVideoDuration, typeLabel, type VideoItem } from '@/lib/video'
import { clock, type SearchHit, type Transcript } from '@/lib/videoAi'
import { InsightsManager, StudyAids, TranscriptManager, TranscriptView } from './VideoAiPanels'
import { ChapterList, ChaptersEditor, VideoNotes } from './VideoStudyPanels'

type Course = { id: string; code: string; title: string; status: string }
type CourseOutline = { course: Course; draftVersion?: unknown; viewingDraft?: boolean; modules: { id: string; title: string; lessons: { id: string; title: string }[] }[] }
type Usage = { count: number; totalBytes: number; quotaBytes?: number | null }
type Analytics = { eligibleLearners: number; viewers: number; completed: number; plays: number; watchedSeconds: number; averageWatchedPercent: number | null; durationSeconds: number | null; funnel: { percent: number; viewers: number }[] }

const TYPE_FILTERS = [['All', 'All'], ['Uploaded', 'Uploaded'], ['LiveRecording', 'Class recordings'], ['External', 'Linked']] as const
const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

export type VideoSort = 'newest' | 'oldest' | 'title' | 'longest' | 'largest'
export const SORTS: [VideoSort, string][] = [['newest', 'Newest first'], ['oldest', 'Oldest first'], ['title', 'Title A–Z'], ['longest', 'Longest first'], ['largest', 'Largest first']]
const STATUS_FILTERS = [['All', 'Any state'], ['Ready', 'Ready'], ['Processing', 'Converting'], ['Failed', 'Failed']] as const

/** Search, course, type, tag and state narrowing of the list on screen, in the order chosen (newest first when none is). */
export function filterVideos(videos: VideoItem[], search: string, courseId: string, type: string, more: { tag?: string; status?: string; sort?: VideoSort } = {}): VideoItem[] {
  const needle = search.trim().toLowerCase()
  const found = videos.filter((video) => (courseId === 'All' || video.courseId === courseId) && (type === 'All' || video.type === type)
    && (!more.tag || (video.tags ?? []).includes(more.tag)) && (!more.status || more.status === 'All' || video.status === more.status)
    && (!needle || video.title.toLowerCase().includes(needle) || (video.description ?? '').toLowerCase().includes(needle) || video.courseTitle.toLowerCase().includes(needle) || (video.tags ?? []).some((tag) => tag.includes(needle))))
  const byNewest = (a: VideoItem, b: VideoItem) => b.createdAtUtc.localeCompare(a.createdAtUtc)
  switch (more.sort ?? 'newest') {
    case 'oldest': return [...found].sort((a, b) => -byNewest(a, b))
    case 'title': return [...found].sort((a, b) => a.title.localeCompare(b.title) || byNewest(a, b))
    case 'longest': return [...found].sort((a, b) => (b.durationSeconds ?? -1) - (a.durationSeconds ?? -1) || byNewest(a, b))
    case 'largest': return [...found].sort((a, b) => b.sizeBytes - a.sizeBytes || byNewest(a, b))
    default: return [...found].sort(byNewest)
  }
}

/** The first problem with the "Add video" form, or null when it can be sent. */
export function validateAddVideo(mode: 'upload' | 'link', values: { title: string; courseId: string; url: string; file: File | null }): string | null {
  if (!values.title.trim()) return 'Give the video a title.'
  if (values.title.trim().length > 250) return 'The title must be 250 characters or fewer.'
  if (!values.courseId) return 'Choose the course this video belongs to.'
  if (mode === 'upload') {
    if (!values.file) return 'Choose a video file.'
    if (!['video/mp4', 'video/webm', 'video/ogg'].includes(values.file.type)) return 'Choose an MP4, WebM or OGG video.'
  } else if (!/^https:\/\/\S+$/i.test(values.url.trim())) return 'Paste the video’s link (an https address).'
  return null
}

export default function VideoLibraryPage() {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('course.manage') ?? false
  const [videos, setVideos] = useState<VideoItem[] | null>(null)
  const [courses, setCourses] = useState<Course[]>([])
  const [usage, setUsage] = useState<Usage | null>(null)
  const [search, setSearch] = useState('')
  const [courseFilter, setCourseFilter] = useState('All')
  const [typeFilter, setTypeFilter] = useState('All')
  const [statusFilter, setStatusFilter] = useState('All')
  const [tagFilter, setTagFilter] = useState<string | null>(null)
  const [sort, setSort] = useState<VideoSort>('newest')
  const [picked, setPicked] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [selected, setSelected] = useState<VideoItem | null>(null)
  const [adding, setAdding] = useState(false)
  const [hits, setHits] = useState<SearchHit[]>([])
  const [startAt, setStartAt] = useState<{ seconds: number; nonce: number } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const list = await apiRequest<VideoItem[]>('/api/v1/tenant/videos')
      setVideos(list)
      setSelected((current) => (current ? list.find((item) => item.id === current.id) ?? current : current))   // keeps an open video up to date while it is converted
    }
    catch (exception) { setError(readError(exception, 'Unable to load the videos.')) }
    if (canManage) {
      apiRequest<Usage>('/api/v1/tenant/videos/usage').then((value) => setUsage(value && typeof value === 'object' ? value : null)).catch(() => setUsage(null))
      apiRequest<Course[]>('/api/v1/tenant/courses').then((list) => setCourses(Array.isArray(list) ? list.filter((course) => course.status !== 'Archived') : [])).catch(() => setCourses([]))
    }
  }, [canManage])
  useEffect(() => { void load() }, [load])

  // Videos being converted finish by themselves: check back until none is left.
  const converting = (videos ?? []).some((item) => item.status === 'Processing')
  useEffect(() => {
    if (!converting) return
    const timer = window.setInterval(() => { void load() }, 4000)
    return () => window.clearInterval(timer)
  }, [converting, load])

  // Words typed in the search box are also looked for in what is said in the videos.
  useEffect(() => {
    const query = search.trim()
    if (query.length < 2) { setHits([]); return }
    let current = true
    const timer = window.setTimeout(() => {
      apiRequest<SearchHit[]>('/api/v1/tenant/videos/search?q=' + encodeURIComponent(query))
        .then((list) => { if (current) setHits(Array.isArray(list) ? list : []) })
        .catch(() => { if (current) setHits([]) })
    }, 350)
    return () => { current = false; window.clearTimeout(timer) }
  }, [search])

  const openAt = (hit: SearchHit) => {
    const target = (videos ?? []).find((item) => item.id === hit.videoId)
    if (!target) return
    setStartAt({ seconds: hit.startSeconds, nonce: Date.now() })
    setSelected(target); setAdding(false); setNotice(null)
  }

  const courseOptions = useMemo(() => {
    const seen = new Map<string, string>()
    for (const video of videos ?? []) seen.set(video.courseId, video.courseTitle)
    return [...seen.entries()].map(([id, title]) => ({ id, title })).sort((a, b) => a.title.localeCompare(b.title))
  }, [videos])
  const shown = filterVideos(videos ?? [], search, courseFilter, typeFilter, { tag: tagFilter ?? undefined, status: statusFilter, sort })
  const tagCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const video of videos ?? []) for (const tag of video.tags ?? []) counts.set(tag, (counts.get(tag) ?? 0) + 1)
    return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
  }, [videos])
  // A tag nobody uses any more (after deleting or retagging) must not stay chosen with an empty list.
  useEffect(() => { if (tagFilter && !tagCounts.some(([tag]) => tag === tagFilter)) setTagFilter(null) }, [tagCounts, tagFilter])
  const pickedShown = picked.filter((id) => shown.some((video) => video.id === id))

  async function deletePicked() {
    if (pickedShown.length === 0) return
    if (!window.confirm(`Delete ${pickedShown.length} video${pickedShown.length === 1 ? '' : 's'}? Learners lose access to ${pickedShown.length === 1 ? 'it' : 'them'}. Lessons that already show ${pickedShown.length === 1 ? 'it' : 'them'} keep working.`)) return
    setBusy(true); setError(null); setNotice(null)
    let deleted = 0
    const failed: string[] = []
    for (const id of pickedShown) {
      try { await apiRequest(`/api/v1/tenant/videos/${id}`, { method: 'DELETE' }); deleted += 1 }
      catch { failed.push((videos ?? []).find((video) => video.id === id)?.title ?? 'a video') }
    }
    setPicked(failed.length === 0 ? [] : picked.filter((id) => !pickedShown.includes(id) || failed.includes((videos ?? []).find((video) => video.id === id)?.title ?? '')))
    if (failed.length > 0) setError(`Could not delete ${failed.join(', ')}.`)
    if (deleted > 0) setNotice(`${deleted} video${deleted === 1 ? ' was' : 's were'} deleted.`)
    setBusy(false)
    await load()
  }

  const replace = (updated: VideoItem) => { setVideos((current) => (current ?? []).map((item) => (item.id === updated.id ? updated : item))); setSelected(updated) }
  const closeAll = () => { setSelected(null); setAdding(false) }

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Video library" description={canManage ? 'Upload and manage the videos of your courses, put them in lessons, and see how they are watched.' : 'Watch the videos of the courses you are enrolled in. Playback resumes where you left off.'}
        actions={<>
          {canManage && usage ? <span className="text-sm text-muted-foreground">{usage.count} video{usage.count === 1 ? '' : 's'} · {formatBytes(usage.totalBytes)}{usage.quotaBytes ? ` of ${formatBytes(usage.quotaBytes)}` : ''} stored</span> : null}
          {canManage ? <Button onClick={() => { setAdding(true); setSelected(null); setNotice(null) }}><Plus className="mr-1 h-4 w-4" aria-hidden />Add video</Button> : null}
        </>} />
      {selected || adding ? null : <ErrorBanner message={error} />}
      {selected || adding ? null : <NoticeBanner message={notice} />}

      <div className="flex flex-wrap items-center gap-3">
        <Input className="max-w-sm" type="search" placeholder="Search videos" aria-label="Search videos" value={search} onChange={(event) => setSearch(event.target.value)} />
        {courseOptions.length > 1 ? (
          <Select aria-label="Filter by course" className="w-56" value={courseFilter} onChange={(event) => setCourseFilter(event.target.value)}>
            <option value="All">All courses</option>
            {courseOptions.map((course) => <option key={course.id} value={course.id}>{course.title}</option>)}
          </Select>
        ) : null}
        <div className="flex flex-wrap gap-1.5" role="group" aria-label="Filter by type">
          {TYPE_FILTERS.map(([value, label]) => <Button key={value} type="button" size="sm" variant={typeFilter === value ? 'secondary' : 'outline'} aria-pressed={typeFilter === value} onClick={() => setTypeFilter(value)}>{label}</Button>)}
        </div>
        {canManage ? (
          <Select aria-label="Filter by state" className="w-40" value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)}>
            {STATUS_FILTERS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </Select>
        ) : null}
        <Select aria-label="Sort videos" className="w-44" value={sort} onChange={(event) => setSort(event.target.value as VideoSort)}>
          {SORTS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        </Select>
      </div>

      {tagCounts.length > 0 ? (
        <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Filter by tag">
          <small className="text-muted-foreground">Tags</small>
          {tagCounts.slice(0, 30).map(([tag, count]) => <Button key={tag} type="button" size="sm" variant={tagFilter === tag ? 'secondary' : 'outline'} aria-pressed={tagFilter === tag} onClick={() => setTagFilter(tagFilter === tag ? null : tag)}>{tag} <small className="ml-1 text-muted-foreground">{count}</small></Button>)}
        </div>
      ) : null}

      {canManage && shown.length > 0 ? (
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <label className="flex items-center gap-2"><input type="checkbox" checked={pickedShown.length === shown.length} onChange={(event) => setPicked(event.target.checked ? shown.map((video) => video.id) : [])} />Select all {shown.length}</label>
          {pickedShown.length > 0 ? <Button type="button" size="sm" variant="softDestructive" disabled={busy} onClick={() => void deletePicked()}>Delete {pickedShown.length} selected</Button> : null}
        </div>
      ) : null}

      {hits.length > 0 ? (
        <section aria-label="Found in what is said" className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">Found in what is said</h3>
          <RowList label="Matches in transcripts">
            {hits.slice(0, 20).map((hit, index) => (
              <ListRow key={hit.videoId + ':' + hit.startSeconds + ':' + index} columns="sm:grid-cols-[minmax(0,1fr)_auto]">
                <div className="min-w-0"><strong className="block truncate">{hit.videoTitle}</strong><small className="text-muted-foreground">{hit.courseTitle} · at {clock(hit.startSeconds)}</small><p className="truncate text-sm">{hit.text}</p></div>
                <div className="flex justify-end"><Button type="button" size="sm" variant="secondary" aria-label={'Watch ' + hit.videoTitle + ' from ' + clock(hit.startSeconds)} onClick={() => openAt(hit)}>Watch from {clock(hit.startSeconds)}</Button></div>
              </ListRow>
            ))}
          </RowList>
        </section>
      ) : null}

      {videos === null && !error ? <p className="text-sm text-muted-foreground">Loading the videos…</p> : null}
      {videos !== null && videos.length === 0 ? <EmptyState>{canManage ? 'No videos yet. Use Add video to upload one or link one.' : 'There are no videos for your courses yet.'}</EmptyState> : null}
      {videos !== null && videos.length > 0 && shown.length === 0 ? <EmptyState>No videos match.</EmptyState> : null}
      {shown.length > 0 ? (
        <RowList label="Videos">
          {shown.map((video) => {
            const progress = progressLabel(video)
            return (
              <ListRow key={video.id} selected={video.id === selected?.id} columns="sm:grid-cols-[minmax(0,1fr)_auto] md:grid-cols-[minmax(0,2fr)_150px_80px_90px_130px_auto]">
                <div className="flex min-w-0 items-center gap-3">
                  {canManage ? <input type="checkbox" aria-label={`Select ${video.title}`} checked={picked.includes(video.id)} onChange={(event) => setPicked((current) => (event.target.checked ? [...current, video.id] : current.filter((id) => id !== video.id)))} /> : null}
                  <span aria-hidden className="flex h-10 w-14 shrink-0 items-center justify-center overflow-hidden rounded bg-muted text-muted-foreground">{video.posterUrl ? <img src={video.posterUrl} alt="" className="h-full w-full object-cover" /> : <Film className="h-5 w-5" />}</span>
                  <div className="min-w-0"><strong className="block truncate">{video.title}</strong><small className="text-muted-foreground">{video.courseTitle}{(video.tags ?? []).length > 0 ? ` · ${(video.tags ?? []).join(', ')}` : ''}</small></div>
                </div>
                <div className="flex flex-wrap gap-1.5"><Badge variant="outline">{typeLabel(video.type)}</Badge>{video.status !== 'Ready' ? <Badge variant={video.status === 'Failed' ? 'destructive' : 'secondary'}>{video.status === 'Processing' ? 'Converting' : video.status}</Badge> : null}</div>
                <div className="hidden text-muted-foreground md:block"><small className="block">Length</small>{formatDuration(video.durationSeconds)}</div>
                <div className="hidden text-muted-foreground md:block"><small className="block">Size</small>{formatBytes(video.sizeBytes)}</div>
                <div className="hidden md:block">{progress ? <Badge variant={video.myProgress?.completed ? 'default' : 'secondary'}>{progress}</Badge> : null}</div>
                <div className="flex justify-end"><Button type="button" size="sm" variant="secondary" aria-label={`View details for ${video.title}`} aria-expanded={video.id === selected?.id} onClick={() => { setSelected(video); setStartAt(null); setAdding(false); setNotice(null) }}>{canManage ? 'View details' : 'Watch'}</Button></div>
              </ListRow>
            )
          })}
        </RowList>
      ) : null}

      <SidePanel open={adding} label="Add a video" onClose={closeAll}>
        <AddVideoForm courses={courses} onDone={(video) => { setAdding(false); setNotice(`“${video.title}” was added.`); void load() }} onCancel={() => setAdding(false)} />
      </SidePanel>

      <SidePanel open={selected !== null} label={selected?.title ?? 'Video'} onClose={closeAll}>
        {selected ? <VideoDetails key={selected.id} video={selected} canManage={canManage} startAt={startAt} onChanged={replace} onDeleted={() => { closeAll(); setNotice('The video was deleted.'); void load() }} /> : null}
      </SidePanel>
    </section>
  )
}

// ---------- adding ----------
function AddVideoForm({ courses, onDone, onCancel }: { courses: Course[]; onDone: (video: VideoItem) => void; onCancel: () => void }) {
  const [mode, setMode] = useState<'upload' | 'link'>('upload')
  const [title, setTitle] = useState('')
  const [courseId, setCourseId] = useState('')
  const [description, setDescription] = useState('')
  const [url, setUrl] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [progress, setProgress] = useState<number | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const message = validateAddVideo(mode, { title, courseId, url, file })
    setProblem(message)
    if (message) return
    setBusy(true)
    try {
      if (mode === 'link') {
        onDone(await apiRequest<VideoItem>('/api/v1/tenant/videos/external', { method: 'POST', body: JSON.stringify({ courseId, title: title.trim(), description: description.trim() || null, url: url.trim() }) }))
      } else {
        const seconds = await readVideoDuration(file!)
        setProgress(0)
        onDone(await uploadVideoInPieces<VideoItem>(file!, { courseId, title: title.trim(), description: description.trim(), durationSeconds: seconds }, setProgress))
      }
    } catch (exception) { setProblem(readError(exception, 'Unable to add the video.')) }
    finally { setBusy(false); setProgress(null) }
  }

  return (
    <FormLayout onSubmit={submit}>
      <ErrorBanner message={problem} />
      <FormSection title="Video">
        <div role="radiogroup" aria-label="How to add the video" className="flex gap-2">
          <Button type="button" role="radio" aria-checked={mode === 'upload'} variant={mode === 'upload' ? 'secondary' : 'outline'} onClick={() => setMode('upload')}>Upload a file</Button>
          <Button type="button" role="radio" aria-checked={mode === 'link'} variant={mode === 'link' ? 'secondary' : 'outline'} onClick={() => setMode('link')}>Link to a video</Button>
        </div>
        {mode === 'upload' ? (
          <Field id="video-file" label="Video file" required hint="MP4, WebM or OGG. The video is sent in pieces: if the connection drops or you close the page, choose the same file again to carry on where it stopped.">
            <Input id="video-file" type="file" accept="video/mp4,video/webm,video/ogg" onChange={(event) => setFile(event.target.files?.[0] ?? null)} />
          </Field>
        ) : (
          <Field id="video-url" label="Video link" required hint="A link to a video hosted elsewhere, such as YouTube or Vimeo. Videos from trusted sites play inside the page.">
            <Input id="video-url" type="url" placeholder="https://" value={url} onChange={(event) => setUrl(event.target.value)} />
          </Field>
        )}
      </FormSection>
      <FormSection title="About the video">
        <Field id="video-title" label="Title" required><Input id="video-title" maxLength={250} value={title} onChange={(event) => setTitle(event.target.value)} /></Field>
        <Field id="video-course" label="Course" required hint="Learners enrolled in this course can watch the video once the course is published.">
          <Select id="video-course" value={courseId} onChange={(event) => setCourseId(event.target.value)}>
            <option value="">Choose a course</option>
            {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
          </Select>
        </Field>
        <Field id="video-description" label="Description"><Textarea id="video-description" rows={3} maxLength={2000} value={description} onChange={(event) => setDescription(event.target.value)} /></Field>
      </FormSection>
      {progress !== null ? (
        <div role="progressbar" aria-label="Upload progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(progress * 100)} className="flex flex-col gap-1">
          <div className="h-2 overflow-hidden rounded-full bg-muted"><div className="h-2 rounded-full bg-primary transition-all" style={{ width: `${Math.round(progress * 100)}%` }} /></div>
          <small className="text-muted-foreground">Uploading… {Math.round(progress * 100)}%</small>
        </div>
      ) : null}
      <FormActions busy={busy} submitLabel={mode === 'upload' ? 'Upload video' : 'Add video'} busyLabel={mode === 'upload' ? 'Uploading…' : 'Adding…'} onCancel={onCancel} />
    </FormLayout>
  )
}

// ---------- one video ----------
const NO_TRANSCRIPT: Transcript = { status: 'None', source: null, language: null, provider: null, statusMessage: null, segments: null }

function VideoDetails({ video, canManage, startAt, onChanged, onDeleted }: { video: VideoItem; canManage: boolean; startAt: { seconds: number; nonce: number } | null; onChanged: (video: VideoItem) => void; onDeleted: () => void }) {
  const progress = progressLabel(video)
  const ready = video.status === 'Ready'
  const [seek, setSeek] = useState<{ seconds: number; nonce: number } | null>(startAt)
  const [time, setTime] = useState(0)
  const [transcript, setTranscript] = useState<Transcript | null>(null)
  useEffect(() => { if (startAt) setSeek(startAt) }, [startAt])
  useEffect(() => {
    if (!ready) return
    apiRequest<Transcript>('/api/v1/tenant/videos/' + video.id + '/transcript')
      .then((value) => setTranscript(value && typeof value === 'object' && 'status' in value ? value : NO_TRANSCRIPT))
      .catch(() => setTranscript(NO_TRANSCRIPT))
  }, [video.id, ready])
  const jump = (seconds: number) => setSeek({ seconds, nonce: Date.now() + Math.random() })
  const player = ready
    ? <VideoPlayer video={video} seek={seek} onTime={setTime} onSeekDone={() => setSeek(null)} onProgress={(next) => onChanged({ ...video, myProgress: next })} />
    : <VideoNotReady video={video} canManage={canManage} onChanged={onChanged} />
  const reading = ready ? (
    <>
      {video.type !== 'External' ? <ChapterList videoId={video.id} time={time} onSeek={jump} /> : null}
      {video.type !== 'External' ? <VideoNotes videoId={video.id} time={time} onSeek={jump} /> : null}
      {transcript?.status === 'Ready' ? <TranscriptView transcript={transcript} time={time} onSeek={jump} /> : null}
      {canManage ? null : <StudyAids video={video} onSeek={jump} />}
    </>
  ) : null
  const head = (
    <div className="flex flex-col gap-1">
      <div className="flex flex-wrap items-center gap-2"><h3 className="text-xl font-semibold">{video.title}</h3><Badge variant="outline">{typeLabel(video.type)}</Badge>{progress ? <Badge variant="secondary">{progress}</Badge> : null}</div>
      <small className="text-muted-foreground">{video.courseTitle} · {formatDuration(video.durationSeconds)}{video.sizeBytes ? ` · ${formatBytes(video.sizeBytes)}` : ''} · added by {video.createdBy} on {new Date(video.createdAtUtc).toLocaleDateString()}</small>
      {video.description ? <p className="mt-1 whitespace-pre-wrap text-sm">{video.description}</p> : null}
    </div>
  )
  if (!canManage) return <div className="flex flex-col gap-4">{head}{player}{reading}</div>
  return (
    <div className="flex flex-col gap-4">
      {head}
      <Tabs defaultValue="watch">
        <TabsList>
          <TabsTrigger value="watch">Watch</TabsTrigger>
          <TabsTrigger value="chapters">Chapters</TabsTrigger>
          <TabsTrigger value="transcript">Transcript</TabsTrigger>
          <TabsTrigger value="aids">Study aids</TabsTrigger>
          <TabsTrigger value="details">Details</TabsTrigger>
          <TabsTrigger value="lesson">Lesson</TabsTrigger>
          <TabsTrigger value="analytics">Analytics</TabsTrigger>
        </TabsList>
        <TabsContent value="watch"><div className="flex flex-col gap-4">{player}{reading}</div></TabsContent>
        <TabsContent value="chapters"><ChaptersEditor video={video} /></TabsContent>
        <TabsContent value="transcript"><TranscriptManager video={video} transcript={transcript ?? NO_TRANSCRIPT} onChange={setTranscript} /></TabsContent>
        <TabsContent value="aids"><InsightsManager video={video} hasTranscript={transcript?.status === 'Ready'} /></TabsContent>
        <TabsContent value="details"><EditVideo video={video} onChanged={onChanged} onDeleted={onDeleted} /></TabsContent>
        <TabsContent value="lesson"><AttachToLesson video={video} onChanged={onChanged} /></TabsContent>
        <TabsContent value="analytics"><VideoAnalytics video={video} /></TabsContent>
      </Tabs>
    </div>
  )
}

/** A video that is still being converted, or could not be. Staff can try again once the cause is fixed. */
function VideoNotReady({ video, canManage, onChanged }: { video: VideoItem; canManage: boolean; onChanged: (video: VideoItem) => void }) {
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  async function retry() {
    setBusy(true); setProblem(null)
    try { onChanged(await apiRequest<VideoItem>(`/api/v1/tenant/videos/${video.id}/reprocess`, { method: 'POST' })) }
    catch (exception) { setProblem(readError(exception, 'Unable to start the conversion.')) }
    finally { setBusy(false) }
  }
  if (video.status === 'Failed') {
    return (
      <div className="flex flex-col items-start gap-2 rounded-md border border-destructive/40 bg-destructive/5 p-4 text-sm">
        <ErrorBanner message={problem} />
        <p className="font-medium text-destructive">This video could not be prepared for playback.</p>
        {video.statusMessage ? <p className="text-muted-foreground">{video.statusMessage}</p> : null}
        {canManage ? <Button type="button" variant="outline" disabled={busy} onClick={() => void retry()}>Try again</Button> : null}
      </div>
    )
  }
  return <p role="status" className="rounded-md border border-border p-4 text-sm text-muted-foreground">This video is being prepared for playback. It will be ready in a moment; this page updates by itself.</p>
}

function EditVideo({ video, onChanged, onDeleted }: { video: VideoItem; onChanged: (video: VideoItem) => void; onDeleted: () => void }) {
  const [title, setTitle] = useState(video.title)
  const [description, setDescription] = useState(video.description ?? '')
  const [tags, setTags] = useState((video.tags ?? []).join(', '))
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setNotice(null)
    if (!title.trim()) return setProblem('Give the video a title.')
    setProblem(null); setBusy(true)
    try { onChanged(await apiRequest<VideoItem>(`/api/v1/tenant/videos/${video.id}`, { method: 'PUT', body: JSON.stringify({ title: title.trim(), description: description.trim() || null, tags: tags.split(',').map((tag) => tag.trim()).filter(Boolean) }) })); setNotice('Saved.') }
    catch (exception) { setProblem(readError(exception, 'Unable to save the video.')) }
    finally { setBusy(false) }
  }

  async function convert() {
    setBusy(true); setProblem(null); setNotice(null)
    try { onChanged(await apiRequest<VideoItem>(`/api/v1/tenant/videos/${video.id}/reprocess`, { method: 'POST' })); setNotice('Conversion started. Check back in a moment.') }
    catch (exception) { setProblem(readError(exception, 'Unable to start the conversion.')) }
    finally { setBusy(false) }
  }

  async function remove() {
    if (!window.confirm(`Delete "${video.title}"? Learners lose access to it. Lessons that already show it keep working.`)) return
    setBusy(true); setProblem(null)
    try { await apiRequest(`/api/v1/tenant/videos/${video.id}`, { method: 'DELETE' }); onDeleted() }
    catch (exception) { setProblem(readError(exception, 'Unable to delete the video.')); setBusy(false) }
  }

  return (
    <FormLayout onSubmit={save}>
      <ErrorBanner message={problem} />
      <NoticeBanner message={notice} />
      <FormSection title="About the video">
        <Field id="edit-video-title" label="Title" required><Input id="edit-video-title" maxLength={250} value={title} onChange={(event) => setTitle(event.target.value)} /></Field>
        <Field id="edit-video-description" label="Description"><Textarea id="edit-video-description" rows={3} maxLength={2000} value={description} onChange={(event) => setDescription(event.target.value)} /></Field>
        <Field id="edit-video-tags" label="Tags" hint="Words that group videos, separated by commas, such as week 1, calculus. Up to 10."><Input id="edit-video-tags" value={tags} onChange={(event) => setTags(event.target.value)} /></Field>
      </FormSection>
      {video.type !== 'External' ? (
        <FormSection title="Streaming" description={video.hasStreaming ? 'Prepared for streaming: it starts quickly and seeking is smooth, and a poster image is shown.' : 'Not converted. It plays as the original file. Converting makes it stream in small pieces and adds a poster image.'}>
          {video.hasStreaming || video.status === 'Processing' ? null : <div><Button type="button" variant="outline" disabled={busy} onClick={() => void convert()}>Convert for streaming</Button></div>}
        </FormSection>
      ) : null}
      <FormActions busy={busy} submitLabel="Save changes"><Button type="button" variant="softDestructive" disabled={busy} onClick={() => void remove()}>Delete video</Button></FormActions>
    </FormLayout>
  )
}

function AttachToLesson({ video, onChanged }: { video: VideoItem; onChanged: (video: VideoItem) => void }) {
  const [outline, setOutline] = useState<CourseOutline | null>(null)
  const [lessonId, setLessonId] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    // A draft course is edited directly; a published one only through its new version, so show that copy when there is one.
    apiRequest<CourseOutline>(`/api/v1/tenant/courses/${video.courseId}`)
      .then(async (live) => setOutline(live.course.status === 'Published' && live.draftVersion ? await apiRequest<CourseOutline>(`/api/v1/tenant/courses/${video.courseId}?version=draft`) : live))
      .catch((exception) => setProblem(readError(exception, 'Unable to load the course outline.')))
  }, [video.courseId])

  const editable = outline !== null && (outline.course.status === 'Draft' || outline.viewingDraft === true)
  const lessons = (outline?.modules ?? []).flatMap((module) => module.lessons.map((lesson) => ({ id: lesson.id, label: `${module.title} › ${lesson.title}` })))

  async function attach(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setNotice(null)
    if (!lessonId) return setProblem('Choose the lesson.')
    setProblem(null); setBusy(true)
    try {
      await apiRequest(`/api/v1/tenant/videos/${video.id}/attach`, { method: 'POST', body: JSON.stringify({ lessonId }) })
      onChanged({ ...video, lessonId })
      setNotice('Added to the lesson. Learners will see it there once the course is published.')
    } catch (exception) { setProblem(readError(exception, 'Unable to add the video to the lesson.')) }
    finally { setBusy(false) }
  }

  if (!outline) return problem ? <ErrorBanner message={problem} /> : <p className="text-sm text-muted-foreground">Loading the lessons…</p>
  if (!editable) return <EmptyState>This course is published, so its lessons can only change in a new version. Start a new version in Course authoring, then come back to add the video.</EmptyState>
  if (lessons.length === 0) return <EmptyState>This course has no lessons yet. Add one on the Outline tab of Course authoring first.</EmptyState>
  return (
    <FormLayout onSubmit={attach}>
      <ErrorBanner message={problem} />
      <NoticeBanner message={notice} />
      <FormSection title="Show this video in a lesson" description="It is added to the end of the lesson as a video block. The video stays in the library too.">
        <Field id="attach-lesson" label="Lesson" required>
          <Select id="attach-lesson" value={lessonId} onChange={(event) => setLessonId(event.target.value)}>
            <option value="">Choose a lesson</option>
            {lessons.map((lesson) => <option key={lesson.id} value={lesson.id}>{lesson.label}</option>)}
          </Select>
        </Field>
      </FormSection>
      <FormActions busy={busy} submitLabel="Add to lesson" busyLabel="Adding…" />
    </FormLayout>
  )
}

function VideoAnalytics({ video }: { video: VideoItem }) {
  const [data, setData] = useState<Analytics | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    apiRequest<Analytics>(`/api/v1/tenant/videos/${video.id}/analytics`).then(setData).catch((exception) => setError(readError(exception, 'Unable to load the analytics.')))
  }, [video.id])
  if (error) return <ErrorBanner message={error} />
  if (!data) return <p className="text-sm text-muted-foreground">Loading…</p>
  const tiles: [string, string][] = [
    ['Enrolled learners', String(data.eligibleLearners)], ['Started watching', String(data.viewers)], ['Finished', String(data.completed)],
    ['Plays', String(data.plays)], ['Average watched', data.averageWatchedPercent === null ? '—' : `${data.averageWatchedPercent}%`], ['Time watched', formatDuration(data.watchedSeconds)],
  ]
  return (
    <div className="flex flex-col gap-4">
      <div className="grid gap-3 sm:grid-cols-3">{tiles.map(([label, value]) => <div key={label} className="rounded-md border border-border px-4 py-3"><p className="text-xs text-muted-foreground">{label}</p><p className="mt-0.5 text-lg font-semibold">{value}</p></div>)}</div>
      <Card>
        <CardContent className="flex flex-col gap-3 pt-5">
          <h4 className="text-sm font-semibold">Where people got to</h4>
          {data.funnel.length === 0 ? <p className="text-sm text-muted-foreground">Needs the length of the video. It is filled in the first time someone plays it.</p> : data.funnel.map((step) => {
            const share = data.viewers > 0 ? Math.round((step.viewers * 100) / data.viewers) : 0
            return (
              <div key={step.percent} className="flex items-center gap-3 text-sm">
                <span className="w-24 shrink-0 text-muted-foreground">{step.percent === 100 ? 'Finished' : `Reached ${step.percent}%`}</span>
                <div className="h-2 flex-1 overflow-hidden rounded-full bg-muted" aria-hidden><div className="h-2 rounded-full bg-primary" style={{ width: `${share}%` }} /></div>
                <span className="w-24 shrink-0 text-right">{step.viewers} of {data.viewers}</span>
              </div>
            )
          })}
        </CardContent>
      </Card>
    </div>
  )
}
