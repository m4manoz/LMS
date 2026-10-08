import { ApiError, apiRequest } from './api'

export type PieceUploadFields = { courseId: string; title: string; description: string; lessonId?: string | null; durationSeconds?: number | null }
type UploadSession = { id: string; chunkSize: number; totalChunks: number; received: number[] }
type Options = {
  signal?: AbortSignal
  /** How many times one piece is tried before the upload stops (it can be carried on later). */
  tries?: number
  wait?: (milliseconds: number) => Promise<void>
}

const rememberKey = (file: File, courseId: string) => `lms.videoUpload:${courseId}:${file.name}:${file.size}:${file.lastModified}`
const sleep = (milliseconds: number) => new Promise<void>((resolve) => window.setTimeout(resolve, milliseconds))

function remembered(key: string): string | null { try { return window.localStorage.getItem(key) } catch { return null } }
function remember(key: string, id: string | null) { try { if (id) window.localStorage.setItem(key, id); else window.localStorage.removeItem(key) } catch { /* private windows: the upload still works, it just cannot be carried on later */ } }

/** An error that sending the same thing again cannot fix. */
const final = (error: unknown) => error instanceof ApiError && error.status >= 400 && error.status < 500 && ![0, 408, 429].includes(error.status)

/**
 * Sends a video in pieces and then asks for them to be joined. If the page is closed or the connection drops, choosing the same file again carries on
 * from the pieces already stored. A piece that fails is tried again a few times, with a pause that grows, before the upload gives up.
 */
export async function uploadVideoInPieces<T>(file: File, fields: PieceUploadFields, onProgress: (fraction: number) => void, options: Options = {}): Promise<T> {
  const tries = options.tries ?? 4
  const wait = options.wait ?? sleep
  const key = rememberKey(file, fields.courseId)
  const cancelled = () => new ApiError('The upload was cancelled.', 0)

  let session: UploadSession | null = null
  const earlier = remembered(key)
  if (earlier) {
    try { session = await apiRequest<UploadSession>(`/api/v1/tenant/videos/uploads/${earlier}`) }
    catch (error) { if (!(error instanceof ApiError) || error.status !== 404) throw error; remember(key, null) }
  }
  session ??= await apiRequest<UploadSession>('/api/v1/tenant/videos/uploads', {
    method: 'POST',
    body: JSON.stringify({ courseId: fields.courseId, title: fields.title, description: fields.description || null, lessonId: fields.lessonId ?? null, fileName: file.name, contentType: file.type, sizeBytes: file.size, durationSeconds: fields.durationSeconds ?? null }),
  })
  remember(key, session.id)

  const send = async (current: UploadSession) => {
    const have = new Set(current.received)
    let done = have.size
    onProgress(done / current.totalChunks)
    for (let index = 0; index < current.totalChunks; index++) {
      if (have.has(index)) continue
      if (options.signal?.aborted) throw cancelled()
      const piece = file.slice(index * current.chunkSize, Math.min(file.size, (index + 1) * current.chunkSize))
      for (let attempt = 1; ; attempt++) {
        try { await apiRequest(`/api/v1/tenant/videos/uploads/${current.id}/chunks/${index}`, { method: 'PUT', body: piece, signal: options.signal }); break }
        catch (error) {
          if (options.signal?.aborted) throw cancelled()
          if (final(error)) { if (error instanceof ApiError && error.status === 400) remember(key, null); throw error }
          if (attempt >= tries) throw new ApiError('The connection was lost. Choose the same file again to carry on from where it stopped.', error instanceof ApiError ? error.status : 0)
          await wait(1000 * 2 ** (attempt - 1))
        }
      }
      done += 1
      onProgress(Math.min(0.99, done / current.totalChunks))
    }
  }

  await send(session)
  let video: T
  try { video = await apiRequest<T>(`/api/v1/tenant/videos/uploads/${session.id}/complete`, { method: 'POST' }) }
  catch (error) {
    // Pieces that went missing from storage are sent again, once.
    if (!(error instanceof ApiError) || error.status !== 409) throw error
    const fresh = await apiRequest<UploadSession>(`/api/v1/tenant/videos/uploads/${session.id}`)
    await send(fresh)
    video = await apiRequest<T>(`/api/v1/tenant/videos/uploads/${session.id}/complete`, { method: 'POST' })
  }
  remember(key, null)
  onProgress(1)
  return video
}
