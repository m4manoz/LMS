import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CourseRatingPanel from './CourseRatingPanel'
import CourseReviewsPanel from './CourseReviewsPanel'
import CourseDialog from './landing/CourseDialog'
import PictureField, { pictureProblem } from './landing/PictureField'
import { BannerCarousel, CourseCard, RatingLabel, SiteFooter } from './landing/parts'
import LandingContentPage from './LandingContentPage'
import type { LandingContent, LandingEditorData, PublicCourse, PublicCourseDetail } from '@/lib/publicApi'

const request = vi.fn()
const getPublicCourse = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/publicApi', async () => {
  const actual = await vi.importActual<typeof import('@/lib/publicApi')>('@/lib/publicApi')
  return { ...actual, getPublicCourse: (...args: unknown[]) => getPublicCourse(...args) }
})
const calls = (method: string, fragment = '') => request.mock.calls.filter((call) => (call[1]?.method ?? 'GET') === method && String(call[0]).includes(fragment))
const bodyOf = (call: unknown[]) => JSON.parse((call[1] as { body: string }).body)

beforeEach(() => { request.mockReset(); getPublicCourse.mockReset(); request.mockResolvedValue(null) })

describe('CourseRatingPanel', () => {
  const serve = (mine: unknown) => request.mockImplementation((_path: string, init?: { method?: string }) => Promise.resolve(init?.method ? null : mine))

  it('asks for stars first, then saves the rating with its review', async () => {
    serve({ rating: null, cannotRateBecause: null })
    render(<CourseRatingPanel courseId="c1" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Save rating' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose from 1 to 5 stars.')
    expect(calls('PUT')).toHaveLength(0)
    await userEvent.click(screen.getByLabelText('4 stars'))
    await userEvent.type(screen.getByLabelText('Your review'), 'Well paced')
    await userEvent.click(screen.getByRole('button', { name: 'Save rating' }))
    await waitFor(() => expect(calls('PUT', '/courses/c1/rating')).toHaveLength(1))
    expect(bodyOf(calls('PUT')[0])).toEqual({ stars: 4, review: 'Well paced' })
    expect(await screen.findByText('Thank you. Your rating is saved.')).toBeInTheDocument()
  })

  it('shows an existing rating, lets it be changed or removed, and says when staff hid it', async () => {
    serve({ rating: { stars: 2, review: 'Hmm', isHidden: true, updatedAtUtc: '2026-01-01T00:00:00Z' }, cannotRateBecause: null })
    render(<CourseRatingPanel courseId="c1" />)
    expect(await screen.findByText('Your rating')).toBeInTheDocument()
    expect(screen.getByLabelText('2 stars')).toBeChecked()
    expect(screen.getByLabelText('Your review')).toHaveValue('Hmm')
    expect(screen.getByRole('status')).toHaveTextContent('Staff have hidden your rating')
    await userEvent.click(screen.getByLabelText('5 stars'))
    await userEvent.click(screen.getByRole('button', { name: 'Update rating' }))
    await waitFor(() => expect(bodyOf(calls('PUT')[0])).toEqual({ stars: 5, review: 'Hmm' }))
    await userEvent.click(screen.getByRole('button', { name: 'Remove my rating' }))
    await waitFor(() => expect(calls('DELETE', '/courses/c1/rating')).toHaveLength(1))
  })

  it('shows no form to someone who cannot rate, only the reason', async () => {
    serve({ rating: null, cannotRateBecause: 'Start the course before you rate it.' })
    render(<CourseRatingPanel courseId="c1" />)
    expect(await screen.findByText('Start the course before you rate it.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save rating' })).toBeNull()
  })
})

