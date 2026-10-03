import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AppShell from './AppShell'
import type { StoredSession } from '@/lib/api'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

const session = (permissions: string[]): StoredSession => ({
  accessToken: 't', refreshToken: 'r', expiresAtUtc: '', tenant: { id: '1', slug: 'acme', name: 'Acme' },
  user: { id: 'u', email: 'a@b.co', displayName: 'A' }, role: 'Learner', roles: ['Learner'], permissions,
})

const shell = (permissions: string[], onNavigate = vi.fn()) => render(
  <AppShell session={session(permissions)} activeMenu="dashboard" onNavigate={onNavigate} onLogout={vi.fn()} calendarMode="AD" onCalendarModeChange={vi.fn()}>
    <p>content</p>
  </AppShell>,
)

describe('AppShell notification bell', () => {
  beforeEach(() => request.mockReset())

  it('shows the unread count and opens notifications when clicked', async () => {
    request.mockImplementation((path?: string) => Promise.resolve(String(path).includes('unread-count') ? { count: 0 } : [{}, {}, {}]))
    const onNavigate = vi.fn()
    shell(['notification.read'], onNavigate)
    const bell = await screen.findByRole('button', { name: 'Notifications, 3 unread' })
    await userEvent.click(bell)
    expect(onNavigate).toHaveBeenCalledWith('notifications')
  })

  it('shows no badge when everything is read', async () => {
    request.mockImplementation((path?: string) => Promise.resolve(String(path).includes('unread-count') ? { count: 0 } : []))
    shell(['notification.read'])
    expect(await screen.findByLabelText('Notifications')).toBeInTheDocument()
    expect(screen.queryByLabelText(/unread/)).not.toBeInTheDocument()
  })

  it('hides the bell from users without notification access and does not poll', () => {
    shell([])
    expect(screen.queryByLabelText(/^Notifications/)).not.toBeInTheDocument()
    expect(request).not.toHaveBeenCalled()
  })

  it('shows unread messages on their own icon and opens Messages', async () => {
    request.mockImplementation((path?: string) => Promise.resolve(String(path).includes('unread-count') ? { count: 4 } : []))
    const onNavigate = vi.fn()
    shell(['collaboration.read'], onNavigate)
    await userEvent.click(await screen.findByRole('button', { name: 'Messages, 4 unread' }))
    expect(onNavigate).toHaveBeenCalledWith('messages')
  })
})
