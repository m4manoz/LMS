import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import LandingPage from './LandingPage'
import { filterCourses } from './landing/CatalogPage'
import { relatedCourses } from './landing/CoursePage'
import type { PublicCourse, PublicLanding } from '@/lib/publicApi'
import { catalogRoute, parseSiteRoute, siteHref } from '@/lib/siteRoutes'

const course = (over: Partial<PublicCourse> & { id: string; title: string }): PublicCourse => ({
  code: over.id.toUpperCase(), summary: '', categoryId: null, category: null, teacher: null, startDate: null, endDate: null, seatsLeft: null, ratingAverage: null, ratingCount: 0, ...over,
})
const courses: PublicCourse[] = [
  course({ id: 'c1', title: 'Algebra Foundations', summary: 'Start from zero.', categoryId: 'k1', category: 'Mathematics', teacher: 'Tara Teacher', ratingAverage: 4.2, ratingCount: 10 }),
  course({ id: 'c2', title: 'Drawing Basics', summary: 'Pencil and paper.', categoryId: 'k2', category: 'Art', teacher: 'Olga Artist', ratingAverage: 4.8, ratingCount: 3 }),
  course({ id: 'c3', title: 'Geometry', summary: 'Shapes.', categoryId: 'k1', category: 'Mathematics', teacher: 'Tara Teacher', ratingAverage: 4.2, ratingCount: 40 }),
  course({ id: 'c4', title: 'Basket Weaving', summary: 'With cane.', categoryId: 'k2', category: 'Art' }),
]

describe('site routes', () => {
  it('turn addresses into pages and back', () => {
    expect(parseSiteRoute('')).toEqual({ page: 'home' })
    expect(parseSiteRoute('#/')).toEqual({ page: 'home' })
    expect(parseSiteRoute('#/about')).toEqual({ page: 'about' })
    expect(parseSiteRoute('#/course/c1')).toEqual({ page: 'course', id: 'c1' })
    expect(parseSiteRoute('#/courses')).toEqual(catalogRoute())
    expect(parseSiteRoute('#/courses?category=k1&q=alg%20ebra&sort=rating')).toEqual(catalogRoute({ category: 'k1', q: 'alg ebra', sort: 'rating' }))
    for (const route of [{ page: 'home' }, { page: 'about' }, { page: 'course', id: 'c1' }, catalogRoute({ category: 'k2', q: 'draw', sort: 'title' }), catalogRoute()] as const) expect(parseSiteRoute(siteHref(route))).toEqual(route)
  })

  it('leave every other address alone, so sections, invitations and the operator console keep working', () => {
    for (const hash of ['#faq', '#courses', '#invite=abc&tenant=acme', '#reset=abc', '#/platform', '#/course', '#/course/a/b', '#/nonsense']) expect(parseSiteRoute(hash)).toBeNull()
  })

  it('ignore a bad sort or category and cut a long search', () => {
    expect(parseSiteRoute('#/courses?sort=popularity-contest&category=%3Cscript%3E')).toEqual(catalogRoute())
    expect(parseSiteRoute(`#/courses?q=${'x'.repeat(500)}`)).toMatchObject({ page: 'courses' })
    expect((parseSiteRoute(`#/courses?q=${'x'.repeat(500)}`) as { q: string }).q).toHaveLength(100)
  })

  it('keep the catalog address short by leaving out what is at its default', () => {
    expect(siteHref(catalogRoute())).toBe('#/courses')
    expect(siteHref(catalogRoute({ q: '  ', sort: 'newest' }))).toBe('#/courses')
    expect(siteHref(catalogRoute({ category: 'k1' }))).toBe('#/courses?category=k1')
  })
})

describe('filtering and ordering the catalog', () => {
  const titles = (list: PublicCourse[]) => list.map((item) => item.title)

  it('needs every word, in the title, summary, category or teacher', () => {
    expect(titles(filterCourses(courses, { category: null, q: 'tara', sort: 'newest' }))).toEqual(['Algebra Foundations', 'Geometry'])
    expect(titles(filterCourses(courses, { category: null, q: 'art cane', sort: 'newest' }))).toEqual(['Basket Weaving'])
    expect(filterCourses(courses, { category: null, q: 'nothing like this', sort: 'newest' })).toEqual([])
  })

  it('narrows to a category and keeps the newest-first order by default', () => {
    expect(titles(filterCourses(courses, { category: 'k2', q: '', sort: 'newest' }))).toEqual(['Drawing Basics', 'Basket Weaving'])
  })

  it('sorts by rating (more raters win a tie, unrated last) and by title', () => {
    expect(titles(filterCourses(courses, { category: null, q: '', sort: 'rating' }))).toEqual(['Drawing Basics', 'Geometry', 'Algebra Foundations', 'Basket Weaving'])
    expect(titles(filterCourses(courses, { category: null, q: '', sort: 'title' }))).toEqual(['Algebra Foundations', 'Basket Weaving', 'Drawing Basics', 'Geometry'])
  })

  it('suggests courses from the same category first, never the course itself', () => {
    expect(titles(relatedCourses(courses, courses[0]))).toEqual(['Geometry', 'Drawing Basics', 'Basket Weaving'])
    expect(titles(relatedCourses(courses, courses[0], 2))).toEqual(['Geometry', 'Drawing Basics'])
    expect(relatedCourses([courses[0]], courses[0])).toEqual([])
  })
})

