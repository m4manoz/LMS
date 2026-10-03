import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ApplicationsPage, { describeApproval } from './ApplicationsPage'
import LandingContentPage, { findProblem } from './LandingContentPage'
import { ApiError } from '@/lib/api'
import type { LandingContent, LandingEditorData } from '@/lib/publicApi'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
const calls = (fragment: string, method?: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || (call[1]?.method ?? 'GET') === method))

const content = (): LandingContent => ({
  hero: { title: 'Learn without limits', subtitle: 'Sub', primaryLabel: 'Explore courses', primaryLink: '#courses', searchPlaceholder: 'Search' },
  banners: [{ id: 'b1', title: 'Welcome', text: 'Hi', buttonLabel: 'Go', link: '#courses', theme: 'blue' }],
  intents: { title: 'What brings you here?', items: [{ label: 'Start my career', link: '#courses' }] },
  rows: [{ id: 'r1', title: 'Most popular', subtitle: null, mode: 'popular', categoryId: null, courseIds: [], limit: 8 }],
  showCategories: true, categoriesTitle: 'Explore categories', featuresTitle: 'Why learn with us', features: [{ title: 'Live classes', text: 'Join live.', icon: 'video' }],
  stats: [], testimonialsTitle: 'What learners say', testimonials: [], faqTitle: 'Questions', faq: [{ question: 'Is it free?', answer: 'Yes.' }],
  footerAbout: 'About us', footerGroups: [{ title: 'Learn', links: [{ label: 'Courses', url: '#courses' }] }], copyright: '© Acme',
})
const editor = (over: Partial<LandingEditorData> = {}): LandingEditorData => ({ content: content(), isDefault: true, updatedAtUtc: null, slug: 'acme', themes: ['blue', 'green', 'purple', 'amber', 'dark'], modes: ['newest', 'popular', 'category', 'manual'], icons: ['video', 'book', 'star'], ...over })

describe('findProblem', () => {
  it('accepts the standard content', () => expect(findProblem(content())).toBeNull())
  it('finds the first thing the server would refuse', () => {
    const base = content()
    expect(findProblem({ ...base, hero: { ...base.hero, title: ' ' } })).toMatch(/main heading/)
    expect(findProblem({ ...base, hero: { ...base.hero, primaryLink: 'javascript:alert(1)' } })).toMatch(/main button/)
    expect(findProblem({ ...base, banners: [{ ...base.banners[0], link: 'http://insecure.example.org' }] })).toMatch(/banner “Welcome”/)
    expect(findProblem({ ...base, banners: [{ ...base.banners[0], buttonLabel: '', link: 'nonsense' }] })).toBeNull()          // no button, so no link to check
    expect(findProblem({ ...base, rows: [{ ...base.rows[0], mode: 'category', categoryId: null }] })).toMatch(/Choose a category/)
    expect(findProblem({ ...base, rows: [{ ...base.rows[0], mode: 'manual', courseIds: [] }] })).toMatch(/at least one course/)
    expect(findProblem({ ...base, footerGroups: [{ title: 'Learn', links: [{ label: 'Bad', url: 'ftp://x' }] }] })).toMatch(/footer link/)
  })
})

