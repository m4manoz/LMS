import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api'
import type { VideoItem } from '@/lib/video'
import { ChapterList, ChaptersEditor, VideoNotes } from './VideoStudyPanels'

const request = vi.fn()
vi.mock('@/lib/api', async () => ({ ...(await vi.importActual<typeof import('@/lib/api')>('@/lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))

const calls = (method: string) => request.mock.calls.filter(([, options]) => (options as RequestInit | undefined)?.method === method)
const chapters = [{ startSeconds: 0, title: 'Introduction' }, { startSeconds: 150, title: 'The main idea' }, { startSeconds: 600, title: 'Summary' }]

describe('ChapterList', () => {
  beforeEach(() => { request.mockReset() })

  it('shows the outline, marks the part playing now and goes to a part when it is clicked', async () => {
    request.mockResolvedValue(chapters)
    const onSeek = vi.fn()
    const { rerender } = render(<ChapterList videoId="v1" time={10} onSeek={onSeek} />)
    const list = await screen.findByRole('region', { name: 'Chapters' })
    expect(within(list).getAllByRole('button').map((item) => item.textContent)).toEqual(['0:00Introduction', '2:30The main idea', '10:00Summary'])
    expect(within(list).getByRole('button', { name: /Introduction/ })).toHaveAttribute('aria-current', 'true')
    rerender(<ChapterList videoId="v1" time={200} onSeek={onSeek} />)
    expect(within(list).getByRole('button', { name: /The main idea/ })).toHaveAttribute('aria-current', 'true')
    expect(within(list).getByRole('button', { name: /Introduction/ })).not.toHaveAttribute('aria-current')
    await userEvent.click(within(list).getByRole('button', { name: /Summary/ }))
    expect(onSeek).toHaveBeenCalledWith(600)
  })

  it('shows nothing for a video without chapters or when they cannot be read', async () => {
    request.mockResolvedValue([])
    const { container, unmount } = render(<ChapterList videoId="v1" time={0} onSeek={vi.fn()} />)
    await waitFor(() => expect(request).toHaveBeenCalled())
    expect(container).toBeEmptyDOMElement()
    unmount()
    request.mockRejectedValue(new ApiError('no', 404))
    const failed = render(<ChapterList videoId="v1" time={0} onSeek={vi.fn()} />)
    await waitFor(() => expect(request).toHaveBeenCalledTimes(2))
    expect(failed.container).toBeEmptyDOMElement()
  })
})

describe('VideoNotes', () => {
  const note = (id: string, positionSeconds: number, text: string) => ({ id, positionSeconds, text, createdAtUtc: `2030-01-01T10:0${id}:00Z`, updatedAtUtc: '2030-01-01T10:00:00Z' })
  beforeEach(() => { request.mockReset() })

  it('lists my notes in the order of the video and jumps to one', async () => {
    request.mockResolvedValue([note('1', 90, 'Check the second example'), note('2', 10, '')])
    const onSeek = vi.fn()
    render(<VideoNotes videoId="v1" time={0} onSeek={onSeek} />)
    const list = await screen.findByRole('region', { name: 'My notes' })
    await within(list).findByText('Check the second example')
    expect(within(list).getAllByRole('listitem').map((item) => item.textContent?.slice(0, 5))).toEqual(['0:10B', '1:30C'])
    expect(within(list).getByText('Bookmark')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: 'Go to 1:30' }))
    expect(onSeek).toHaveBeenCalledWith(90)
  })

  it('adds a note at the moment the video is at, and a plain bookmark with no words', async () => {
    request.mockImplementation((_path: string, options?: RequestInit) => options?.method === 'POST'
      ? Promise.resolve(note('3', JSON.parse(options.body as string).positionSeconds, JSON.parse(options.body as string).text)) : Promise.resolve([]))
    render(<VideoNotes videoId="v1" time={75.8} onSeek={vi.fn()} />)
    await screen.findByText('No notes yet.')
    await userEvent.type(screen.getByLabelText('Note'), '  Remember this  ')
    await userEvent.click(screen.getByRole('button', { name: 'Add note at 1:15' }))
    await screen.findByText('Remember this')
    expect(JSON.parse((calls('POST')[0][1] as RequestInit).body as string)).toEqual({ positionSeconds: 75, text: 'Remember this' })
    expect(screen.getByLabelText('Note')).toHaveValue('')
    await userEvent.click(screen.getByRole('button', { name: 'Add note at 1:15' }))
    await waitFor(() => expect(calls('POST')).toHaveLength(2))
    expect(JSON.parse((calls('POST')[1][1] as RequestInit).body as string).text).toBe('')
  })

  it('edits and deletes a note', async () => {
    request.mockImplementation((path: string, options?: RequestInit) => {
      if (options?.method === 'PUT') return Promise.resolve(note('1', 90, JSON.parse(options.body as string).text))
      if (options?.method === 'DELETE') return Promise.resolve(null)
      return Promise.resolve([note('1', 90, 'old words')])
    })
    render(<VideoNotes videoId="v1" time={0} onSeek={vi.fn()} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Edit the note at 1:30' }))
    const box = screen.getByLabelText('Edit note')
    await userEvent.clear(box)
    await userEvent.type(box, 'new words')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await screen.findByText('new words')
    expect(calls('PUT')[0][0]).toBe('/api/v1/tenant/videos/v1/notes/1')
    await userEvent.click(screen.getByRole('button', { name: 'Delete the note at 1:30' }))
    await screen.findByText('No notes yet.')
    expect(calls('DELETE')).toHaveLength(1)
  })

  it('shows what went wrong', async () => {
    request.mockImplementation((_path: string, options?: RequestInit) => options?.method === 'POST' ? Promise.reject(new ApiError('You can keep at most 300 notes on one video.', 400)) : Promise.resolve([]))
    render(<VideoNotes videoId="v1" time={5} onSeek={vi.fn()} />)
    await userEvent.click(await screen.findByRole('button', { name: /Add note/ }))
    expect(await screen.findByRole('alert')).toHaveTextContent('at most 300 notes')
  })
})

describe('ChaptersEditor', () => {
  const video = { id: 'v1', type: 'Uploaded', durationSeconds: 700 } as VideoItem
  beforeEach(() => { request.mockReset() })

  it('shows the chapters as lines, checks what is typed and saves them', async () => {
    request.mockImplementation((_path: string, options?: RequestInit) => options?.method === 'PUT' ? Promise.resolve(JSON.parse(options.body as string).chapters) : Promise.resolve([chapters[0]]))
    render(<ChaptersEditor video={video} />)
    const box = await screen.findByLabelText(/^Chapters/)
    await waitFor(() => expect(box).toHaveValue('0:00 Introduction'))
    await userEvent.clear(box)
    await userEvent.type(box, '0:10 Late start')
    await userEvent.click(screen.getByRole('button', { name: 'Save chapters' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The first chapter must start at 0:00.')
    expect(calls('PUT')).toHaveLength(0)

    await userEvent.clear(box)
    await userEvent.type(box, '0:00 Introduction{enter}12:00 Too late')
    await userEvent.click(screen.getByRole('button', { name: 'Save chapters' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('after the video ends (11:40)')
    expect(calls('PUT')).toHaveLength(0)

    await userEvent.clear(box)
    await userEvent.type(box, '2:30 The main idea{enter}0:00 Introduction')
    await userEvent.click(screen.getByRole('button', { name: 'Save chapters' }))
    expect(await screen.findByText('Saved 2 chapters.')).toBeInTheDocument()
    expect(JSON.parse((calls('PUT')[0][1] as RequestInit).body as string).chapters).toEqual([{ startSeconds: 0, title: 'Introduction' }, { startSeconds: 150, title: 'The main idea' }])
    expect(box).toHaveValue('0:00 Introduction\n2:30 The main idea')
  })

  it('clears the outline when emptied, and passes on the server message when saving is refused', async () => {
    request.mockImplementation((_path: string, options?: RequestInit) => options?.method === 'PUT' ? Promise.reject(new ApiError('Two chapters cannot start at the same second.', 400)) : Promise.resolve([chapters[0]]))
    render(<ChaptersEditor video={video} />)
    const box = await screen.findByLabelText(/^Chapters/)
    await waitFor(() => expect(box).toHaveValue('0:00 Introduction'))
    await userEvent.click(screen.getByRole('button', { name: 'Save chapters' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('same second')
  })

  it('says linked videos cannot have chapters', () => {
    render(<ChaptersEditor video={{ ...video, type: 'External' }} />)
    expect(screen.getByText(/not on linked videos/)).toBeInTheDocument()
  })
})
