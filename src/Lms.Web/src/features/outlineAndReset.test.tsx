import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CoursesPage from './CoursesPage'
import PasswordResetPage from './PasswordResetPage'
import { moveItem } from '@/lib/order'
import { parseResetHash } from '@/lib/inviteLink'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't' } }) }))

const callsTo = (fragment: string, method?: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || call[1]?.method === method))

describe('moveItem', () => {
  it('moves an item and leaves the original alone', () => {
    const list = ['a', 'b', 'c']
    expect(moveItem(list, 2, -1)).toEqual(['a', 'c', 'b'])
    expect(moveItem(list, 0, 1)).toEqual(['b', 'a', 'c'])
    expect(list).toEqual(['a', 'b', 'c'])
  })
  it('does nothing at the ends or out of range', () => {
    expect(moveItem(['a', 'b'], 0, -1)).toEqual(['a', 'b'])
    expect(moveItem(['a', 'b'], 1, 1)).toEqual(['a', 'b'])
    expect(moveItem(['a', 'b'], 5, -1)).toEqual(['a', 'b'])
  })
})

describe('parseResetHash', () => {
  it('reads a reset link and ignores invitation links', () => {
    expect(parseResetHash('#reset=abc-123&tenant=acme')).toEqual({ token: 'abc-123', tenant: 'acme' })
    expect(parseResetHash('#invite=abc&tenant=acme')).toBeNull()
    expect(parseResetHash('#reset=abc')).toBeNull()
  })
})

// ---------- outline editing ----------
const detail = (status = 'Draft') => ({
  course: { id: 'c1', code: 'C1', slug: 'c1', title: 'Algebra', status },
  currentVersion: { id: 'v1', versionNumber: 1, status },
  modules: [
    { id: 'm1', title: 'Module One', displayOrder: 1, lessons: [{ id: 'l1', title: 'Lesson A', displayOrder: 1 }, { id: 'l2', title: 'Lesson B', displayOrder: 2 }] },
    { id: 'm2', title: 'Module Two', displayOrder: 2, lessons: [] },
  ],
  workflow: [],
})

describe('course outline editing', () => {
  beforeEach(() => {
    request.mockReset(); permissions = ['course.manage', 'course.review', 'course.publish']
    request.mockImplementation((path: string) => Promise.resolve(String(path) === '/api/v1/tenant/courses' ? [{ id: 'c1', code: 'C1', slug: 'c1', title: 'Algebra', status: 'Draft' }] : detail()))
  })

  async function openOutline() {
    render(<CoursesPage mode="authoring" />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra' }))
    await userEvent.click(await screen.findByRole('tab', { name: 'Outline' }))
    await screen.findByText('Module One')
  }

  it('reorders modules and lessons by sending the full new order', async () => {
    await openOutline()
    await userEvent.click(screen.getByRole('button', { name: 'Move module Module One down' }))
    await waitFor(() => expect(callsTo('/modules/order', 'PUT')).toHaveLength(1))
    expect(JSON.parse(callsTo('/modules/order', 'PUT')[0][1].body)).toEqual({ ids: ['m2', 'm1'] })

    await userEvent.click(screen.getByRole('button', { name: 'Move lesson Lesson B up' }))
    await waitFor(() => expect(callsTo('/modules/m1/lessons/order', 'PUT')).toHaveLength(1))
    expect(JSON.parse(callsTo('/modules/m1/lessons/order', 'PUT')[0][1].body)).toEqual({ ids: ['l2', 'l1'] })
  })

  it('cannot move the first item up or the last item down', async () => {
    await openOutline()
    expect(screen.getByRole('button', { name: 'Move module Module One up' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Move module Module Two down' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Move lesson Lesson B down' })).toBeDisabled()
  })

  it('deletes a lesson or module only after confirming', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true)
    await openOutline()
    await userEvent.click(screen.getByRole('button', { name: 'Delete lesson Lesson A' }))
    expect(callsTo('/lessons/l1', 'DELETE')).toHaveLength(0)
    await userEvent.click(screen.getByRole('button', { name: 'Delete lesson Lesson A' }))
    await waitFor(() => expect(callsTo('/lessons/l1', 'DELETE')).toHaveLength(1))
    await userEvent.click(screen.getByRole('button', { name: 'Delete module Module Two' }))
    await waitFor(() => expect(callsTo('/modules/m2', 'DELETE')).toHaveLength(1))
    expect(confirm).toHaveBeenCalledTimes(3)
    confirm.mockRestore()
  })

  it('shows no editing controls on a published course', async () => {
    request.mockImplementation((path: string) => Promise.resolve(String(path) === '/api/v1/tenant/courses' ? [{ id: 'c1', code: 'C1', slug: 'c1', title: 'Algebra', status: 'Published' }] : detail('Published')))
    await openOutline()
    expect(screen.queryByRole('button', { name: /Delete lesson/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /Move module/ })).toBeNull()
  })
})

