import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import VideoPlayer from '@/components/VideoPlayer'
import VideoLibraryPage, { filterVideos, validateAddVideo } from './VideoLibraryPage'
import { ApiError } from '@/lib/api'
import type { VideoItem } from '@/lib/video'

const request = vi.fn()
const upload = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))
vi.mock('@/lib/upload', () => ({ uploadWithProgress: (...args: unknown[]) => upload(...args) }))
vi.mock('@/lib/video', async () => {
  const actual = await vi.importActual<typeof import('@/lib/video')>('@/lib/video')
  return { ...actual, readVideoDuration: () => Promise.resolve(754) }
})

const video = (over: Partial<VideoItem> = {}): VideoItem => ({
  id: 'v1', courseId: 'c1', courseTitle: 'Algebra', lessonId: null, title: 'Intro to limits', description: 'About limits', type: 'Uploaded', status: 'Ready', statusMessage: null,
  contentType: 'video/mp4', sizeBytes: 5 * 1024 * 1024, durationSeconds: 600, externalUrl: null, createdBy: 'Tara', createdAtUtc: '2026-10-01T10:00:00Z', myProgress: null, ...over,
})
const calls = (fragment: string, method?: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || call[1]?.method === method))
const stream = { kind: 'stream', url: '/api/v1/tenant/videos/v1/stream?token=abc', expiresAtUtc: null, embeddable: false }

describe('filterVideos and validateAddVideo', () => {
  const list = [video({ id: '1', title: 'Limits' }), video({ id: '2', title: 'Poetry', courseId: 'c2', courseTitle: 'English', type: 'External', description: null })]
  it('narrows by text (title, description or course), course and type', () => {
    expect(filterVideos(list, 'POET', 'All', 'All').map((item) => item.id)).toEqual(['2'])
    expect(filterVideos(list, 'english', 'All', 'All').map((item) => item.id)).toEqual(['2'])
    expect(filterVideos(list, '', 'c1', 'All').map((item) => item.id)).toEqual(['1'])
    expect(filterVideos(list, '', 'All', 'External').map((item) => item.id)).toEqual(['2'])
    expect(filterVideos(list, 'limits', 'All', 'External')).toEqual([])
  })

  it('reports the first problem with the add form', () => {
    const ok = { title: 'T', courseId: 'c1', url: '', file: new File(['x'], 'a.mp4', { type: 'video/mp4' }) }
    expect(validateAddVideo('upload', ok)).toBeNull()
    expect(validateAddVideo('upload', { ...ok, title: ' ' })).toMatch(/title/)
    expect(validateAddVideo('upload', { ...ok, courseId: '' })).toMatch(/course/)
    expect(validateAddVideo('upload', { ...ok, file: null })).toMatch(/video file/)
    expect(validateAddVideo('upload', { ...ok, file: new File(['x'], 'a.pdf', { type: 'application/pdf' }) })).toMatch(/MP4/)
    expect(validateAddVideo('link', { ...ok, url: 'https://www.youtube.com/watch?v=1' })).toBeNull()
    for (const bad of ['', 'http://x.example.org/1', 'javascript:alert(1)', 'not a link']) expect(validateAddVideo('link', { ...ok, url: bad }), bad).toMatch(/https/)
  })
})

