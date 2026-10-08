import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import PlatformConsolePage from './PlatformConsolePage'
import { isPlatformAddress } from '@/lib/platformApi'

const school = { id: 't1', slug: 'north-school', name: 'North School', status: 'Active', createdAtUtc: '2026-01-01T00:00:00Z', updatedAtUtc: '2026-01-01T00:00:00Z', members: 12, courses: 3 }
const closed = { ...school, id: 't2', slug: 'old-school', name: 'Old School', status: 'Archived', members: 1, courses: 0 }
const detail = { ...school, storageBucket: 'north-files', ownBucket: true, enrollments: 40, admins: [{ userId: 'u1', email: 'boss@north.test', displayName: 'Boss', status: 'Active' }], domains: ['learn.north.test'] }

const fetchMock = vi.fn()
const calls = (method: string) => fetchMock.mock.calls.filter((call) => (call[1]?.method ?? 'GET') === method)
const reply = (body: unknown, status = 200) => Promise.resolve(new Response(body === null ? null : JSON.stringify(body), { status }))

describe('PlatformConsolePage', () => {
  beforeEach(() => {
    sessionStorage.clear()
    fetchMock.mockReset()
    fetchMock.mockImplementation((path: string, init?: RequestInit) => {
      if (init?.method === 'POST' || init?.method === 'PUT') return reply({ status: 'Suspended' })
      if (path.endsWith('/north-school')) return reply(detail)
      return reply([school, closed])
    })
    vi.stubGlobal('fetch', fetchMock)
  })
  afterEach(() => vi.unstubAllGlobals())

  it('asks for the platform key first and sends it with every call', async () => {
    render(<PlatformConsolePage />)
    expect(screen.queryByText('North School')).toBeNull()
    await userEvent.type(screen.getByLabelText(/Platform key/), 'secret-key')
    await userEvent.click(screen.getByRole('button', { name: 'Open console' }))
    expect(await screen.findByText('North School')).toBeInTheDocument()
    expect(fetchMock.mock.calls[0][1].headers['X-Platform-Key']).toBe('secret-key')
    expect(screen.getByText('12 people · 3 courses')).toBeInTheDocument()
  })

  it('goes back to the key prompt when the key is refused', async () => {
    sessionStorage.setItem('lms-platform-key', 'wrong')
    fetchMock.mockImplementation(() => reply(null, 401))
    render(<PlatformConsolePage />)
    expect(await screen.findByRole('alert')).toHaveTextContent('The platform key was not accepted.')
    expect(screen.getByLabelText(/Platform key/)).toBeInTheDocument()
    expect(sessionStorage.getItem('lms-platform-key')).toBeNull()
  })

  it('suspends an active organization after confirming, and only offers archive where it applies', async () => {
    sessionStorage.setItem('lms-platform-key', 'k')
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<PlatformConsolePage />)
    await screen.findByText('North School')
    expect(screen.queryByRole('button', { name: 'Archive Old School' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Activate Old School' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Suspend North School' }))
    await waitFor(() => expect(calls('POST')).toHaveLength(1))
    expect(String(calls('POST')[0][0])).toBe('/api/v1/platform/tenants/north-school/suspend')
    expect(await screen.findByText('North School is now suspended.')).toBeInTheDocument()
    confirm.mockRestore()
  })

  it('does nothing when the confirmation is declined', async () => {
    sessionStorage.setItem('lms-platform-key', 'k')
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<PlatformConsolePage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Archive North School' }))
    expect(calls('POST')).toHaveLength(0)
    confirm.mockRestore()
  })

  it('opens an organization to show its administrators and addresses, and renames it', async () => {
    sessionStorage.setItem('lms-platform-key', 'k')
    render(<PlatformConsolePage />)
    await userEvent.click(await screen.findByRole('button', { name: 'North School' }))
    const panel = await screen.findByRole('dialog', { name: 'North School' })
    expect(within(panel).getByText(/boss@north\.test/)).toBeInTheDocument()
    expect(within(panel).getByText('learn.north.test')).toBeInTheDocument()
    expect(within(panel).getByText(/Its own bucket/)).toHaveTextContent('north-files')
    const name = within(panel).getByLabelText(/Organization name/)
    await userEvent.clear(name)
    await userEvent.type(name, 'North Academy')
    await userEvent.click(within(panel).getByRole('button', { name: 'Save name' }))
    await waitFor(() => expect(calls('PUT')).toHaveLength(1))
    expect(JSON.parse(calls('PUT')[0][1].body)).toEqual({ name: 'North Academy' })
  })

  it('checks a new organization before creating it, then creates it with its first administrator', async () => {
    sessionStorage.setItem('lms-platform-key', 'k')
    render(<PlatformConsolePage />)
    await userEvent.click(await screen.findByRole('button', { name: 'New organization' }))
    const panel = await screen.findByRole('dialog', { name: 'New organization' })
    await userEvent.click(within(panel).getByRole('button', { name: 'Create organization' }))
    expect(await within(panel).findByText(/Enter the organization/)).toBeInTheDocument()
    expect(calls('POST')).toHaveLength(0)

    await userEvent.type(within(panel).getAllByLabelText('Name')[0], 'East School')   // the organization's name; the administrator's comes second
    await userEvent.type(within(panel).getByLabelText(/Short name/), 'Bad Name!')
    await userEvent.click(within(panel).getByRole('button', { name: 'Create organization' }))
    expect(await within(panel).findByText(/lowercase letters/)).toBeInTheDocument()

    await userEvent.clear(within(panel).getByLabelText(/Short name/))
    await userEvent.type(within(panel).getByLabelText(/Short name/), 'east-school')
    await userEvent.type(within(panel).getByLabelText('Email'), 'a@east.test')
    await userEvent.click(within(panel).getByRole('button', { name: 'Create organization' }))
    expect(await within(panel).findByText(/password for the first administrator/)).toBeInTheDocument()

    await userEvent.type(within(panel).getByLabelText('Password'), 'Passw0rd!123')
    await userEvent.click(within(panel).getByRole('button', { name: 'Create organization' }))
    await waitFor(() => expect(calls('POST')).toHaveLength(2))
    expect(String(calls('POST')[1][0])).toBe('/api/v1/platform/tenants/east-school/bootstrap-admin')
  })

  it('recognises the console address', () => {
    expect(isPlatformAddress('#/platform')).toBe(true)
    expect(isPlatformAddress('#platform')).toBe(true)
    expect(isPlatformAddress('#/invite/abc')).toBe(false)
  })
})
