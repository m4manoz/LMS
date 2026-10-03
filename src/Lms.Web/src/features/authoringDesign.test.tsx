import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Label } from '@/components/ui/label'
import { CalendarProvider } from '@/lib/calendarSettings'
import CoursesPage from './CoursesPage'
import CourseList from './CourseList'
import { countLessons, filterCourses, isReady, readiness, toPayload, validateForm, type Course, type Module } from './courseAuthoring'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't' } }) }))

const lesson = (id: string) => ({ id, title: id, displayOrder: 1 })
const mod = (id: string, lessons: number): Module => ({ id, title: `Module ${id}`, displayOrder: 1, lessons: Array.from({ length: lessons }, (_, i) => lesson(`${id}-${i}`)) })
const course = (over: Partial<Course> = {}): Course => ({ id: 'c1', code: 'MATH-1', slug: 'm', title: 'Algebra', status: 'Draft', ...over })

describe('readiness', () => {
  it('needs a module and a lesson in every module, and only recommends a description', () => {
    expect(isReady(readiness([], null))).toBe(false)
    expect(isReady(readiness([mod('a', 0)], null))).toBe(false)
    expect(isReady(readiness([mod('a', 2), mod('b', 0)], 'x'))).toBe(false)
    expect(isReady(readiness([mod('a', 2)], null))).toBe(true)
    expect(readiness([mod('a', 2)], null).find((check) => check.id === 'description')?.ok).toBe(false)
  })
  it('names the empty modules in the hint', () => {
    const hint = readiness([mod('a', 1), mod('b', 0)], 'x').find((check) => check.id === 'lessons')!.hint
    expect(hint).toContain('Module b')
    expect(hint).not.toContain('Module a')
  })
  it('counts lessons across modules', () => expect(countLessons([mod('a', 2), mod('b', 3)])).toBe(5))
})

describe('filterCourses', () => {
  const all = [course({ id: '1', title: 'Algebra', code: 'MATH-1' }), course({ id: '2', title: 'Biology', code: 'BIO-1', status: 'Published' }), course({ id: '3', title: 'Calculus', code: 'MATH-2', status: 'InReview' })]
  it('narrows by category, with none meaning uncategorised', () => {
    const rows = [course({ id: '1', categoryId: 'a' }), course({ id: '2', categoryId: 'b' }), course({ id: '3' })]
    expect(filterCourses(rows, '', 'All', 'a').map((item) => item.id)).toEqual(['1'])
    expect(filterCourses(rows, '', 'All', 'none').map((item) => item.id)).toEqual(['3'])
    expect(filterCourses(rows, '', 'All').length).toBe(3)
  })
  it('matches title or code, ignoring case', () => {
    expect(filterCourses(all, 'math', 'All').map((item) => item.id)).toEqual(['1', '3'])
    expect(filterCourses(all, '  BIOLOGY ', 'All').map((item) => item.id)).toEqual(['2'])
  })
  it('narrows by status and combines with search', () => {
    expect(filterCourses(all, '', 'Published').map((item) => item.id)).toEqual(['2'])
    expect(filterCourses(all, 'math', 'InReview').map((item) => item.id)).toEqual(['3'])
  })
})

describe('Label', () => {
  it('draws the asterisk only when required', () => {
    render(<><Label required>Name</Label><Label>Note</Label></>)
    expect(screen.getByText('Name')).toHaveClass('after:text-red-500')
    expect(screen.getByText('Note')).not.toHaveClass('after:text-red-500')
  })
})

describe('course form rules', () => {
  const ok = { code: 'A-1', title: 'T', description: '', startDateAd: '', endDateAd: '', capacity: '', categoryId: '' }
  it('accepts the minimum and rejects each kind of mistake', () => {
    expect(validateForm(ok, true)).toBeNull()
    expect(validateForm({ ...ok, code: ' ' }, true)).toMatch(/code/)
    expect(validateForm({ ...ok, code: ' ' }, false)).toBeNull() // the code is fixed when editing
    expect(validateForm({ ...ok, title: ' ' }, true)).toMatch(/title/i)
    expect(validateForm({ ...ok, startDateAd: '2026-05-02', endDateAd: '2026-05-01' }, true)).toMatch(/end date/)
    for (const capacity of ['0', '-1', '2.5', 'abc', '1000001']) expect(validateForm({ ...ok, capacity }, true)).toMatch(/Capacity/)
  })
  it('turns blanks into nulls', () => {
    expect(toPayload({ ...ok, code: ' a-1 ', description: '  ', capacity: ' 30 ' })).toEqual({ code: 'a-1', title: 'T', description: null, startDateAd: null, endDateAd: null, capacity: 30, categoryId: null })
  })
})

