import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from './api'
import { uploadVideoInPieces } from './pieceUpload'

const request = vi.fn()
vi.mock('./api', async () => ({ ...(await vi.importActual<typeof import('./api')>('./api')), apiRequest: (...args: unknown[]) => request(...args) }))

const file = new File([new Uint8Array(25)], 'lesson.mp4', { type: 'video/mp4', lastModified: 1 })
const fields = { courseId: 'c1', title: 'Limits', description: '', durationSeconds: 90 }
const session = (over: Partial<{ id: string; chunkSize: number; totalChunks: number; received: number[] }> = {}) => ({ id: 'u1', chunkSize: 10, totalChunks: 3, received: [] as number[], ...over })
const noWait = () => Promise.resolve()
const calls = (suffix: string, method?: string) => request.mock.calls.filter(([path, options]) => String(path).endsWith(suffix) && (!method || (options as RequestInit | undefined)?.method === method))

describe('uploadVideoInPieces', () => {
  beforeEach(() => { request.mockReset(); window.localStorage.clear() })

  it('starts an upload, sends every piece in order, joins them and reports progress up to the end', async () => {
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (path.endsWith('/uploads') && options?.method === 'POST') return Promise.resolve(session())
      if (path.endsWith('/complete')) return Promise.resolve({ id: 'v1' })
      return Promise.resolve(session())
    })
    const progress: number[] = []
    const result = await uploadVideoInPieces<{ id: string }>(file, fields, (value) => progress.push(value), { wait: noWait })
    expect(result).toEqual({ id: 'v1' })
    const body = JSON.parse((calls('/uploads', 'POST')[0][1] as RequestInit).body as string)
    expect(body).toMatchObject({ courseId: 'c1', title: 'Limits', fileName: 'lesson.mp4', contentType: 'video/mp4', sizeBytes: 25, durationSeconds: 90 })
    const sent = calls('/chunks/0', 'PUT').concat(calls('/chunks/1', 'PUT'), calls('/chunks/2', 'PUT'))
    expect(sent.map(([, options]) => ((options as RequestInit).body as Blob).size)).toEqual([10, 10, 5])      // the last piece is what is left
    expect(progress[0]).toBe(0)
    expect(progress.at(-1)).toBe(1)
    expect([...progress].sort((a, b) => a - b)).toEqual(progress)                                          // never goes backwards
    expect(window.localStorage.length).toBe(0)                                                              // nothing left to carry on from
  })

  it('carries on from the pieces already stored when the same file is chosen again', async () => {
    window.localStorage.setItem('lms.videoUpload:c1:lesson.mp4:25:1', 'u1')
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (path.endsWith('/uploads/u1') && !options) return Promise.resolve(session({ received: [0, 1] }))
      if (path.endsWith('/complete')) return Promise.resolve({ id: 'v1' })
      return Promise.resolve(session())
    })
    const progress: number[] = []
    await uploadVideoInPieces(file, fields, (value) => progress.push(value), { wait: noWait })
    expect(calls('/uploads', 'POST')).toHaveLength(0)                                                        // no new upload
    expect(calls('/chunks/0')).toHaveLength(0)
    expect(calls('/chunks/1')).toHaveLength(0)
    expect(calls('/chunks/2', 'PUT')).toHaveLength(1)
    expect(progress[0]).toBeCloseTo(2 / 3)
  })

  it('starts again when the earlier upload is gone', async () => {
    window.localStorage.setItem('lms.videoUpload:c1:lesson.mp4:25:1', 'old')
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (path.endsWith('/uploads/old')) return Promise.reject(new ApiError('gone', 404))
      if (path.endsWith('/uploads') && options?.method === 'POST') return Promise.resolve(session({ id: 'new' }))
      if (path.endsWith('/complete')) return Promise.resolve({ id: 'v1' })
      return Promise.resolve(session())
    })
    await uploadVideoInPieces(file, fields, () => undefined, { wait: noWait })
    expect(calls('/uploads', 'POST')).toHaveLength(1)
    expect(calls('/uploads/new/chunks/0', 'PUT')).toHaveLength(1)
  })

  it('tries a failed piece again, pausing longer each time, and then goes on', async () => {
    let failures = 2
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (path.endsWith('/uploads') && options?.method === 'POST') return Promise.resolve(session({ totalChunks: 1 }))
      if (path.endsWith('/chunks/0')) return failures-- > 0 ? Promise.reject(new ApiError('network', 0)) : Promise.resolve(session())
      return Promise.resolve({ id: 'v1' })
    })
    const waits: number[] = []
    await uploadVideoInPieces(file, fields, () => undefined, { wait: (ms) => { waits.push(ms); return Promise.resolve() } })
    expect(waits).toEqual([1000, 2000])
    expect(calls('/chunks/0', 'PUT')).toHaveLength(3)
  })

  it('gives up after a few tries, says how to carry on and keeps the upload so it can be', async () => {
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (path.endsWith('/uploads') && options?.method === 'POST') return Promise.resolve(session())
      if (path.includes('/chunks/')) return Promise.reject(new ApiError('network', 0))
      return Promise.resolve(session())
    })
    await expect(uploadVideoInPieces(file, fields, () => undefined, { wait: noWait, tries: 3 })).rejects.toThrow(/Choose the same file again/)
    expect(calls('/chunks/0', 'PUT')).toHaveLength(3)
    expect(window.localStorage.getItem('lms.videoUpload:c1:lesson.mp4:25:1')).toBe('u1')
  })

  it('does not repeat a piece the server refused, and forgets an upload whose file was rejected', async () => {
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (path.endsWith('/uploads') && options?.method === 'POST') return Promise.resolve(session())
      if (path.endsWith('/chunks/0')) return Promise.reject(new ApiError('The file contents do not match its declared type.', 400))
      return Promise.resolve(session())
    })
    await expect(uploadVideoInPieces(file, fields, () => undefined, { wait: noWait })).rejects.toThrow(/do not match/)
    expect(calls('/chunks/0', 'PUT')).toHaveLength(1)
    expect(window.localStorage.length).toBe(0)
  })

  it('stops between pieces when cancelled', async () => {
    const controller = new AbortController()
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (path.endsWith('/uploads') && options?.method === 'POST') return Promise.resolve(session())
      if (path.endsWith('/chunks/0')) { controller.abort(); return Promise.resolve(session()) }
      return Promise.resolve(session())
    })
    await expect(uploadVideoInPieces(file, fields, () => undefined, { wait: noWait, signal: controller.signal })).rejects.toThrow(/cancelled/)
    expect(calls('/chunks/1')).toHaveLength(0)
  })

  it('sends missing pieces again once when joining says some are not stored', async () => {
    let joins = 0
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (path.endsWith('/uploads') && options?.method === 'POST') return Promise.resolve(session({ totalChunks: 1 }))
      if (path.endsWith('/complete')) return joins++ === 0 ? Promise.reject(new ApiError('1 piece has not arrived yet.', 409)) : Promise.resolve({ id: 'v1' })
      if (path.endsWith('/uploads/u1') && !options) return Promise.resolve(session({ totalChunks: 1, received: [] }))
      return Promise.resolve(session({ totalChunks: 1 }))
    })
    expect(await uploadVideoInPieces(file, fields, () => undefined, { wait: noWait })).toEqual({ id: 'v1' })
    expect(calls('/chunks/0', 'PUT')).toHaveLength(2)
  })
})
