import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CourseContentEditor, { blockFields, toBody } from './CourseContentEditor'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args), fetchBlobUrl: vi.fn(), downloadFile: vi.fn() }
})

const modules = [{ id: 'm1', title: 'Module 1', lessons: [{ id: 'l1', title: 'Lesson 1' }] }]
const blocks = [
  { id: 'a', type: 'Text', displayOrder: 1, title: null, text: 'First block', language: null, url: null, caption: null, file: null },
  { id: 'b', type: 'Text', displayOrder: 2, title: null, text: 'Second block', language: null, url: null, caption: null, file: null },
]

describe('block helpers', () => {
  it('asks only for the fields a block type needs', () => {
    expect(blockFields('Text')).toMatchObject({ text: true, file: false, url: false })
    expect(blockFields('Code')).toMatchObject({ text: true, language: true })
    expect(blockFields('Embed')).toMatchObject({ url: true, file: false })
    expect(blockFields('Image')).toMatchObject({ file: true, text: false })
    expect(blockFields('Image').accept).toContain('image/png')
  })

  it('sends text blocks as JSON and file blocks as multipart', () => {
    const json = toBody({ type: 'Text', title: 'T', text: 'Body', language: '', url: '', caption: '', file: null })
    expect(typeof json).toBe('string')
    expect(JSON.parse(json as string)).toMatchObject({ type: 'Text', text: 'Body' })
    const form = toBody({ type: 'Image', title: 'Pic', text: '', language: '', url: '', caption: 'cap', file: new File(['x'], 'a.png', { type: 'image/png' }) })
    expect(form).toBeInstanceOf(FormData)
    expect((form as FormData).get('type')).toBe('Image')
    expect((form as FormData).get('file')).toBeInstanceOf(File)
  })
})

describe('CourseContentEditor', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path?: string, options?: { method?: string }) => Promise.resolve(options?.method ? undefined : blocks))
  })

  it('guides the author to add a lesson first when there are none', () => {
    render(<CourseContentEditor courseId="c" modules={[]} editable />)
    expect(screen.getByText(/Add a module and a lesson/)).toBeInTheDocument()
  })

  it('lists blocks with move, edit and delete controls while the course is a draft', async () => {
    render(<CourseContentEditor courseId="c" modules={modules} editable />)
    expect(await screen.findByText('First block')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Move block 1 up' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Move block 2 down' })).toBeDisabled()
    expect(screen.getAllByRole('button', { name: 'Delete' })).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'Add block' })).toBeInTheDocument()
  })

  it('is read-only once the course is no longer a draft', async () => {
    render(<CourseContentEditor courseId="c" modules={modules} editable={false} />)
    expect(await screen.findByText('First block')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add block' })).not.toBeInTheDocument()
    expect(screen.getByText(/no longer a draft/)).toBeInTheDocument()
  })

  it('reorders by sending the full new order', async () => {
    render(<CourseContentEditor courseId="c" modules={modules} editable />)
    await userEvent.click(await screen.findByRole('button', { name: 'Move block 2 up' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/courses/c/lessons/l1/blocks/order', { method: 'PUT', body: JSON.stringify({ blockIds: ['b', 'a'] }) })
  })

  it('adds a text block and clears the form', async () => {
    render(<CourseContentEditor courseId="c" modules={modules} editable />)
    const box = await screen.findByLabelText('Text')
    await userEvent.type(box, 'Hello learners')
    await userEvent.click(screen.getByRole('button', { name: 'Add block' }))
    const post = request.mock.calls.find((call) => call[1]?.method === 'POST')!
    expect(post[0]).toBe('/api/v1/tenant/courses/c/lessons/l1/blocks')
    expect(JSON.parse(post[1].body)).toMatchObject({ type: 'Text', text: 'Hello learners' })
    expect(box).toHaveValue('')
  })

  it('marks required fields and blocks an empty text block', async () => {
    render(<CourseContentEditor courseId="c" modules={modules} editable />)
    await screen.findByText('First block')
    expect(document.querySelector('label[for="new-block-text"]')!.className).toContain("content-['*'/'']")
    expect(screen.getByText('Title (optional)').className).not.toContain("content-['*'/'']")
    await userEvent.click(screen.getByRole('button', { name: 'Add block' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter the text.')
    expect(request.mock.calls.some((call) => call[1]?.method === 'POST')).toBe(false)
  })

  it('requires a link for link blocks', async () => {
    render(<CourseContentEditor courseId="c" modules={modules} editable />)
    await userEvent.selectOptions(await screen.findByLabelText('Type'), 'Link')
    expect(document.querySelector('label[for="new-block-url"]')!.className).toContain("content-['*'/'']")
    await userEvent.click(screen.getByRole('button', { name: 'Add block' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter the link.')
  })

  it('edits a block with the same request as before and validates first', async () => {
    render(<CourseContentEditor courseId="c" modules={modules} editable />)
    await userEvent.click((await screen.findAllByRole('button', { name: 'Edit' }))[0])
    const box = screen.getByLabelText('Text', { selector: '#edit-a-text' })
    await userEvent.clear(box)
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter the text.')
    expect(request.mock.calls.some((call) => call[1]?.method === 'PUT')).toBe(false)
    await userEvent.type(box, 'Changed')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    const put = request.mock.calls.find((call) => call[1]?.method === 'PUT')!
    expect(put[0]).toBe('/api/v1/tenant/courses/c/lessons/l1/blocks/a')
    expect(JSON.parse(put[1].body)).toEqual({ title: '', text: 'Changed', language: '', url: '', caption: '' })
  })

  it('asks before deleting a block', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    render(<CourseContentEditor courseId="c" modules={modules} editable />)
    const [first] = await screen.findAllByRole('button', { name: 'Delete' })
    await userEvent.click(first)
    expect(request.mock.calls.some((call) => call[1]?.method === 'DELETE')).toBe(false)
    await userEvent.click(first)
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/courses/c/lessons/l1/blocks/a', { method: 'DELETE' })
    confirm.mockRestore()
  })

  it('needs a file before an image block can be added', async () => {
    render(<CourseContentEditor courseId="c" modules={modules} editable />)
    await userEvent.selectOptions(await screen.findByLabelText('Type'), 'Image')
    expect(screen.getByRole('button', { name: 'Add block' })).toBeDisabled()
    expect(screen.getByLabelText('File')).toHaveAttribute('accept', expect.stringContaining('image/png'))
  })
})
