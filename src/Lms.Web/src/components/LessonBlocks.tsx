import { useEffect, useState } from 'react'
import { Download } from 'lucide-react'
import { Button } from '@/components/ui/button'
import VideoPlayer from '@/components/VideoPlayer'
import { ChapterList, VideoNotes } from '@/features/VideoStudyPanels'
import { ApiError, apiRequest, downloadFile, fetchBlobUrl } from '@/lib/api'
import type { VideoItem } from '@/lib/video'

export type BlockFile = { fileName: string; contentType: string; sizeBytes: number; downloadPath: string }
export type Block = {
  id: string; type: 'Text' | 'Code' | 'Link' | 'Embed' | 'Image' | 'Pdf' | 'Video' | 'Audio' | 'Download'; displayOrder: number
  title: string | null; text: string | null; language: string | null; url: string | null; caption: string | null; file: BlockFile | null
  /** Set when the file is a video in the library, which then plays in the library's player. */
  videoId?: string | null
}

/** Only these types are ever shown inline; anything else is offered as a download, never rendered. */
const inlineImage = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']
const inlineVideo = ['video/mp4', 'video/webm', 'video/ogg']
const inlineAudio = ['audio/mpeg', 'audio/ogg', 'audio/wav', 'audio/x-wav', 'audio/mp4', 'audio/webm']

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/** Splits text into paragraphs on blank lines. The text is rendered as text, never as HTML. */
export const toParagraphs = (text: string) => text.split(/\n{2,}/).map((part) => part.trim()).filter(Boolean)

export const hostOf = (url: string) => { try { return new URL(url).hostname } catch { return url } }

/** Renders one block. Files are fetched with the user's credentials because plain tags cannot send them. */
export function BlockView({ block }: { block: Block }) {
  return (
    <figure className="flex flex-col gap-2">
      {block.title && block.type !== 'Link' ? <figcaption className="text-sm font-medium">{block.title}</figcaption> : null}
      <BlockBody block={block} />
      {block.caption ? <small className="text-muted-foreground">{block.caption}</small> : null}
    </figure>
  )
}

function BlockBody({ block }: { block: Block }) {
  switch (block.type) {
    case 'Text':
      return <div className="flex flex-col gap-2 text-sm leading-relaxed">{toParagraphs(block.text ?? '').map((paragraph, index) => <p key={index} className="whitespace-pre-wrap">{paragraph}</p>)}</div>
    case 'Code':
      return (
        <div className="overflow-hidden rounded-md border border-border">
          {block.language ? <div className="border-b border-border bg-muted px-3 py-1 text-xs text-muted-foreground">{block.language}</div> : null}
          <pre className="overflow-x-auto bg-background p-3 text-sm"><code>{block.text}</code></pre>
        </div>
      )
    case 'Link':
      return block.url ? (
        <a href={block.url} target="_blank" rel="noopener noreferrer" className="inline-flex flex-col rounded-md border border-border px-3 py-2 text-sm hover:bg-muted">
          <strong className="text-primary">{block.title || hostOf(block.url)}</strong>
          <small className="text-muted-foreground">{hostOf(block.url)}</small>
        </a>
      ) : null
    case 'Embed':
      return block.url ? (
        <div className="aspect-video w-full overflow-hidden rounded-md border border-border">
          <iframe src={block.url} title={block.title || 'Embedded content'} className="h-full w-full" loading="lazy" allow="fullscreen; picture-in-picture"
            referrerPolicy="strict-origin-when-cross-origin" sandbox="allow-scripts allow-same-origin allow-presentation allow-popups" />
        </div>
      ) : null
    default:
      if (block.type === 'Video' && block.videoId) return <LibraryVideo block={block} videoId={block.videoId} />
      return block.file ? <FileBlock block={block} file={block.file} /> : null
  }
}

/**
 * A lesson's video that is also in the video library plays in the library's player: streaming, quality, speed, captions, resume,
 * and the chapters and notes. Watching it is recorded, so a lesson can complete when its videos have been watched.
 * If the library video cannot be read, the plain file is shown instead.
 */