describe('CourseReviewsPanel', () => {
  const rows = [
    { id: 'r1', learnerName: 'Lena', stars: 5, review: 'Lovely', isHidden: false, createdAtUtc: '2026-01-01T00:00:00Z', updatedAtUtc: '2026-01-02T00:00:00Z' },
    { id: 'r2', learnerName: 'Spam', stars: 1, review: 'Buy pills', isHidden: true, createdAtUtc: '2026-01-01T00:00:00Z', updatedAtUtc: '2026-01-02T00:00:00Z' },
  ]
  beforeEach(() => request.mockImplementation((_path: string, init?: { method?: string }) => Promise.resolve(init?.method ? null : { summary: { average: 5, count: 1, distribution: [0, 0, 0, 0, 1] }, ratings: rows })))

  it('lists every rating with the learner’s name and lets staff hide and show them', async () => {
    render(<CourseReviewsPanel courseId="c1" />)
    expect(await screen.findByText('Lena')).toBeInTheDocument()
    expect(screen.getByText('Hidden')).toBeInTheDocument()
    expect(screen.getByLabelText(/Rated 5\.0 out of 5 by 1 learner/)).toBeInTheDocument()   // only visible ratings count
    await userEvent.click(screen.getByRole('button', { name: 'Hide the rating by Lena' }))
    await waitFor(() => expect(calls('POST', '/ratings/r1/hide')).toHaveLength(1))
    await userEvent.click(screen.getByRole('button', { name: 'Show the rating by Spam' }))
    await waitFor(() => expect(calls('POST', '/ratings/r2/show')).toHaveLength(1))
  })

  it('says so when nobody has rated', async () => {
    request.mockImplementation(() => Promise.resolve({ summary: { average: null, count: 0, distribution: [0, 0, 0, 0, 0] }, ratings: [] }))
    render(<CourseReviewsPanel courseId="c1" />)
    expect(await screen.findByText('No learner has rated this course yet.')).toBeInTheDocument()
  })
})

describe('the public page', () => {
  const course: PublicCourse = { id: 'c1', code: 'MATH', title: 'Algebra', summary: 'Numbers', categoryId: null, category: 'Maths', teacher: 'Mr Rai', startDate: null, endDate: null, seatsLeft: null, ratingAverage: 4.5, ratingCount: 12 }

  it('shows stars with a spoken rating on a card, and nothing for an unrated course', () => {
    const { rerender } = render(<CourseCard course={course} onOpen={() => undefined} />)
    expect(screen.getByLabelText('Rated 4.5 out of 5 by 12 learners')).toHaveTextContent('4.5')
    rerender(<CourseCard course={{ ...course, ratingAverage: null, ratingCount: 0 }} onOpen={() => undefined} />)
    expect(screen.queryByLabelText(/Rated/)).toBeNull()
    rerender(<RatingLabel average={5} count={1} />)
    expect(screen.getByLabelText('Rated 5.0 out of 5 by 1 learner')).toBeInTheDocument()
  })

  it('shows a banner picture and a logo only when there is one', () => {
    const banner = { id: 'b', title: 'Hello', text: '', buttonLabel: '', link: '', theme: 'blue' }
    const { container, rerender } = render(<BannerCarousel banners={[banner]} slug="acme" onLink={() => undefined} />)
    expect(container.querySelector('img')).toBeNull()
    rerender(<BannerCarousel banners={[{ ...banner, imageId: 'img-1' }]} slug="acme" onLink={() => undefined} />)
    expect(container.querySelector('img')).toHaveAttribute('src', '/api/v1/public/acme/landing-images/img-1')
    const footer = render(<SiteFooter name="Acme" about="" groups={[]} copyright="" onLink={() => undefined} logoUrl="/logo.png" />)
    expect(footer.container.querySelector('img')).toHaveAttribute('src', '/logo.png')
  })

  it('shows the rating summary, the bars and the reviews in the course dialog', async () => {
    const detail: PublicCourseDetail = {
      id: 'c1', code: 'MATH', title: 'Algebra', description: 'About', category: null, teacher: null, startDate: null, endDate: null, seatsLeft: null, modules: [],
      rating: { average: 4.5, count: 4, distribution: [0, 0, 0, 2, 2] },
      reviews: [{ stars: 5, text: 'Clear and well paced', author: 'Lena K.', atUtc: '2026-01-01T00:00:00Z' }],
    }
    getPublicCourse.mockResolvedValue(detail)
    render(<CourseDialog organization="acme" course={course} onClose={() => undefined} onLogin={() => undefined} />)
    const section = await screen.findByRole('region', { name: 'What learners say' })
    expect(within(section).getByText('4.5')).toBeInTheDocument()
    expect(within(section).getByText('4 ratings')).toBeInTheDocument()
    const bars = within(section).getByRole('list', { name: 'Ratings by stars' })
    expect(within(bars).getAllByRole('listitem')[0]).toHaveTextContent('5 stars')
    expect(within(bars).getAllByRole('listitem')[0]).toHaveTextContent('2')
    expect(within(section).getByText('Lena K.')).toBeInTheDocument()
    expect(within(section).getByText('Clear and well paced')).toBeInTheDocument()
  })

  it('leaves the ratings section out for a course nobody rated', async () => {
    getPublicCourse.mockResolvedValue({ id: 'c1', code: 'MATH', title: 'Algebra', description: 'About', category: null, teacher: null, startDate: null, endDate: null, seatsLeft: null, modules: [], rating: { average: null, count: 0, distribution: [0, 0, 0, 0, 0] }, reviews: [] })
    render(<CourseDialog organization="acme" course={{ ...course, ratingAverage: null, ratingCount: 0 }} onClose={() => undefined} onLogin={() => undefined} />)
    await screen.findByText('About')
    expect(screen.queryByRole('region', { name: 'What learners say' })).toBeNull()
  })
})

