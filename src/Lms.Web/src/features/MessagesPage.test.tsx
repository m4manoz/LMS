import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MessagesPage, { formatMessageTime } from './MessagesPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

const conversation = { id: 'c1', kind: 'Direct', title: 'Ms Rai', courseId: null, lastMessagePreview: 'See you Monday', lastActivityAtUtc: new Date().toISOString(), unreadCount: 2 }
const message = { id: 'm1', senderUserId: 'u2', senderName: 'Ms Rai', body: 'See you Monday', createdAtUtc: new Date().toISOString(), isMine: false }

describe('formatMessageTime', () => {
  it('shows a clock time for today and a short date otherwise', () => {
    const now = new Date(2026, 9, 15, 12, 0)
    expect(formatMessageTime(new Date(2026, 9, 15, 9, 5).toISOString(), now)).toMatch(/9:05|09:05/)
    expect(formatMessageTime(new Date(2026, 9, 3, 9, 5).toISOString(), now)).toMatch(/3/)
    expect(formatMessageTime(new Date(2026, 9, 3, 9, 5).toISOString(), now)).not.toMatch(/:/)
  })
})

describe('MessagesPage', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path.endsWith('/conversations') && !options?.method) return Promise.resolve([conversation])
      if (path.endsWith('/messages') && !options?.method) return Promise.resolve([message])
      if (path.includes('/contacts')) return Promise.resolve([{ userId: 'u9', name: 'Mr Karki', email: 'karki@example.com', isStaff: true }])
      if (path.endsWith('/conversations/direct')) return Promise.resolve({ id: 'c1' })
      return Promise.resolve(undefined)
    })
  })

  it('lists conversations with their unread count and opens one', async () => {
    permissions = ['collaboration.read', 'collaboration.manage']
    render(<MessagesPage />)
    expect(await screen.findByText('2 new')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Ms Rai/ }))
    expect((await screen.findAllByText('See you Monday')).length).toBeGreaterThan(0)
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/messages/conversations/c1/read', { method: 'POST' })
  })

  it('sends a message and clears the box', async () => {
    permissions = ['collaboration.read', 'collaboration.manage']
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Ms Rai/ }))
    const box = await screen.findByLabelText('Message')
    await userEvent.type(box, 'Thank you!')
    await userEvent.click(screen.getByRole('button', { name: 'Send' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/messages/conversations/c1/messages', { method: 'POST', body: JSON.stringify({ body: 'Thank you!' }) })
    expect(box).toHaveValue('')
  })

  it('is read-only and hides new-message tools for users who cannot send', async () => {
    permissions = ['collaboration.read']
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Ms Rai/ }))
    expect(await screen.findByText('You can read messages but not send them.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /New message/ })).not.toBeInTheDocument()
  })

  it('opens the new message form in a panel, marks required fields and blocks an empty submit', async () => {
    permissions = ['collaboration.read', 'collaboration.manage']
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /New message/ }))
    const panel = await screen.findByRole('dialog', { name: 'New message' })
    expect(within(panel).getByText('Person')).toHaveClass('after:text-red-500')
    await userEvent.click(within(panel).getByRole('button', { name: 'Start conversation' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Choose a person to message.')
    expect(request).not.toHaveBeenCalledWith('/api/v1/tenant/messages/conversations/direct', expect.anything())
  })

  it('starts a direct conversation with the same request as before', async () => {
    permissions = ['collaboration.read', 'collaboration.manage']
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /New message/ }))
    const panel = await screen.findByRole('dialog', { name: 'New message' })
    await within(panel).findByRole('option', { name: /Mr Karki/ })
    await userEvent.selectOptions(within(panel).getByLabelText('Person'), 'u9')
    await userEvent.click(within(panel).getByRole('button', { name: 'Start conversation' }))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/messages/conversations/direct', { method: 'POST', body: JSON.stringify({ userId: 'u9' }) })
  })
})