// ---------- the player ----------
describe('VideoPlayer', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path: string) => Promise.resolve(String(path).endsWith('/link') ? stream : { lastPositionSeconds: 0, percent: 0, completed: false }))
  })

  const player = async (item = video()) => {
    const { container } = render(<VideoPlayer video={item} />)
    await waitFor(() => expect(container.querySelector('video')).not.toBeNull())
    return container.querySelector('video') as HTMLVideoElement
  }
  const at = (element: HTMLVideoElement, time: number, duration = 600) => {
    Object.defineProperty(element, 'duration', { configurable: true, value: duration })
    Object.defineProperty(element, 'currentTime', { configurable: true, writable: true, value: time })
  }

  it('plays the signed link it was given', async () => {
    const element = await player()
    expect(element).toHaveAttribute('src', stream.url)
    expect(calls('/videos/v1/link')).toHaveLength(1)
  })

  it('resumes where the person stopped and says so', async () => {
    const element = await player(video({ myProgress: { lastPositionSeconds: 125, percent: 20, completed: false } }))
    at(element, 0)
    fireEvent.loadedMetadata(element)
    expect(element.currentTime).toBe(125)
    expect(await screen.findByText('Resumed from 2:05.')).toBeInTheDocument()
  })

  it('starts from the beginning when the video was all but finished', async () => {
    const element = await player(video({ myProgress: { lastPositionSeconds: 598, percent: 99, completed: true } }))
    at(element, 0)
    fireEvent.loadedMetadata(element)
    expect(element.currentTime).toBe(0)
    expect(screen.queryByText(/Resumed from/)).toBeNull()
  })

  it('reports the position when paused, with the time actually played and that playback started', async () => {
    const element = await player()
    at(element, 0)
    fireEvent.timeUpdate(element)
    at(element, 1); fireEvent.timeUpdate(element)
    at(element, 2); fireEvent.timeUpdate(element)
    at(element, 30); fireEvent.timeUpdate(element)       // a jump: seeking is not watching
    fireEvent.pause(element)
    await waitFor(() => expect(calls('/progress', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/progress', 'POST')[0][1].body)).toMatchObject({ positionSeconds: 30, durationSeconds: 600, watchedSecondsDelta: 2, started: true })
  })

  it('reports again every 15 seconds of playback without being asked to stop', async () => {
    const element = await player()
    at(element, 0); fireEvent.timeUpdate(element)
    for (let t = 1; t <= 16; t += 1) { at(element, t); fireEvent.timeUpdate(element) }
    await waitFor(() => expect(calls('/progress', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/progress', 'POST')[0][1].body).watchedSecondsDelta).toBe(15)
  })

  it('marks the video finished when it ends', async () => {
    const element = await player()
    at(element, 600)
    fireEvent.ended(element)
    await waitFor(() => expect(calls('/progress', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/progress', 'POST')[0][1].body)).toMatchObject({ completed: true, positionSeconds: 600 })
  })

  it('sends nothing for a video that was never played', async () => {
    const element = await player()
    at(element, 0)
    fireEvent.pause(element)
    expect(calls('/progress', 'POST')).toHaveLength(0)
  })

  it('keeps playing when a report fails', async () => {
    const element = await player()
    request.mockImplementation((path: string) => String(path).endsWith('/link') ? Promise.resolve(stream) : Promise.resolve().then(() => { throw new ApiError('Server error', 500) }))
    at(element, 20); fireEvent.timeUpdate(element); at(element, 21); fireEvent.timeUpdate(element)
    fireEvent.pause(element)
    await waitFor(() => expect(calls('/progress', 'POST').length).toBeGreaterThan(0))
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('asks for a fresh link when the first one runs out, but gives up after two tries', async () => {
    const element = await player()
    await act(async () => { fireEvent.error(element) })
    await waitFor(() => expect(calls('/videos/v1/link')).toHaveLength(2))
    await act(async () => { fireEvent.error(document.querySelector('video')!) })
    await waitFor(() => expect(calls('/videos/v1/link')).toHaveLength(3))
    await act(async () => { fireEvent.error(document.querySelector('video')!) })
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be played')
  })

  it('shows the server message when the link cannot be had, with a way to try again', async () => {
    request.mockImplementation(() => Promise.resolve().then(() => { throw new ApiError('The video file is not available.', 409) }))
    render(<VideoPlayer video={video()} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('The video file is not available.')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })

  it('embeds a video from a trusted site and sends everything else to its own tab', async () => {
    request.mockResolvedValue({ kind: 'external', url: 'https://www.youtube-nocookie.com/embed/abc', expiresAtUtc: null, embeddable: true })
    const { container, unmount } = render(<VideoPlayer video={video({ type: 'External' })} />)
    await waitFor(() => expect(container.querySelector('iframe')).not.toBeNull())
    expect(container.querySelector('iframe')).toHaveAttribute('src', 'https://www.youtube-nocookie.com/embed/abc')
    expect(container.querySelector('iframe')?.getAttribute('sandbox')).not.toContain('allow-top-navigation')
    unmount()

    request.mockResolvedValue({ kind: 'external', url: 'https://videos.school.example.org/1', expiresAtUtc: null, embeddable: false })
    render(<VideoPlayer video={video({ type: 'External' })} />)
    const open = await screen.findByRole('link', { name: 'Open the video' })
    expect(open).toHaveAttribute('href', 'https://videos.school.example.org/1')
    expect(open).toHaveAttribute('rel', expect.stringContaining('noopener'))
    expect(calls('/progress')).toHaveLength(0)             // progress is only tracked for videos we play ourselves
  })
})

// ---------- the library page ----------
describe('VideoLibraryPage as a learner', () => {
  beforeEach(() => {
    request.mockReset(); permissions = ['course.read']
    request.mockImplementation((path: string) => {
      if (path === '/api/v1/tenant/videos') return Promise.resolve([video({ myProgress: { lastPositionSeconds: 240, percent: 40, completed: false } }), video({ id: 'v2', title: 'Derivatives', courseId: 'c2', courseTitle: 'Calculus', type: 'External', durationSeconds: null, sizeBytes: 0, myProgress: { lastPositionSeconds: 590, percent: 98, completed: true } })])
      if (path.endsWith('/link')) return Promise.resolve(stream)
      return Promise.resolve(null)
    })
  })

  it('lists the videos with length, size and how far the learner got, and opens nothing by default', async () => {
    render(<VideoLibraryPage />)
    const list = await screen.findByRole('list', { name: 'Videos' })
    expect(within(list).getByText('Intro to limits')).toBeInTheDocument()
    expect(within(list).getByText('10:00')).toBeInTheDocument()
    expect(within(list).getByText('5 MB')).toBeInTheDocument()
    expect(within(list).getByText('Watched 40%')).toBeInTheDocument()
    expect(within(list).getByText('Completed')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Add video' })).toBeNull()
    expect(calls('/videos/usage')).toHaveLength(0)
  })

  it('watches a video in a panel with no management tabs', async () => {
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Intro to limits' }))
    const panel = await screen.findByRole('dialog', { name: 'Intro to limits' })
    await waitFor(() => expect(panel.querySelector('video')).not.toBeNull())
    expect(within(panel).queryByRole('tab')).toBeNull()
    expect(within(panel).getByText(/added by Tara/)).toBeInTheDocument()
  })

  it('searches and filters by course and type', async () => {
    render(<VideoLibraryPage />)
    await screen.findByText('Derivatives')
    await userEvent.type(screen.getByLabelText('Search videos'), 'deriv')
    expect(screen.queryByText('Intro to limits')).toBeNull()
    await userEvent.clear(screen.getByLabelText('Search videos'))
    await userEvent.selectOptions(screen.getByLabelText('Filter by course'), 'Algebra')
    expect(screen.queryByText('Derivatives')).toBeNull()
    await userEvent.selectOptions(screen.getByLabelText('Filter by course'), 'All courses')
    await userEvent.click(screen.getByRole('button', { name: 'Linked' }))
    expect(screen.queryByText('Intro to limits')).toBeNull()
    expect(screen.getByText('Derivatives')).toBeInTheDocument()
  })

  it('says so when there are no videos', async () => {
    request.mockImplementation((path: string) => Promise.resolve(path === '/api/v1/tenant/videos' ? [] : null))
    render(<VideoLibraryPage />)
    expect(await screen.findByText(/no videos for your courses yet/)).toBeInTheDocument()
  })

  it('shows the server message when the list cannot be loaded', async () => {
    request.mockImplementation(() => Promise.resolve().then(() => { throw new ApiError('Not allowed.', 403) }))
    render(<VideoLibraryPage />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Not allowed.')
  })
})

describe('VideoLibraryPage as staff', () => {
  const course = { id: 'c1', code: 'MATH-1', title: 'Algebra', status: 'Draft' }
  beforeEach(() => {
    request.mockReset(); upload.mockReset(); permissions = ['course.read', 'course.manage']
    request.mockImplementation((path: string, options?: { method?: string; body?: string }) => {
      if (path === '/api/v1/tenant/videos' && !options?.method) return Promise.resolve([video()])
      if (path === '/api/v1/tenant/videos/usage') return Promise.resolve({ count: 1, totalBytes: 5 * 1024 * 1024 })
      if (path === '/api/v1/tenant/courses') return Promise.resolve([course, { id: 'c9', code: 'OLD-1', title: 'Old', status: 'Archived' }])
      if (path.endsWith('/videos/external')) return Promise.resolve(video({ id: 'v9', title: JSON.parse(options!.body!).title, type: 'External' }))
      if (path.endsWith('/link')) return Promise.resolve(stream)
      if (path === '/api/v1/tenant/courses/c1') return Promise.resolve({ course, modules: [{ id: 'm1', title: 'Limits', lessons: [{ id: 'l1', title: 'What is a limit?' }] }] })
      if (path.endsWith('/analytics')) return Promise.resolve({ eligibleLearners: 4, viewers: 3, completed: 1, plays: 5, watchedSeconds: 1500, averageWatchedPercent: 42, durationSeconds: 600, funnel: [{ percent: 25, viewers: 3 }, { percent: 50, viewers: 2 }, { percent: 75, viewers: 1 }, { percent: 100, viewers: 1 }] })
      if (path.endsWith('/attach')) return Promise.resolve({ blockId: 'b1', blockType: 'Video', lessonId: 'l1' })
      if (options?.method === 'PUT') return Promise.resolve(video({ title: JSON.parse(options.body!).title }))
      return Promise.resolve(null)
    })
  })

  it('shows how much is stored and offers Add video', async () => {
    render(<VideoLibraryPage />)
    expect(await screen.findByText('1 video · 5 MB stored')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Add video' })).toBeInTheDocument()
  })

  it('uploads a video with a progress bar, then lists it and says it was added', async () => {
    upload.mockImplementation((_path: string, _form: FormData, onProgress: (value: number) => void) => ({ promise: Promise.resolve().then(() => { onProgress(0.5); return video({ id: 'v7', title: 'Uploaded one' }) }), cancel: vi.fn() }))
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Add video' }))
    const panel = await screen.findByRole('dialog', { name: 'Add a video' })
    for (const label of ['Video file', 'Title', 'Course']) expect(within(panel).getByText(label)).toHaveClass('after:text-red-500')
    expect(within(panel).queryByRole('option', { name: /Old/ })).toBeNull()                  // archived courses cannot get videos

    await userEvent.click(within(panel).getByRole('button', { name: 'Upload video' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Give the video a title.')
    expect(upload).not.toHaveBeenCalled()

    await userEvent.type(within(panel).getByLabelText('Title'), 'Uploaded one')
    await userEvent.selectOptions(within(panel).getByLabelText('Course'), 'c1')
    await userEvent.upload(within(panel).getByLabelText('Video file'), new File(['abc'], 'lesson.mp4', { type: 'video/mp4' }))
    await userEvent.click(within(panel).getByRole('button', { name: 'Upload video' }))

    await waitFor(() => expect(upload).toHaveBeenCalledTimes(1))
    const [path, form] = upload.mock.calls[0] as [string, FormData]
    expect(path).toBe('/api/v1/tenant/videos')
    expect(form.get('courseId')).toBe('c1')
    expect(form.get('title')).toBe('Uploaded one')
    expect(form.get('durationSeconds')).toBe('754')                                          // read from the file itself
    expect((form.get('file') as File).name).toBe('lesson.mp4')
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(await screen.findByText('“Uploaded one” was added.')).toBeInTheDocument()
  })

  it('shows the server message when an upload is refused', async () => {
    upload.mockImplementation(() => ({ promise: Promise.resolve().then(() => { throw new ApiError('The file contents do not match its declared type.', 400) }), cancel: vi.fn() }))
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Add video' }))
    const panel = await screen.findByRole('dialog', { name: 'Add a video' })
    await userEvent.type(within(panel).getByLabelText('Title'), 'Broken')
    await userEvent.selectOptions(within(panel).getByLabelText('Course'), 'c1')
    await userEvent.upload(within(panel).getByLabelText('Video file'), new File(['abc'], 'x.mp4', { type: 'video/mp4' }))
    await userEvent.click(within(panel).getByRole('button', { name: 'Upload video' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('do not match')
    expect(within(panel).queryByRole('progressbar')).toBeNull()
  })

  it('adds a linked video, checking the address first', async () => {
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Add video' }))
    const panel = await screen.findByRole('dialog', { name: 'Add a video' })
    await userEvent.click(within(panel).getByRole('radio', { name: 'Link to a video' }))
    await userEvent.type(within(panel).getByLabelText('Title'), 'Khan limits')
    await userEvent.selectOptions(within(panel).getByLabelText('Course'), 'c1')
    await userEvent.type(within(panel).getByLabelText('Video link'), 'http://insecure.example.org/1')
    await userEvent.click(within(panel).getByRole('button', { name: 'Add video' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('https')
    expect(calls('/videos/external', 'POST')).toHaveLength(0)

    await userEvent.clear(within(panel).getByLabelText('Video link'))
    await userEvent.type(within(panel).getByLabelText('Video link'), 'https://www.youtube.com/watch?v=1')
    await userEvent.click(within(panel).getByRole('button', { name: 'Add video' }))
    await waitFor(() => expect(calls('/videos/external', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/videos/external', 'POST')[0][1].body)).toEqual({ courseId: 'c1', title: 'Khan limits', description: null, url: 'https://www.youtube.com/watch?v=1' })
  })

  it('edits the title and deletes the video after confirming', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true)
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Intro to limits' }))
    const panel = await screen.findByRole('dialog', { name: 'Intro to limits' })
    await userEvent.click(within(panel).getByRole('tab', { name: 'Details' }))
    await userEvent.clear(within(panel).getByLabelText('Title'))
    await userEvent.type(within(panel).getByLabelText('Title'), 'Limits, part 1')
    await userEvent.click(within(panel).getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(calls('/videos/v1', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('/videos/v1', 'PUT')[0][1].body)).toEqual({ title: 'Limits, part 1', description: 'About limits' })
    expect(await within(panel).findByText('Saved.')).toBeInTheDocument()

    await userEvent.click(within(panel).getByRole('button', { name: 'Delete video' }))
    expect(calls('/videos/v1', 'DELETE')).toHaveLength(0)             // declined
    await userEvent.click(within(panel).getByRole('button', { name: 'Delete video' }))
    await waitFor(() => expect(calls('/videos/v1', 'DELETE')).toHaveLength(1))
    expect(await screen.findByText('The video was deleted.')).toBeInTheDocument()
    confirm.mockRestore()
  })

  it('adds a video to a lesson of a draft course', async () => {
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Intro to limits' }))
    const panel = await screen.findByRole('dialog', { name: 'Intro to limits' })
    await userEvent.click(within(panel).getByRole('tab', { name: 'Lesson' }))
    await userEvent.click(await within(panel).findByRole('button', { name: 'Add to lesson' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Choose the lesson.')
    await userEvent.selectOptions(within(panel).getByRole('combobox', { name: 'Lesson' }), 'l1')
    await userEvent.click(within(panel).getByRole('button', { name: 'Add to lesson' }))
    await waitFor(() => expect(calls('/videos/v1/attach', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/videos/v1/attach', 'POST')[0][1].body)).toEqual({ lessonId: 'l1' })
    expect(await within(panel).findByText(/Added to the lesson/)).toBeInTheDocument()
  })

  it('explains that a published course needs a new version first', async () => {
    request.mockImplementation((path: string) => {
      if (path === '/api/v1/tenant/videos') return Promise.resolve([video()])
      if (path === '/api/v1/tenant/courses/c1') return Promise.resolve({ course: { ...course, status: 'Published' }, modules: [] })
      if (path.endsWith('/link')) return Promise.resolve(stream)
      return Promise.resolve(path === '/api/v1/tenant/courses' ? [course] : null)
    })
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Intro to limits' }))
    const panel = await screen.findByRole('dialog', { name: 'Intro to limits' })
    await userEvent.click(within(panel).getByRole('tab', { name: 'Lesson' }))
    expect(await within(panel).findByText(/lessons can only change in a new version/)).toBeInTheDocument()
  })

  it('shows how people watched it', async () => {
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Intro to limits' }))
    const panel = await screen.findByRole('dialog', { name: 'Intro to limits' })
    await userEvent.click(within(panel).getByRole('tab', { name: 'Analytics' }))
    expect(await within(panel).findByText('Enrolled learners')).toBeInTheDocument()
    expect(within(panel).getByText('42%')).toBeInTheDocument()
    expect(within(panel).getByText('25:00')).toBeInTheDocument()                       // time watched
    expect(within(panel).getByText('Reached 25%')).toBeInTheDocument()
    expect(within(panel).getAllByText('Finished').length).toBe(2)                     // the count and the last step of the funnel
    expect(within(panel).getAllByText('3 of 3').length).toBeGreaterThan(0)
  })
})
