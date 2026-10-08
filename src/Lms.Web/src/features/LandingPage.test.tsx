import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import LandingPage, { normalizeOrganization, validateOrganization } from './LandingPage'
import { seatsLabel, whenLabel, coverFor } from './landing/parts'
import { isSafeLink, validateApplication, type PublicLanding } from '@/lib/publicApi'
import { organizationFromUrl } from '@/lib/organization'

describe('organization names', () => {
  it('are tidied: spaces trimmed and letters lower-cased', () => expect(normalizeOrganization('  Riverside-School ')).toBe('riverside-school'))
  it('accept 3 to 63 letters, numbers and inner hyphens', () => {
    for (const ok of ['acme', 'ab1', 'a-b', 'riverside-school-2026', 'x'.repeat(63)]) expect(validateOrganization(ok), ok).toBeNull()
  })
  it('refuse everything else with a reason', () => {
    expect(validateOrganization('   ')).toMatch(/Enter your organization/)
    for (const bad of ['ab', '-abc', 'abc-', 'has space', 'under_score', 'dot.name', 'x'.repeat(64), 'ünï']) expect(validateOrganization(bad), bad).toMatch(/3 to 63/)
  })
  it('can be named in the address, and only when valid', () => {
    expect(organizationFromUrl('?org=Acme')).toBe('acme')
    expect(organizationFromUrl('?org=a')).toBeNull()
    expect(organizationFromUrl('')).toBeNull()
  })
})

describe('helpers', () => {
  it('say when a course starts, is open, or has ended', () => {
    const today = new Date('2026-10-10T12:00:00')
    expect(whenLabel({ startDate: '2026-12-01', endDate: null }, today)).toMatch(/^Starts .*2026/)
    expect(whenLabel({ startDate: '2026-01-01', endDate: '2026-12-31' }, today)).toBe('Open enrollment')
    expect(whenLabel({ startDate: null, endDate: null }, today)).toBe('Open enrollment')
    expect(whenLabel({ startDate: '2025-01-01', endDate: '2025-06-01' }, today)).toBe('Ended')
  })
  it('only mention seats when they are few', () => {
    expect(seatsLabel(null)).toBeNull(); expect(seatsLabel(40)).toBeNull()
    expect(seatsLabel(1)).toBe('1 seat left'); expect(seatsLabel(7)).toBe('7 seats left'); expect(seatsLabel(0)).toBe('Full: join the waitlist')
  })
  it('give a course the same colours every time', () => { expect(coverFor('Science')).toBe(coverFor('Science')); expect(coverFor('Science')).toMatch(/^from-/) })
  it('accept only safe links', () => {
    for (const ok of ['#faq', '/?org=a', 'https://example.org', 'mailto:a@b.c']) expect(isSafeLink(ok), ok).toBe(true)
    for (const bad of ['javascript:alert(1)', 'http://example.org', '//evil.org', '', '#', 'plain']) expect(isSafeLink(bad), bad).toBe(false)
  })
  it('check an application before it is sent', () => {
    const ok = { fullName: 'Nina Newcomer', email: 'nina@example.org', phone: '', message: '', website: '' }
    expect(validateApplication(ok)).toBeNull()
    expect(validateApplication({ ...ok, fullName: 'N' })).toMatch(/name/)
    expect(validateApplication({ ...ok, email: 'nope' })).toMatch(/email/)
    expect(validateApplication({ ...ok, phone: '9'.repeat(41) })).toMatch(/phone/)
    expect(validateApplication({ ...ok, message: 'm'.repeat(1001) })).toMatch(/message/)
  })
})