function LibraryVideo({ block, videoId }: { block: Block; videoId: string }) {
  const [video, setVideo] = useState<VideoItem | null>(null)
  const [failed, setFailed] = useState(false)
  const [time, setTime] = useState(0)
  const [seek, setSeek] = useState<{ seconds: number; nonce: number } | null>(null)
  useEffect(() => {
    let active = true
    setVideo(null); setFailed(false)
    apiRequest<VideoItem>(`/api/v1/tenant/videos/${videoId}`).then((item) => { if (active) setVideo(item) }).catch(() => { if (active) setFailed(true) })
    return () => { active = false }
  }, [videoId])
  if (failed) return block.file ? <FileBlock block={block} file={block.file} /> : null
  if (!video) return <p className="text-sm text-muted-foreground">Loading the video…</p>
  const jump = (seconds: number) => setSeek({ seconds, nonce: Date.now() + Math.random() })
  return (
    <div className="flex flex-col gap-3">
      <VideoPlayer video={video} seek={seek} onTime={setTime} onSeekDone={() => setSeek(null)} onProgress={(next) => setVideo((current) => (current ? { ...current, myProgress: next } : current))} />
      <ChapterList videoId={video.id} time={time} onSeek={jump} />
      <VideoNotes videoId={video.id} time={time} onSeek={jump} />
    </div>
  )
}

function FileBlock({ block, file }: { block: Block; file: BlockFile }) {
  // direct: the browser streams from object storage with a short-lived signed link; otherwise the file is fetched through the API.
  const [source, setSource] = useState<{ url: string; direct: boolean } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const contentType = file.contentType.split(';')[0].toLowerCase()
  const inline = (block.type === 'Image' && inlineImage.includes(contentType)) || (block.type === 'Video' && inlineVideo.includes(contentType))
    || (block.type === 'Audio' && inlineAudio.includes(contentType)) || (block.type === 'Pdf' && contentType === 'application/pdf')

  useEffect(() => {
    if (!inline) return undefined
    let active = true
    let created: string | null = null
    async function load() {
      // Ask for a signed link first; storage without direct links (or any failure) falls back to an authenticated fetch.
      const link = await apiRequest<{ url: string | null }>(`${file.downloadPath}/link`).catch(() => null)
      if (!active) return
      if (link?.url) { setSource({ url: link.url, direct: true }); return }
      const result = await fetchBlobUrl(file.downloadPath)
      if (!active) { URL.revokeObjectURL(result.url); return }
      created = result.url
      setSource({ url: result.url, direct: false })
    }
    load().catch((exception) => { if (active) setError(exception instanceof ApiError ? exception.message : 'The file could not be loaded.') })
    return () => { active = false; if (created) URL.revokeObjectURL(created) }
  }, [file.downloadPath, inline, attempt])

  // A signed link can expire while the page is open (a paused video, a long reading session): ask for a fresh one, twice at most.
  const refreshLink = () => { if (source?.direct && attempt < 2) setAttempt((current) => current + 1) }

  const saveButton = (
    <Button variant="outline" size="sm" onClick={() => void downloadFile(file.downloadPath, file.fileName)}>
      <Download className="h-4 w-4" />Download {file.fileName} ({formatBytes(file.sizeBytes)})
    </Button>
  )
  if (!inline) return <div>{saveButton}</div>
  if (error) return <p role="alert" className="text-sm text-destructive">{error}</p>
  if (!source) return <p className="text-sm text-muted-foreground">Loading {file.fileName}â€¦</p>

  return (
    <div className="flex flex-col gap-2">
      {block.type === 'Image' ? <img src={source.url} alt={block.caption || block.title || file.fileName} onError={refreshLink} className="max-h-[32rem] max-w-full rounded-md border border-border object-contain" /> : null}
      {block.type === 'Video' ? <video src={source.url} controls onError={refreshLink} className="max-h-[32rem] w-full rounded-md border border-border" /> : null}
      {block.type === 'Audio' ? <audio src={source.url} controls onError={refreshLink} className="w-full" /> : null}
      {block.type === 'Pdf' ? <iframe src={source.url} title={block.title || file.fileName} className="h-[32rem] w-full rounded-md border border-border" /> : null}
      {block.type === 'Pdf' ? <div>{saveButton}</div> : null}
    </div>
  )
}

/** Loads and shows all blocks of a lesson. */
export default function LessonBlocks({ courseId, lessonId, emptyText }: { courseId: string; lessonId: string; emptyText?: string }) {
  const [blocks, setBlocks] = useState<Block[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    setBlocks(null); setError(null)
    apiRequest<Block[]>(`/api/v1/tenant/courses/${courseId}/lessons/${lessonId}/blocks`)
      .then((items) => { if (active) setBlocks(items) })
      .catch((exception) => { if (active) setError(exception instanceof ApiError ? exception.message : 'The lesson content could not be loaded.') })
    return () => { active = false }
  }, [courseId, lessonId])

  if (error) return <p role="alert" className="text-sm text-destructive">{error}</p>
  if (blocks === null) return <p className="text-sm text-muted-foreground">Loading lesson content…</p>
  if (blocks.length === 0) return emptyText ? <p className="text-sm text-muted-foreground">{emptyText}</p> : null
  return <div className="flex flex-col gap-5">{blocks.map((block) => <BlockView key={block.id} block={block} />)}</div>
}
