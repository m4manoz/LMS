import { ApiError, getStoredSession } from './api'

type Xhr = Pick<XMLHttpRequest, 'open' | 'setRequestHeader' | 'send' | 'abort' | 'upload' | 'status' | 'responseText' | 'onload' | 'onerror' | 'onabort'>

/**
 * Sends a form (a file and its fields) with the person's credentials and reports progress as it goes.
 * fetch cannot report upload progress, and a large video can take minutes, so this uses XMLHttpRequest.
 */
export function uploadWithProgress<T>(path: string, form: FormData, onProgress: (fraction: number) => void, create: () => Xhr = () => new XMLHttpRequest()): { promise: Promise<T>; cancel: () => void } {
  const request = create()
  const promise = new Promise<T>((resolve, reject) => {
    const session = getStoredSession()
    request.open('POST', path)
    if (session?.accessToken) request.setRequestHeader('Authorization', `Bearer ${session.accessToken}`)
    if (session?.tenant.slug) request.setRequestHeader('X-Tenant-Slug', session.tenant.slug)
    request.upload.onprogress = (event: ProgressEvent) => { if (event.lengthComputable && event.total > 0) onProgress(Math.min(1, event.loaded / event.total)) }
    request.onload = () => {
      let body: unknown
      try { body = request.responseText ? JSON.parse(request.responseText) : null } catch { body = request.responseText }
      if (request.status >= 200 && request.status < 300) { onProgress(1); resolve(body as T); return }
      const message = typeof body === 'object' && body !== null && 'message' in body ? String((body as { message: unknown }).message) : `Upload failed with status ${request.status}.`
      reject(new ApiError(message, request.status))
    }
    request.onerror = () => reject(new ApiError('The upload could not be completed. Check your connection and try again.', 0))
    request.onabort = () => reject(new ApiError('The upload was cancelled.', 0))
    request.send(form)
  })
  return { promise, cancel: () => request.abort() }
}
