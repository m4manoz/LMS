import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import EnrollLearnersPage from './EnrollLearnersPage'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

const summary = { courseId: 'c', capacity: null, active: 0, completed: 0, freeSeats: null, justPromoted: 0, waitlist: [] }
const calls = (fragment: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment))

describe('EnrollLearnersPage', () => {
  beforeEach(() => { request.mockReset() })

  function serve(courses: unknown[]) {
    request.mockImplementation((path: string) => {
      if (path === '/api/v1/tenant/courses') return Promise.resolve(courses)
      if (path.includes('/enrollable-learners')) return Promise.resolve([{ userId: 'u1', name: 'Ada Learner', email: 'ada@x.test', role: 'Learner' }])
      if (path.endsWith('/enrollments')) return Promise.resolve([])
      return Promise.resolve(summary)
    })
  }

  it('starts on the first published course, with the people who can be added', async () => {
    serve([{ id: 'd1', code: 'DRAFT-1', title: 'Draft one', status: 'Draft' }, { id: 'p1', code: 'MATH-1', title: 'Algebra', status: 'Published' }, { id: 'a1', code: 'OLD-1', title: 'Old', status: 'Archived' }])
    render(<EnrollLearnersPage />)
    expect(await screen.findByRole('heading', { name: 'Enroll learners' })).toBeInTheDocument()
    expect(await screen.findByLabelText('Ada Learner', { exact: false })).toBeInTheDocument()
    const choice = screen.getByLabelText('Course') as HTMLSelectElement
    expect(choice.value).toBe('p1')                                                         // published first, not the draft
    expect(Array.from(choice.options).map((option) => option.textContent)).toEqual(['DRAFT-1 · Draft one (draft)', 'MATH-1 · Algebra'])   // archived courses are left out
    expect(calls('/courses/p1/enrollable-learners').length).toBeGreaterThan(0)
  })

  it('switches the panel to the course chosen, and explains a draft course cannot take learners', async () => {
    serve([{ id: 'p1', code: 'MATH-1', title: 'Algebra', status: 'Published' }, { id: 'd1', code: 'DRAFT-1', title: 'Draft one', status: 'Draft' }])
    render(<EnrollLearnersPage />)
    await screen.findByLabelText('Ada Learner', { exact: false })
    await userEvent.selectOptions(screen.getByLabelText('Course'), 'd1')
    expect(await screen.findByText(/Publish the course first/)).toBeInTheDocument()
    expect(calls('/courses/d1/enrollable-learners').length).toBeGreaterThan(0)
  })

  it('says so when there are no courses', async () => {
    serve([])
    render(<EnrollLearnersPage />)
    expect(await screen.findByText(/There are no courses yet/)).toBeInTheDocument()
  })
})