const landing = (): PublicLanding => ({
  organization: { slug: 'acme', name: 'Acme Academy' },
  content: {
    hero: { title: 'Learn without limits', subtitle: 'Courses from Acme.', primaryLabel: 'Explore courses', primaryLink: '#courses', searchPlaceholder: 'Search' },
    banners: [], intents: { title: '', items: [] }, rows: [], showCategories: true, categoriesTitle: 'Explore categories', featuresTitle: 'Why learn with us',
    features: [{ title: 'Live classes', text: 'Join your teacher live.', icon: 'video' }], stats: [{ value: '5,000', label: 'learners' }],
    testimonialsTitle: 'What learners say', testimonials: [{ name: 'Sarah W.', role: 'Analyst', quote: 'Flexible and practical.' }],
    faqTitle: 'Questions', faq: [{ question: 'Is it free to apply?', answer: 'Yes, applying is free.' }],
    footerAbout: 'Acme helps you learn.', footerGroups: [{ title: 'Learn', links: [{ label: 'Courses', url: '#courses' }, { label: 'Questions', url: '#faq' }] }], copyright: '© Acme',
  },
  courses, categories: [{ id: 'k1', name: 'Mathematics', courses: 2 }, { id: 'k2', name: 'Art', courses: 2 }],
  rows: [{ id: 'r1', title: 'Most popular', subtitle: null, courseIds: ['c1', 'c3'] }],
})
const detail = { id: 'c1', code: 'C1', title: 'Algebra Foundations', description: 'A gentle start.', category: 'Mathematics', teacher: 'Tara Teacher', startDate: null, endDate: null, seatsLeft: 3, modules: [{ title: 'Numbers', lessons: ['Counting'] }] }
const json = (status: number, body: unknown) => Promise.resolve(new Response(JSON.stringify(body), { status }))

