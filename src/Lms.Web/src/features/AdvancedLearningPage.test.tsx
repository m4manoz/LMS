import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AdvancedLearningPage from './AdvancedLearningPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, tenant: { id: 't' }, user: { id: 'u' } } }) }))

const lab = { id: 'l1', code: 'PHY', name: 'Physics lab', description: null, providerType: 'external-simulation', launchUrl: null, status: 'Active', healthStatus: 'Healthy', updatedAtUtc: '2030-01-01T00:00:00Z' }
const recommendation = { courseId: 'c1', courseCode: 'MATH-1', title: 'Algebra', score: 0.8, reason: 'r', explanation: 'because', variant: 'a' }

describe('AdvancedLearningPage', () => {
  beforeEach(() => {
    request.mockReset()
    permissions = ['virtuallab.manage']
    request.mockImplementation((path: string) => {
      if (path === '/api/v1/tenant/virtual-labs') return Promise.resolve([lab])
      if (path === '/api/v1/tenant/learning/recommendations') return Promise.resolve([recommendation])
      if (path === '/api/v1/tenant/gamification/me') return Promise.resolve({ totalPoints: 5, currentStreakDays: 1, longestStreakDays: 2, badges: [], recentEvents: [] })
      if (path === '/api/v1/tenant/gamification/settings') return Promise.resolve({ isEnabled: true, courseCompletionPoints: 10, dailyPointCap: 100 })
      return Promise.resolve(undefined)
    })
  })

  it('lists virtual labs as rows', async () => {
    render(<AdvancedLearningPage focus="virtual-labs" />)
    const list = await screen.findByRole('list', { name: 'Virtual labs' })
    expect(within(list).getByText('Physics lab')).toBeInTheDocument()
  })

  it('opens the register panel, marks required fields and blocks an empty submit', async () => {
    render(<AdvancedLearningPage focus="virtual-labs" />)
    await userEvent.click(await screen.findByRole('button', { name: /Register lab/ }))
    const panel = await screen.findByRole('dialog', { name: 'Register a lab integration' })
    expect(within(panel).getByText('Lab code')).toHaveClass('after:text-red-500')
    await userEvent.click(within(panel).getByRole('button', { name: 'Register lab' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Enter a lab code.')
    expect(request).not.toHaveBeenCalledWith('/api/v1/tenant/virtual-labs', expect.objectContaining({ method: 'POST' }))
  })

  it('registers a lab with the same request body', async () => {
    render(<AdvancedLearningPage focus="virtual-labs" />)
    await userEvent.click(await screen.findByRole('button', { name: /Register lab/ }))
    const panel = await screen.findByRole('dialog', { name: 'Register a lab integration' })
    await userEvent.type(within(panel).getByLabelText('Lab code'), 'CHEM')
    await userEvent.type(within(panel).getByLabelText('Lab name'), 'Chemistry')
    await userEvent.click(within(panel).getByRole('button', { name: 'Register lab' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/virtual-labs', { method: 'POST', body: JSON.stringify({ code: 'CHEM', name: 'Chemistry', providerType: 'external-simulation', description: null, launchUrl: null }) })
  })

  it('shows recommendations as rows with a dismiss action', async () => {
    permissions = []
    render(<AdvancedLearningPage focus="recommendations" />)
    const list = await screen.findByRole('list', { name: 'Recommendations' })
    expect(within(list).getByText('Algebra')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: 'Dismiss recommendation Algebra' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/learning/recommendations/c1/dismiss', { method: 'POST', body: '{}' })
  })

  it('marks the policy fields as required and sends the policy unchanged', async () => {
    permissions = ['gamification.manage']
    render(<AdvancedLearningPage focus="gamification" />)
    expect(await screen.findByText('Course completion points')).toHaveClass('after:text-red-500')
    await userEvent.click(screen.getByRole('button', { name: 'Save policy' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/gamification/settings', { method: 'PUT', body: JSON.stringify({ isEnabled: true, courseCompletionPoints: 10, dailyPointCap: 100 }) })
  })
})