describe('CourseList', () => {
  const list = [course({ id: '1', title: 'Algebra' }), course({ id: '2', title: 'Biology', code: 'BIO-1', status: 'Published' })]
  it('searches, filters by status and marks the selected course', async () => {
    const onSelect = vi.fn()
    render(<CourseList courses={list} selectedId="1" showFilters onSelect={onSelect} />)
    expect(screen.getByRole('button', { name: 'View details for Algebra' })).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('button', { name: 'View details for Biology' })).toHaveAttribute('aria-expanded', 'false')
    await userEvent.type(screen.getByLabelText('Search courses'), 'bio')
    expect(screen.queryByText('Algebra')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'View details for Biology' }))
    expect(onSelect).toHaveBeenCalledWith('2')
    await userEvent.clear(screen.getByLabelText('Search courses'))
    await userEvent.click(screen.getByRole('button', { name: /^Published/ }))
    expect(screen.queryByText('Algebra')).toBeNull()
    expect(screen.getByText('Biology')).toBeInTheDocument()
  })
  it('says so when nothing matches, and hides the filters for the learner catalog', async () => {
    render(<CourseList courses={list} showFilters={false} onSelect={vi.fn()} />)
    expect(screen.queryByRole('group', { name: 'Filter by status' })).toBeNull()
    await userEvent.type(screen.getByLabelText('Search courses'), 'zzz')
    expect(screen.getByText('No courses match.')).toBeInTheDocument()
  })
})

// ---------- the page ----------
const detail = (over: object = {}, modules: Module[] = [mod('m1', 1)], description: string | null = null) => ({
  course: course({ description, ...over }), currentVersion: { id: 'v1', versionNumber: 1, status: 'Draft' }, modules, workflow: [],
})

async function renderAuthoring(open = true) {
  render(<CalendarProvider><CoursesPage mode="authoring" /></CalendarProvider>)
  if (open) await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra' }))
}