describe('the website', () => {
  const onSignIn = vi.fn()
  const fetchMock = vi.fn()
  beforeEach(() => {
    onSignIn.mockReset(); fetchMock.mockReset(); localStorage.clear()
    window.history.pushState({}, '', '/')
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockImplementation((path: string) => path.endsWith('/acme/landing') ? json(200, landing()) : path.includes('/courses/c1') ? json(200, detail) : json(404, { message: 'not found' }))
    window.HTMLElement.prototype.scrollIntoView = vi.fn()
  })
  afterEach(() => { vi.unstubAllGlobals(); window.history.pushState({}, '', '/') })

  const open = async (hash = '') => {
    window.location.hash = hash
    render(<LandingPage fixed={{ slug: 'acme', name: 'Acme Academy' }} onSignIn={onSignIn} onJoin={vi.fn()} />)
    await screen.findByRole('navigation', { name: 'Main' })
  }

  it('has a main menu to every page, and marks the one you are on', async () => {
    await open()
    const menu = screen.getByRole('navigation', { name: 'Main' })
    expect(within(menu).getByRole('link', { name: 'Home' })).toHaveAttribute('aria-current', 'page')
    expect(within(menu).getByRole('link', { name: 'Courses' })).toHaveAttribute('href', '#/courses')
    expect(within(menu).getByRole('link', { name: 'About' })).toHaveAttribute('href', '#/about')
    await userEvent.click(within(menu).getByRole('link', { name: 'About' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'About Acme Academy' })).toBeInTheDocument()
    expect(within(menu).getByRole('link', { name: 'About' })).toHaveAttribute('aria-current', 'page')
  })

  it('shows every course in the catalog and filters by category and search, in the address too', async () => {
    await open('#/courses')
    expect(await screen.findByRole('heading', { level: 1, name: 'All courses' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('4 courses')
    await userEvent.click(within(screen.getByRole('group', { name: 'Categories' })).getByRole('button', { name: /Art/ }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Art' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('2 courses in Art')
    expect(window.location.hash).toBe('#/courses?category=k2')
    await userEvent.type(screen.getByLabelText('Search the catalog'), 'cane')
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('1 course in Art for “cane”'))
    await waitFor(() => expect(window.location.hash).toBe('#/courses?category=k2&q=cane'))
    await userEvent.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'All courses' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('4 courses')
  })

  it('opens the catalog already filtered when the address says so, and sorts it', async () => {
    await open('#/courses?category=k1&sort=rating')
    expect(await screen.findByRole('heading', { level: 1, name: 'Mathematics' })).toBeInTheDocument()
    const cards = screen.getAllByRole('button', { name: /View details and apply/ })
    expect(cards.map((card) => card.getAttribute('aria-label')?.split('.')[0])).toEqual(['Geometry', 'Algebra Foundations'])   // equal ratings: the one more people rated first
    await userEvent.selectOptions(screen.getByLabelText('Sort courses'), 'title')
    expect(window.location.hash).toBe('#/courses?category=k1&sort=title')
  })

  it('says so when no course matches or none exist', async () => {
    await open('#/courses?q=zzzz')
    expect(await screen.findByText(/No course matches/)).toBeInTheDocument()
  })

  it('opens a course on its own page from a card, with a way back and more like it', async () => {
    await open('#/courses')
    await userEvent.click(await screen.findByRole('button', { name: /Algebra Foundations\. View details/ }))
    expect(await screen.findByRole('heading', { level: 2, name: 'Algebra Foundations' })).toBeInTheDocument()
    expect(window.location.hash).toBe('#/course/c1')
    expect(await screen.findByText('A gentle start.')).toBeInTheDocument()
    const crumbs = screen.getByRole('navigation', { name: 'Breadcrumb' })
    expect(within(crumbs).getByRole('link', { name: 'Courses' })).toHaveAttribute('href', '#/courses')
    expect(screen.getByRole('heading', { name: 'More in Mathematics' })).toBeInTheDocument()
    expect(document.title).toBe('Algebra Foundations · Acme Academy')
    // Opening one of the suggestions goes to that course.
    await userEvent.click(within(screen.getByRole('region', { name: 'More courses' })).getByRole('button', { name: /Geometry\. View details/ }))
    expect(await screen.findByRole('heading', { level: 2, name: 'Geometry' })).toBeInTheDocument()
    expect(window.location.hash).toBe('#/course/c3')
  })

  it('opens a shared course link straight away, and says kindly when the course is gone', async () => {
    await open('#/course/c1')
    expect(await screen.findByRole('heading', { level: 2, name: 'Algebra Foundations' })).toBeInTheDocument()
    window.location.hash = '#/course/not-there'
    expect(await screen.findByRole('heading', { level: 1, name: 'That course is not available' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'See all courses' })).toHaveAttribute('href', '#/courses')
  })

  it('gathers the organization’s own words on the About page', async () => {
    await open('#/about')
    expect(await screen.findByRole('heading', { level: 1, name: 'About Acme Academy' })).toBeInTheDocument()
    expect(screen.getByText('Acme helps you learn.', { selector: 'header p' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Why learn with us' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'In numbers' })).toBeInTheDocument()
    expect(screen.getByText('Flexible and practical.', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('Is it free to apply?')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Browse courses' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'All courses' })).toBeInTheDocument()
  })

  it('sends the footer’s section links to the page that has them when you are not on the home page', async () => {
    await open('#/about')
    await screen.findByRole('heading', { level: 1, name: 'About Acme Academy' })
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Learn' })).getByRole('button', { name: 'Courses' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'All courses' })).toBeInTheDocument()
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Learn' })).getByRole('button', { name: 'Questions' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'About Acme Academy' })).toBeInTheDocument()
  })

  it('searching in the header from another page opens the catalog with that search', async () => {
    await open('#/about')
    await screen.findByRole('heading', { level: 1, name: 'About Acme Academy' })
    await userEvent.type(screen.getAllByLabelText('Search courses')[0], 'draw{enter}')
    expect(await screen.findByRole('heading', { level: 1, name: 'All courses' })).toBeInTheDocument()
    expect(screen.getByLabelText('Search the catalog')).toHaveValue('draw')
    expect(screen.getByRole('status')).toHaveTextContent('1 course for “draw”')
  })

  it('keeps the home page as it was, with a way into the catalog', async () => {
    await open()
    expect(await screen.findByRole('heading', { level: 1, name: 'Learn without limits' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Most popular' })).toBeInTheDocument()
    await userEvent.click(within(screen.getByRole('region', { name: 'Most popular' })).getByRole('button', { name: /Geometry\. View details/ }))
    expect(await screen.findByRole('heading', { level: 2, name: 'Geometry' })).toBeInTheDocument()
  })
})
