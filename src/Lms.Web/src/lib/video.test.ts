import { describe, expect, it } from 'vitest'
import { ApiError } from './api'
import { uploadWithProgress } from './upload'
import { formatBytes, formatDuration, progressLabel, resumePoint, typeLabel } from './video'

describe('formatDuration', () => {
  it('shows minutes and seconds, adding hours when needed', () => {
    expect(formatDuration(0)).toBe('0:00')
    expect(formatDuration(9)).toBe('0:09')
    expect(formatDuration(754)).toBe('12:34')
    expect(formatDuration(3723)).toBe('1:02:03')
  })
  it('shows a dash when the length is not known', () => {
    for (const value of [null, undefined, -5, Number.NaN]) expect(formatDuration(value)).toBe('—')
  })
})

describe('formatBytes', () => {
  it('picks a readable unit', () => {
    expect(formatBytes(0)).toBe('—')
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(1536)).toBe('1.5 KB')
    expect(formatBytes(5 * 1024 * 1024)).toBe('5 MB')
    expect(formatBytes(250 * 1024 * 1024)).toBe('250 MB')
    expect(formatBytes(3.2 * 1024 * 1024 * 1024)).toBe('3.2 GB')
  })
})

describe('progressLabel and typeLabel', () => {
  it('says how far the person got', () => {
    expect(progressLabel({ myProgress: null })).toBeNull()
    expect(progressLabel({ myProgress: { lastPositionSeconds: 0, percent: 0, completed: false } })).toBeNull()
    expect(progressLabel({ myProgress: { lastPositionSeconds: 90, percent: 40, completed: false } })).toBe('Watched 40%')
    expect(progressLabel({ myProgress: { lastPositionSeconds: 590, percent: 98, completed: true } })).toBe('Completed')
  })
  it('names each kind of video', () => {
    expect(typeLabel('Uploaded')).toBe('Uploaded')
    expect(typeLabel('LiveRecording')).toBe('Class recording')
    expect(typeLabel('External')).toBe('Linked')
  })
})

describe('resumePoint', () => {
  it('resumes only where there is something worth resuming', () => {
    expect(resumePoint(undefined, 600)).toBeNull()
    expect(resumePoint(3, 600)).toBeNull()          // barely started
    expect(resumePoint(125, 600)).toBe(125)
    expect(resumePoint(598, 600)).toBeNull()        // basically finished: start again
    expect(resumePoint(125, null)).toBe(125)        // length unknown: trust the position
  })
})

// ---------- upload with progress ----------
class FakeXhr {
  status = 0
  responseText = ''
  headers: Record<string, string> = {}
  upload: { onprogress: ((event: ProgressEvent) => void) | null } = { onprogress: null }
  onload: (() => void) | null = null
  onerror: (() => void) | null = null
  onabort: (() => void) | null = null
  opened: [string, string] | null = null
  sent: unknown = null
  open(method: string, url: string) { this.opened = [method, url] }
  setRequestHeader(name: string, value: string) { this.headers[name] = value }
  send(body: unknown) { this.sent = body }
  abort() { this.onabort?.() }
  respond(status: number, body: unknown) { this.status = status; this.responseText = JSON.stringify(body); this.onload?.() }
}

describe('uploadWithProgress', () => {
  const run = (fake: FakeXhr, progress: (value: number) => void = () => undefined) => uploadWithProgress<{ id: string }>('/api/v1/tenant/videos', new FormData(), progress, () => fake as never)

  it('posts the form, reports progress and returns the answer', async () => {
    const fake = new FakeXhr()
    const seen: number[] = []
    const { promise } = run(fake, (value) => seen.push(value))
    expect(fake.opened).toEqual(['POST', '/api/v1/tenant/videos'])
    fake.upload.onprogress?.({ lengthComputable: true, loaded: 25, total: 100 } as ProgressEvent)
    fake.upload.onprogress?.({ lengthComputable: true, loaded: 90, total: 100 } as ProgressEvent)
    fake.upload.onprogress?.({ lengthComputable: false, loaded: 5, total: 0 } as ProgressEvent)   // nothing to report when the size is not known
    fake.respond(201, { id: 'v1' })
    expect(await promise).toEqual({ id: 'v1' })
    expect(seen).toEqual([0.25, 0.9, 1])
  })

  it('turns an error answer into the server message', async () => {
    const fake = new FakeXhr()
    const { promise } = run(fake)
    fake.respond(400, { message: 'Choose an MP4, WebM or OGG video.' })
    await expect(promise).rejects.toMatchObject({ message: 'Choose an MP4, WebM or OGG video.', status: 400 })
    await expect(promise).rejects.toBeInstanceOf(ApiError)
  })

  it('has a plain message when the answer has none', async () => {
    const fake = new FakeXhr()
    const { promise } = run(fake)
    fake.status = 413; fake.responseText = '<html>too big</html>'; fake.onload?.()
    await expect(promise).rejects.toMatchObject({ message: 'Upload failed with status 413.' })
  })

  it('reports a lost connection and a cancel', async () => {
    const lost = new FakeXhr()
    const first = run(lost)
    lost.onerror?.()
    await expect(first.promise).rejects.toMatchObject({ message: expect.stringContaining('Check your connection') })

    const cancelled = new FakeXhr()
    const second = run(cancelled)
    second.cancel()
    await expect(second.promise).rejects.toMatchObject({ message: 'The upload was cancelled.' })
  })
})
