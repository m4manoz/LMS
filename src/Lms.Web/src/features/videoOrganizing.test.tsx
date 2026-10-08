import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api'
import type { VideoItem } from '@/lib/video'
import VideoLibraryPage, { filterVideos } from './VideoLibraryPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => ({ ...(await vi.importActual<typeof import('@/lib/api')>('@/lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))
vi.mock('@/lib/pieceUpload', () => ({ uploadVideoInPieces: vi.fn() }))

const video = (over: Partial<VideoItem> = {}): VideoItem => ({
  id: 'v1', courseId: 'c1', courseTitle: 'Algebra', lessonId: null, title: 'Limits', description: null, type: 'Uploaded', status: 'Ready', statusMessage: null,
  contentType: 'video/mp4', sizeBytes: 5 * 1024 * 1024, durationSeconds: 600, externalUrl: null, createdBy: 'Tara', createdAtUtc: '2026-10-01T10:00:00Z', myProgress: null, tags: [], ...over,
})
const library = [
  video({ id: 'a', title: 'Series', createdAtUtc: '2026-10-03T10:00:00Z', durationSeconds: 300, sizeBytes: 9 * 1024 * 1024, tags: ['calculus', 'week 2'] }),
  video({ id: 'b', title: 'Limits', createdAtUtc: '2026-10-01T10:00:00Z', durationSeconds: 900, sizeBytes: 2 * 1024 * 1024, tags: ['calculus', 'week 1'] }),
  video({ id: 'c', title: 'Poetry', courseId: 'c2', courseTitle: 'English', createdAtUtc: '2026-10-02T10:00:00Z', durationSeconds: null, status: 'Failed', tags: [] }),
]
const titles = () => within(screen.getByRole('list', { name: 'Videos' })).getAllByRole('listitem').map((row) => row.querySelector('strong')?.textContent)
const calls = (method: string) => request.mock.calls.filter(([, options]) => (options as { method?: string } | undefined)?.method === method)

describe('filterVideos with tags, state and order', () => {
  const ids = (list: VideoItem[]) => list.map((item) => item.id)
  it('narrows to a tag exactly, and finds tags in the search', () => {
    expect(ids(filterVideos(library, '', 'All', 'All', { tag: 'calculus' }))).toEqual(['a', 'b'])
    expect(ids(filterVideos(library, '', 'All', 'All', { tag: 'week 1' }))).toEqual(['b'])
    expect(ids(filterVideos(library, '', 'All', 'All', { tag: 'calc' }))).toEqual([])
    expect(ids(filterVideos(library, 'week 2', 'All', 'All'))).toEqual(['a'])
  })
  it('narrows by state', () => {
    expect(ids(filterVideos(library, '', 'All', 'All', { status: 'Failed' }))).toEqual(['c'])
    expect(ids(filterVideos(library, '', 'All', 'All', { status: 'All' }))).toHaveLength(3)
  })
  it('puts the list in the order asked for, newest first by default', () => {
    expect(ids(filterVideos(library, '', 'All', 'All'))).toEqual(['a', 'c', 'b'])
    expect(ids(filterVideos(library, '', 'All', 'All', { sort: 'oldest' }))).toEqual(['b', 'c', 'a'])
    expect(ids(filterVideos(library, '', 'All', 'All', { sort: 'title' }))).toEqual(['b', 'c', 'a'])
    expect(ids(filterVideos(library, '', 'All', 'All', { sort: 'longest' }))).toEqual(['b', 'a', 'c'])       // an unknown length goes last
    expect(ids(filterVideos(library, '', 'All', 'All', { sort: 'largest' }))).toEqual(['a', 'c', 'b'])
  })
  it('does not change the list it was given', () => {
    const before = ids(library)
    filterVideos(library, '', 'All', 'All', { sort: 'title' })
    expect(ids(library)).toEqual(before)
  })
})

