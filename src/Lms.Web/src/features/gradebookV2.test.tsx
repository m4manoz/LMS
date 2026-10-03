import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import GradebookPage from './GradebookPage'
import GradebookSetup, { categoriesError, categoryTotal } from './GradebookSetup'
import GradeScalesEditor, { bandsError } from './GradeScalesEditor'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args), downloadFile: vi.fn() }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

describe('category validation', () => {
  it('accepts no categories, or weights that add to 100', () => {
    expect(categoriesError([])).toBeNull()
    expect(categoriesError([{ name: 'A', weightPercent: 60 }, { name: 'B', weightPercent: 40 }])).toBeNull()
    expect(categoryTotal([{ name: 'A', weightPercent: 33.33 }, { name: 'B', weightPercent: 66.67 }])).toBe(100)
  })
  it('explains what is wrong', () => {
    expect(categoriesError([{ name: 'A', weightPercent: 60 }, { name: 'B', weightPercent: 30 }])).toMatch(/90%/)
    expect(categoriesError([{ name: ' ', weightPercent: 100 }])).toMatch(/name/)
    expect(categoriesError([{ name: 'Quiz', weightPercent: 50 }, { name: 'quiz', weightPercent: 50 }])).toMatch(/different/)
    expect(categoriesError([{ name: 'A', weightPercent: 100 }, { name: 'B', weightPercent: 0 }])).toMatch(/more than 0/)
  })
})

describe('band validation', () => {
  const band = (minPercent: string, label: string, points = '') => ({ minPercent, label, points })
  it('accepts a usable scale', () => {
    expect(bandsError([band('90', 'A', '4'), band('0', 'F')])).toBeNull()
  })
  it('rejects the mistakes the server rejects', () => {
    expect(bandsError([])).toMatch(/at least one/)
    expect(bandsError([band('50', 'Pass')])).toMatch(/start at 0/)
    expect(bandsError([band('0', 'F'), band('0', 'G')])).toMatch(/same percentage/)
    expect(bandsError([band('0', 'A'), band('50', 'a')])).toMatch(/different/)
    expect(bandsError([band('0', '')])).toMatch(/label/)
    expect(bandsError([band('0', 'F'), band('150', 'A')])).toMatch(/0 to 100/)
    expect(bandsError([band('0', 'F', '11')])).toMatch(/points/)
  })
})

const settings = {
  gradeScaleId: null, passPercent: 50, categories: [{ id: 'c1', name: 'Homework', weightPercent: 30 }, { id: 'c2', name: 'Project', weightPercent: 70 }],
  items: [{ itemId: 'a1', kind: 'assignment', title: 'Essay', categoryId: 'c1' }, { itemId: 'a2', kind: 'assignment', title: 'Report', categoryId: null }],
}
const scales = [{ id: null, name: 'Standard (built-in)', isDefault: true, builtIn: true, bands: [{ minPercent: 90, label: 'A', points: 4 }, { minPercent: 0, label: 'F', points: 0 }] }]

describe('GradebookSetup', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path?: string, options?: { method?: string }) => {
      const url = String(path)
      if (url.endsWith('/scales')) return Promise.resolve(scales)
      if (url.endsWith('/settings')) return Promise.resolve(settings)
      return Promise.resolve(options?.method ? undefined : [])
    })
  })

  it('shows the weights, their total and which items are not counted', async () => {
    render(<GradebookSetup courseId="c" onSaved={vi.fn()} />)
    expect(await screen.findByLabelText('Category name 1')).toHaveValue('Homework')
    expect(screen.getByRole('status')).toHaveTextContent('Total 100% of 100%')
    expect(screen.getByText(/1 item is not in a category/)).toBeInTheDocument()
  })

  it('blocks saving while the weights do not add up and explains why', async () => {
    render(<GradebookSetup courseId="c" onSaved={vi.fn()} />)
    const weight = await screen.findByLabelText('Weight 1 (%)')
    await userEvent.clear(weight)
    await userEvent.type(weight, '20')
    expect(screen.getByRole('button', { name: 'Save grading setup' })).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('90%')
  })

  it('marks the pass mark as required, rejects an empty one and saves the same request as before', async () => {
    render(<GradebookSetup courseId="c" onSaved={vi.fn()} />)
    const pass = await screen.findByLabelText('Pass mark (%)')
    expect(screen.getByText('Pass mark (%)').className).toContain("content-['*'/'']")
    expect(screen.getByText('Grade scale').className).not.toContain("content-['*'/'']")
    await userEvent.clear(pass)
    await userEvent.click(screen.getByRole('button', { name: 'Save grading setup' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('pass mark must be a number from 0 to 100')
    expect(request.mock.calls.some((call) => call[1]?.method === 'PUT')).toBe(false)
    await userEvent.type(pass, '60')
    await userEvent.click(screen.getByRole('button', { name: 'Save grading setup' }))
    const put = request.mock.calls.find((call) => call[1]?.method === 'PUT')!
    expect(put[0]).toBe('/api/v1/tenant/gradebook/courses/c/settings')
    expect(JSON.parse(put[1].body)).toEqual({ gradeScaleId: null, passPercent: 60, categories: [{ id: 'c1', name: 'Homework', weightPercent: 30 }, { id: 'c2', name: 'Project', weightPercent: 70 }] })
  })

  it('moves an item into a category straight away', async () => {
    const onSaved = vi.fn()
    render(<GradebookSetup courseId="c" onSaved={onSaved} />)
    await userEvent.selectOptions(await screen.findByLabelText('Category for Report'), 'c2')
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/gradebook/courses/c/items/category', { method: 'PUT', body: JSON.stringify({ itemKind: 'assignment', itemId: 'a2', categoryId: 'c2' }) })
    expect(onSaved).toHaveBeenCalled()
  })
})

describe('GradeScalesEditor', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path?: string, options?: { method?: string }) => Promise.resolve(options?.method ? undefined : scales))
  })

  it('lists the built-in scale as read-only', async () => {
    render(<GradeScalesEditor />)
    expect(await screen.findByText('Standard (built-in)')).toBeInTheDocument()
    expect(screen.getByText('Built in')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
  })

  it('starts a new scale from the standard bands and refuses an invalid edit', async () => {
    render(<GradeScalesEditor />)
    await userEvent.click(await screen.findByRole('button', { name: 'New scale' }))
    await userEvent.type(screen.getByLabelText('Name'), 'Honours')
    expect(screen.getByRole('button', { name: 'Save scale' })).toBeEnabled()
    const lowest = screen.getByLabelText('From %', { selector: '#band-min-4' })
    await userEvent.clear(lowest)
    await userEvent.type(lowest, '10')
    expect(screen.getByRole('alert')).toHaveTextContent('start at 0')
    expect(screen.getByRole('button', { name: 'Save scale' })).toBeDisabled()
  })
})

