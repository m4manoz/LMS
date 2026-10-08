import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api'
import { BlockView, type Block } from './LessonBlocks'

const request = vi.fn()
vi.mock('@/lib/api', async () => ({ ...(await vi.importActual<typeof import('@/lib/api')>('@/lib/api')), apiRequest: (...args: unknown[]) => request(...args), fetchBlobUrl: vi.fn().mockResolvedValue({ url: 'blob:x', contentType: 'video/mp4' }) }))
vi.mock('@/components/VideoPlayer', () => ({ default: ({ video }: { video: { id: string; title: string } }) => <div role="group" aria-label="Library player">{video.title} ({video.id})</div> }))

const block = (over: Partial<Block> = {}): Block => ({
  id: 'b1', type: 'Video', displayOrder: 1, title: 'Part one', text: null, language: null, url: null, caption: null,
  file: { fileName: 'one.mp4', contentType: 'video/mp4', sizeBytes: 100, downloadPath: '/api/v1/tenant/courses/c1/assets/a1' }, ...over,
})

describe('a lesson video that is in the library', () => {
  beforeEach(() => { request.mockReset() })

  it('plays in the library player with its chapters and my notes', async () => {
    request.mockImplementation((path: string) => path.endsWith('/videos/v1') ? Promise.resolve({ id: 'v1', title: 'Part one', durationSeconds: 100 }) : Promise.resolve([]))
    render(<BlockView block={block({ videoId: 'v1' })} />)
    expect(await screen.findByRole('group', { name: 'Library player' })).toHaveTextContent('Part one (v1)')
    expect(await screen.findByRole('region', { name: 'My notes' })).toBeInTheDocument()
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/videos/v1')
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/videos/v1/notes')
  })

  it('falls back to the plain file when the library video cannot be read', async () => {
    request.mockImplementation((path: string) => path.endsWith('/videos/v1') ? Promise.reject(new ApiError('Not found', 404)) : Promise.resolve({ url: null }))
    const { container } = render(<BlockView block={block({ videoId: 'v1' })} />)
    await vi.waitFor(() => expect(container.querySelector('video')).not.toBeNull())
    expect(screen.queryByRole('group', { name: 'Library player' })).toBeNull()
  })

  it('shows a video that is not in the library as before, with no player of ours', async () => {
    request.mockResolvedValue({ url: null })
    const { container } = render(<BlockView block={block()} />)
    await vi.waitFor(() => expect(container.querySelector('video')).not.toBeNull())
    expect(screen.queryByRole('group', { name: 'Library player' })).toBeNull()
    expect(screen.queryByRole('region', { name: 'My notes' })).toBeNull()
  })
})
