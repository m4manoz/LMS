import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LessonBlocks, { BlockView, formatBytes, hostOf, toParagraphs, type Block } from './LessonBlocks'

const request = vi.fn()
const failNext = { message: '' }
const fetchBlobUrl = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => (failNext.message ? Promise.reject(new actual.ApiError(failNext.message, 500)) : request(...args)), fetchBlobUrl: (...args: unknown[]) => fetchBlobUrl(...args), downloadFile: vi.fn() }
})

const block = (overrides: Partial<Block>): Block => ({
  id: 'b', type: 'Text', displayOrder: 1, title: null, text: null, language: null, url: null, caption: null, file: null, ...overrides,
})
const file = (contentType: string, name = 'file.bin') => ({ fileName: name, contentType, sizeBytes: 2048, downloadPath: '/api/v1/tenant/courses/c/assets/a' })

describe('helpers', () => {
  it('formats sizes and splits paragraphs on blank lines', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(2048)).toBe('2 KB')
    expect(formatBytes(5 * 1024 * 1024)).toBe('5.0 MB')
    expect(toParagraphs('One\n\nTwo\nstill two\n\n\n\nThree')).toEqual(['One', 'Two\nstill two', 'Three'])
    expect(hostOf('https://example.org/a/b')).toBe('example.org')
  })
})

describe('BlockView', () => {
  beforeEach(() => { fetchBlobUrl.mockReset(); request.mockReset() })

  it('shows text as plain text and never interprets HTML', () => {
    render(<BlockView block={block({ text: '<img src=x onerror=alert(1)>\n\nSecond paragraph' })} />)
    expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
    expect(screen.getByText('Second paragraph')).toBeInTheDocument()
  })

  it('shows code with its language', () => {
    render(<BlockView block={block({ type: 'Code', language: 'python', text: "print('hi')" })} />)
    expect(screen.getByText('python')).toBeInTheDocument()
    expect(screen.getByText("print('hi')")).toBeInTheDocument()
  })

  it('opens links safely in a new tab', () => {
    render(<BlockView block={block({ type: 'Link', title: 'Docs', url: 'https://example.org/docs' })} />)
    const link = screen.getByRole('link', { name: /Docs/ })
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
  })

  it('sandboxes embeds', () => {
    render(<BlockView block={block({ type: 'Embed', title: 'Intro video', url: 'https://www.youtube.com/embed/abc' })} />)
    const frame = screen.getByTitle('Intro video')
    expect(frame).toHaveAttribute('sandbox')
    expect(frame.getAttribute('sandbox')).not.toContain('allow-top-navigation')
  })

  it('streams media straight from storage when a signed link is available', async () => {
    request.mockResolvedValue({ url: 'https://objects.test/t/c/a.png?sig=1' })
    render(<BlockView block={block({ type: 'Image', caption: 'Chart', file: file('image/png', 'c.png') })} />)
    expect(await screen.findByAltText('Chart')).toHaveAttribute('src', 'https://objects.test/t/c/a.png?sig=1')
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/courses/c/assets/a/link')
    expect(fetchBlobUrl).not.toHaveBeenCalled()
  })

  it('falls back to the authenticated fetch when storage has no direct links', async () => {
    request.mockResolvedValue({ url: null })
    fetchBlobUrl.mockResolvedValue({ url: 'blob:fallback', contentType: 'image/png' })
    render(<BlockView block={block({ type: 'Image', caption: 'Chart', file: file('image/png', 'c.png') })} />)
    expect(await screen.findByAltText('Chart')).toHaveAttribute('src', 'blob:fallback')
  })

  it('asks for a fresh link when an expired one stops working', async () => {
    request.mockResolvedValueOnce({ url: 'https://objects.test/old' }).mockResolvedValueOnce({ url: 'https://objects.test/new' })
    render(<BlockView block={block({ type: 'Video', title: 'Lecture', file: file('video/mp4', 'l.mp4') })} />)
    const video = await vi.waitFor(() => { const element = document.querySelector('video'); if (!element) throw new Error('no video'); return element })
    expect(video).toHaveAttribute('src', 'https://objects.test/old')
    video.dispatchEvent(new Event('error'))
    await vi.waitFor(() => expect(document.querySelector('video')).toHaveAttribute('src', 'https://objects.test/new'))
    expect(request).toHaveBeenCalledTimes(2)
  })

  it('loads an image through the authenticated fetch', async () => {
    request.mockResolvedValue({ url: null })
    fetchBlobUrl.mockResolvedValue({ url: 'blob:image', contentType: 'image/png' })
    render(<BlockView block={block({ type: 'Image', caption: 'A diagram', file: file('image/png', 'd.png') })} />)
    expect(await screen.findByAltText('A diagram')).toHaveAttribute('src', 'blob:image')
    expect(fetchBlobUrl).toHaveBeenCalledWith('/api/v1/tenant/courses/c/assets/a')
  })

  it('never renders an unexpected file type inline, only offers a download', () => {
    render(<BlockView block={block({ type: 'Image', file: file('text/html', 'page.html') })} />)
    expect(fetchBlobUrl).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: /Download page\.html/ })).toBeInTheDocument()
    expect(document.querySelector('img, iframe, video')).toBeNull()
  })

  it('offers downloads for download blocks without fetching them', () => {
    render(<BlockView block={block({ type: 'Download', file: file('application/zip', 'bundle.zip') })} />)
    expect(screen.getByRole('button', { name: /Download bundle\.zip \(2 KB\)/ })).toBeInTheDocument()
    expect(fetchBlobUrl).not.toHaveBeenCalled()
  })

  it('shows a clear message when a file cannot be loaded', async () => {
    request.mockResolvedValue({ url: null })
    fetchBlobUrl.mockRejectedValue(new Error('boom'))
    render(<BlockView block={block({ type: 'Video', file: file('video/mp4', 'v.mp4') })} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be loaded')
  })
})

describe('LessonBlocks', () => {
  beforeEach(() => request.mockReset())

  it('loads the blocks of a lesson in order', async () => {
    request.mockResolvedValue([block({ id: '1', text: 'First' }), block({ id: '2', type: 'Code', text: 'x = 1' })])
    render(<LessonBlocks courseId="c" lessonId="l" />)
    expect(await screen.findByText('First')).toBeInTheDocument()
    expect(screen.getByText('x = 1')).toBeInTheDocument()
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/courses/c/lessons/l/blocks')
  })

  it('shows the empty message only when asked to', async () => {
    request.mockResolvedValue([])
    render(<LessonBlocks courseId="c" lessonId="l" emptyText="Nothing here yet." />)
    expect(await screen.findByText('Nothing here yet.')).toBeInTheDocument()
  })

  it('shows nothing extra for an empty lesson when no message is given', async () => {
    request.mockResolvedValue([])
    const { container } = render(<LessonBlocks courseId="c" lessonId="l" />)
    await screen.findByText('Loading lesson content…').catch(() => undefined)
    await vi.waitFor(() => expect(container).toBeEmptyDOMElement())
  })

  it('reports a load failure', async () => {
    failNext.message = 'Not available.'
    try {
      render(<LessonBlocks courseId="c" lessonId="l" />)
      expect(await screen.findByRole('alert')).toHaveTextContent('Not available.')
    } finally { failNext.message = '' }
  })
})
