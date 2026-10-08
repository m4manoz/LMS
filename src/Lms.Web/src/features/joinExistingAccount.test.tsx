import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import JoinWithInvitationPage from './JoinWithInvitationPage'
import { ApiError } from '@/lib/api'

const request = vi.fn()
const login = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ login }) }))

const preview = { organizationName: 'Acme School', tenantSlug: 'acme', courseTitle: 'Algebra', email: 'ada@x.test', invitedBy: 'Mr Rai', message: null, expiresAtUtc: '2026-12-01T00:00:00Z', hasAccount: true, isMember: false }
const calls = (fragment: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment))

beforeEach(() => { request.mockReset(); login.mockReset(); login.mockResolvedValue(undefined) })

describe('joining with an account from another organization', () => {
  it('asks only for the existing password, then signs in with it', async () => {
    request.mockImplementation((path: string) => Promise.resolve(String(path).endsWith('/lookup') ? preview : { email: 'ada@x.test', outcome: 'Enrolled' }))
    render(<JoinWithInvitationPage link={{ token: 'tok', tenant: 'acme' }} onBack={vi.fn()} />)
    expect(await screen.findByText(/already have an account for/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Your name')).toBeNull()
    expect(screen.queryByLabelText('Choose a password')).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: 'Join with my account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter the password of your existing account.')
    expect(calls('/register')).toHaveLength(0)

    await userEvent.type(screen.getByLabelText('Your existing password'), 'my-old-password')
    await userEvent.click(screen.getByRole('button', { name: 'Join with my account' }))
    await waitFor(() => expect(login).toHaveBeenCalledWith('acme', 'ada@x.test', 'my-old-password'))
    expect(JSON.parse(calls('/register')[0][1].body)).toEqual({ token: 'tok', password: 'my-old-password' })   // no name is sent
  })

  it('shows the server’s message for a wrong password and does not sign in', async () => {
    request.mockImplementation((path: string) => String(path).endsWith('/lookup')
      ? Promise.resolve(preview)
      : Promise.reject(new ApiError('That is not the password of the existing account for this email address.', 400)))
    render(<JoinWithInvitationPage link={{ token: 'tok', tenant: 'acme' }} onBack={vi.fn()} />)
    await userEvent.type(await screen.findByLabelText('Your existing password'), 'wrong')
    await userEvent.click(screen.getByRole('button', { name: 'Join with my account' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('not the password of the existing account')
    expect(login).not.toHaveBeenCalled()
  })

  it('still tells people who already belong to this organization to sign in', async () => {
    request.mockResolvedValue({ ...preview, isMember: true })
    const onBack = vi.fn()
    render(<JoinWithInvitationPage link={{ token: 'tok', tenant: 'acme' }} onBack={onBack} />)
    expect(await screen.findByText(/There is already an account for/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Your existing password')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Go to sign in' }))
    expect(onBack).toHaveBeenCalled()
  })
})
