import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AssessmentQuestionsPanel from './AssessmentQuestionsPanel'
import AssessmentsPage from './AssessmentsPage'
import { availability, fromLocalInput, toLocalInput, type AssessmentDetail } from '@/lib/assessments'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't' } }) }))

const calls = (method: string, fragment = '') => request.mock.calls.filter((call) => (call[1]?.method ?? 'GET') === method && String(call[0]).includes(fragment))
const bodyOf = (call: unknown[]) => JSON.parse((call[1] as { body: string }).body)
const hours = (n: number) => new Date(Date.now() + n * 3_600_000).toISOString()

beforeEach(() => { request.mockReset(); request.mockResolvedValue(null); permissions = [] })

describe('date helpers', () => {
  it('turn an ISO time into a form value and back to the same moment', () => {
    const iso = '2026-03-04T10:30:00.000Z'
    expect(new Date(fromLocalInput(toLocalInput(iso))!).getTime()).toBe(new Date(iso).getTime())
    expect(toLocalInput(null)).toBe('')
    expect(fromLocalInput('')).toBeNull()
    expect(fromLocalInput('not a date')).toBeNull()
  })

  it('says whether an assessment is open, not open yet or closed', () => {
    expect(availability({})).toBe('open')
    expect(availability({ opensAtUtc: hours(2) })).toBe('not-open')
    expect(availability({ opensAtUtc: hours(-2), dueAtUtc: hours(2) })).toBe('open')
    expect(availability({ dueAtUtc: hours(-1) })).toBe('closed')
  })
})

describe('AssessmentQuestionsPanel settings', () => {
  const detail = (over: Partial<AssessmentDetail['assessment']> = {}): AssessmentDetail => ({
    assessment: { id: 'as1', courseId: 'c1', title: 'Quiz', status: 'Draft', attemptLimit: 1, questionCount: 1, ...over }, questions: [], pools: [],
  })

  it('shows the saved dates and sends changed ones, or null when cleared', async () => {
    const opens = '2026-05-01T08:00:00.000Z'
    render(<AssessmentQuestionsPanel detail={detail({ opensAtUtc: opens })} onChanged={vi.fn().mockResolvedValue(undefined)} />)
    expect(screen.getByLabelText('Opens at')).toHaveValue(toLocalInput(opens))
    expect(screen.getByLabelText('Deadline')).toHaveValue('')
    fireChange(screen.getByLabelText('Deadline'), toLocalInput('2026-05-02T08:00:00.000Z'))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(calls('PUT', '/assessments/as1')).toHaveLength(1))
    const sent = bodyOf(calls('PUT', '/assessments/as1')[0])
    expect(new Date(sent.opensAtUtc).getTime()).toBe(new Date(opens).getTime())
    expect(new Date(sent.dueAtUtc).getTime()).toBe(new Date('2026-05-02T08:00:00.000Z').getTime())
  })

  it('refuses a deadline before the opening time without sending anything', async () => {
    render(<AssessmentQuestionsPanel detail={detail()} onChanged={vi.fn().mockResolvedValue(undefined)} />)
    fireChange(screen.getByLabelText('Opens at'), '2026-06-02T10:00')
    fireChange(screen.getByLabelText('Deadline'), '2026-06-01T10:00')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The deadline must be after the opening time.')
    expect(calls('PUT')).toHaveLength(0)
  })
})

describe('AssessmentsPage', () => {
  function serve(assessment: object) {
    request.mockImplementation((path: string) => {
      if (path === '/api/v1/tenant/courses') return Promise.resolve([{ id: 'c1', code: 'C1', title: 'Course', status: 'Published' }])
      if (path.endsWith('/assessments')) return Promise.resolve([assessment])
      return Promise.resolve({ assessment, questions: [], attemptsUsed: 0, effectiveAttemptLimit: 1 })
    })
  }
  const base = { id: 'as1', courseId: 'c1', title: 'Timed', status: 'Published', attemptLimit: 1, questionCount: 2 }
  const open = async () => { render(<AssessmentsPage />); await userEvent.click(await screen.findByRole('button', { name: 'View details for Timed' })) }

  it('tells a learner when it opens and will not let them start early', async () => {
    permissions = ['assessment.read', 'assessment.attempt']
    serve({ ...base, opensAtUtc: hours(5), dueAtUtc: hours(9) })
    await open()
    expect(await screen.findByText(/^Opens .*Closes /)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start attempt' })).toBeDisabled()
  })

  it('shows that it has closed and blocks starting', async () => {
    permissions = ['assessment.read', 'assessment.attempt']
    serve({ ...base, dueAtUtc: hours(-3) })
    await open()
    expect(await screen.findByText(/^Closed /)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start attempt' })).toBeDisabled()
  })

  it('lets a learner start while it is open and shows the deadline', async () => {
    permissions = ['assessment.read', 'assessment.attempt']
    serve({ ...base, dueAtUtc: hours(30) })
    await open()
    expect(await screen.findByText(/^Closes /)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start attempt' })).toBeEnabled()
  })

  it('sends the dates when an author creates an assessment, and leaves them out when empty', async () => {
    permissions = ['assessment.read', 'assessment.manage']
    serve(base)
    render(<AssessmentsPage />)
    await screen.findByText('Timed')
    await userEvent.click(screen.getByRole('button', { name: 'New assessment' }))
    await userEvent.type(screen.getByLabelText('Title'), 'Final')
    await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))
    await waitFor(() => expect(calls('POST', '/assessments')).toHaveLength(1))
    expect(bodyOf(calls('POST', '/assessments')[0])).toEqual({ title: 'Final', instructions: '', timeLimitMinutes: null, attemptLimit: 1 })

    await userEvent.click(screen.getByRole('button', { name: 'New assessment' }))
    await userEvent.type(screen.getByLabelText('Title'), 'Dated')
    fireChange(screen.getByLabelText('Deadline'), '2026-08-01T17:00')
    await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))
    await waitFor(() => expect(calls('POST', '/assessments')).toHaveLength(2))
    expect(new Date(bodyOf(calls('POST', '/assessments')[1]).dueAtUtc).getTime()).toBe(new Date('2026-08-01T17:00').getTime())
  })
})

/** datetime-local fields do not take typed text well in jsdom, so set the value the way the browser would. */
function fireChange(element: HTMLElement, value: string) {
  const input = element as HTMLInputElement
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  setter.call(input, value)
  input.dispatchEvent(new Event('input', { bubbles: true }))
}
