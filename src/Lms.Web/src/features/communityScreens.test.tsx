import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LearningPathsPage from './LearningPathsPage'
import ResourcesPage from './ResourcesPage'
import AnnouncementsPage from './AnnouncementsPage'
import ForumsPage from './ForumsPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't', user: { id: 'u1' } } }) }))

const courses = [{ id: 'c1', code: 'MATH-1', title: 'Algebra' }, { id: 'c2', code: 'BIO-1', title: 'Biology' }]
const posts = () => request.mock.calls.filter(([, init]) => init?.method === 'POST')

function route(table: Record<string, unknown>) {
  request.mockImplementation(async (url: string, init?: { method?: string }) => {
    const key = `${init?.method ?? 'GET'} ${url}`
    if (key in table) return table[key]
    return []
  })
}

beforeEach(() => { request.mockReset(); permissions = []; vi.restoreAllMocks() })

describe('LearningPathsPage', () => {
  const paths = [{ id: 'p1', title: 'Maths track', description: 'Start here', courses: [courses[0]] }]
  beforeEach(() => { permissions = ['course.manage']; route({ 'GET /api/v1/tenant/catalog/paths': paths, 'GET /api/v1/tenant/courses': courses }) })

  it('lists rows and opens details with an accent', async () => {
    render(<LearningPathsPage />)
    await screen.findByText('Maths track')
    expect(screen.queryByRole('dialog')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'View details for Maths track' }))
    expect(screen.getByRole('dialog', { name: 'Learning path details' })).toBeInTheDocument()
    expect(screen.getByText('Algebra')).toBeInTheDocument()
  })

  it('marks required fields, blocks an empty submit and sends the same body', async () => {
    render(<LearningPathsPage />)
    await screen.findByText('Maths track')
    await userEvent.click(screen.getByRole('button', { name: /New path/ }))
    expect(screen.getByText('Title')).toHaveClass('after:text-red-500')
    await userEvent.click(screen.getByRole('button', { name: 'Create path' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/title/i)
    expect(posts()).toHaveLength(0)
    await userEvent.type(screen.getByLabelText('Title'), 'New track')
    await userEvent.click(screen.getByRole('button', { name: 'Create path' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/course/i)
    await userEvent.click(screen.getByLabelText(/Algebra/))
    await userEvent.click(screen.getByRole('button', { name: 'Create path' }))
    await waitFor(() => expect(posts()).toHaveLength(1))
    expect(posts()[0][0]).toBe('/api/v1/tenant/catalog/paths')
    expect(JSON.parse(posts()[0][1].body)).toEqual({ title: 'New track', description: '', courseIds: ['c1'] })
  })

  it('hides New path without permission', async () => {
    permissions = []
    render(<LearningPathsPage />)
    await screen.findByText('Maths track')
    expect(screen.queryByRole('button', { name: /New path/ })).toBeNull()
  })
})

describe('ResourcesPage', () => {
  const items = [{ id: 'r1', type: 'Link', title: 'Khan', description: 'Videos', url: 'https://khan.org', uploadedAtUtc: '2026-01-01T00:00:00Z' }]
  beforeEach(() => { permissions = ['course.manage']; route({ 'GET /api/v1/tenant/catalog/resources': items }) })

  it('lists rows and deletes after confirmation', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<ResourcesPage />)
    await screen.findByText('Khan')
    await userEvent.click(screen.getByRole('button', { name: 'Delete resource Khan' }))
    await waitFor(() => expect(request).toHaveBeenCalledWith('/api/v1/tenant/catalog/resources/r1', { method: 'DELETE' }))
  })

  it('marks required fields, blocks an empty submit and sends the same body', async () => {
    render(<ResourcesPage />)
    await screen.findByText('Khan')
    await userEvent.click(screen.getByRole('button', { name: /Add resource/ }))
    expect(screen.getByText('Title')).toHaveClass('after:text-red-500')
    expect(screen.getByText('URL')).toHaveClass('after:text-red-500')
    const submit = within(screen.getByRole('dialog')).getByRole('button', { name: 'Add resource' })
    await userEvent.click(submit)
    expect(await screen.findByRole('alert')).toHaveTextContent(/title/i)
    expect(posts()).toHaveLength(0)
    await userEvent.type(screen.getByLabelText('Title'), 'Docs')
    await userEvent.type(screen.getByLabelText('URL'), 'https://x.org')
    await userEvent.click(submit)
    await waitFor(() => expect(posts()).toHaveLength(1))
    expect(JSON.parse(posts()[0][1].body)).toEqual({ type: 'Link', title: 'Docs', description: '', url: 'https://x.org' })
  })
})

