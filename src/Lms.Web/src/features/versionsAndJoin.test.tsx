import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CourseVersionPanel, { versionActions } from './CourseVersionPanel'
import JoinWithInvitationPage from './JoinWithInvitationPage'
import { emailOutcome } from './InvitationsPage'
import { ApiError } from '@/lib/api'
import { parseInviteHash } from '@/lib/inviteLink'

const request = vi.fn()
const login = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ login, session: null }) }))

const all = { canManage: true, canReview: true, canPublish: true }

describe('versionActions', () => {
  it('only offers a new version on a published course that has none in progress', () => {
    expect(versionActions('Published', null, all).start).toBe(true)
    expect(versionActions('Published', { id: 'v', versionNumber: 2, status: 'Draft' }, all).start).toBe(false)
    expect(versionActions('Draft', null, all).start).toBe(false)
    expect(versionActions('Published', null, { ...all, canManage: false }).start).toBe(false)
  })
  it('moves a version through review to publication, each step by the right permission', () => {
    const draft = { id: 'v', versionNumber: 2, status: 'Draft' }
    const inReview = { ...draft, status: 'InReview' }
    expect(versionActions('Published', draft, all)).toMatchObject({ submit: true, publish: false, discard: true })
    expect(versionActions('Published', inReview, all)).toMatchObject({ submit: false, publish: true, discard: true })
    expect(versionActions('Published', draft, { ...all, canReview: false }).submit).toBe(false)
    expect(versionActions('Published', inReview, { ...all, canPublish: false }).publish).toBe(false)
  })
})

describe('CourseVersionPanel', () => {
  const props = { courseStatus: 'Published', current: { id: 'v1', versionNumber: 1, status: 'Published' }, draft: null, ...all, busy: false, onStart: vi.fn(), onSubmit: vi.fn(), onPublish: vi.fn(), onDiscard: vi.fn() }
  beforeEach(() => { Object.values(props).forEach((value) => { if (typeof value === 'function' && 'mockReset' in value) (value as ReturnType<typeof vi.fn>).mockReset() }) })

  it('starts a new version with the described change', async () => {
    render(<CourseVersionPanel {...props} />)
    await userEvent.type(screen.getByLabelText(/what is changing/i), 'Add week 5')
    await userEvent.click(screen.getByRole('button', { name: 'Start new version' }))
    expect(props.onStart).toHaveBeenCalledWith('Add week 5')
  })

  it('submits a draft version for review and can discard it after confirming', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<CourseVersionPanel {...props} draft={{ id: 'v2', versionNumber: 2, status: 'Draft', changeSummary: 'Add week 5' }} />)
    expect(screen.queryByRole('button', { name: 'Start new version' })).toBeNull()
    expect(screen.getByText('Add week 5')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Submit version for review' }))
    expect(props.onSubmit).toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Discard version' }))
    expect(confirm).toHaveBeenCalled()
    expect(props.onDiscard).toHaveBeenCalled()
    confirm.mockRestore()
  })

  it('does not discard when the confirmation is declined', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<CourseVersionPanel {...props} draft={{ id: 'v2', versionNumber: 2, status: 'Draft' }} />)
    await userEvent.click(screen.getByRole('button', { name: 'Discard version' }))
    expect(props.onDiscard).not.toHaveBeenCalled()
    confirm.mockRestore()
  })

  it('publishes a version that is in review, and says it can no longer be edited', async () => {
    render(<CourseVersionPanel {...props} draft={{ id: 'v2', versionNumber: 2, status: 'InReview' }} />)
    await userEvent.click(screen.getByRole('button', { name: 'Publish version 2' }))
    expect(props.onPublish).toHaveBeenCalled()
    expect(screen.getByText(/cannot be edited/i)).toBeInTheDocument()
  })

  it('explains that an unpublished course has no versions yet', () => {
    render(<CourseVersionPanel {...props} courseStatus="Draft" />)
    expect(screen.getByText(/not been published yet/i)).toBeInTheDocument()
    expect(screen.queryByRole('button')).toBeNull()
  })
})

describe('parseInviteHash', () => {
  it('reads the code and organization from the link fragment', () => {
    expect(parseInviteHash('#invite=abc_DEF-123&tenant=acme')).toEqual({ token: 'abc_DEF-123', tenant: 'acme' })
  })
  it('ignores anything that is not a complete invitation link', () => {
    for (const hash of ['', '#', '#invite=abc', '#tenant=acme', '#other=1']) expect(parseInviteHash(hash)).toBeNull()
  })
})

describe('emailOutcome', () => {
  const base = { email: 'nia@x.test', emailError: null, hasAccount: false }
  it('tells the sender what happened to the email', () => {
    expect(emailOutcome({ ...base, emailStatus: 'Sent' })).toMatch(/emailed the invitation.*nia@x.test/)
    expect(emailOutcome({ ...base, emailStatus: 'NotConfigured' })).toMatch(/not set up/)
    expect(emailOutcome({ ...base, emailStatus: 'Failed', emailError: 'Timed out' })).toMatch(/could not be sent \(Timed out\)/)
    expect(emailOutcome({ ...base, emailStatus: 'NotNeeded', hasAccount: true })).toMatch(/already has an account/)
  })
})

