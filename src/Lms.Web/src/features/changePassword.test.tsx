import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ChangePasswordForm from './ChangePasswordForm'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

async function fill(current: string, next: string, confirm: string) {
  if (current) await userEvent.type(screen.getByLabelText('Current password'), current)
  if (next) await userEvent.type(screen.getByLabelText('New password'), next)
  if (confirm) await userEvent.type(screen.getByLabelText('Confirm new password'), confirm)
  await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
}

describe('ChangePasswordForm', () => {
  beforeEach(() => { request.mockReset(); request.mockResolvedValue(null) })

  it('checks the fields before asking the server', async () => {
    render(<ChangePasswordForm />)
    await fill('', 'short', 'other')
    expect(await screen.findByText('Enter your current password.')).toBeInTheDocument()
    expect(screen.getByText('Use at least 8 characters.')).toBeInTheDocument()
    expect(screen.getByText('The passwords do not match.')).toBeInTheDocument()
    expect(request).not.toHaveBeenCalled()
  })

  it('refuses a new password equal to the current one', async () => {
    render(<ChangePasswordForm />)
    await fill('same-password', 'same-password', 'same-password')
    expect(await screen.findByText('Choose a password different from the current one.')).toBeInTheDocument()
    expect(request).not.toHaveBeenCalled()
  })

  it('sends both passwords, confirms the change and clears the fields', async () => {
    render(<ChangePasswordForm />)
    await fill('old-password', 'new-password-1', 'new-password-1')
    await waitFor(() => expect(request).toHaveBeenCalledTimes(1))
    expect(request.mock.calls[0][0]).toBe('/api/v1/tenant/me/password')
    expect(JSON.parse(request.mock.calls[0][1].body)).toEqual({ currentPassword: 'old-password', newPassword: 'new-password-1' })
    expect(await screen.findByRole('status')).toHaveTextContent('Your password was changed')
    expect(screen.getByLabelText('Current password')).toHaveValue('')
  })

  it('shows the server’s reason when the current password is wrong and keeps what was typed', async () => {
    request.mockRejectedValue(new Error('The current password is not correct.'))
    render(<ChangePasswordForm />)
    await fill('wrong-password', 'new-password-1', 'new-password-1')
    expect(await screen.findByRole('alert')).toHaveTextContent('The current password is not correct.')
    expect(screen.getByLabelText('New password')).toHaveValue('new-password-1')
  })
})
