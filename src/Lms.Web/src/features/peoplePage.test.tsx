import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import PeoplePage from './PeoplePage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))
vi.mock('@/components/CalendarDateField', () => ({
  default: ({ id, label, value, onChange }: { id: string; label: string; value: string; onChange: (value: string) => void }) =>
    <label>{label}<input id={id} value={value} onChange={(event) => onChange(event.target.value)} /></label>,
}))

const learnerRows = [
  { userId: 'u1', displayName: 'Lena Learner', email: 'lena@school.test', status: 'Active', joinedAtUtc: '2026-01-01T00:00:00Z', detail1: 'S-100', detail2: 'Grade 8', count: 2 },
  { userId: 'u2', displayName: 'Liam Learner', email: 'liam@school.test', status: 'Suspended', joinedAtUtc: '2026-01-02T00:00:00Z', detail1: null, detail2: null, count: 0 },
]
const learnerProfile = {
  userId: 'u1', displayName: 'Lena Learner', email: 'lena@school.test', status: 'Active', roles: ['LEARNER'], joinedAtUtc: '2026-01-01T00:00:00Z',
  learner: { studentNumber: 'S-100', gradeLevel: 'Grade 8', dateOfBirthAd: '2012-04-05' }, teacher: null,
  summary: { activeCourses: 1, completedCourses: 1, averageProgressPercent: 50, coursesTaught: 0, learnersTaught: 0 },
  enrollments: [
    { courseId: 'c1', code: 'MATH', title: 'Maths', status: 'Active', progressPercent: 50, enrolledAtUtc: '2026-02-01T00:00:00Z', lastAccessedAtUtc: null, completedAtUtc: null },
    { courseId: 'c2', code: 'ART', title: 'Art', status: 'Completed', progressPercent: 100, enrolledAtUtc: '2026-01-05T00:00:00Z', lastAccessedAtUtc: null, completedAtUtc: null },
  ], courses: [],
}
const teacherProfile = {
  userId: 't1', displayName: 'Tara Teacher', email: 'tara@school.test', status: 'Active', roles: ['TEACHER'], joinedAtUtc: '2026-01-01T00:00:00Z',
  learner: null, teacher: { employeeNumber: 'E-7', subjectSpecialty: 'Physics' },
  summary: { activeCourses: 0, completedCourses: 0, averageProgressPercent: 0, coursesTaught: 1, learnersTaught: 3 },
  enrollments: [], courses: [{ courseId: 'c9', code: 'PHY', title: 'Physics 1', status: 'Published', learners: 3 }],
}
const callsTo = (method: string) => request.mock.calls.filter((call) => call[1]?.method === method)

describe('PeoplePage', () => {
  beforeEach(() => {
    request.mockReset(); permissions = ['user.read', 'user.manage']
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (options?.method === 'PUT') return Promise.resolve(null)
      if (path.includes('/people/u1')) return Promise.resolve(learnerProfile)
      if (path.includes('/people/t1')) return Promise.resolve(teacherProfile)
      if (path.includes('kind=instructors')) return Promise.resolve([{ userId: 't1', displayName: 'Tara Teacher', email: 'tara@school.test', status: 'Active', joinedAtUtc: '2026-01-01T00:00:00Z', detail1: 'E-7', detail2: 'Physics', count: 1 }])
      return Promise.resolve(learnerRows)
    })
  })

  it('lists learners with their details and course counts', async () => {
    render(<PeoplePage kind="learners" />)
    expect(await screen.findByText('Lena Learner')).toBeInTheDocument()
    expect(screen.getByText('S-100 · Grade 8')).toBeInTheDocument()
    expect(screen.getByText('2 courses')).toBeInTheDocument()
    expect(screen.getByText('0 courses')).toBeInTheDocument()
    expect(screen.getByText('Suspended')).toBeInTheDocument()
    expect(request.mock.calls[0][0]).toBe('/api/v1/tenant/people?kind=learners')
  })

  it('searches the directory', async () => {
    render(<PeoplePage kind="learners" />)
    await screen.findByText('Lena Learner')
    await userEvent.type(screen.getByLabelText('Search learners'), 'lia')
    await waitFor(() => expect(request.mock.calls.some((call) => call[0] === '/api/v1/tenant/people?kind=learners&q=lia')).toBe(true))
  })

  it('opens a learner profile with progress and courses', async () => {
    render(<PeoplePage kind="learners" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Lena Learner' }))
    const panel = await screen.findByRole('dialog', { name: 'Lena Learner' })
    expect(within(panel).getByText('50%')).toBeInTheDocument()
    expect(within(panel).getByText('Maths')).toBeInTheDocument()
    expect(within(panel).getByText('Completed · 100%')).toBeInTheDocument()
    expect(within(panel).getByText('2012-04-05')).toBeInTheDocument()
  })

  it('shows an instructor’s courses and learners', async () => {
    render(<PeoplePage kind="instructors" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Tara Teacher' }))
    const panel = await screen.findByRole('dialog', { name: 'Tara Teacher' })
    expect(within(panel).getByText('Physics 1')).toBeInTheDocument()
    expect(within(panel).getByText('Published · 3 learners')).toBeInTheDocument()
    expect(within(panel).getByText('E-7')).toBeInTheDocument()
  })

  it('lets a user manager edit the details and sends them', async () => {
    render(<PeoplePage kind="learners" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Lena Learner' }))
    const panel = await screen.findByRole('dialog', { name: 'Lena Learner' })
    await userEvent.click(within(panel).getByRole('button', { name: 'Edit details' }))
    const grade = within(panel).getByLabelText('Grade or level')
    await userEvent.clear(grade)
    await userEvent.type(grade, 'Grade 9')
    await userEvent.click(within(panel).getByRole('button', { name: 'Save details' }))
    await waitFor(() => expect(callsTo('PUT')).toHaveLength(1))
    expect(String(callsTo('PUT')[0][0])).toBe('/api/v1/tenant/people/u1/profile')
    expect(JSON.parse(callsTo('PUT')[0][1].body)).toEqual({ kind: 'learner', studentNumber: 'S-100', gradeLevel: 'Grade 9', dateOfBirthAd: '2012-04-05' })
    expect(await screen.findByText('Details saved.')).toBeInTheDocument()
  })

  it('does not offer editing to staff who cannot manage users', async () => {
    permissions = ['enrollment.manage']
    render(<PeoplePage kind="learners" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Lena Learner' }))
    await screen.findByRole('dialog', { name: 'Lena Learner' })
    expect(screen.queryByRole('button', { name: 'Edit details' })).toBeNull()
  })

  it('says so when nobody matches', async () => {
    request.mockImplementation(() => Promise.resolve([]))
    render(<PeoplePage kind="instructors" />)
    expect(await screen.findByText('No instructors yet.')).toBeInTheDocument()
  })
})