describe('PictureField', () => {
  const pictures = [{ id: 'p1', fileName: 'logo.png', sizeBytes: 2048 }, { id: 'p2', fileName: 'hero.jpg', sizeBytes: 4096 }]

  it('checks the type and size before uploading', () => {
    expect(pictureProblem({ type: 'image/png', size: 1000 })).toBeNull()
    expect(pictureProblem({ type: 'image/svg+xml', size: 1000 })).toMatch(/PNG, JPEG, WebP or GIF/)
    expect(pictureProblem({ type: 'application/pdf', size: 1000 })).not.toBeNull()
    expect(pictureProblem({ type: 'image/png', size: 4 * 1024 * 1024 })).toMatch(/3 MB/)
  })

  it('uploads a picture, hands it back and chooses it', async () => {
    request.mockResolvedValue({ id: 'p9', fileName: 'new.png', sizeBytes: 10 })
    const onChange = vi.fn(); const onUploaded = vi.fn()
    render(<PictureField id="x" label="Logo" value="" pictures={pictures} slug="acme" onChange={onChange} onUploaded={onUploaded} />)
    await userEvent.upload(screen.getByLabelText('Upload a new picture for Logo'), new File(['x'], 'new.png', { type: 'image/png' }))
    await waitFor(() => expect(onChange).toHaveBeenCalledWith('p9'))
    expect(onUploaded).toHaveBeenCalledWith({ id: 'p9', fileName: 'new.png', sizeBytes: 10 })
    expect((calls('POST', '/landing/images')[0][1] as { body: FormData }).body.get('file')).toBeInstanceOf(File)
  })

  it('refuses a wrong file without sending it and shows the server’s reason when it refuses', async () => {
    const onChange = vi.fn()
    render(<PictureField id="x" label="Logo" value="" pictures={pictures} slug="acme" onChange={onChange} onUploaded={vi.fn()} />)
    await userEvent.upload(screen.getByLabelText('Upload a new picture for Logo'), new File(['x'], 'logo.svg', { type: 'image/svg+xml' }), { applyAccept: false })
    expect(await screen.findByRole('alert')).toHaveTextContent('PNG, JPEG, WebP or GIF')
    expect(request).not.toHaveBeenCalled()
    const { ApiError } = await import('@/lib/api')
    request.mockRejectedValue(new ApiError('There can be at most 30 pictures.', 409))
    await userEvent.upload(screen.getByLabelText('Upload a new picture for Logo'), new File(['x'], 'ok.png', { type: 'image/png' }))
    expect(await screen.findByText('There can be at most 30 pictures.')).toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('picks an uploaded picture, previews it and can take it off the page', async () => {
    const onChange = vi.fn()
    render(<PictureField id="x" label="Logo" value="p1" pictures={pictures} slug="acme" onChange={onChange} onUploaded={vi.fn()} />)
    expect(screen.getByAltText('Logo preview')).toHaveAttribute('src', '/api/v1/public/acme/landing-images/p1')
    await userEvent.selectOptions(screen.getByLabelText('Logo', { selector: 'select' }), 'p2')
    expect(onChange).toHaveBeenCalledWith('p2')
    await userEvent.click(screen.getByRole('button', { name: 'Remove from the page' }))
    expect(onChange).toHaveBeenCalledWith('')
  })
})

describe('LandingContentPage pictures', () => {
  const content = (): LandingContent => ({
    hero: { title: 'Learn', subtitle: '', primaryLabel: '', primaryLink: '', searchPlaceholder: '' },
    banners: [{ id: 'b1', title: 'Welcome', text: '', buttonLabel: '', link: '', theme: 'blue', imageId: 'p1' }],
    intents: { title: '', items: [] }, rows: [], showCategories: false, categoriesTitle: '', featuresTitle: '', features: [], stats: [], testimonialsTitle: '', testimonials: [],
    faqTitle: '', faq: [], footerAbout: '', footerGroups: [], copyright: '', logoImageId: 'p1', heroImageId: '',
  })
  const editor = (): LandingEditorData => ({ content: content(), isDefault: false, updatedAtUtc: null, slug: 'acme', themes: ['blue'], modes: ['newest'], icons: ['star'] })

  beforeEach(() => {
    request.mockImplementation((path: string, init?: { method?: string; body?: string }) => {
      if (path === '/api/v1/tenant/landing' && init?.method === 'PUT') return Promise.resolve({ ...editor(), content: JSON.parse(init.body!) })
      if (path === '/api/v1/tenant/landing') return Promise.resolve(editor())
      if (path === '/api/v1/tenant/landing/images' && !init?.method) return Promise.resolve([{ id: 'p1', fileName: 'logo.png', sizeBytes: 2048 }])
      return Promise.resolve(path === '/api/v1/tenant/courses' || path.includes('categories') ? [] : null)
    })
  })

  it('shows the chosen logo and lists the pictures', async () => {
    render(<LandingContentPage />)
    expect(await screen.findByAltText('Logo preview')).toHaveAttribute('src', '/api/v1/public/acme/landing-images/p1')
    expect(within(screen.getByRole('list', { name: 'Your pictures' })).getByText(/logo\.png/)).toBeInTheDocument()
  })

  it('takes a deleted picture off the page and saves picture choices with the page', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<LandingContentPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Delete picture logo.png' }))
    await waitFor(() => expect(calls('DELETE', '/landing/images/p1')).toHaveLength(1))
    expect(await screen.findByText('Picture deleted.')).toBeInTheDocument()
    expect(screen.queryByAltText('Logo preview')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: /Save changes/ }))
    await waitFor(() => expect(calls('PUT', '/api/v1/tenant/landing')).toHaveLength(1))
    const saved = bodyOf(calls('PUT', '/api/v1/tenant/landing')[0])
    expect(saved.logoImageId).toBe('')
    expect(saved.banners[0].imageId).toBe('')   // the banner that used it lost it too, so the page can still be saved
  })
})