const landing = (over: Partial<PublicLanding> = {}): PublicLanding => ({
  organization: { slug: 'acme', name: 'Acme Academy' },
  content: {
    hero: { title: 'Learn without limits', subtitle: 'Courses and live classes from Acme.', primaryLabel: 'Explore courses', primaryLink: '#courses', searchPlaceholder: 'What do you want to learn?' },
    banners: [
      { id: 'b1', title: 'Apply for a course', text: 'Pick one and apply.', buttonLabel: 'Browse', link: '#courses', theme: 'blue' },
      { id: 'b2', title: 'Learn together, live', text: 'Live classes.', buttonLabel: 'How it works', link: '#faq', theme: 'green' },
    ],
    intents: { title: 'What brings you here?', items: [{ label: 'Start my career', link: '#courses' }, { label: 'Read our blog', link: 'https://blog.example.org' }] },
    rows: [], showCategories: true, categoriesTitle: 'Explore categories', featuresTitle: 'Why learn with us',
    features: [{ title: 'Live classes', text: 'Join your teacher live.', icon: 'video' }], stats: [{ value: '5,000', label: 'learners' }],
    testimonialsTitle: 'What learners say', testimonials: [{ name: 'Sarah W.', role: 'Data analyst', quote: 'Flexible and practical.' }],
    faqTitle: 'Frequently asked questions', faq: [{ question: 'Is it free to apply?', answer: 'Yes, applying is free.' }],
    footerAbout: 'Acme helps you learn.', footerGroups: [{ title: 'Learn', links: [{ label: 'Courses', url: '#courses' }, { label: 'Log in', url: '#login' }, { label: 'Elsewhere', url: 'https://example.org/x' }] }], copyright: '© 2026 Acme Academy',
  },
  courses: [
    { id: 'c1', code: 'MATH-1', title: 'Algebra Foundations', summary: 'Start from zero.', categoryId: 'k1', category: 'Mathematics', teacher: 'Tara Teacher', startDate: null, endDate: null, seatsLeft: 3 },
    { id: 'c2', code: 'ART-1', title: 'Drawing Basics', summary: 'Pencil and paper.', categoryId: 'k2', category: 'Art', teacher: 'Olga Artist', startDate: '2030-01-01', endDate: null, seatsLeft: null },
    { id: 'c3', code: 'MATH-2', title: 'Geometry', summary: 'Shapes.', categoryId: 'k1', category: 'Mathematics', teacher: 'Tara Teacher', startDate: null, endDate: null, seatsLeft: 0 },
  ],
  categories: [{ id: 'k1', name: 'Mathematics', courses: 2 }, { id: 'k2', name: 'Art', courses: 1 }],
  rows: [{ id: 'r1', title: 'Most popular', subtitle: 'What others take', courseIds: ['c1', 'c3'] }, { id: 'r2', title: 'New', subtitle: null, courseIds: ['c2'] }],
  ...over,
})

const detail = { id: 'c1', code: 'MATH-1', title: 'Algebra Foundations', description: 'A gentle start.', category: 'Mathematics', teacher: 'Tara Teacher', startDate: null, endDate: null, seatsLeft: 3, modules: [{ title: 'Numbers', lessons: ['Counting', 'Fractions'] }, { title: 'Equations', lessons: ['One unknown'] }] }
const json = (status: number, body: unknown) => Promise.resolve(new Response(JSON.stringify(body), { status }))

