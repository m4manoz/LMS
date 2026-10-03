import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AssignmentsPage from './AssignmentsPage'

const assignment = {
  id: 'a1', courseId: 'c1', courseTitle: 'English', title: 'Essay one', instructions: 'Write 300 words.', maxPoints: 50,
  dueAtUtc: null, allowLate: false, latePenaltyPercent: 0, status: 'Published', submissionCount: 0, gradedCount: 0, mySubmission: null,
}
const course = { id: 'c1', code: 'ENG-1', title: 'English', status: 'Published' }

const request = vi.fn()
let permissions: string[] = []

vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args), downloadFile: vi.fn() }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, user: { id: 'u1' } } }) }))

beforeEach(() => {
  request.mockReset()
  request.mockImplementation((path: string) => Promise.resolve(path.includes('/courses') ? [course] : path.includes('/submissions') ? [] : [assignment]))
})

const posts = (fragment: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && call[1]?.method === 'POST')

describe('AssignmentsPage', () => {
  it('lists assignments as rows, opens nothing by default, and shows the submission form for a learner', async () => {
    permissions = ['assessment.read', 'assessment.attempt']
    render(<AssignmentsPage />)
    expect(await screen.findByText('Essay one')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.queryByRole('button', { name: 'New assignment' })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'View details for Essay one' }))
    expect(await screen.findByRole('dialog', { name: 'Assignment details' })).toBeInTheDocument()
    expect(await screen.findByText('Write 300 words.')).toBeInTheDocument()
    expect(screen.getByLabelText('Written response')).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: 'Grading' })).not.toBeInTheDocument()
  })

  it('shows a Grading tab to graders but no submission form', async () => {
    permissions = ['assessment.read', 'assessment.manage', 'grade.manage']
    render(<AssignmentsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Essay one' }))
    expect(screen.getByRole('tab', { name: 'Grading' })).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: 'New assignment' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Written response')).not.toBeInTheDocument()
  })

  it('hides the submission form once the deadline has passed and late work is not accepted', async () => {
    permissions = ['assessment.read', 'assessment.attempt']
    request.mockImplementation((path: string) => Promise.resolve(path.includes('/courses') ? [] : [{ ...assignment, dueAtUtc: '2020-01-01T00:00:00Z' }]))
    render(<AssignmentsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Essay one' }))
    expect(await screen.findByText('Past due')).toBeInTheDocument()
    expect(screen.queryByLabelText('Written response')).not.toBeInTheDocument()
  })

  it('opens the New assignment panel, marks required fields and blocks an empty submit', async () => {
    permissions = ['assessment.read', 'assessment.manage']
    render(<AssignmentsPage />)
    await screen.findByText('Essay one')
    await userEvent.click(screen.getByRole('button', { name: 'New assignment' }))
    expect(await screen.findByRole('dialog', { name: 'New assignment' })).toBeInTheDocument()
    for (const name of ['Course', 'Title', 'Maximum points']) {
      const label = screen.getAllByText(name).find((element) => element.tagName === 'LABEL' && element.className.includes("after:content-['*'/'']"))
      expect(label).toBeTruthy()
    }
    await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose a course first.')
    expect(posts('/api/v1/tenant/assignments')).toHaveLength(0)
    await userEvent.selectOptions(screen.getByLabelText('Course', { selector: '#new-assignment-course' }), 'c1')
    await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a title.')
    expect(posts('/api/v1/tenant/assignments')).toHaveLength(0)
  })

  it('creates an assignment with the same request body as before', async () => {
    permissions = ['assessment.read', 'assessment.manage']
    render(<AssignmentsPage />)
    await screen.findByText('Essay one')
    await userEvent.click(screen.getByRole('button', { name: 'New assignment' }))
    await userEvent.selectOptions(screen.getByLabelText('Course', { selector: '#new-assignment-course' }), 'c1')
    await userEvent.type(screen.getByLabelText('Title'), 'Quiz prep')
    await userEvent.type(screen.getByLabelText('Instructions'), 'Read chapter 1')
    await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))
    await waitFor(() => expect(posts('/api/v1/tenant/assignments')).toHaveLength(1))
    expect(JSON.parse(posts('/api/v1/tenant/assignments')[0][1].body)).toEqual({
      courseId: 'c1', title: 'Quiz prep', instructions: 'Read chapter 1', maxPoints: 100, dueAtUtc: null, allowLate: false, latePenaltyPercent: 0,
    })
    expect(await screen.findByText(/Assignment created as a draft/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).toBeNull()
  })
})