describe('LandingContentPage', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path: string, options?: { method?: string; body?: string }) => {
      if (path === '/api/v1/tenant/landing' && !options?.method) return Promise.resolve(editor())
      if (path === '/api/v1/tenant/landing' && options?.method === 'PUT') return Promise.resolve(editor({ content: JSON.parse(options.body!), isDefault: false }))
      if (path.endsWith('/landing/reset')) return Promise.resolve(editor())
      if (path === '/api/v1/tenant/courses') return Promise.resolve([{ id: 'c1', code: 'MATH-1', title: 'Algebra', status: 'Published' }, { id: 'c2', code: 'DRAFT-1', title: 'Draft', status: 'Draft' }])
      if (path.includes('/catalog/categories')) return Promise.resolve([{ id: 'k1', name: 'Mathematics' }])
      return Promise.resolve(null)
    })
  })

  it('starts on the standard page with nothing to save, and a link to view the page', async () => {
    render(<LandingContentPage />)
    expect(await screen.findByText(/Visitors now see the standard page/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Save changes/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: /Use standard page/ })).toBeDisabled()
    expect(screen.getByRole('link', { name: /View page/ })).toHaveAttribute('href', '/?org=acme')
    expect(screen.getByLabelText(/Heading/, { selector: '#hero-title' })).toHaveValue('Learn without limits')
  })

  it('saves what was changed, then says visitors see it', async () => {
    render(<LandingContentPage />)
    const heading = await screen.findByLabelText(/Heading/, { selector: '#hero-title' })
    await userEvent.clear(heading); await userEvent.type(heading, 'Study with us')
    expect(screen.getByRole('status')).toHaveTextContent('not saved yet')
    await userEvent.click(screen.getByRole('button', { name: /Save changes/ }))
    await waitFor(() => expect(calls('/api/v1/tenant/landing', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('/api/v1/tenant/landing', 'PUT')[0][1].body).hero.title).toBe('Study with us')
    expect(await screen.findByText(/Visitors see the new page now/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Save changes/ })).toBeDisabled()
    expect(screen.queryByText(/Visitors now see the standard page/)).toBeNull()
  })

  it('adds, edits, reorders and removes banners', async () => {
    render(<LandingContentPage />)
    await screen.findByLabelText(/Heading/, { selector: '#hero-title' })
    await userEvent.click(screen.getByRole('button', { name: 'Add a banner' }))
    const list = screen.getByRole('list', { name: 'banner' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(2)
    await userEvent.type(screen.getByLabelText(/Title/, { selector: '#banner-title-1' }), 'Second banner')
    await userEvent.click(screen.getByRole('button', { name: 'Move banner 2 up' }))
    expect(screen.getByLabelText(/Title/, { selector: '#banner-title-0' })).toHaveValue('Second banner')
    await userEvent.click(screen.getByRole('button', { name: 'Remove banner 1' }))
    expect(within(screen.getByRole('list', { name: 'banner' })).getAllByRole('listitem')).toHaveLength(1)
    expect(screen.getByLabelText(/Title/, { selector: '#banner-title-0' })).toHaveValue('Welcome')
  })

  it('does not send a page the server would refuse, and says what to fix', async () => {
    render(<LandingContentPage />)
    const link = await screen.findByLabelText(/Button link/, { selector: '#hero-link' })
    await userEvent.clear(link); await userEvent.type(link, 'javascript:alert(1)')
    await userEvent.click(screen.getByRole('button', { name: /Save changes/ }))
    expect(await screen.findByText(/main button needs a link/)).toBeInTheDocument()
    expect(calls('/api/v1/tenant/landing', 'PUT')).toHaveLength(0)
  })

  it('shows the server message when saving fails', async () => {
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (options?.method === 'PUT') return Promise.resolve().then(() => { throw new ApiError('A page can have at most 6 banners.', 400) })
      return Promise.resolve(path === '/api/v1/tenant/landing' ? editor() : [])
    })
    render(<LandingContentPage />)
    const heading = await screen.findByLabelText(/Heading/, { selector: '#hero-title' })
    await userEvent.type(heading, '!')
    await userEvent.click(screen.getByRole('button', { name: /Save changes/ }))
    expect(await screen.findByText('A page can have at most 6 banners.')).toBeInTheDocument()
  })

  it('lets a row show a category or chosen published courses', async () => {
    render(<LandingContentPage />)
    await screen.findByLabelText(/Heading/, { selector: '#hero-title' })
    await userEvent.click(screen.getByRole('tab', { name: 'Course rows' }))
    await userEvent.selectOptions(screen.getByLabelText('Which courses'), 'category')
    expect(screen.getByLabelText(/Category/, { selector: '#row-cat-0' })).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText(/Category/, { selector: '#row-cat-0' }), 'k1')
    await userEvent.selectOptions(screen.getByLabelText('Which courses'), 'manual')
    const checks = within(screen.getByRole('group', { name: /Courses \(published ones\)/ })).getAllByRole('checkbox')
    expect(checks).toHaveLength(1)                                                                // drafts are not offered
    await userEvent.click(checks[0])
    await userEvent.click(screen.getByRole('button', { name: /Save changes/ }))
    await waitFor(() => expect(calls('/api/v1/tenant/landing', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('/api/v1/tenant/landing', 'PUT')[0][1].body).rows[0]).toMatchObject({ mode: 'manual', courseIds: ['c1'], categoryId: null })
  })

  it('edits the footer columns and links', async () => {
    render(<LandingContentPage />)
    await screen.findByLabelText(/Heading/, { selector: '#hero-title' })
    await userEvent.click(screen.getByRole('tab', { name: 'Footer' }))
    await userEvent.click(screen.getByRole('button', { name: 'Add a link' }))
    const links = screen.getByRole('list', { name: 'link in column 1' })
    expect(within(links).getAllByRole('listitem')).toHaveLength(2)
    await userEvent.type(screen.getByLabelText(/Label/, { selector: '#group-0-link-label-1' }), 'Blog')
    await userEvent.type(screen.getByLabelText(/Address/, { selector: '#group-0-link-url-1' }), 'https://blog.example.org')
    await userEvent.click(screen.getByRole('button', { name: /Save changes/ }))
    await waitFor(() => expect(calls('/api/v1/tenant/landing', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('/api/v1/tenant/landing', 'PUT')[0][1].body).footerGroups[0].links[1]).toEqual({ label: 'Blog', url: 'https://blog.example.org' })
  })

  it('goes back to the standard page after confirming', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    request.mockImplementation((path: string) => path.endsWith('/landing/reset') ? Promise.resolve(editor()) : path === '/api/v1/tenant/landing' ? Promise.resolve(editor({ isDefault: false })) : Promise.resolve([]))
    render(<LandingContentPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Use standard page/ }))
    await waitFor(() => expect(calls('/landing/reset', 'POST')).toHaveLength(1))
    expect(await screen.findByText('The standard page is back.')).toBeInTheDocument()
  })
})