describe('AnnouncementsPage', () => {
  const items = [{ id: 'a1', title: 'Exam week', body: 'Bring a pen', isPinned: true, courseId: null, courseTitle: null, authorName: 'Ada', createdAtUtc: '2026-01-01T00:00:00Z', expiresAtUtc: null }]
  beforeEach(() => { permissions = ['announcement.manage']; route({ 'GET /api/v1/tenant/community/announcements': items, 'GET /api/v1/tenant/courses': courses }) })

  it('lists rows and shows the message in the details panel', async () => {
    render(<AnnouncementsPage />)
    await screen.findByText('Exam week')
    expect(screen.queryByText('Bring a pen')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'View details for Exam week' }))
    expect(screen.getByText('Bring a pen')).toBeInTheDocument()
  })

  it('marks required fields, blocks an empty submit and sends the same body', async () => {
    render(<AnnouncementsPage />)
    await screen.findByText('Exam week')
    await userEvent.click(screen.getByRole('button', { name: /New announcement/ }))
    expect(screen.getByText('Title')).toHaveClass('after:text-red-500')
    expect(screen.getByText('Message', { selector: 'label' })).toHaveClass('after:text-red-500')
    await userEvent.click(screen.getByRole('button', { name: 'Publish' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/title/i)
    expect(posts()).toHaveLength(0)
    await userEvent.type(screen.getByLabelText('Title'), 'Hello')
    await userEvent.type(screen.getByLabelText('Message'), 'World')
    await userEvent.click(screen.getByRole('button', { name: 'Publish' }))
    await waitFor(() => expect(posts()).toHaveLength(1))
    expect(JSON.parse(posts()[0][1].body)).toEqual({ title: 'Hello', body: 'World', courseId: null, isPinned: false, expiresAtUtc: null })
  })
})

describe('ForumsPage', () => {
  const threads = [{ id: 't1', courseId: null, courseTitle: null, title: 'Help with limits', authorName: 'Ada', isPinned: false, isLocked: false, replyCount: 1, createdAtUtc: '2026-01-01T00:00:00Z', lastActivityAtUtc: '2026-01-02T00:00:00Z' }]
  const detail = { id: 't1', courseId: null, title: 'Help with limits', body: 'How do limits work?', authorUserId: 'u2', authorName: 'Ada', isPinned: false, isLocked: false, createdAtUtc: '2026-01-01T00:00:00Z', replies: [{ id: 'x1', authorUserId: 'u3', authorName: 'Bo', body: 'Read chapter 2', createdAtUtc: '2026-01-02T00:00:00Z' }] }
  beforeEach(() => {
    permissions = ['collaboration.manage']
    route({ 'GET /api/v1/tenant/community/threads': threads, 'GET /api/v1/tenant/community/threads/t1': detail, 'GET /api/v1/tenant/courses': courses })
  })

  it('lists rows and opens a discussion in the panel', async () => {
    render(<ForumsPage />)
    await screen.findByText('Help with limits')
    await userEvent.click(screen.getByRole('button', { name: 'View details for Help with limits' }))
    const panel = await screen.findByRole('dialog', { name: 'Discussion details' })
    expect(within(panel).getByText('How do limits work?')).toBeInTheDocument()
    expect(within(panel).getByText('Read chapter 2')).toBeInTheDocument()
  })

  it('blocks an empty reply and sends the same reply body', async () => {
    render(<ForumsPage />)
    await screen.findByText('Help with limits')
    await userEvent.click(screen.getByRole('button', { name: 'View details for Help with limits' }))
    await screen.findByRole('dialog', { name: 'Discussion details' })
    expect(screen.getByText('Your reply')).toHaveClass('after:text-red-500')
    await userEvent.click(screen.getByRole('button', { name: 'Post reply' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/reply/i)
    expect(posts()).toHaveLength(0)
    await userEvent.type(screen.getByLabelText('Your reply'), 'Thanks')
    await userEvent.click(screen.getByRole('button', { name: 'Post reply' }))
    await waitFor(() => expect(posts()).toHaveLength(1))
    expect(posts()[0][0]).toBe('/api/v1/tenant/community/threads/t1/replies')
    expect(JSON.parse(posts()[0][1].body)).toEqual({ body: 'Thanks' })
  })

  it('starts a discussion with validation and the same request body', async () => {
    render(<ForumsPage />)
    await screen.findByText('Help with limits')
    await userEvent.click(screen.getByRole('button', { name: /Start a discussion/ }))
    expect(screen.getByText('Title')).toHaveClass('after:text-red-500')
    await userEvent.click(screen.getByRole('button', { name: 'Post discussion' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/title/i)
    expect(posts()).toHaveLength(0)
    request.mockImplementation(async (url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return { id: 't1' }
      if (url === '/api/v1/tenant/community/threads/t1') return detail
      if (url === '/api/v1/tenant/courses') return courses
      return threads
    })
    await userEvent.type(screen.getByLabelText('Title'), 'Hi')
    await userEvent.type(screen.getByLabelText('Message'), 'There')
    await userEvent.click(screen.getByRole('button', { name: 'Post discussion' }))
    await waitFor(() => expect(posts()).toHaveLength(1))
    expect(JSON.parse(posts()[0][1].body)).toEqual({ courseId: null, title: 'Hi', body: 'There' })
    await screen.findByRole('dialog', { name: 'Discussion details' })
  })
})
