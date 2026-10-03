import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import VideoPlayer from '@/components/VideoPlayer'
import VideoLibraryPage from './VideoLibraryPage'
import type { VideoItem } from '@/lib/video'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

const video = (over: Partial<VideoItem> = {}): VideoItem => ({
  id: 'v1', courseId: 'c1', courseTitle: 'Algebra', lessonId: null, title: 'Intro to limits', description: null, type: 'Uploaded', status: 'Ready', statusMessage: null,
  contentType: 'video/mp4', sizeBytes: 1024, durationSeconds: 600, externalUrl: null, createdBy: 'Tara', createdAtUtc: '2026-10-01T10:00:00Z', myProgress: null, ...over,
})
const calls = (fragment: string, method?: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || call[1]?.method === method))

describe('streaming playback', () => {
  it('falls back to the original file when the browser cannot stream', async () => {
    request.mockReset()
    request.mockImplementation((path: string) => Promise.resolve(String(path).endsWith('/link')
      ? { kind: 'hls', url: '/api/v1/tenant/videos/v1/hls/index.m3u8?token=t', fallbackUrl: '/api/v1/tenant/videos/v1/stream?token=t', expiresAtUtc: null, embeddable: false }
      : { lastPositionSeconds: 0, percent: 0, completed: false }))
    const { container } = render(<VideoPlayer video={video({ hasStreaming: true, posterUrl: '/poster?token=t' })} />)
    await waitFor(() => expect(container.querySelector('video')).not.toBeNull())
    const element = container.querySelector('video') as HTMLVideoElement
    expect(element).toHaveAttribute('poster', '/poster?token=t')
    // jsdom has no media support, so streaming cannot start and the original file takes over.
    await waitFor(() => expect(element).toHaveAttribute('src', '/api/v1/tenant/videos/v1/stream?token=t'))
  })
})

describe('videos being converted', () => {
  beforeEach(() => {
    request.mockReset()
    permissions = ['course.read', 'course.manage']
  })

  function serve(list: VideoItem[]) {
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path === '/api/v1/tenant/videos' && !options?.method) return Promise.resolve(list)
      if (path === '/api/v1/tenant/videos/usage') return Promise.resolve({ count: list.length, totalBytes: 0 })
      if (path === '/api/v1/tenant/courses') return Promise.resolve([])
      if (path.endsWith('/reprocess')) return Promise.resolve(video({ id: 'v2', status: 'Processing' }))
      return Promise.resolve(null)
    })
  }

  it('shows a poster in the list and marks a video that is still converting', async () => {
    serve([video({ id: 'v1', title: 'Ready one', posterUrl: '/poster1?token=t', hasStreaming: true }), video({ id: 'v2', title: 'Busy one', status: 'Processing' })])
    render(<VideoLibraryPage />)
    await screen.findByText('Ready one')
    const row = (text: string) => screen.getAllByRole('listitem').find((item) => item.textContent?.includes(text))!
    const ready = row('Ready one')
    expect(ready.querySelector('img')).toHaveAttribute('src', '/poster1?token=t')
    expect(within(row('Busy one')).getByText('Converting')).toBeInTheDocument()
  })

  it('says a converting video is being prepared instead of trying to play it', async () => {
    serve([video({ id: 'v2', title: 'Busy one', status: 'Processing' })])
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Busy one' }))
    const panel = await screen.findByRole('dialog', { name: 'Busy one' })
    expect(within(panel).getByRole('status')).toHaveTextContent(/being prepared/)
    expect(calls('/link')).toHaveLength(0)
  })

  it('explains a failure and lets staff try again', async () => {
    serve([video({ id: 'v2', title: 'Broken one', status: 'Failed', statusMessage: 'The file could not be read as a video.' })])
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Broken one' }))
    const panel = await screen.findByRole('dialog', { name: 'Broken one' })
    expect(within(panel).getByText('The file could not be read as a video.')).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(calls('/v2/reprocess', 'POST')).toHaveLength(1))
    expect(await within(panel).findByRole('status')).toHaveTextContent(/being prepared/)
  })

  it('offers conversion for an older upload with no streaming version, on the Details tab', async () => {
    serve([video({ id: 'v3', title: 'Old upload' })])
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Old upload' }))
    const panel = await screen.findByRole('dialog', { name: 'Old upload' })
    await userEvent.click(within(panel).getByRole('tab', { name: 'Details' }))
    await userEvent.click(within(panel).getByRole('button', { name: 'Convert for streaming' }))
    await waitFor(() => expect(calls('/v3/reprocess', 'POST')).toHaveLength(1))
  })
})
