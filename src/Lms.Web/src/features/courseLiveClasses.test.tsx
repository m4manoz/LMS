import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CourseLiveClasses, { arrangeClasses } from './CourseLiveClasses'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

const item = (over: Record<string, unknown> = {}) => ({ id: 'a', courseId: 'c1', title: 'Algebra', description: null, provider: 'livekit', startAtUtc: '2030-01-02T10:00:00Z', endAtUtc: '2030-01-02T11:00:00Z', status: 'Scheduled', ...over })

describe('arrangeClasses', () => {
  it('keeps only this course, drops cancelled classes, and orders on now, coming up (soonest first), then earlier (latest first)', () => {
    const arranged = arrangeClasses([
      item({ id: 'late', startAtUtc: '2030-03-01T10:00:00Z' }), item({ id: 'soon', startAtUtc: '2030-02-01T10:00:00Z' }),
      item({ id: 'live', status: 'Live' }), item({ id: 'old1', status: 'Completed', startAtUtc: '2029-01-01T10:00:00Z' }), item({ id: 'old2', status: 'Completed', startAtUtc: '2029-06-01T10:00:00Z' }),
      item({ id: 'gone', status: 'Cancelled' }), item({ id: 'other', courseId: 'c2' }), item({ id: 'nocourse', courseId: null }),
    ] as never, 'c1')
    expect(arranged.now.map((x) => x.id)).toEqual(['live'])
    expect(arranged.upcoming.map((x) => x.id)).toEqual(['soon', 'late'])
    expect(arranged.past.map((x) => x.id)).toEqual(['old2', 'old1'])
  })
})

describe('CourseLiveClasses', () => {
  beforeEach(() => { request.mockReset(); permissions = ['liveclass.read'] })

  it('lists the classes of the course with their state and tool, and opens one', async () => {
    request.mockResolvedValue([item({ id: 'a', title: 'Algebra', status: 'Live' }), item({ id: 'b', title: 'Geometry', provider: 'jitsi' }), item({ id: 'c', title: 'Old class', status: 'Completed', startAtUtc: '2029-01-01T10:00:00Z' }), item({ id: 'x', title: 'Other course', courseId: 'c2' })])
    const open = vi.fn()
    render(<CourseLiveClasses courseId="c1" onOpen={open} />)
    const now = await screen.findByRole('region', { name: 'On now' })
    expect(within(now).getByText('Algebra')).toBeInTheDocument()
    expect(within(now).getByText('Live')).toBeInTheDocument()
    expect(within(now).getByText('In this app')).toBeInTheDocument()
    const next = screen.getByRole('region', { name: 'Coming up' })
    expect(within(next).getByText('Jitsi')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Earlier classes' })).toHaveTextContent('Old class')
    expect(screen.queryByText('Other course')).toBeNull()
    await userEvent.click(within(now).getByRole('button', { name: 'Open class Algebra' }))
    expect(open).toHaveBeenCalledWith('a')
    await userEvent.click(within(screen.getByRole('region', { name: 'Earlier classes' })).getByRole('button', { name: 'View details of Old class' }))
    expect(open).toHaveBeenCalledWith('c')
  })

  it('says there are none, and tells staff how to schedule one', async () => {
    request.mockResolvedValue([item({ courseId: 'c2' })])
    const { unmount } = render(<CourseLiveClasses courseId="c1" />)
    expect(await screen.findByText('No live classes for this course yet.')).toBeInTheDocument()
    unmount()
    render(<CourseLiveClasses courseId="c1" canSchedule />)
    expect(await screen.findByText(/Schedule one under Classes and learners/)).toBeInTheDocument()
  })

  it('does not ask for classes when the person may not see any, and shows a failure message', async () => {
    permissions = []
    const { unmount } = render(<CourseLiveClasses courseId="c1" />)
    expect(screen.getByText('Live classes are not available to your account.')).toBeInTheDocument()
    expect(request).not.toHaveBeenCalled()
    unmount()
    permissions = ['liveclass.read']
    const { ApiError } = await import('@/lib/api')
    request.mockImplementation(() => Promise.resolve().then(() => { throw new ApiError('Could not load.', 500) }))
    render(<CourseLiveClasses courseId="c1" />)
    expect(await screen.findByText('Could not load.')).toBeInTheDocument()
  })
})
