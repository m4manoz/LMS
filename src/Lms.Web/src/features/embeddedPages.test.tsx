import { render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AssessmentsPage from './AssessmentsPage'
import AssignmentsPage from './AssignmentsPage'
import ForumsPage from './ForumsPage'
import LiveClassesPage from './LiveClassesPage'
import MessagesPage from './MessagesPage'
import VideoLibraryPage from './VideoLibraryPage'
import AssessmentResults, { resultLabel } from './AssessmentResults'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => ({ ...(await vi.importActual<typeof import('@/lib/api')>('@/lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))
vi.mock('../lib/api', async () => ({ ...(await vi.importActual<typeof import('../lib/api')>('../lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't', user: { id: 'me', displayName: 'Ada' } } }) }))
vi.mock('../lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't', user: { id: 'me', displayName: 'Ada' } } }) }))
vi.mock('@/lib/pieceUpload', () => ({ uploadVideoInPieces: vi.fn() }))

const learner = ['course.read', 'assessment.read', 'assessment.attempt', 'liveclass.read', 'collaboration.read', 'collaboration.manage', 'grade.read']
const paths = () => request.mock.calls.map(([path]) => String(path))

describe('pages shown inside one course', () => {
  beforeEach(() => {
    request.mockReset(); permissions = learner
    request.mockImplementation(() => Promise.resolve([]))
  })

  it('assessments: reads only this course, has no course picker or page title, and shows my results', async () => {
    render(<AssessmentsPage embedded courseId="c1" />)
    await waitFor(() => expect(paths()).toContain('/api/v1/tenant/courses/c1/assessments'))
    expect(paths()).toContain('/api/v1/tenant/courses/c1/assessment-results')
    expect(paths()).not.toContain('/api/v1/tenant/courses')                         // no list of courses to choose from
    expect(screen.queryByLabelText('Course')).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Assessments' })).toBeNull()
    expect(await screen.findByRole('region', { name: 'My results' })).toBeInTheDocument()
  })

  it('assignments: reads only this course and has no course picker or page title', async () => {
    render(<AssignmentsPage embedded courseId="c1" />)
    await waitFor(() => expect(paths()).toContain('/api/v1/tenant/assignments?courseId=c1'))
    expect(paths()).not.toContain('/api/v1/tenant/assignments')
    expect(screen.queryByLabelText('Course')).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Assignments' })).toBeNull()
  })

  it('forum: reads only this course and has no course picker or page title', async () => {
    permissions = [...learner]
    render(<ForumsPage embedded courseId="c1" />)
    await waitFor(() => expect(paths()).toContain('/api/v1/tenant/community/threads?courseId=c1'))
    expect(screen.queryByLabelText('Course')).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Forums' })).toBeNull()
    expect(await screen.findByRole('button', { name: /Start a discussion/ })).toBeInTheDocument()   // still possible to post
  })

  it('videos: reads only this course and has no course picker or page title', async () => {
    render(<VideoLibraryPage embedded courseId="c1" />)
    await waitFor(() => expect(paths()).toContain('/api/v1/tenant/videos?courseId=c1'))
    expect(screen.queryByLabelText('Filter by course')).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Video library' })).toBeNull()
  })

  it('live classes: asks for this course only and has no page title', async () => {
    render(<LiveClassesPage embedded courseId="c1" />)
    await waitFor(() => expect(paths()).toContain('/api/v1/tenant/live-classes/sessions?courseId=c1'))
    expect(screen.queryByRole('heading', { name: 'Live classes' })).toBeNull()
    expect(screen.getByRole('button', { name: /Refresh/ })).toBeInTheDocument()
  })

  it('course chat: opens this course\'s chat and shows no inbox or page title', async () => {
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path.endsWith('/messages/conversations/course') && options?.method === 'POST') return Promise.resolve({ id: 'conv1' })
      if (path.endsWith('/messages/conversations')) return Promise.resolve([{ id: 'conv1', kind: 'Course', title: 'Algebra chat', lastActivityAtUtc: '2030-01-01T10:00:00Z', lastMessagePreview: null, unreadCount: 0 }])
      return Promise.resolve([])
    })
    render(<MessagesPage courseId="c1" />)
    await waitFor(() => expect(request.mock.calls.some(([path, options]) => String(path).endsWith('/messages/conversations/course') && (options as { body?: string })?.body === JSON.stringify({ courseId: 'c1' }))).toBe(true))
    expect(await screen.findByText('Everyone enrolled in this course and its teachers can read this chat.')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Messages' })).toBeNull()
  })

  it('on their own the same pages still ask for everything and keep their picker', async () => {
    render(<AssignmentsPage />)
    await waitFor(() => expect(paths()).toContain('/api/v1/tenant/assignments'))
    expect(screen.getByLabelText('Course')).toBeInTheDocument()
  })
})

describe('my assessment results', () => {
  const attempt = (over: Record<string, unknown> = {}) => ({ id: 'a1', attemptNumber: 1, status: 'Graded', scorePoints: 8, possiblePoints: 10, percentage: 80, startedAtUtc: '2030-01-01T10:00:00Z', submittedAtUtc: '2030-01-01T10:20:00Z', gradedAtUtc: '2030-01-01T11:00:00Z', ...over })
  beforeEach(() => request.mockReset())

  it('says where an attempt stands', () => {
    expect(resultLabel(attempt() as never)).toBe('8 / 10 · 80%')
    expect(resultLabel(attempt({ status: 'InProgress', percentage: null }) as never)).toBe('In progress')
    expect(resultLabel(attempt({ status: 'Submitted', percentage: null }) as never)).toBe('Waiting to be marked')
  })

  it('lists attempts with their assessment, and says kindly when there are none', async () => {
    request.mockResolvedValueOnce([{ assessmentId: 'x', assessmentTitle: 'Quiz 1', attempt: attempt() }, { assessmentId: 'x', assessmentTitle: 'Quiz 1', attempt: attempt({ id: 'a2', attemptNumber: 2, status: 'InProgress', percentage: null }) }])
    const { unmount } = render(<AssessmentResults courseId="c1" />)
    expect(await screen.findAllByText('Quiz 1')).toHaveLength(2)
    expect(screen.getByText('8 / 10 · 80%')).toBeInTheDocument()
    expect(screen.getAllByText('In progress').length).toBeGreaterThan(0)
    unmount()
    request.mockResolvedValueOnce([])
    render(<AssessmentResults courseId="c1" />)
    expect(await screen.findByText('You have not taken an assessment in this course yet.')).toBeInTheDocument()
  })
})