describe('organizing the library as a learner', () => {
  beforeEach(() => { request.mockReset(); permissions = ['course.read']; request.mockImplementation(() => Promise.resolve(library.filter((item) => item.status === 'Ready'))) })

  it('sorts, and narrows to a tag with a button that can be pressed again to let go', async () => {
    render(<VideoLibraryPage />)
    await screen.findByText('Limits')
    expect(titles()).toEqual(['Series', 'Limits'])
    await userEvent.selectOptions(screen.getByLabelText('Sort videos'), 'Title A–Z')
    expect(titles()).toEqual(['Limits', 'Series'])
    const tags = screen.getByRole('group', { name: 'Filter by tag' })
    expect(within(tags).getAllByRole('button').map((item) => item.textContent)).toEqual(['calculus 2', 'week 1 1', 'week 2 1'])
    await userEvent.click(within(tags).getByRole('button', { name: /week 2/ }))
    expect(titles()).toEqual(['Series'])
    expect(within(tags).getByRole('button', { name: /week 2/ })).toHaveAttribute('aria-pressed', 'true')
    await userEvent.click(within(tags).getByRole('button', { name: /week 2/ }))
    expect(titles()).toEqual(['Limits', 'Series'])
  })

  it('offers no selection, state filter or deleting to a learner', async () => {
    render(<VideoLibraryPage />)
    await screen.findByText('Limits')
    expect(screen.queryByRole('checkbox')).toBeNull()
    expect(screen.queryByLabelText('Filter by state')).toBeNull()
  })
})

describe('organizing the library as staff', () => {
  beforeEach(() => {
    request.mockReset(); permissions = ['course.read', 'course.manage']
    request.mockImplementation((path: string, options?: { method?: string; body?: string }) => {
      if (path === '/api/v1/tenant/videos' && !options?.method) return Promise.resolve(library)
      if (path === '/api/v1/tenant/videos/usage') return Promise.resolve({ count: 3, totalBytes: 16 * 1024 * 1024, quotaBytes: 100 * 1024 * 1024 })
      if (path === '/api/v1/tenant/courses') return Promise.resolve([])
      if (options?.method === 'PUT') return Promise.resolve(video({ id: 'b', title: 'Limits', tags: JSON.parse(options.body!).tags }))
      return Promise.resolve(null)
    })
  })
  afterEach(() => { vi.restoreAllMocks() })

  it('shows how full the library is when it has a limit', async () => {
    render(<VideoLibraryPage />)
    expect(await screen.findByText('3 videos · 16 MB of 100 MB stored')).toBeInTheDocument()
  })

  it('narrows to videos that failed', async () => {
    render(<VideoLibraryPage />)
    await screen.findByText('Poetry')
    await userEvent.selectOptions(screen.getByLabelText('Filter by state'), 'Failed')
    expect(titles()).toEqual(['Poetry'])
  })

  it('selects several videos and deletes them after asking', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<VideoLibraryPage />)
    await screen.findByText('Poetry')
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Series' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Poetry' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete 2 selected' }))
    await waitFor(() => expect(calls('DELETE')).toHaveLength(2))
    expect(calls('DELETE').map(([path]) => path).sort()).toEqual(['/api/v1/tenant/videos/a', '/api/v1/tenant/videos/c'])
    expect(await screen.findByText('2 videos were deleted.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /selected/ })).toBeNull()
  })

  it('does nothing when the question is answered no', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<VideoLibraryPage />)
    await screen.findByText('Poetry')
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select all 3' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete 3 selected' }))
    expect(calls('DELETE')).toHaveLength(0)
    expect(screen.getByRole('button', { name: 'Delete 3 selected' })).toBeInTheDocument()
  })

  it('names the videos that could not be deleted and keeps them selected', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (options?.method === 'DELETE') return path.endsWith('/c') ? Promise.reject(new ApiError('nope', 500)) : Promise.resolve(null)
      if (path === '/api/v1/tenant/videos') return Promise.resolve(library)
      return Promise.resolve(path === '/api/v1/tenant/courses' ? [] : null)
    })
    render(<VideoLibraryPage />)
    await screen.findByText('Poetry')
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Series' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Poetry' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete 2 selected' }))
    expect(await screen.findByText('Could not delete Poetry.')).toBeInTheDocument()
    expect(screen.getByText('1 video was deleted.')).toBeInTheDocument()
  })

  it('sets tags from a comma separated list', async () => {
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Limits' }))
    const panel = await screen.findByRole('dialog', { name: 'Limits' })
    await userEvent.click(within(panel).getByRole('tab', { name: 'Details' }))
    const field = within(panel).getByLabelText('Tags')
    expect(field).toHaveValue('calculus, week 1')
    await userEvent.clear(field)
    await userEvent.type(field, 'Calculus,  week 3 , ,exam')
    await userEvent.click(within(panel).getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(calls('PUT')).toHaveLength(1))
    expect(JSON.parse((calls('PUT')[0][1] as { body: string }).body).tags).toEqual(['Calculus', 'week 3', 'exam'])
  })
})
