import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import GradebookPage, { formatPercent, percentTone } from './GradebookPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args), downloadFile: vi.fn() }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

describe('percent helpers', () => {
  it('formats whole numbers, decimals and missing values', () => {
    expect(formatPercent(80)).toBe('80%')
    expect(formatPercent(66.666)).toBe('66.7%')
    expect(formatPercent(null)).toBe('—')
  })
  it('flags weak results and mutes missing ones', () => {
    expect(percentTone(30)).toBe('text-destructive')
    expect(percentTone(90)).toBe('text-emerald-400')
    expect(percentTone(null)).toBe('text-muted-foreground')
  })
})

describe('GradebookPage', () => {
  beforeEach(() => request.mockReset())

  it('shows a learner their own grades and feedback, with no class view', async () => {
    permissions = ['grade.read']
    request.mockResolvedValue([{
      courseId: 'c1', courseCode: 'MAT-1', courseTitle: 'Maths', earnedPoints: 15, possiblePoints: 20, overallPercent: 75,
      rows: [{ kind: 'assignment', title: 'Essay', status: 'Graded', score: 15, maxPoints: 20, percent: 75, isLate: false, feedback: 'Well done' }],
    }])
    render(<GradebookPage />)
    expect(await screen.findByText('Maths')).toBeInTheDocument()
    expect(screen.getByText('Feedback: Well done')).toBeInTheDocument()
    expect(screen.getAllByText('75%').length).toBeGreaterThan(0)
    expect(screen.queryByRole('tab', { name: 'Class gradebook' })).not.toBeInTheDocument()
  })

  it('shows a teacher the class matrix with pending and missing work', async () => {
    permissions = ['grade.read', 'grade.manage']
    const courses = [{ id: 'c1', code: 'MAT-1', title: 'Maths', status: 'Published' }]
    const book = {
      courseId: 'c1', courseCode: 'MAT-1', courseTitle: 'Maths', classAverage: 80, itemAverages: [80],
      items: [{ id: 'assignment:1', kind: 'assignment', title: 'Essay', maxPoints: 50, dueAtUtc: null }],
      learners: [
        { userId: 'u1', name: 'Lena', email: 'lena@x.test', earnedPoints: 40, possiblePoints: 50, overallPercent: 80, cells: [{ itemId: 'assignment:1', status: 'Graded', score: 40, maxPoints: 50, percent: 80, isLate: false }] },
        { userId: 'u2', name: 'Otto', email: 'otto@x.test', earnedPoints: 0, possiblePoints: 0, overallPercent: null, cells: [{ itemId: 'assignment:1', status: 'Pending', score: null, maxPoints: 50, percent: null, isLate: false }] },
      ],
    }
    request.mockImplementation((path?: string) => Promise.resolve(String(path).endsWith('/courses') ? courses : book))
    render(<GradebookPage />)
    expect(await screen.findByText('Lena')).toBeInTheDocument()
    expect(screen.getByText('Otto')).toBeInTheDocument()
    expect(screen.getByText('Pending')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Export CSV' })).toBeEnabled()
  })
})