const application = (over: Record<string, unknown> = {}) => ({ id: 'a1', courseId: 'c1', courseTitle: 'Algebra', fullName: 'Nina Newcomer', email: 'nina@example.org', phone: '98', message: 'I want to learn.', status: 'Pending', createdAtUtc: '2026-10-01T10:00:00Z', decidedAtUtc: null, invitationId: null, ...over })
const approval = (over: Record<string, unknown> = {}) => ({ application: application({ status: 'Approved' }), token: 'CODE123', link: 'https://app.example.org/#invite=CODE123&tenant=acme', expiresAtUtc: '2026-10-15T10:00:00Z', emailStatus: 'Sent', emailError: null, ...over })

describe('describeApproval', () => {
  it('says where the invitation went', () => {
    expect(describeApproval(approval() as never)).toBe('An invitation was emailed to nina@example.org.')
    expect(describeApproval(approval({ emailStatus: 'NotNeeded' }) as never)).toMatch(/already has an account/)
    expect(describeApproval(approval({ emailStatus: 'AlreadyEnrolled' }) as never)).toMatch(/already in the course/)
    expect(describeApproval(approval({ emailStatus: 'Failed', emailError: 'SMTP down' }) as never)).toMatch(/could not be sent \(SMTP down\)\. Share the link/)
  })
})

describe('ApplicationsPage', () => {
  let list: ReturnType<typeof application>[]
  beforeEach(() => {
    request.mockReset(); list = [application(), application({ id: 'a2', fullName: 'Omar Other', email: 'omar@example.org', message: null, phone: null })]
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path.startsWith('/api/v1/tenant/applications') && !options?.method) return Promise.resolve(list)
      if (path.endsWith('/approve')) { list = list.filter((item) => !path.includes(item.id)); return Promise.resolve(approval()) }
      if (path.endsWith('/decline')) { list = list.filter((item) => !path.includes(item.id)); return Promise.resolve(application({ status: 'Declined' })) }
      return Promise.resolve(null)
    })
  })

  it('lists the waiting applications with what the person wrote, and filters by status', async () => {
    render(<ApplicationsPage />)
    const rows = await screen.findByRole('list', { name: 'Applications' })
    expect(within(rows).getByText('Nina Newcomer')).toBeInTheDocument()
    expect(within(rows).getByText(/I want to learn\./)).toBeInTheDocument()
    expect(within(rows).getAllByText('Algebra')).toHaveLength(2)
    expect(calls('?status=Pending')).toHaveLength(1)
    await userEvent.selectOptions(screen.getByLabelText('Show applications'), 'All')
    await waitFor(() => expect(calls('/api/v1/tenant/applications', 'GET').some((call) => call[0] === '/api/v1/tenant/applications')).toBe(true))
  })

  it('approves one, says an invitation was emailed, and removes it from the waiting list', async () => {
    render(<ApplicationsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Approve Nina Newcomer' }))
    await waitFor(() => expect(calls('/a1/approve', 'POST')).toHaveLength(1))
    expect(await screen.findByText('An invitation was emailed to nina@example.org.')).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Invitation to share' })).toBeNull()               // email did the work
    await waitFor(() => expect(screen.queryByText('Nina Newcomer')).toBeNull())
  })

  it('gives staff the link and code to share when the email could not be sent', async () => {
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/approve') ? Promise.resolve(approval({ emailStatus: 'Failed', emailError: 'SMTP down' })) : Promise.resolve(options?.method ? null : list))
    render(<ApplicationsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Approve Nina Newcomer' }))
    const share = await screen.findByRole('region', { name: 'Invitation to share' })
    expect(within(share).getByText('CODE123')).toBeInTheDocument()
    expect(within(share).getByText(/#invite=CODE123/)).toBeInTheDocument()
  })

  it('declines after confirming, and shows the server message when something fails', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<ApplicationsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Decline Omar Other' }))
    await waitFor(() => expect(calls('/a2/decline', 'POST')).toHaveLength(1))
    expect(await screen.findByText(/Omar Other.s application was declined/)).toBeInTheDocument()
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/approve') ? Promise.resolve().then(() => { throw new ApiError('The course is not published any more, so it cannot take new learners.', 409) }) : Promise.resolve(options?.method ? null : list))
    await userEvent.click(await screen.findByRole('button', { name: 'Approve Nina Newcomer' }))
    expect(await screen.findByText(/not published any more/)).toBeInTheDocument()
  })

  it('says when nothing is waiting', async () => {
    request.mockImplementation(() => Promise.resolve([]))
    render(<ApplicationsPage />)
    expect(await screen.findByText('No applications are waiting.')).toBeInTheDocument()
  })
})
