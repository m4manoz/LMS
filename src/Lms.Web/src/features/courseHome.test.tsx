import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CourseHome from './CourseHome'
import { comingUp, tabForKind, type TaskItem } from './CourseHomePanels'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => ({ ...(await vi.importActual<typeof import('@/lib/api')>('@/lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))
// The other pages are tested on their own; here they only show which course they were given.
vi.mock('./AssessmentsPage', () => ({ default: (props: { courseId?: string; embedded?: boolean }) => <p>Assessments page for {props.courseId} {props.embedded ? 'embedded' : 'standalone'}</p> }))
vi.mock('./AssignmentsPage', () => ({ default: (props: { courseId?: string; embedded?: boolean }) => <p>Assignments page for {props.courseId} {props.embedded ? 'embedded' : 'standalone'}</p> }))
vi.mock('./LiveClassesPage', () => ({ default: (props: { courseId?: string; embedded?: boolean }) => <p>Live classes page for {props.courseId} {props.embedded ? 'embedded' : 'standalone'}</p> }))
vi.mock('./VideoLibraryPage', () => ({ default: (props: { courseId?: string; embedded?: boolean }) => <p>Video library page for {props.courseId} {props.embedded ? 'embedded' : 'standalone'}</p> }))
vi.mock('./ForumsPage', () => ({ default: (props: { courseId?: string; embedded?: boolean }) => <p>Forum page for {props.courseId} {props.embedded ? 'embedded' : 'standalone'}</p> }))
vi.mock('./MessagesPage', () => ({ default: (props: { courseId?: string }) => <p>Chat for {props.courseId}</p> }))
vi.mock('./CourseRatingPanel', () => ({ default: ({ courseId }: { courseId: string }) => <p>Rating for {courseId}</p> }))

const course = { id: 'c1', code: 'MATH-1', title: 'Algebra', description: 'Numbers and symbols.' }
const learner = ['course.read', 'assessment.read', 'liveclass.read', 'collaboration.read', 'grade.read', 'certificate.read']
const feed = (items: Partial<TaskItem>[]) => ({ items: items.map((item, index) => ({ id: `t${index}`, kind: 'assignment', title: 'Item', courseTitle: 'Algebra', startAtUtc: null, dueAtUtc: null, status: 'Todo', target: 'assignments', ...item })) })

function open(over: Partial<React.ComponentProps<typeof CourseHome>> = {}) {
  return render(<CourseHome course={course} enrollment={{ status: 'Active', progressPercent: 40 }} onWithdraw={vi.fn()} lessons={<p>The lessons area</p>} continueLesson="Variables" {...over} />)
}

describe('comingUp', () => {
  const item = (over: Partial<TaskItem>): TaskItem => ({ id: 'x', kind: 'assignment', title: 'T', courseTitle: null, startAtUtc: null, dueAtUtc: null, status: 'Todo', target: '', ...over })
  it('keeps only what is still to do', () => {
    expect(comingUp([item({ id: 'a', status: 'Done' }), item({ id: 'b', status: 'Graded' }), item({ id: 'c', status: 'Submitted' }), item({ id: 'd', status: 'Todo' }), item({ id: 'e', status: 'Ended' })]).map((entry) => entry.id)).toEqual(['d'])
  })
  it('puts late work first, then a class that is on, then by date, undated last', () => {
    const list = comingUp([
      item({ id: 'later', dueAtUtc: '2030-03-01T00:00:00Z' }), item({ id: 'undated' }), item({ id: 'soon', dueAtUtc: '2030-01-01T00:00:00Z' }),
      item({ id: 'live', kind: 'live-class', status: 'Live', startAtUtc: '2030-02-01T00:00:00Z' }), item({ id: 'late', status: 'Overdue', dueAtUtc: '2029-01-01T00:00:00Z' }),
    ])
    expect(list.map((entry) => entry.id)).toEqual(['late', 'live', 'soon', 'later', 'undated'])
  })
  it('knows which tab each kind of item belongs to', () => {
    expect([tabForKind('assignment'), tabForKind('assessment'), tabForKind('live-class'), tabForKind('grading')]).toEqual(['assignments', 'assessments', 'live', 'overview'])
  })
})

describe('the course home', () => {
  beforeEach(() => {
    request.mockReset(); permissions = learner
    request.mockImplementation((path: string) => {
      if (path.includes('/tasks')) return Promise.resolve(feed([]))
      return Promise.resolve([])
    })
  })

  it('shows the course, its progress and one tab for each thing a learner does in it', () => {
    open()
    expect(screen.getByText('Algebra')).toBeInTheDocument()
    expect(screen.getByText('40% complete', { exact: false })).toBeInTheDocument()
    expect(screen.getAllByRole('tab').map((item) => item.textContent)).toEqual(['Overview', 'Lessons', 'Assessments', 'Assignments', 'Live classes', 'Videos', 'Discussion', 'Grades and certificate'])
  })

  it('shows only the tabs the person may use', () => {
    permissions = ['course.read']
    open()
    expect(screen.getAllByRole('tab').map((item) => item.textContent)).toEqual(['Overview', 'Lessons', 'Videos'])
  })

  it('opens each tab with the page for this course only', async () => {
    open()
    for (const [tab, text] of [['Assessments', 'Assessments page for c1 embedded'], ['Assignments', 'Assignments page for c1 embedded'], ['Live classes', 'Live classes page for c1 embedded'], ['Videos', 'Video library page for c1 embedded']]) {
      await userEvent.click(screen.getByRole('tab', { name: tab }))
      expect(await screen.findByText(text)).toBeInTheDocument()
    }
    await userEvent.click(screen.getByRole('tab', { name: 'Lessons' }))
    expect(await screen.findByText('The lessons area')).toBeInTheDocument()
  })

  it('keeps the discussion, the course chat and the announcements together', async () => {
    request.mockImplementation((path: string) => path.includes('/announcements') ? Promise.resolve([{ id: 'n1', title: 'Exam moved', body: 'Now on Friday.', isPinned: true, courseId: 'c1', courseTitle: 'Algebra', authorName: 'Tara', createdAtUtc: '2030-01-01T10:00:00Z' }]) : Promise.resolve([]))
    open()
    await userEvent.click(screen.getByRole('tab', { name: 'Discussion' }))
    expect(await screen.findByText('Forum page for c1 embedded')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Course chat' }))
    expect(await screen.findByText('Chat for c1')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Announcements' }))
    expect(await screen.findByText('Exam moved')).toBeInTheDocument()
  })

  it('withdraws only while the enrollment is active', async () => {
    const onWithdraw = vi.fn()
    const { unmount } = open({ onWithdraw })
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw' }))
    expect(onWithdraw).toHaveBeenCalledTimes(1)
    unmount()
    open({ enrollment: { status: 'Completed', progressPercent: 100 } })
    expect(screen.queryByRole('button', { name: 'Withdraw' })).toBeNull()
  })
})

describe('the overview', () => {
  beforeEach(() => { request.mockReset(); permissions = learner })

  it('offers to carry on and goes to the lessons', async () => {
    request.mockResolvedValue({ items: [] })
    open()
    expect(screen.getByText('Next: Variables')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByText('The lessons area')).toBeInTheDocument()
  })

  it('says so when the course is finished', () => {
    request.mockResolvedValue({ items: [] })
    open({ enrollment: { status: 'Completed', progressPercent: 100 } })
    expect(screen.getByText('You have finished this course')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Review the lessons' })).toBeInTheDocument()
  })

  it('lists what is coming up, asks only for this course, and opens the right tab', async () => {
    request.mockImplementation((path: string) => path.includes('/tasks') ? Promise.resolve(feed([
      { id: 'a', kind: 'assignment', title: 'Homework 1', dueAtUtc: '2030-01-05T10:00:00Z' },
      { id: 'q', kind: 'assessment', title: 'Quiz 1', status: 'Overdue', dueAtUtc: '2029-12-01T10:00:00Z' },
      { id: 'l', kind: 'live-class', title: 'Live revision', status: 'Scheduled', startAtUtc: '2030-01-03T10:00:00Z' },
      { id: 'd', kind: 'assignment', title: 'Old homework', status: 'Graded' },
    ])) : Promise.resolve([]))
    open()
    const section = await screen.findByRole('region', { name: 'Coming up' })
    expect(request.mock.calls.some(([path]) => String(path).includes('/tasks?courseId=c1'))).toBe(true)
    expect(within(section).getAllByRole('listitem').map((row) => row.querySelector('strong')?.textContent)).toEqual(['Quiz 1', 'Live revision', 'Homework 1'])
    expect(within(section).queryByText('Old homework')).toBeNull()
    expect(within(section).getByText('Overdue')).toBeInTheDocument()
    await userEvent.click(within(section).getByRole('button', { name: 'Open Quiz 1' }))
    expect(await screen.findByText('Assessments page for c1 embedded')).toBeInTheDocument()
  })

  it('says nothing is waiting when nothing is', async () => {
    request.mockResolvedValue({ items: [] })
    open()
    expect(await screen.findByText('Nothing is waiting for you in this course.')).toBeInTheDocument()
  })

  it('shows the latest announcements and the rating of the course', async () => {
    const note = (id: string, title: string, courseId: string | null) => ({ id, title, body: `${title} body`, isPinned: false, courseId, courseTitle: null, authorName: 'Tara', createdAtUtc: '2030-01-01T10:00:00Z' })
    request.mockImplementation((path: string) => path.includes('/announcements') ? Promise.resolve([note('1', 'One', 'c1'), note('2', 'Two', null), note('3', 'Three', 'c1'), note('4', 'Four', 'c1')]) : Promise.resolve({ items: [] }))
    open()
    const news = await screen.findByRole('region', { name: 'Announcements' })
    expect(within(news).getAllByRole('listitem')).toHaveLength(3)
    expect(within(news).getByText('Whole organization')).toBeInTheDocument()
    expect(within(news).getByText('1 more in the Discussion tab.')).toBeInTheDocument()
    expect(request.mock.calls.some(([path]) => String(path).includes('/announcements?courseId=c1'))).toBe(true)
    expect(screen.getByText('Rating for c1')).toBeInTheDocument()
  })
})

describe('grades and certificate', () => {
  beforeEach(() => { permissions = learner })

  it('shows the course grades row by row and the certificate with a way to verify it', async () => {
    request.mockReset()
    request.mockImplementation((path: string) => {
      if (path.includes('/gradebook/me')) return Promise.resolve([{ courseId: 'c1', rows: [
        { kind: 'Assessment', title: 'Quiz 1', status: 'Graded', score: 8, maxPoints: 10, percent: 80, isLate: false, feedback: null },
        { kind: 'Assignment', title: 'Homework 1', status: 'Graded', score: 7, maxPoints: 10, percent: 70, isLate: true, feedback: 'Good effort.' }], earnedPoints: 15, possiblePoints: 20, overallPercent: 75, letter: 'B', passed: true }])
      if (path.includes('/certificates')) return Promise.resolve([{ id: 'k1', certificateNumber: 'CERT-001', courseTitle: 'Algebra', scorePercentage: 75, issuedAtUtc: '2030-02-01T00:00:00Z', verificationPath: '/verify/abc' }])
      return Promise.resolve({ items: [] })
    })
    open()
    await userEvent.click(screen.getByRole('tab', { name: 'Grades and certificate' }))
    const grades = await screen.findByRole('region', { name: 'My grades' })
    expect(within(grades).getByText('75%')).toBeInTheDocument()
    expect(within(grades).getByText('B')).toBeInTheDocument()
    expect(within(grades).getByText('Passing')).toBeInTheDocument()
    expect(within(grades).getByText('15 of 20 points')).toBeInTheDocument()
    expect(within(grades).getByText('8 / 10 · 80%')).toBeInTheDocument()
    expect(within(grades).getByText(/Assignment · Graded · late/)).toBeInTheDocument()
    expect(within(grades).getByText('Good effort.')).toBeInTheDocument()
    const certificate = await screen.findByRole('region', { name: 'My certificate' })
    expect(within(certificate).getByText(/CERT-001/)).toBeInTheDocument()
    expect(within(certificate).getByRole('link', { name: 'Open and verify the certificate' })).toHaveAttribute('href', '/verify/abc')
    expect(request.mock.calls.some(([path]) => String(path).includes('/gradebook/me?courseId=c1'))).toBe(true)
    expect(request.mock.calls.some(([path]) => String(path).includes('/certificates?courseId=c1'))).toBe(true)
  })

  it('explains an empty state kindly', async () => {
    request.mockReset()
    request.mockImplementation((path: string) => path.includes('/gradebook/me') || path.includes('/certificates') ? Promise.resolve([]) : Promise.resolve({ items: [] }))
    open()
    await userEvent.click(screen.getByRole('tab', { name: 'Grades and certificate' }))
    expect(await screen.findByText(/Grades appear here once/)).toBeInTheDocument()
    expect(await screen.findByText('You will get a certificate when you complete the course.')).toBeInTheDocument()
  })
})
