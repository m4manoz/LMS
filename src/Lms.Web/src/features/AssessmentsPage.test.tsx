import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AssessmentsPage from './AssessmentsPage'

const course = { id: 'c1', code: 'ENG-1', title: 'English', status: 'Published' }
const assessment = { id: 'q1', courseId: 'c1', title: 'Midterm', instructions: null, status: 'Draft', timeLimitMinutes: 30, attemptLimit: 2, questionCount: 0 }
const detail = { assessment, questions: [] }

const request = vi.fn()
let permissions: string[] = []

vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't' } }) }))

beforeEach(() => {
  request.mockReset()
  request.mockImplementation((path: string, options?: { method?: string }) => {
    if (options?.method) return Promise.resolve({})
    if (path === '/api/v1/tenant/courses') return Promise.resolve([course])
    if (path.endsWith('/assessments')) return Promise.resolve([assessment])
    if (path.endsWith('/attempts')) return Promise.resolve([])
    return Promise.resolve(detail)
  })
})

const posts = (fragment: string) => request.mock.calls.filter((call) => String(call[0]).endsWith(fragment) && call[1]?.method === 'POST')
const isRequired = (name: string) => screen.getAllByText(name).some((element) => element.tagName === 'LABEL' && element.className.includes("after:content-['*'/'']"))

describe('AssessmentsPage', () => {
  it('lists assessments as rows and opens nothing by default', async () => {
    permissions = ['assessment.read']
    render(<AssessmentsPage />)
    expect(await screen.findByText('Midterm')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.queryByRole('button', { name: 'New assessment' })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'View details for Midterm' }))
    expect(await screen.findByRole('dialog', { name: 'Assessment details' })).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: 'Questions' })).toBeNull()
  })

  it('opens the New assessment panel, marks required fields, blocks an empty submit and sends the same body', async () => {
    permissions = ['assessment.read', 'assessment.manage']
    render(<AssessmentsPage />)
    await screen.findByText('Midterm')
    await userEvent.click(screen.getByRole('button', { name: 'New assessment' }))
    expect(await screen.findByRole('dialog', { name: 'New assessment' })).toBeInTheDocument()
    expect(isRequired('Title')).toBe(true)
    expect(isRequired('Attempts allowed')).toBe(true)
    expect(isRequired('Instructions')).toBe(false)
    await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a title.')
    expect(posts('/assessments')).toHaveLength(0)
    await userEvent.type(screen.getByLabelText('Title'), 'Final')
    await userEvent.type(screen.getByLabelText('Time limit (minutes)'), '45')
    await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))
    await waitFor(() => expect(posts('/assessments')).toHaveLength(1))
    expect(JSON.parse(posts('/assessments')[0][1].body)).toEqual({ title: 'Final', instructions: '', timeLimitMinutes: 45, attemptLimit: 1 })
    expect(await screen.findByText(/Draft assessment created/)).toBeInTheDocument()
  })

  it('validates and adds a question from the Questions tab', async () => {
    permissions = ['assessment.read', 'assessment.manage']
    render(<AssessmentsPage initialTab="questions" />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Midterm' }))
    expect(await screen.findByLabelText('Prompt')).toBeInTheDocument()
    expect(isRequired('Prompt')).toBe(true)
    expect(isRequired('Options')).toBe(true)
    await userEvent.click(screen.getByRole('button', { name: 'Add question' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Question prompt is required.')
    expect(posts('/questions')).toHaveLength(0)
    await userEvent.type(screen.getByLabelText('Prompt'), 'Capital of France?')
    await userEvent.type(screen.getByLabelText('Options'), 'Paris, Rome')
    await userEvent.type(screen.getByLabelText('Correct answers'), 'Paris')
    await userEvent.click(screen.getByRole('button', { name: 'Add question' }))
    await waitFor(() => expect(posts('/questions')).toHaveLength(1))
    expect(JSON.parse(posts('/questions')[0][1].body)).toEqual({ type: 'MultipleChoice', prompt: 'Capital of France?', options: ['Paris', 'Rome'], correctAnswers: ['Paris'], points: 1 })
  })
})
