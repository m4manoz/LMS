import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import IntegrationsPage, { toForm, toRequest } from './IntegrationsPage'
import NotificationPreferences, { isEnabled } from './NotificationPreferences'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

const settings = {
  provider: 'Smtp', enabled: true, fromAddress: 'noreply@school.edu', fromName: 'School', smtpHost: 'smtp.school.edu', smtpPort: 587,
  smtpUseSsl: true, smtpUsername: 'mailer', hasPassword: true, smtpPasswordReference: null, updatedAtUtc: '2026-10-01T00:00:00Z',
} as const

describe('email settings request', () => {
  it('omits the password when it was left blank so the stored one is kept', () => {
    expect(toRequest(toForm(settings))).not.toHaveProperty('smtpPassword')
  })
  it('sends a new password, or an empty string to clear it', () => {
    expect(toRequest({ ...toForm(settings), smtpPassword: 'new-secret' }).smtpPassword).toBe('new-secret')
    expect(toRequest({ ...toForm(settings), clearPassword: true }).smtpPassword).toBe('')
  })
  it('does not send SMTP details for the log provider', () => {
    expect(toRequest({ ...toForm(settings), provider: 'Log' }).smtpHost).toBeNull()
  })
})

describe('IntegrationsPage', () => {
  beforeEach(() => request.mockReset())

  it('never shows the saved password and lets the admin send a test', async () => {
    request.mockImplementation((path?: string, options?: { method?: string }) => {
      const url = String(path)
      if (url.endsWith('/email/test')) return Promise.resolve({ success: true, message: 'A test email was sent to admin@school.edu.' })
      if (url.endsWith('/outbox')) return Promise.resolve([])
      return Promise.resolve(options?.method ? settings : settings)
    })
    render(<IntegrationsPage />)
    const password = await screen.findByLabelText('Password')
    expect(password).toHaveValue('')
    expect(password).toHaveAttribute('placeholder', 'Saved — leave blank to keep')
    await userEvent.click(screen.getByRole('button', { name: 'Send test email' }))
    expect(await screen.findByText('A test email was sent to admin@school.edu.')).toBeInTheDocument()
  })

  it('marks required fields and blocks saving without a host', async () => {
    request.mockImplementation((path?: string) => Promise.resolve(String(path).endsWith('/outbox') ? [] : { ...settings, smtpHost: null }))
    const { container } = render(<IntegrationsPage />)
    const host = await screen.findByLabelText('Host')
    expect(container.querySelector('label[for="smtp-host"]')?.className).toContain('content-')
    expect(container.querySelector('label[for="email-from"]')?.className).toContain('content-')
    expect(host).toHaveValue('')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter the SMTP host.')
    expect(request.mock.calls.some((call) => call[1]?.method === 'PUT')).toBe(false)
  })

  it('saves with the same request body as before', async () => {
    request.mockImplementation((path?: string, options?: { method?: string }) => Promise.resolve(String(path).endsWith('/outbox') ? [] : options?.method === 'PUT' ? settings : settings))
    render(<IntegrationsPage />)
    await screen.findByLabelText('Host')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByText('Saved.')).toBeInTheDocument()
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/integrations/email', { method: 'PUT', body: JSON.stringify(toRequest(toForm(settings))) })
  })

  it('shows the delivery log with failures explained', async () => {
    request.mockImplementation((path?: string) => Promise.resolve(String(path).endsWith('/outbox')
      ? [{ id: '1', recipient: 'lena@school.edu', subject: 'Exam timetable', status: 'DeadLetter', attemptCount: 5, lastError: 'Connection refused', createdAtUtc: '2026-10-01T00:00:00Z', sentAtUtc: null }]
      : settings))
    render(<IntegrationsPage />)
    await userEvent.click(await screen.findByRole('tab', { name: 'Delivery log' }))
    expect(await screen.findByText('Connection refused')).toBeInTheDocument()
    expect(screen.getByText('Gave up')).toBeInTheDocument()
  })
})

describe('NotificationPreferences', () => {
  beforeEach(() => request.mockReset())

  it('treats a notification as on until it is switched off', () => {
    expect(isEnabled([], 'ANNOUNCEMENT', 'Email')).toBe(true)
    expect(isEnabled([{ templateCode: 'ANNOUNCEMENT', channel: 'Email', enabled: false }], 'ANNOUNCEMENT', 'Email')).toBe(false)
    expect(isEnabled([{ templateCode: 'ANNOUNCEMENT', channel: 'Email', enabled: false }], 'ANNOUNCEMENT', 'InApp')).toBe(true)
  })

  it('saves an email opt-out for one type', async () => {
    request.mockImplementation((path?: string, options?: { method?: string }) => Promise.resolve(options?.method === 'PUT' ? undefined : []))
    render(<NotificationPreferences />)
    await userEvent.click(await screen.findByLabelText('Announcements — email'))
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/notification-preferences', { method: 'PUT', body: JSON.stringify({ templateCode: 'ANNOUNCEMENT', channel: 'Email', enabled: false }) })
    expect(await screen.findByLabelText('Announcements — email')).not.toBeChecked()
  })
})