describe('JoinWithInvitationPage', () => {
  const preview = { organizationName: 'Acme School', tenantSlug: 'acme', courseTitle: 'Algebra', email: 'nia@x.test', invitedBy: 'Mr Rai', message: 'See you there', expiresAtUtc: '2026-12-01T00:00:00Z', hasAccount: false }
  beforeEach(() => { request.mockReset(); login.mockReset(); login.mockResolvedValue(undefined) })

  const calls = (fragment: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment))

  it('checks the link, then creates the account for the invited address and signs in', async () => {
    request.mockImplementation((path: string) => Promise.resolve(String(path).endsWith('/lookup') ? preview : { email: 'nia@x.test', outcome: 'Enrolled' }))
    render(<JoinWithInvitationPage link={{ token: 'tok', tenant: 'acme' }} onBack={vi.fn()} />)

    expect(await screen.findByText(/Mr Rai invited you to Algebra at Acme School/)).toBeInTheDocument()
    expect(calls('/lookup')[0][1].headers['X-Tenant-Slug']).toBe('acme')
    expect(JSON.parse(calls('/lookup')[0][1].body)).toEqual({ token: 'tok' })
    expect(screen.getByLabelText('Email')).toHaveValue('nia@x.test')
    expect(screen.getByLabelText('Email')).toHaveAttribute('readonly')
    expect(screen.getByText('See you there')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Your name'), 'Nia')
    await userEvent.type(screen.getByLabelText('Choose a password'), 'a-long-password')
    await userEvent.click(screen.getByRole('button', { name: 'Create account and join' }))

    await waitFor(() => expect(login).toHaveBeenCalledWith('acme', 'nia@x.test', 'a-long-password'))
    expect(JSON.parse(calls('/register')[0][1].body)).toEqual({ token: 'tok', displayName: 'Nia', password: 'a-long-password' })
  })

  it('marks the fields as required and does not register with an empty or short form', async () => {
    request.mockResolvedValue(preview)
    render(<JoinWithInvitationPage link={{ token: 'tok', tenant: 'acme' }} onBack={vi.fn()} />)
    await screen.findByLabelText('Your name')
    expect(screen.getByText('Your name').className).toContain("content-['*'/'']")
    expect(screen.getByText('Choose a password').className).toContain("content-['*'/'']")
    await userEvent.click(screen.getByRole('button', { name: 'Create account and join' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter your name.')
    await userEvent.type(screen.getByLabelText('Your name'), 'Nia')
    await userEvent.type(screen.getByLabelText('Choose a password'), 'short')
    await userEvent.click(screen.getByRole('button', { name: 'Create account and join' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('at least 8 characters')
    expect(calls('/register')).toHaveLength(0)
  })

  it('sends people who already have an account to sign in', async () => {
    request.mockResolvedValue({ ...preview, hasAccount: true })
    const onBack = vi.fn()
    render(<JoinWithInvitationPage link={{ token: 'tok', tenant: 'acme' }} onBack={onBack} />)
    expect(await screen.findByText(/already an account for/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Choose a password')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Go to sign in' }))
    expect(onBack).toHaveBeenCalled()
  })

  it('asks for the organization and code when there is no link, and reports a code that is not valid', async () => {
    let failNext = true
    request.mockImplementation(() => { if (failNext) { failNext = false; return Promise.resolve().then(() => { throw new ApiError('This invitation is not valid.', 404) }) } return Promise.resolve(preview) })
    render(<JoinWithInvitationPage link={null} onBack={vi.fn()} />)
    await userEvent.type(screen.getByLabelText(/organization/i), ' acme ')
    await userEvent.type(screen.getByLabelText('Invitation code'), 'wrong')
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('This invitation is not valid.')
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByLabelText('Your name')).toBeInTheDocument()
    expect(calls('/lookup')[1][1].headers['X-Tenant-Slug']).toBe('acme') // surrounding spaces are trimmed
  })

  it('shows the server message when registration is refused', async () => {
    let step = 0
    request.mockImplementation(() => { step += 1; if (step === 1) return Promise.resolve(preview); return Promise.resolve().then(() => { throw new ApiError('Password must contain between 8 and 200 characters.', 400) }) })
    render(<JoinWithInvitationPage link={{ token: 'tok', tenant: 'acme' }} onBack={vi.fn()} />)
    await userEvent.type(await screen.findByLabelText('Your name'), 'Nia')
    await userEvent.type(screen.getByLabelText('Choose a password'), '12345678')
    await userEvent.click(screen.getByRole('button', { name: 'Create account and join' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Password must contain')
    expect(login).not.toHaveBeenCalled()
  })
})
