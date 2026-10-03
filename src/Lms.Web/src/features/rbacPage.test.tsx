import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import RbacPage from './RbacPage'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions: ['role.manage', 'user.manage'], accessToken: 't' } }) }))
vi.mock('../lib/auth', () => ({ useAuth: () => ({ session: { permissions: ['role.manage', 'user.manage'], accessToken: 't' } }) }))
vi.mock('../lib/api', async () => {
  const actual = await vi.importActual<typeof import('../lib/api')>('../lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

const roles = [
  { id: 'r1', code: 'ADMIN', name: 'Administrator', isSystemRole: true, permissions: ['course.read', 'role.manage'] },
  { id: 'r2', code: 'TEACHER', name: 'Teacher', isSystemRole: false, permissions: ['course.read'] },
]
const users = [
  { id: 'u1', email: 'asha@school.edu', displayName: 'Asha Rai', userStatus: 'Active', membershipStatus: 'Active', roleCode: 'ADMIN', roleName: 'Administrator' },
  { id: 'u2', email: 'ben@school.edu', displayName: 'Ben Lama', userStatus: 'Active', membershipStatus: 'Active', roleCode: 'TEACHER', roleName: 'Teacher' },
]

describe('RbacPage', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path?: string) => {
      const url = String(path)
      if (url.endsWith('/roles')) return Promise.resolve(roles)
      if (url.endsWith('/users')) return Promise.resolve(users)
      if (url.endsWith('/permissions')) return Promise.resolve(['course.read', 'liveclass.read', 'collaboration.read', 'role.manage'])
      return Promise.resolve({})
    })
  })

  it('lists users in rows and filters them with the search box', async () => {
    render(<RbacPage initialTab="users" />)
    expect(await screen.findByText('Asha Rai')).toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Users' })).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Search users'), 'ben')
    expect(screen.queryByText('Asha Rai')).not.toBeInTheDocument()
    expect(screen.getByText('Ben Lama')).toBeInTheDocument()
  })

  it('opens the New user panel, marks required fields, blocks an empty submit, then sends the request', async () => {
    const { baseElement } = render(<RbacPage />)
    await screen.findByText('Asha Rai')
    await userEvent.click(screen.getByRole('button', { name: 'New user' }))
    const dialog = screen.getByRole('dialog', { name: 'New user' })
    expect(dialog.querySelectorAll('label[class*="content-"]').length).toBe(4)
    await userEvent.click(screen.getByRole('button', { name: 'Create user' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a valid email address.')
    expect(request.mock.calls.some((call) => call[1]?.method === 'POST')).toBe(false)

    await userEvent.type(screen.getByLabelText('Display name'), 'Cara Shah')
    await userEvent.type(screen.getByLabelText('Email'), 'cara@school.edu')
    await userEvent.type(screen.getByLabelText('Password'), 'longenough1')
    await userEvent.selectOptions(screen.getByLabelText('Role'), 'TEACHER')
    await userEvent.click(screen.getByRole('button', { name: 'Create user' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/users', { method: 'POST', body: JSON.stringify({ email: 'cara@school.edu', displayName: 'Cara Shah', password: 'longenough1', roleCode: 'TEACHER' }) })
    expect(await screen.findByText('User “Cara Shah” created.')).toBeInTheDocument()
    expect(baseElement.querySelector('[role="dialog"]')).toBeNull()
  })

  it('shows roles, their permissions in a panel, and creates a role', async () => {
    render(<RbacPage initialTab="roles" />)
    expect(await screen.findByText('Administrator')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'View details for Teacher' }))
    expect(screen.getByRole('dialog', { name: 'Role: Teacher' })).toHaveTextContent('course.read')
    await userEvent.click(screen.getByRole('button', { name: 'Close panel' }))

    await userEvent.click(screen.getByRole('button', { name: 'New role' }))
    const dialog = screen.getByRole('dialog', { name: 'New role' })
    expect(dialog.querySelectorAll('label[class*="content-"]').length).toBe(2)
    await userEvent.click(screen.getByRole('button', { name: 'Create role' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a role code.')
    expect(request.mock.calls.some((call) => call[1]?.method === 'POST')).toBe(false)

    await userEvent.type(screen.getByLabelText('Role code'), 'AUDITOR')
    await userEvent.type(screen.getByLabelText('Role name'), 'Auditor')
    await userEvent.click(screen.getByRole('button', { name: 'Create role' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/roles', { method: 'POST', body: JSON.stringify({ code: 'AUDITOR', name: 'Auditor', permissions: ['course.read', 'liveclass.read', 'collaboration.read'] }) })
  })
})