describe('CoursesPage authoring', () => {
  let current: ReturnType<typeof detail>
  beforeEach(() => {
    request.mockReset(); permissions = ['course.manage', 'course.review', 'course.publish']
    current = detail()
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (String(path) === '/api/v1/tenant/courses' && !options?.method) return Promise.resolve([current.course])
      return Promise.resolve(current)
    })
  })
  const callsTo = (fragment: string, method: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && call[1]?.method === method)

  it('shows the checklist and lets a ready draft be submitted from the header', async () => {
    await renderAuthoring()
    expect(await screen.findByText('Ready for review?')).toBeInTheDocument()
    expect(screen.getByText('Has a description')).toBeInTheDocument() // optional and not done
    await userEvent.click(screen.getByRole('button', { name: 'Submit for review' }))
    await waitFor(() => expect(callsTo('/submit-review', 'POST')).toHaveLength(1))
  })

  it('holds back submission while the outline is incomplete', async () => {
    current = detail({}, [mod('m1', 0)])
    await renderAuthoring()
    expect(await screen.findByText(/Add a lesson to/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Submit for review' })).toBeDisabled()
  })

  it('edits details on the Details tab without sending the course code', async () => {
    await renderAuthoring()
    await userEvent.click(await screen.findByRole('tab', { name: 'Details' }))
    expect(screen.getByLabelText('Course code')).toBeDisabled()
    await userEvent.clear(screen.getByLabelText('Title'))
    await userEvent.type(screen.getByLabelText('Title'), 'Algebra II')
    await userEvent.click(screen.getByRole('button', { name: 'Save details' }))
    await waitFor(() => expect(callsTo('/api/v1/tenant/courses/c1', 'PUT')).toHaveLength(1))
    expect(JSON.parse(callsTo('/api/v1/tenant/courses/c1', 'PUT')[0][1].body)).toEqual({ title: 'Algebra II', description: null, startDateAd: null, endDateAd: null, capacity: null, categoryId: null })
    expect(await screen.findByText('Details saved.')).toBeInTheDocument()
  })

  it('locks the details once the course is published and offers a new version instead', async () => {
    current = detail({ status: 'Published' })
    await renderAuthoring()
    expect(await screen.findByRole('button', { name: 'Edit with a new version' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Details' }))
    expect(screen.getByRole('button', { name: 'Save details' })).toBeDisabled()
    expect(screen.queryByText('Ready for review?')).toBeNull()
  })

  it('creates a course from the New course button, validating before it sends', async () => {
    await renderAuthoring(false)
    await userEvent.click(await screen.findByRole('button', { name: 'New course' }))
    await userEvent.click(screen.getByRole('button', { name: 'Create draft course' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a course code.')
    expect(callsTo('/api/v1/tenant/courses', 'POST')).toHaveLength(0)
    await userEvent.type(screen.getByLabelText('Course code'), 'PHYS-1')
    await userEvent.type(screen.getByLabelText('Title'), 'Physics')
    await userEvent.click(screen.getByRole('button', { name: 'Create draft course' }))
    await waitFor(() => expect(callsTo('/api/v1/tenant/courses', 'POST')).toHaveLength(1))
    expect(JSON.parse(callsTo('/api/v1/tenant/courses', 'POST')[0][1].body)).toMatchObject({ code: 'PHYS-1', title: 'Physics' })
    expect(await screen.findByText(/Course created/)).toBeInTheDocument()
  })

  it('hides authoring controls for the learner catalog', async () => {
    permissions = []
    render(<CoursesPage mode="catalog" />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra' }))
    await screen.findByText('About this course')
    expect(screen.queryByRole('button', { name: 'New course' })).toBeNull()
    expect(screen.queryByRole('tab', { name: 'Details' })).toBeNull()
    expect(screen.queryByText('Ready for review?')).toBeNull()
  })


  it('offers the categories on the form and sends the chosen one', async () => {
    const science = { id: 'cat-1', name: 'Science' }
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (String(path).includes('/catalog/categories')) return Promise.resolve([science, { id: 'cat-2', name: 'Arts' }])
      if (String(path) === '/api/v1/tenant/courses' && !options?.method) return Promise.resolve([{ ...current.course, categoryId: 'cat-1', categoryName: 'Science' }])
      return Promise.resolve(current)
    })
    await renderAuthoring(false)
    expect(await screen.findByText(/Algebra/)).toBeInTheDocument()
    expect(screen.getByText(/MATH-1 · Science/)).toBeInTheDocument()   // the list shows it

    await userEvent.click(screen.getByRole('button', { name: 'New course' }))
    await userEvent.type(screen.getByLabelText('Course code'), 'ART-1')
    await userEvent.type(screen.getByLabelText('Title'), 'Painting')
    await userEvent.selectOptions(screen.getByLabelText('Category'), 'Arts')
    await userEvent.click(screen.getByRole('button', { name: 'Create draft course' }))
    await waitFor(() => expect(callsTo('/api/v1/tenant/courses', 'POST')).toHaveLength(1))
    expect(JSON.parse(callsTo('/api/v1/tenant/courses', 'POST')[0][1].body)).toMatchObject({ code: 'ART-1', categoryId: 'cat-2' })
  })

  it('filters the list by category', async () => {
    const rows = [course({ id: '1', title: 'Algebra', categoryId: 'cat-1', categoryName: 'Science' }), course({ id: '2', title: 'Painting', code: 'ART-1', categoryId: 'cat-2', categoryName: 'Arts' }), course({ id: '3', title: 'Misc', code: 'M-1' })]
    render(<CourseList courses={rows} categories={[{ id: 'cat-1', name: 'Science' }, { id: 'cat-2', name: 'Arts' }]} showFilters onSelect={vi.fn()} />)
    await userEvent.selectOptions(screen.getByLabelText('Filter by category'), 'Arts')
    expect(screen.queryByText('Algebra')).toBeNull()
    expect(screen.getByText('Painting')).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText('Filter by category'), 'No category')
    expect(screen.getByText('Misc')).toBeInTheDocument()
    expect(screen.queryByText('Painting')).toBeNull()
  })

  it('marks required fields with a red asterisk that does not change their names', async () => {
    await renderAuthoring(false)
    await userEvent.click(await screen.findByRole('button', { name: 'New course' }))
    for (const name of ['Course code', 'Title']) expect(screen.getByText(name)).toHaveClass("after:content-['*'/'']", 'after:text-red-500')
    expect(screen.getByText('Description')).not.toHaveClass("after:content-['*'/'']")
    expect(screen.getByText('Category')).not.toHaveClass("after:content-['*'/'']")
  })

  it('opens nothing until a course is chosen, then shows its details in an overlay that can be dismissed three ways', async () => {
    await renderAuthoring(false)
    await screen.findByText('Algebra')
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(callsTo('/api/v1/tenant/courses/c1', 'GET')).toHaveLength(0)
    expect(request.mock.calls.some((call) => String(call[0]) === '/api/v1/tenant/courses/c1')).toBe(false) // no detail request either

    await userEvent.click(screen.getByRole('button', { name: 'View details for Algebra' }))
    expect(await screen.findByRole('dialog', { name: 'Course details' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Close panel' }))
    expect(screen.queryByRole('dialog')).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: 'View details for Algebra' }))
    await screen.findByRole('dialog')
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: 'View details for Algebra' }))
    await userEvent.click(await screen.findByTestId('side-panel-backdrop'))
    expect(screen.queryByRole('dialog')).toBeNull()
  })
})

describe('typing inside the details panel', () => {
  it('keeps focus in the field while the page re-renders on every keystroke', async () => {
    request.mockReset(); permissions = ['course.manage']
    const detailWithModule = { course: course({ description: 'x' }), currentVersion: { id: 'v1', versionNumber: 1, status: 'Draft' }, modules: [mod('m1', 0)], workflow: [] }
    request.mockImplementation((path: string, options?: { method?: string }) => Promise.resolve(String(path) === '/api/v1/tenant/courses' && !options?.method ? [detailWithModule.course] : String(path).includes('/catalog/') ? [] : detailWithModule))
    render(<CalendarProvider><CoursesPage mode="authoring" /></CalendarProvider>)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra' }))
    await userEvent.click(await screen.findByRole('tab', { name: 'Outline' }))
    const input = await screen.findByLabelText('New lesson title for Module m1')
    await userEvent.type(input, 'Introduction')   // one keystroke at a time
    expect(input).toHaveValue('Introduction')
    expect(input).toHaveFocus()
  })
})
