import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LoginPage from './LoginPage'
import { ApiError } from '@/lib/api'

const login = vi.fn()
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ login, session: null, logout: vi.fn() }) }))

describe('LoginPage', () => {
  beforeEach(() => localStorage.clear())   // a successful sign-in remembers the organization, which must not leak into the next test

  it('submits tenant, email and password', async () => {
    login.mockResolvedValueOnce(undefined)
    render(<LoginPage />)
    await userEvent.type(screen.getByLabelText(/organization/i), 'acme')
    await userEvent.type(screen.getByLabelText('Email'), 'admin@acme.test')
    await userEvent.type(screen.getByLabelText('Password'), 'secret-pass')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(login).toHaveBeenCalledWith('acme', 'admin@acme.test', 'secret-pass')
  })

  it('marks the fields as required and blocks an empty submit', async () => {
    login.mockClear()
    render(<LoginPage />)
    for (const name of ['Email', 'Password', 'Organization (tenant slug)']) expect(screen.getByText(name).className).toContain("content-['*'/'']")
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter your organization.')
    expect(login).not.toHaveBeenCalled()
  })

  it('shows the API error message when sign-in fails', async () => {
    login.mockRejectedValueOnce(new ApiError('Invalid credentials.', 401))
    render(<LoginPage />)
    await userEvent.type(screen.getByLabelText(/organization/i), 'acme')
    await userEvent.type(screen.getByLabelText('Email'), 'a@b.co')
    await userEvent.type(screen.getByLabelText('Password'), 'x')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid credentials.')
  })

  it("on an organization’s own website names the organization and never asks for it", async () => {
    login.mockResolvedValueOnce(undefined)
    render(<LoginPage locked={{ slug: "acme", name: "Acme Academy" }} initialTenant="someone-else" />)
    expect(screen.queryByLabelText(/organization/i)).toBeNull()
    expect(screen.getByText(/Use your Acme Academy account/)).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText("Email"), "a@acme.test")
    await userEvent.type(screen.getByLabelText("Password"), "secret-pass")
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }))
    expect(login).toHaveBeenLastCalledWith("acme", "a@acme.test", "secret-pass")
  })
})
