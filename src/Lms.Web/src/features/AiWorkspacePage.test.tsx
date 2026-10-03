import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AiWorkspacePage from './AiWorkspacePage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

const job = { id: 'j1', feature: 'LessonSummary', status: 'Completed', courseId: 'c1', provider: 'local', model: 'm', attemptCount: 1, lastError: null, createdAtUtc: '2030-01-01T10:00:00Z', completedAtUtc: null, outputCount: 1 }
const detail = { ...job, requestedByUserId: 'u', instruction: 'Do it', outputLanguage: 'en', outputs: [] }

describe('AiWorkspacePage', () => {
  beforeEach(() => {
    request.mockReset()
    permissions = ['ai.manage']
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path === '/api/v1/tenant/courses') return Promise.resolve([{ id: 'c1', title: 'Algebra', status: 'Published' }])
      if (path === '/api/v1/tenant/ai/jobs' && !options?.method) return Promise.resolve([job])
      if (path === '/api/v1/tenant/ai/jobs' && options?.method === 'POST') return Promise.resolve(job)
      if (path === '/api/v1/tenant/ai/jobs/j1') return Promise.resolve(detail)
      return Promise.resolve(undefined)
    })
  })

  it('lists jobs as rows and opens one in a panel', async () => {
    render(<AiWorkspacePage />)
    const list = await screen.findByRole('list', { name: 'AI jobs' })
    expect(within(list).getByText('Lesson summary')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: /View details for Lesson summary/ }))
    expect(await screen.findByRole('dialog', { name: 'Lesson summary job' })).toBeInTheDocument()
  })

  it('opens the New draft panel, marks required fields and blocks an empty course', async () => {
    render(<AiWorkspacePage />)
    await userEvent.click(await screen.findByRole('button', { name: /New draft/ }))
    const panel = await screen.findByRole('dialog', { name: 'New draft' })
    expect(within(panel).getByText('Published course')).toHaveClass('after:text-red-500')
    expect(within(panel).getByText('Instruction')).toHaveClass('after:text-red-500')
    await userEvent.click(within(panel).getByRole('button', { name: 'Generate draft' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Choose a published course.')
    expect(request).not.toHaveBeenCalledWith('/api/v1/tenant/ai/jobs', expect.objectContaining({ method: 'POST' }))
  })

  it('queues a draft with the same request body', async () => {
    render(<AiWorkspacePage initialTab="new" />)
    const panel = await screen.findByRole('dialog', { name: 'New draft' })
    await within(panel).findByRole('option', { name: 'Algebra' })
    await userEvent.selectOptions(within(panel).getByLabelText('Published course'), 'c1')
    await userEvent.click(within(panel).getByRole('button', { name: 'Generate draft' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/ai/jobs', { method: 'POST', body: JSON.stringify({ feature: 'LessonSummary', courseId: 'c1', instruction: 'Create a concise, learner-friendly draft.', outputLanguage: 'en' }) })
  })
})