// ---------- password reset ----------
describe('PasswordResetPage', () => {
  beforeEach(() => request.mockReset())
  const bodyOf = (fragment: string) => JSON.parse(callsTo(fragment)[0][1].body)

  it('asks for a code, then sets the new password with it', async () => {
    request.mockResolvedValue({ message: 'If that address belongs to an account, we have sent instructions.' })
    render(<PasswordResetPage link={null} onBack={vi.fn()} />)
    await userEvent.type(screen.getByLabelText(/organization/i), 'acme')
    await userEvent.type(screen.getByLabelText('Email'), 'ada@acme.test')
    await userEvent.click(screen.getByRole('button', { name: 'Send reset code' }))
    expect(await screen.findByRole('status')).toHaveTextContent('we have sent instructions')
    expect(bodyOf('/request')).toEqual({ tenantSlug: 'acme', email: 'ada@acme.test' })

    await userEvent.click(screen.getByRole('button', { name: 'I have the code' }))
    expect(screen.getByLabelText('Organization (tenant slug)')).toHaveValue('acme') // carried over from the first step
    await userEvent.type(screen.getByLabelText('Reset code'), ' code-123 ')
    await userEvent.type(screen.getByLabelText('New password'), 'a-new-password')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByText('Password changed')).toBeInTheDocument()
    expect(bodyOf('/confirm')).toEqual({ tenantSlug: 'acme', token: 'code-123', newPassword: 'a-new-password' })
  })

  it('marks the fields as required and blocks an empty request', async () => {
    render(<PasswordResetPage link={null} onBack={vi.fn()} />)
    expect(screen.getByText('Email').className).toContain("content-['*'/'']")
    await userEvent.click(screen.getByRole('button', { name: 'Send reset code' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter the organization.')
    expect(request).not.toHaveBeenCalled()
  })

  it('does not confirm a password shorter than eight characters', async () => {
    render(<PasswordResetPage link={{ token: 'tok', tenant: 'acme' }} onBack={vi.fn()} />)
    await userEvent.type(screen.getByLabelText('New password'), 'short')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('at least 8 characters')
    expect(request).not.toHaveBeenCalled()
  })

  it('opens straight on the new password form from an emailed link', () => {
    render(<PasswordResetPage link={{ token: 'tok', tenant: 'acme' }} onBack={vi.fn()} />)
    expect(screen.getByLabelText('Reset code')).toHaveValue('tok')
    expect(screen.getByLabelText('Organization (tenant slug)')).toHaveValue('acme')
  })

  it('shows the server message for a code that does not work and can go back', async () => {
    const { ApiError } = await import('@/lib/api')
    let fail = true
    request.mockImplementation(() => { if (fail) { fail = false; return Promise.resolve().then(() => { throw new ApiError('This reset code is not valid.', 400) }) } return Promise.resolve(null) })
    const onBack = vi.fn()
    render(<PasswordResetPage link={{ token: 'tok', tenant: 'acme' }} onBack={onBack} />)
    await userEvent.type(screen.getByLabelText('New password'), 'a-new-password')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('This reset code is not valid.')
    await userEvent.click(screen.getByRole('button', { name: 'Back to sign in' }))
    expect(onBack).toHaveBeenCalled()
    expect(within(document.body).queryByText('Password changed')).toBeNull()
  })
})
