import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CategoriesPage from './CategoriesPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

const categories = [
  { id: 'c1', name: 'Science', slug: 'science', courseCount: 2 },
  { id: 'c2', name: 'Arts', slug: 'arts', courseCount: 0 },
]
const callsTo = (method: string) => request.mock.calls.filter((call) => call[1]?.method === method)

describe('CategoriesPage', () => {
  beforeEach(() => {
    request.mockReset(); permissions = ['course.manage']
    request.mockImplementation((_path: string, options?: { method?: string }) => Promise.resolve(options?.method ? null : categories))
  })

  it('lists categories as rows with their course counts and opens nothing by default', async () => {
    render(<CategoriesPage />)
    expect(await screen.findByText('Science')).toBeInTheDocument()
    expect(screen.getByText('2 courses')).toBeInTheDocument()
    expect(screen.getByText('0 courses')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('only lets an unused category be deleted, after confirming', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<CategoriesPage />)
    await screen.findByText('Science')
    expect(screen.getByRole('button', { name: 'Delete category Science' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'Delete category Arts' }))
    await waitFor(() => expect(callsTo('DELETE')).toHaveLength(1))
    expect(String(callsTo('DELETE')[0][0])).toContain('/categories/c2')
    expect(await screen.findByText(/deleted/)).toBeInTheDocument()
    confirm.mockRestore()
  })

  it('creates a category from a panel, checking the name first', async () => {
    render(<CategoriesPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'New category' }))
    expect(screen.getByRole('dialog', { name: 'New category' })).toBeInTheDocument()
    expect(screen.getByText('Name')).toHaveClass("after:content-['*'/'']")

    await userEvent.type(screen.getByLabelText('Name'), 'a')
    await userEvent.click(screen.getByRole('button', { name: 'Create category' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('at least 2 characters')
    expect(callsTo('POST')).toHaveLength(0)

    await userEvent.clear(screen.getByLabelText('Name'))
    await userEvent.type(screen.getByLabelText('Name'), 'Languages')
    await userEvent.click(screen.getByRole('button', { name: 'Create category' }))
    await waitFor(() => expect(callsTo('POST')).toHaveLength(1))
    expect(JSON.parse(callsTo('POST')[0][1].body)).toEqual({ name: 'Languages' })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByText(/Languages” created/)).toBeInTheDocument()
  })

  it('searches by name', async () => {
    render(<CategoriesPage />)
    await screen.findByText('Science')
    await userEvent.type(screen.getByLabelText('Search categories'), 'art')
    expect(screen.queryByText('Science')).toBeNull()
    expect(screen.getByText('Arts')).toBeInTheDocument()
  })

  it('hides creating and deleting from people who cannot manage courses', async () => {
    permissions = []
    render(<CategoriesPage />)
    await screen.findByText('Science')
    expect(screen.queryByRole('button', { name: 'New category' })).toBeNull()
    expect(screen.queryByRole('button', { name: /Delete category/ })).toBeNull()
  })
})