describe('GradeScalesEditor form', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path?: string, options?: { method?: string }) => Promise.resolve(options?.method ? undefined : scales))
  })

  it('marks required fields, blocks an empty name and posts the same body as before', async () => {
    render(<GradeScalesEditor />)
    await userEvent.click(await screen.findByRole('button', { name: 'New scale' }))
    expect(screen.getByText('Name').className).toContain("content-['*'/'']")
    expect(screen.getAllByText('Label')[0].className).toContain("content-['*'/'']")
    await userEvent.click(screen.getByRole('button', { name: 'Save scale' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a name for the scale.')
    expect(request.mock.calls.some((call) => call[1]?.method === 'POST')).toBe(false)
    await userEvent.type(screen.getByLabelText('Name'), 'Honours')
    await userEvent.click(screen.getByRole('button', { name: 'Save scale' }))
    const post = request.mock.calls.find((call) => call[1]?.method === 'POST')!
    expect(post[0]).toBe('/api/v1/tenant/gradebook/scales')
    expect(JSON.parse(post[1].body)).toMatchObject({ name: 'Honours', isDefault: false, bands: [{ minPercent: 90, label: 'A', points: 4 }, { minPercent: 80, label: 'B', points: 3 }, { minPercent: 70, label: 'C', points: 2 }, { minPercent: 60, label: 'D', points: 1 }, { minPercent: 0, label: 'F', points: 0 }] })
  })
})

describe('GradebookPage v2 views', () => {
  beforeEach(() => request.mockReset())

  it('shows a learner their letter, pass result and category breakdown', async () => {
    permissions = ['grade.read']
    request.mockResolvedValue([{
      courseId: 'c1', courseCode: 'MAT-1', courseTitle: 'Maths', earnedPoints: 90, possiblePoints: 110, overallPercent: 86, letter: 'B', passed: true, weighted: true, scaleName: 'Standard (built-in)',
      categories: [{ name: 'Homework', weightPercent: 30, percent: 100 }, { name: 'Project', weightPercent: 70, percent: 80 }],
      rows: [{ kind: 'assignment', title: 'Essay', status: 'Graded', score: 10, maxPoints: 10, percent: 100, isLate: false, feedback: null, categoryName: 'Homework' }],
    }])
    render(<GradebookPage />)
    expect(await screen.findByText('86% · B')).toBeInTheDocument()
    expect(screen.getByText('Passing')).toBeInTheDocument()
    expect(screen.getByText(/30% of grade/)).toBeInTheDocument()
    expect(screen.getByText(/Assignment · Homework/)).toBeInTheDocument()
  })

  it('shows teachers the weighted columns, letters and a below-pass flag', async () => {
    permissions = ['grade.read', 'grade.manage']
    const courses = [{ id: 'c1', code: 'MAT-1', title: 'Maths', status: 'Published' }]
    const book = {
      courseId: 'c1', courseCode: 'MAT-1', courseTitle: 'Maths', classAverage: 45, itemAverages: [45], weighted: true, scaleName: 'Standard (built-in)', passPercent: 50,
      categories: [{ id: 'k1', name: 'Homework', weightPercent: 100 }],
      items: [{ id: 'assignment:1', kind: 'assignment', title: 'Essay', maxPoints: 10, dueAtUtc: null, categoryId: 'k1' }],
      learners: [{ userId: 'u1', name: 'Lena', email: 'l@x.test', earnedPoints: 4, possiblePoints: 10, overallPercent: 45, categoryPercents: [45], letter: 'F', passed: false,
        cells: [{ itemId: 'assignment:1', status: 'Graded', score: 4, maxPoints: 10, percent: 45, isLate: false }] }],
    }
    request.mockImplementation((path?: string) => Promise.resolve(String(path).endsWith('/courses') ? courses : book))
    render(<GradebookPage />)
    expect(await screen.findByText('Lena')).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Homework/ })).toBeInTheDocument()
    expect(screen.getByText('F')).toBeInTheDocument()
    expect(screen.getByText('Below pass')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Grading setup' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Grade scales' })).toBeInTheDocument()
  })
})
