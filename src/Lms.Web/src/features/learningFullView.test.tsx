import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LearningPage from './LearningPage'

const request = vi.fn()
vi.mock('@/lib/api', async () => ({ ...(await vi.importActual<typeof import('@/lib/api')>('@/lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { accessToken: 't', permissions: ['course.read'], user: { id: 'me', displayName: 'Ada' } } }) }))
vi.mock('./CourseRatingPanel', () => ({ default: () => null }))
vi.mock('@/components/LessonBlocks', () => ({ default: () => <p>Lesson content</p> }))

const course = { id: 'c1', code: 'MATH-1', title: 'Algebra', description: 'Numbers and symbols.', status: 'Published' }
const enrollment = { id: 'e1', courseId: 'c1', courseCode: 'MATH-1', courseTitle: 'Algebra', learnerUserId: 'me', status: 'Active', source: 'SelfService', progressPercent: 50, currentLessonId: null }
const player = {
  course, enrollment,
  modules: [{ id: 'm1', title: 'Basics', description: null, displayOrder: 1, lessons: [{ id: 'l1', title: 'What is a variable?', displayOrder: 1, status: 'NotStarted', positionSeconds: 0 }] }],
}

describe('opening an enrolled course', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path: string) => {
      if (path === '/api/v1/tenant/courses') return Promise.resolve([course])
      if (path === '/api/v1/tenant/enrollments') return Promise.resolve([enrollment])
      if (/\/learning(\?|$)/.test(path)) return Promise.resolve(player)
      return Promise.resolve([])
    })
  })

  it('shows the course over the whole window and closes back to the list', async () => {
    render(<LearningPage />)
    expect(screen.queryByRole('dialog')).toBeNull()
    await userEvent.click(await screen.findByRole('button', { name: /Algebra/ }))
    const view = await screen.findByRole('dialog', { name: 'Algebra' })
    expect(view.className).toContain('max-w-none')                                              // the whole width, not the narrow side panel
    await userEvent.click(within(view).getByRole('tab', { name: 'Lessons' }))
    expect(await within(view).findByRole('navigation', { name: 'Course lessons' })).toBeInTheDocument()
    expect(within(view).getByText('Numbers and symbols.')).toBeInTheDocument()

    await userEvent.click(within(view).getByRole('button', { name: 'Close panel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByText(/Choose a course to continue learning/)).toBeInTheDocument()
  })

  it('closes with the Escape key, and opening another course afterwards starts clean', async () => {
    render(<LearningPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Algebra/ }))
    await screen.findByRole('dialog', { name: 'Algebra' })
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    await userEvent.click(screen.getByRole('button', { name: /Algebra/ }))
    expect(await screen.findByRole('dialog', { name: 'Algebra' })).toBeInTheDocument()
  })

  it('does not show the course view until a course is chosen', async () => {
    render(<LearningPage />)
    await screen.findByText(/Choose a course to continue learning/)
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.queryByRole('navigation', { name: 'Course lessons' })).toBeNull()
  })
})
