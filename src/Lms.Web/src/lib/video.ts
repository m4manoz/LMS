export type VideoProgress = { lastPositionSeconds: number; percent: number | null; completed: boolean }

export type VideoItem = {
  id: string
  courseId: string
  courseTitle: string
  lessonId: string | null
  title: string
  description: string | null
  type: 'Uploaded' | 'LiveRecording' | 'External'
  status: 'Uploading' | 'Processing' | 'Ready' | 'Failed'
  statusMessage: string | null
  contentType: string | null
  sizeBytes: number
  durationSeconds: number | null
  externalUrl: string | null
  createdBy: string
  createdAtUtc: string
  myProgress: VideoProgress | null
  hasStreaming?: boolean
  posterUrl?: string | null
}

export type PlaybackLink = { kind: 'direct' | 'stream' | 'hls' | 'external'; url: string; expiresAtUtc: string | null; embeddable: boolean; fallbackUrl?: string | null; captionsUrl?: string | null; captionsLanguage?: string | null }

/** "360p" for a picture 360 pixels tall; a level that does not say its height is called by its number. */
export function qualityLabel(height: number | undefined, index: number): string {
  return height && height > 0 ? `${height}p` : `Quality ${index + 1}`
}

/** 754 -> "12:34", 3723 -> "1:02:03", unknown -> "—". */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return '—'
  const whole = Math.round(seconds)
  const hours = Math.floor(whole / 3600)
  const minutes = Math.floor((whole % 3600) / 60)
  const rest = String(whole % 60).padStart(2, '0')
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`
}

/** 1536 -> "1.5 KB", 5 * 1024 * 1024 -> "5 MB". External videos have no size here, so zero shows a dash. */
export function formatBytes(bytes: number | null | undefined): string {
  if (!bytes || bytes < 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit += 1 }
  return `${unit === 0 || value >= 10 ? Math.round(value) : Math.round(value * 10) / 10} ${units[unit]}`
}

export const typeLabel = (type: VideoItem['type']) => (type === 'Uploaded' ? 'Uploaded' : type === 'LiveRecording' ? 'Class recording' : 'Linked')

/** What to show next to a video for the person watching: how far they have got. */
export function progressLabel(video: Pick<VideoItem, 'myProgress'>): string | null {
  const progress = video.myProgress
  if (!progress) return null
  if (progress.completed) return 'Completed'
  return progress.percent && progress.percent > 0 ? `Watched ${progress.percent}%` : null
}

/** "2:05" style label for where playback will resume, or null when there is nothing worth resuming. */
export function resumePoint(lastPositionSeconds: number | undefined, durationSeconds: number | null | undefined): number | null {
  if (!lastPositionSeconds || lastPositionSeconds < 5) return null
  if (durationSeconds && lastPositionSeconds > durationSeconds - 5) return null // essentially finished: start again from the beginning
  return lastPositionSeconds
}

/** The length of a video file, read from its own metadata in the browser. Resolves null if it cannot be read in a few seconds. */
export function readVideoDuration(file: Blob): Promise<number | null> {
  return new Promise((resolve) => {
    const element = document.createElement('video')
    const url = URL.createObjectURL(file)
    const finish = (value: number | null) => { URL.revokeObjectURL(url); element.removeAttribute('src'); resolve(value) }
    const timer = window.setTimeout(() => finish(null), 5000)
    element.preload = 'metadata'
    element.onloadedmetadata = () => { window.clearTimeout(timer); finish(Number.isFinite(element.duration) && element.duration > 0 ? Math.round(element.duration) : null) }
    element.onerror = () => { window.clearTimeout(timer); finish(null) }
    element.src = url
  })
}