describe('LandingPage', () => {
  const onSignIn = vi.fn()
  const onJoin = vi.fn()
  const fetchMock = vi.fn()
  const calls = (fragment: string, method?: string) => fetchMock.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || (call[1]?.method ?? 'GET') === method))

  beforeEach(() => {
    onSignIn.mockReset(); onJoin.mockReset(); fetchMock.mockReset(); localStorage.clear()
    window.history.pushState({}, '', '/?org=acme')
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockImplementation((path: string, init?: RequestInit) => {
      if (path.endsWith('/organizations/default')) return json(404, { message: 'none' })
      if (path.endsWith('/acme/landing')) return json(200, landing())
      if (path.endsWith('/courses/c1') ) return json(200, detail)
      if (path.endsWith('/applications') && init?.method === 'POST') return json(202, { message: 'Thank you! We received your application.' })
      return json(404, { message: 'not found' })
    })
    window.HTMLElement.prototype.scrollIntoView = vi.fn()
  })
  afterEach(() => { vi.unstubAllGlobals(); window.history.pushState({}, '', '/') })

  const renderPage = () => render(<LandingPage onSignIn={onSignIn} onJoin={onJoin} />)
  const ready = async () => { renderPage(); await screen.findByRole('heading', { level: 1, name: 'Learn without limits' }) }

  it('shows the organization, its heading, banners, choices, course rows, categories, reasons, numbers, stories, questions and footer', async () => {
    await ready()
    expect(screen.getByRole('link', { name: 'Acme Academy home' })).toBeInTheDocument()
    expect(screen.getByText('Courses and live classes from Acme.')).toBeInTheDocument()
    const featured = screen.getByRole('region', { name: 'Featured' })
    expect(within(featured).getByRole('heading', { name: 'Apply for a course' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start my career' })).toBeInTheDocument()
    const popular = screen.getByRole('region', { name: 'Most popular' })
    expect(within(popular).getByText('What others take')).toBeInTheDocument()
    expect(within(popular).getAllByRole('button', { name: /View details and apply/ })).toHaveLength(2)
    expect(within(popular).getByText('3 seats left')).toBeInTheDocument()
    expect(within(popular).getByText('Full: join the waitlist')).toBeInTheDocument()
    const art = screen.getByRole('region', { name: 'New' })
    expect(within(art).getByText(/^Starts /)).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Explore categories' })).toHaveTextContent('Mathematics')
    expect(screen.getByRole('region', { name: 'Why learn with us' })).toHaveTextContent('Join your teacher live.')
    expect(screen.getByRole('region', { name: 'In numbers' })).toHaveTextContent('5,000')
    expect(screen.getByRole('region', { name: 'What learners say' })).toHaveTextContent('Flexible and practical.')
    expect(screen.getByRole('region', { name: 'Frequently asked questions' })).toHaveTextContent('Is it free to apply?')
    expect(screen.getByText('© 2026 Acme Academy')).toBeInTheDocument()
    expect(localStorage.getItem('lms-organization')).toBe('acme')               // remembered for next time
  })

  it('leaves out sections that have nothing in them', async () => {
    fetchMock.mockImplementation((path: string) => path.endsWith('/acme/landing') ? json(200, landing({ content: { ...landing().content, banners: [], features: [], stats: [], testimonials: [], faq: [], showCategories: false, intents: { title: '', items: [] } }, rows: [] })) : json(404, {}))
    await ready()
    for (const name of ['Featured', 'In numbers', 'What learners say', 'Frequently asked questions', 'Why learn with us', 'Explore categories']) expect(screen.queryByRole('region', { name })).toBeNull()
    expect(screen.getByRole('region', { name: 'Courses' })).toBeInTheDocument()       // with no rows chosen, every course is still shown
  })

  it('says so when there are no courses yet', async () => {
    fetchMock.mockImplementation((path: string) => path.endsWith('/acme/landing') ? json(200, landing({ courses: [], rows: [], categories: [] })) : json(404, {}))
    await ready()
    expect(screen.getByText(/No courses are open yet/)).toBeInTheDocument()
  })

  it('searches courses by what is typed and shows how many were found, and clears again', async () => {
    await ready()
    await userEvent.type(screen.getAllByLabelText('Search courses')[0], 'draw')
    const results = await screen.findByRole('region', { name: 'Search results' })
    expect(within(results).getByRole('status')).toHaveTextContent('1 course for “draw”')
    expect(within(results).getByText('Drawing Basics')).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Most popular' })).toBeNull()
    await userEvent.click(within(results).getByRole('button', { name: 'Clear search' }))
    expect(await screen.findByRole('region', { name: 'Most popular' })).toBeInTheDocument()
    await userEvent.type(screen.getAllByLabelText('Search courses')[0], 'zzzz')
    expect(await screen.findByText(/No course matches/)).toBeInTheDocument()
  })

  it('filters by category from the Explore menu and from the category cards', async () => {
    await ready()
    await userEvent.click(screen.getByRole('button', { name: 'Explore' }))
    await userEvent.click(within(screen.getByRole('menu', { name: 'Categories' })).getByRole('menuitem', { name: /Art/ }))
    let results = await screen.findByRole('region', { name: 'Search results' })
    expect(within(results).getByRole('status')).toHaveTextContent('1 course in Art')
    await userEvent.click(within(screen.getByRole('region', { name: 'Explore categories' })).getByRole('button', { name: /Mathematics/ }))
    results = await screen.findByRole('region', { name: 'Search results' })
    expect(within(results).getByRole('status')).toHaveTextContent('2 courses in Mathematics')
  })

  it('opens a course with its outline and sends an application, with the hidden box empty', async () => {
    await ready()
    await userEvent.click(within(screen.getByRole('region', { name: 'Most popular' })).getByRole('button', { name: /Algebra Foundations/ }))
    await screen.findByRole('heading', { level: 2, name: 'Algebra Foundations' })
    const dialog = screen.getByRole('main')   // the course has a page of its own now
    expect(await within(dialog).findByText('A gentle start.')).toBeInTheDocument()
    expect(within(dialog).getByText('Numbers')).toBeInTheDocument()
    expect(within(dialog).getByText('Fractions')).toBeInTheDocument()
    expect(within(dialog).getByText(/Taught by Tara Teacher/)).toBeInTheDocument()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Apply now' }))
    expect(await within(dialog).findByText(/Enter your name/)).toBeInTheDocument()
    expect(calls('/applications', 'POST')).toHaveLength(0)
    await userEvent.type(within(dialog).getByLabelText(/Full name/), 'Nina Newcomer')
    await userEvent.type(within(dialog).getByLabelText(/^Email/), 'nope')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Apply now' }))
    expect(await within(dialog).findByText(/valid email/)).toBeInTheDocument()
    await userEvent.clear(within(dialog).getByLabelText(/^Email/)); await userEvent.type(within(dialog).getByLabelText(/^Email/), 'nina@example.org')
    await userEvent.type(within(dialog).getByLabelText('Phone'), '9800000000')
    await userEvent.type(within(dialog).getByLabelText(/Why do you want/), 'To learn.')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Apply now' }))
    await waitFor(() => expect(calls('/courses/c1/applications', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/applications', 'POST')[0][1].body)).toEqual({ fullName: 'Nina Newcomer', email: 'nina@example.org', phone: '9800000000', message: 'To learn.', website: '' })
    const thanks = await within(dialog).findByRole('status', { name: 'Application sent' })
    expect(thanks).toHaveTextContent('We received your application')
    expect(within(dialog).queryByLabelText(/Full name/)).toBeNull()
  })

  it('shows the server message when an application is refused, and offers the way to log in', async () => {
    fetchMock.mockImplementation((path: string, init?: RequestInit) => path.endsWith('/applications') && init?.method === 'POST' ? json(429, { message: 'Too many attempts. Try again later.' }) : path.endsWith('/acme/landing') ? json(200, landing()) : json(200, detail))
    await ready()
    await userEvent.click(within(screen.getByRole('region', { name: 'Most popular' })).getByRole('button', { name: /Algebra Foundations/ }))
    await screen.findByRole('heading', { level: 2, name: 'Algebra Foundations' })
    const dialog = screen.getByRole('main')   // the course has a page of its own now
    await userEvent.type(within(dialog).getByLabelText(/Full name/), 'Nina Newcomer'); await userEvent.type(within(dialog).getByLabelText(/^Email/), 'nina@example.org')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Apply now' }))
    expect(await within(dialog).findByText('Too many attempts. Try again later.')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Log in' }))
    expect(onSignIn).toHaveBeenCalledWith('acme')
  })

  it('does what the buttons say: log in, invitation, a section, another address, and nothing for an unsafe link', async () => {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null)
    await ready()
    const header = screen.getByRole('banner')
    await userEvent.click(within(header).getByRole('button', { name: 'Log in' }))
    expect(onSignIn).toHaveBeenCalledWith('acme')
    await userEvent.click(within(header).getByRole('button', { name: 'I have an invitation' }))
    expect(onJoin).toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Explore courses' }))
    expect(window.HTMLElement.prototype.scrollIntoView).toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Read our blog' }))
    expect(open).toHaveBeenCalledWith('https://blog.example.org', '_blank', 'noopener,noreferrer')
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Learn' })).getByRole('button', { name: 'Log in' }))
    expect(onSignIn).toHaveBeenCalledTimes(2)
    open.mockRestore()
  })

  it('turns the banners over with the arrows and the dots', async () => {
    await ready()
    const featured = screen.getByRole('region', { name: 'Featured' })
    await userEvent.click(within(featured).getByRole('button', { name: 'Next banner' }))
    expect(within(featured).getByRole('heading', { name: 'Learn together, live' })).toBeInTheDocument()
    await userEvent.click(within(featured).getByRole('button', { name: /Show banner 1/ }))
    expect(within(featured).getByRole('heading', { name: 'Apply for a course' })).toBeInTheDocument()
    await userEvent.click(within(featured).getByRole('button', { name: 'Previous banner' }))
    expect(within(featured).getByRole('heading', { name: 'Learn together, live' })).toBeInTheDocument()
  })

  it('opens a menu on small screens with search, categories and the same sign-in actions', async () => {
    await ready()
    await userEvent.click(screen.getByRole('button', { name: 'Open menu' }))
    const menu = screen.getByTestId('mobile-menu')
    expect(within(menu).getByLabelText('Search courses')).toBeInTheDocument()
    await userEvent.click(within(menu).getByRole('button', { name: /Art/ }))
    expect(await screen.findByRole('region', { name: 'Search results' })).toBeInTheDocument()
    expect(screen.queryByTestId('mobile-menu')).toBeNull()
  })

  it('on the shared portal asks for the organization to sign in to, with the remembered one filled in', async () => {
    window.history.pushState({}, '', '/')
    localStorage.setItem('lms-organization', 'acme')
    renderPage()
    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to your organization' })).toBeInTheDocument()
    expect(screen.getByLabelText('Your organization')).toHaveValue('acme')
    expect(calls('/landing')).toHaveLength(0)
    await userEvent.click(screen.getByRole('button', { name: 'Continue to sign in' }))
    expect(onSignIn).toHaveBeenCalledWith('acme')
  })

  it('on the shared portal refuses an empty or malformed organization and can preview the organization\'s courses', async () => {
    window.history.pushState({}, '', '/')
    renderPage()
    await screen.findByLabelText('Your organization')
    await userEvent.click(screen.getByRole('button', { name: 'Continue to sign in' }))
    expect(await screen.findByText(/short name/i)).toBeInTheDocument()
    expect(onSignIn).not.toHaveBeenCalled()
    await userEvent.type(screen.getByLabelText('Your organization'), ' Acme ')
    await userEvent.click(screen.getByRole('button', { name: 'See its courses' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Learn without limits' })).toBeInTheDocument()
    expect(calls('/acme/landing')).toHaveLength(1)
    await userEvent.click(within(screen.getByRole('banner')).getByRole('button', { name: 'Change organization' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to your organization' })).toBeInTheDocument()
  })

  it('on an organization\'s own website shows its page at once, never asks for an organization and ignores ?org=', async () => {
    window.history.pushState({}, '', '/?org=someone-else')
    render(<LandingPage fixed={{ slug: 'acme', name: 'Acme Academy' }} onSignIn={onSignIn} onJoin={onJoin} />)
    expect(await screen.findByRole('heading', { level: 1, name: 'Learn without limits' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Your organization')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Change organization' })).toBeNull()
    expect(calls('/someone-else/')).toHaveLength(0)
    await userEvent.click(within(screen.getByRole('banner')).getByRole('button', { name: 'Log in' }))
    expect(onSignIn).toHaveBeenCalledWith('acme')
  })

  it('on an organization\'s own website a failed load offers to try again instead of asking for another organization', async () => {
    fetchMock.mockImplementation(() => json(500, { message: 'boom' }))
    render(<LandingPage fixed={{ slug: 'acme', name: 'Acme Academy' }} onSignIn={onSignIn} onJoin={onJoin} />)
    expect(await screen.findByText(/could not be loaded/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: 'Acme Academy' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Your organization')).toBeNull()
  })

  it('says an organization was not found and lets the visitor try another', async () => {
    window.history.pushState({}, '', '/?org=nowhere')
    renderPage()
    expect(await screen.findByText(/organization was not found/)).toBeInTheDocument()
    expect(screen.getByLabelText('Your organization')).toBeInTheDocument()
  })

  it('shows a plain message when the page cannot be loaded at all', async () => {
    fetchMock.mockImplementation(() => Promise.reject(new TypeError('offline')))
    renderPage()
    await act(async () => { await Promise.resolve() })
    expect(await screen.findByText(/could not be loaded/)).toBeInTheDocument()
  })
})
