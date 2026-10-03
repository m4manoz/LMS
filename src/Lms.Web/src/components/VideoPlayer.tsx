import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { ApiError, apiRequest } from '@/lib/api'
import { formatDuration, qualityLabel, resumePoint, type PlaybackLink, type VideoItem } from '@/lib/video'

/** How often playback is reported while a video plays (seconds). Pauses, endings and leaving the page report straight away. */
export const REPORT_EVERY_SECONDS = 15

type Props = {
  video: VideoItem
  onProgress?: (progress: { lastPositionSeconds: number; percent: number | null; completed: boolean }) => void
  /** Jump to a point (a transcript line, a search hit). A new nonce asks again even for the same second. */
  seek?: { seconds: number; nonce: number } | null
  onTime?: (seconds: number) => void
  /** Called once the jump has been made, so the page can forget the request. */
  onSeekDone?: () => void
}

/**
 * Plays a library video. Uploaded videos get a short-lived link that is renewed if it runs out while the page is open;
 * videos linked from elsewhere are embedded on trusted hosts and opened in their own tab otherwise.
 * Where the person stopped is remembered, so playback resumes there.
 */
export default function VideoPlayer({ video, onProgress, seek, onTime, onSeekDone }: Props) {
  const [link, setLink] = useState<PlaybackLink | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [resumed, setResumed] = useState<number | null>(null)
  const [levels, setLevels] = useState<(number | undefined)[]>([])
  const [quality, setQuality] = useState(-1)                          // -1 lets the player choose by connection speed
  const streaming = useRef<{ destroy: () => void; currentLevel: number } | null>(null)
  const element = useRef<HTMLVideoElement>(null)
  const refreshes = useRef(0)
  const pending = useRef(0)                 // seconds played since the last report
  const lastTime = useRef<number | null>(null)
  const sinceReport = useRef(0)
  const started = useRef(false)
  const duration = useRef<number | null>(video.durationSeconds)
  // The parent may hand over a new callback on every render; reporting must not restart because of that.
  const notify = useRef(onProgress)
  useEffect(() => { notify.current = onProgress })
  const seekDone = useRef(onSeekDone)
  useEffect(() => { seekDone.current = onSeekDone })

  const load = useCallback(async () => {
    setError(null)
    try { setLink(await apiRequest<PlaybackLink>(`/api/v1/tenant/videos/${video.id}/link`)) }
    catch (exception) { setError(exception instanceof ApiError ? exception.message : 'The video could not be loaded.') }
  }, [video.id])
  useEffect(() => { refreshes.current = 0; void load() }, [load])

  const report = useCallback(async (finished = false) => {
    const player = element.current
    if (!player || link?.kind === 'external') return
    const body = { positionSeconds: Math.floor(player.currentTime || 0), durationSeconds: Number.isFinite(player.duration) ? Math.round(player.duration) : undefined, watchedSecondsDelta: Math.round(pending.current), started: started.current ? undefined : true, completed: finished || undefined }
    if (!started.current && pending.current === 0 && body.positionSeconds === 0 && !finished) return
    started.current = true
    pending.current = 0
    sinceReport.current = 0
    try {
      // Send first, then tell the parent: with an optional call the request would be skipped whenever nobody is listening.
      const result = await apiRequest<{ lastPositionSeconds: number; percent: number | null; completed: boolean }>(`/api/v1/tenant/videos/${video.id}/progress`, { method: 'POST', body: JSON.stringify(body) })
      notify.current?.(result)
    }
    catch { /* a lost report only means a little less detail; playback must never be interrupted by it */ }
  }, [link, video.id])

  // Streaming (HLS) videos are fed to the player piece by piece. Safari plays them itself; elsewhere hls.js does it.
  // If streaming cannot start, the original file is played instead.
  const streamUrl = link?.kind === 'hls' ? link.url : null
  const fallbackUrl = link?.kind === 'hls' ? link.fallbackUrl ?? null : null
  useEffect(() => {
    const player = element.current
    if (!player || !streamUrl) return
    let hls: { destroy: () => void; currentLevel: number } | undefined
    let cancelled = false
    const fallBack = () => { hls?.destroy(); hls = undefined; streaming.current = null; setLevels([]); if (fallbackUrl && player.getAttribute('src') !== fallbackUrl) player.src = fallbackUrl }
    // hls.js is preferred wherever the browser can run it: it offers a quality choice. Browsers that cannot (iPhone Safari) play HLS themselves.
    void import('hls.js').then(({ default: Hls }) => {
      if (cancelled) return
      if (!Hls.isSupported()) {
        if (player.canPlayType('application/vnd.apple.mpegurl')) player.src = streamUrl
        else fallBack()
        return
      }
      const instance = new Hls()
      hls = instance
      streaming.current = instance
      instance.on(Hls.Events.MANIFEST_PARSED, (_event, data) => { setLevels(data.levels.map((level) => level.height)); setQuality(-1) })
      instance.on(Hls.Events.ERROR, (_event, data) => { if (data.fatal) { console.warn('Streaming stopped; playing the original file instead.', data.type, data.details); fallBack() } })
      instance.loadSource(streamUrl)
      instance.attachMedia(player)
    }).catch(fallBack)
    return () => { cancelled = true; hls?.destroy(); streaming.current = null }
  }, [streamUrl, fallbackUrl])

  function chooseQuality(value: number) {
    setQuality(value)
    if (streaming.current) streaming.current.currentLevel = value
  }

  // A request to jump (a transcript line, a search hit) is applied once: straight away when the video is ready,
  // otherwise as soon as its length is known. A renewed link must not jump again.
  const pendingSeek = useRef<number | null>(null)
  const handledSeek = useRef<number | null>(null)
  const applySeek = useCallback((player: HTMLVideoElement) => {
    if (pendingSeek.current === null) return
    player.currentTime = pendingSeek.current
    pendingSeek.current = null
    void player.play().catch(() => undefined)
    seekDone.current?.()
  }, [])
  useEffect(() => {
    if (!seek || seek.nonce === handledSeek.current) return
    handledSeek.current = seek.nonce
    pendingSeek.current = seek.seconds
    const player = element.current
    if (player && player.readyState >= 1) applySeek(player)
  }, [seek, link, applySeek])

  // Leaving the page (closing the panel, going elsewhere) sends the last position.
  useEffect(() => () => { void report() }, [report])

  function onLoadedMetadata() {
    const player = element.current
    if (!player) return
    if (Number.isFinite(player.duration)) duration.current = Math.round(player.duration)
    if (pendingSeek.current !== null) { applySeek(player); return }
    const point = resumePoint(video.myProgress?.lastPositionSeconds, duration.current)
    if (point !== null) { player.currentTime = point; setResumed(point) }
  }

  function onTimeUpdate() {
    const player = element.current
    if (!player) return
    const now = player.currentTime
    // Count only ordinary forward playback, so seeking around does not count as watching.
    if (lastTime.current !== null && now > lastTime.current && now - lastTime.current < 2) { pending.current += now - lastTime.current; sinceReport.current += now - lastTime.current }
    lastTime.current = now
    onTime?.(now)
    if (sinceReport.current >= REPORT_EVERY_SECONDS) void report()
  }

  async function onError() {
    // The link may simply have expired during a long pause: ask for a fresh one, a couple of times at most.
    if (refreshes.current >= 2) { setError('The video could not be played. Try again in a moment.'); return }
    refreshes.current += 1
    await load()
  }

  if (error) return <div role="alert" className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">{error} <Button type="button" variant="outline" size="sm" className="ml-2" onClick={() => { refreshes.current = 0; void load() }}>Try again</Button></div>
  if (!link) return <p className="text-sm text-muted-foreground">Loading the video…</p>

  if (link.kind === 'external') {
    return link.embeddable ? (
      <div className="aspect-video w-full overflow-hidden rounded-md border border-border">
        <iframe title={video.title} src={link.url} className="h-full w-full" allow="fullscreen; picture-in-picture" allowFullScreen sandbox="allow-scripts allow-same-origin allow-presentation allow-popups" referrerPolicy="strict-origin-when-cross-origin" />
      </div>
    ) : (
      <div className="flex flex-col items-start gap-2 rounded-md border border-border p-4 text-sm">
        <p className="text-muted-foreground">This video is hosted on another site.</p>
        <Button asChild variant="secondary"><a href={link.url} target="_blank" rel="noreferrer noopener">Open the video</a></Button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-2">
      <video ref={element} src={link.kind === 'hls' ? undefined : link.url} poster={video.posterUrl ?? undefined} controls preload="metadata" className="max-h-[32rem] w-full rounded-md border border-border bg-black"
        onLoadedMetadata={onLoadedMetadata} onTimeUpdate={onTimeUpdate} onPause={() => void report()} onEnded={() => void report(true)} onError={() => void onError()}>
        {link.captionsUrl ? <track kind="captions" src={link.captionsUrl} srcLang={link.captionsLanguage ?? 'und'} label="Transcript" /> : null}
      </video>
      {levels.length > 1 ? (
        <label className="flex items-center gap-2 text-sm text-muted-foreground">Quality
          <select aria-label="Quality" className="rounded-md border border-input bg-background px-2 py-1 text-foreground" value={quality} onChange={(event) => chooseQuality(Number(event.target.value))}>
            <option value={-1}>Auto</option>
            {levels.map((height, index) => <option key={index} value={index}>{qualityLabel(height, index)}</option>)}
          </select>
        </label>
      ) : null}
      {resumed !== null ? <small className="text-muted-foreground">Resumed from {formatDuration(resumed)}.</small> : null}
    </div>
  )
}
