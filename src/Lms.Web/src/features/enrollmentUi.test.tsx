import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CohortsPage, { outcomeLabel, summaryLine } from './CohortsPage'
import CourseEnrollmentPanel, { describeEnrollment, parseCapacity } from './CourseEnrollmentPanel'
import CourseRulesPanel, { describeRule, toAccessBody, toLocalInput } from './CourseRulesPanel'
import InvitationsPage, { acceptedMessage, expiresIn } from './InvitationsPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

const callsTo = (fragment: string, method?: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || call[1]?.method === method))

// ---------- capacity and waitlist ----------
describe('parseCapacity', () => {
  it('treats empty as no limit and accepts whole numbers in range', () => {
    expect(parseCapacity('')).toEqual({ ok: true, value: null })
    expect(parseCapacity(' 25 ')).toEqual({ ok: true, value: 25 })
  })
  it('rejects anything else', () => {
    for (const text of ['0', '-3', '2.5', 'abc', '1000001']) expect(parseCapacity(text)).toEqual({ ok: false })
  })
})

const summary = (overrides = {}) => ({
  courseId: 'c', capacity: 2, active: 2, completed: 1, freeSeats: 0, justPromoted: 0,
  waitlist: [
    { enrollmentId: 'e1', learnerUserId: 'u1', name: 'Ben', email: 'ben@x.test', position: 1, joinedAtUtc: '2026-10-01T00:00:00Z' },
    { enrollmentId: 'e2', learnerUserId: 'u2', name: 'Cleo', email: 'cleo@x.test', position: 2, joinedAtUtc: '2026-10-02T00:00:00Z' },
  ], ...overrides,
})

describe('CourseEnrollmentPanel', () => {
  beforeEach(() => { request.mockReset(); request.mockImplementation(() => Promise.resolve(summary())) })

  it('shows seats and the waitlist in order', async () => {
    render(<CourseEnrollmentPanel courseId="c" published />)
    expect(await screen.findByText('Waitlist (2)')).toBeInTheDocument()
    expect(screen.getByText('Ben')).toBeInTheDocument()
    expect(screen.getByText('#1')).toBeInTheDocument()
    expect(screen.getByText('Free seats').closest('div')).toHaveTextContent('0')
  })

  it('reports how many people a capacity change moved off the waitlist', async () => {
    request.mockImplementation((path?: string, options?: { method?: string }) => Promise.resolve(options?.method === 'PUT' ? summary({ capacity: 4, freeSeats: 0, active: 4, justPromoted: 2, waitlist: [] }) : summary()))
    render(<CourseEnrollmentPanel courseId="c" published />)
    const input = await screen.findByLabelText('Seats (empty for no limit)')
    await userEvent.clear(input)
    await userEvent.type(input, '4')
    await userEvent.click(screen.getByRole('button', { name: 'Save capacity' }))
    expect(callsTo('/capacity', 'PUT')[0][1].body).toBe(JSON.stringify({ capacity: 4 }))
    expect(await screen.findByRole('status')).toHaveTextContent('2 learners moved off the waitlist')
  })

  it('will not save an invalid capacity', async () => {
    render(<CourseEnrollmentPanel courseId="c" published />)
    await userEvent.type(await screen.findByLabelText('Seats (empty for no limit)'), 'x')
    expect(screen.getByRole('button', { name: 'Save capacity' })).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('whole number')
  })

  it('promotes a chosen number of people', async () => {
    render(<CourseEnrollmentPanel courseId="c" published />)
    await userEvent.type(await screen.findByLabelText('How many'), '1')
    await userEvent.click(screen.getByRole('button', { name: 'Promote' }))
    expect(callsTo('/waitlist/promote', 'POST')[0][1].body).toBe(JSON.stringify({ count: 1 }))
  })

  it('offers no promotion for a course that is not published', async () => {
    render(<CourseEnrollmentPanel courseId="c" published={false} />)
    await screen.findByText('Waitlist (2)')
    expect(screen.queryByRole('button', { name: 'Promote' })).not.toBeInTheDocument()
  })
})

// ---------- prerequisites and drip ----------
const rules = {
  prerequisites: [{ courseId: 'p1', code: 'BAS-1', title: 'Basics' }],
  modules: [
    { moduleId: 'm1', title: 'Week 1', displayOrder: 1, releaseAfterDays: null, releaseOnUtc: null, requiresModuleId: null },
    { moduleId: 'm2', title: 'Week 2', displayOrder: 2, releaseAfterDays: 7, releaseOnUtc: null, requiresModuleId: 'm1' },
  ],
}

describe('describeEnrollment', () => {
  it('counts who was enrolled and waitlisted, and gives the reason for anyone who was not let in', () => {
    expect(describeEnrollment({ enrolled: 2, waitlisted: 1, results: [{ learnerUserId: 'a', name: 'Ada', outcome: 'Enrolled', message: null }, { learnerUserId: 'b', name: 'Ben', outcome: 'MissingPrerequisites', message: 'Complete BASE-1 first.' }] }))
      .toBe('2 enrolled. 1 put on the waitlist because the course is full. Ben: Complete BASE-1 first.')
    expect(describeEnrollment({ enrolled: 0, waitlisted: 0, results: [{ learnerUserId: 'a', name: 'Ada', outcome: 'AlreadyEnrolled', message: 'Already enrolled.' }] })).toBe('Ada: Already enrolled.')
    expect(describeEnrollment({ enrolled: 0, waitlisted: 0, results: [] })).toBe('Nobody was enrolled.')
  })
})

describe('adding learners to a course', () => {
  const people = [
    { userId: 'u1', name: 'Ada Learner', email: 'ada@x.test', role: 'Learner' },
    { userId: 'u2', name: 'Ben Learner', email: 'ben@x.test', role: 'Learner' },
  ]
  const enrolled = [{ enrollmentId: 'e1', learnerUserId: 'u9', name: 'Cleo Student', email: 'cleo@x.test', status: 'Active', source: 'Administrator', progressPercent: 40, enrolledAtUtc: '2026-10-01T00:00:00Z' }]
  let roster: typeof enrolled
  beforeEach(() => {
    request.mockReset(); roster = [...enrolled]
    request.mockImplementation((path: string, options?: { method?: string; body?: string }) => {
      if (path.includes('/enrollable-learners')) return Promise.resolve(path.includes('q=ben') ? [people[1]] : people)
      if (path.endsWith('/enrollments') && options?.method === 'POST') return Promise.resolve({ enrolled: 2, waitlisted: 0, results: [] })
      if (path.endsWith('/enrollments')) return Promise.resolve(roster)
      if (path.endsWith('/withdraw')) { roster = []; return Promise.resolve({ status: 'Withdrawn', promoted: 0 }) }
      return Promise.resolve(summary())
    })
  })

  it('lists who is enrolled with their progress, and the people who can be added', async () => {
    render(<CourseEnrollmentPanel courseId="c" published />)
    expect(await screen.findByText('Enrolled learners (1)')).toBeInTheDocument()
    const list = screen.getByRole('list', { name: 'Enrolled learners' })
    expect(within(list).getByText('Cleo Student')).toBeInTheDocument()
    expect(within(list).getByText('40% done')).toBeInTheDocument()
    const offered = await screen.findByRole('list', { name: 'People who can be added' })
    expect(within(offered).getAllByRole('checkbox')).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'Enroll selected' })).toBeDisabled()
  })

  it('enrolls the people chosen, says what happened, and clears the choice', async () => {
    render(<CourseEnrollmentPanel courseId="c" published />)
    const offered = await screen.findByRole('list', { name: 'People who can be added' })
    await userEvent.click(within(offered).getByLabelText(/Ada Learner/))
    await userEvent.click(within(offered).getByLabelText(/Ben Learner/))
    await userEvent.click(screen.getByLabelText(/Enroll even if they have not finished/))
    await userEvent.click(screen.getByRole('button', { name: 'Enroll 2 selected' }))
    await vi.waitFor(() => expect(callsTo('/c/enrollments', 'POST')).toHaveLength(1))
    expect(JSON.parse(callsTo('/c/enrollments', 'POST')[0][1].body)).toEqual({ learnerUserIds: ['u1', 'u2'], overridePrerequisites: true })
    expect(await screen.findByText('2 enrolled.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Enroll selected' })).toBeDisabled()
  })

  it('searches the people offered as you type', async () => {
    render(<CourseEnrollmentPanel courseId="c" published />)
    await screen.findByRole('list', { name: 'People who can be added' })
    await userEvent.type(screen.getByLabelText('Find people'), 'ben')
    await vi.waitFor(() => expect(within(screen.getByRole('list', { name: 'People who can be added' })).queryByText('Ada Learner')).toBeNull(), { timeout: 3000 })
    expect(within(screen.getByRole('list', { name: 'People who can be added' })).getByText('Ben Learner')).toBeInTheDocument()
    expect(callsTo('q=ben').length).toBeGreaterThan(0)
  })

  it('removes someone after confirming', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<CourseEnrollmentPanel courseId="c" published />)
    await userEvent.click(await screen.findByRole('button', { name: 'Remove Cleo Student' }))
    await vi.waitFor(() => expect(callsTo('/e1/withdraw', 'POST')).toHaveLength(1))
    expect(await screen.findByText('Cleo Student was removed from the course.')).toBeInTheDocument()
    expect(await screen.findByText('Nobody is enrolled yet.')).toBeInTheDocument()
  })

  it('does not offer enrolling in a course that is not published, and says why', async () => {
    render(<CourseEnrollmentPanel courseId="c" published={false} />)
    expect(await screen.findByText(/Publish the course first/)).toBeInTheDocument()
    await screen.findByRole('list', { name: 'People who can be added' })
    expect(screen.getByLabelText('Find people')).toBeDisabled()
    for (const box of screen.getAllByRole('checkbox')) expect(box).toBeDisabled()
  })

  it('shows the server message when enrolling fails', async () => {
    const { ApiError } = await import('@/lib/api')
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/enrollments') && options?.method === 'POST'
      ? Promise.resolve().then(() => { throw new ApiError('Publish the course before enrolling learners.', 409) })
      : Promise.resolve(path.includes('/enrollable-learners') ? people : path.endsWith('/enrollments') ? [] : summary()))
    render(<CourseEnrollmentPanel courseId="c" published />)
    await userEvent.click(within(await screen.findByRole('list', { name: 'People who can be added' })).getByLabelText(/Ada Learner/))
    await userEvent.click(screen.getByRole('button', { name: 'Enroll 1 selected' }))
    expect(await screen.findByText('Publish the course before enrolling learners.')).toBeInTheDocument()
  })
})

describe('rule helpers', () => {
  it('describes a rule in plain words', () => {
    expect(describeRule(rules.modules[0], rules.modules)).toBe('Always open')
    expect(describeRule(rules.modules[1], rules.modules)).toBe('Opens 7 days after enrollment, after finishing “Week 1”')
    expect(describeRule({ ...rules.modules[0], releaseAfterDays: 1 }, rules.modules)).toBe('Opens 1 day after enrollment')
  })
  it('turns blank fields into no condition', () => {
    expect(toAccessBody({ days: '', on: '', requires: '' })).toEqual({ releaseAfterDays: null, releaseOnUtc: null, requiresModuleId: null })
    expect(toAccessBody({ days: '0', on: '', requires: 'm1' })).toEqual({ releaseAfterDays: 0, releaseOnUtc: null, requiresModuleId: 'm1' })
    expect(toAccessBody({ days: '', on: '2026-10-05T09:30', requires: '' }).releaseOnUtc).toMatch(/^2026-10-05T/)
  })
  it('round-trips a date into the input format', () => {
    expect(toLocalInput(null)).toBe('')
    expect(toLocalInput(new Date(2026, 9, 5, 9, 30).toISOString())).toBe('2026-10-05T09:30')
  })
})

describe('CourseRulesPanel', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path?: string) => Promise.resolve(String(path).endsWith('/access-rules') ? rules : String(path).endsWith('/courses') ? [{ id: 'c', code: 'ADV-1', title: 'Advanced' }, { id: 'p1', code: 'BAS-1', title: 'Basics' }, { id: 'p2', code: 'BAS-2', title: 'Basics two' }] : rules))
  })

  it('lists the other courses with the current prerequisites ticked, never the course itself', async () => {
    render(<CourseRulesPanel courseId="c" />)
    expect(await screen.findByLabelText(/Basics BAS-1/)).toBeChecked()
    expect(screen.getByLabelText(/Basics two/)).not.toBeChecked()
    expect(screen.queryByLabelText(/Advanced/)).not.toBeInTheDocument()
  })

  it('saves the chosen prerequisites', async () => {
    render(<CourseRulesPanel courseId="c" />)
    await userEvent.click(await screen.findByLabelText(/Basics two/))
    await userEvent.click(screen.getByRole('button', { name: 'Save prerequisites' }))
    expect(callsTo('/prerequisites', 'PUT')[0][1].body).toBe(JSON.stringify({ courseIds: ['p1', 'p2'] }))
  })

  it('only offers earlier modules as "finish first" and saves a module rule', async () => {
    render(<CourseRulesPanel courseId="c" />)
    const first = await screen.findByLabelText('Finish first', { selector: '#requires-m1' })
    expect(first).toBeDisabled() // nothing comes before the first module
    const second = screen.getByLabelText('Finish first', { selector: '#requires-m2' })
    expect(within(second).getAllByRole('option').map((option) => option.textContent)).toEqual(['Nothing', 'Week 1'])
    const days = screen.getByLabelText('Days after enrollment', { selector: '#days-m2' })
    await userEvent.clear(days)
    await userEvent.type(days, '14')
    await userEvent.click(screen.getAllByRole('button', { name: 'Save' })[1])
    expect(callsTo('/modules/m2/access', 'PUT')[0][1].body).toBe(JSON.stringify({ releaseAfterDays: 14, releaseOnUtc: null, requiresModuleId: 'm1' }))
  })

  it('clears a rule', async () => {
    render(<CourseRulesPanel courseId="c" />)
    await userEvent.click((await screen.findAllByRole('button', { name: 'Clear' }))[1])
    expect(callsTo('/modules/m2/access', 'PUT')[0][1].body).toBe(JSON.stringify({ releaseAfterDays: null, releaseOnUtc: null, requiresModuleId: null }))
  })
})

// ---------- cohorts ----------
describe('cohort helpers', () => {
  it('summarises a bulk result', () => {
    expect(summaryLine({ results: [], succeeded: 3, waitlisted: 1, alreadyIn: 2, blocked: 0 }, 'enrolled')).toBe('3 enrolled · 1 on the waitlist · 2 already in')
    expect(summaryLine({ results: [], succeeded: 0, waitlisted: 0, alreadyIn: 0, blocked: 4 }, 'invited')).toBe('0 invited · 4 blocked')
    expect(outcomeLabel.MissingPrerequisites).toBe('Missing a prerequisite')
  })
})

describe('CohortsPage', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path?: string, options?: { method?: string }) => {
      const url = String(path)
      if (url.includes('/enroll')) return Promise.resolve({ succeeded: 1, waitlisted: 0, alreadyIn: 0, blocked: 1, results: [
        { userId: 'u1', name: 'Ada', outcome: 'Enrolled', message: null, missingPrerequisites: [] },
        { userId: 'u2', name: 'Ben', outcome: 'MissingPrerequisites', message: 'x', missingPrerequisites: ['Basics'] }] })
      if (url.includes('/cohorts/k1')) return Promise.resolve({ id: 'k1', name: 'Class A', description: null, startDateAd: null, endDateAd: null, memberCount: 2, skipped: 0, members: [{ userId: 'u1', name: 'Ada', email: 'a@x.test' }, { userId: 'u2', name: 'Ben', email: 'b@x.test' }] })
      if (url.endsWith('/cohorts') && options?.method === 'POST') return Promise.resolve({ id: 'k1', name: 'Class A', description: null, startDateAd: null, endDateAd: null, memberCount: 0, skipped: 0, members: [] })
      if (url.endsWith('/cohorts')) return Promise.resolve([{ id: 'k1', name: 'Class A', description: 'Morning batch', startDateAd: null, endDateAd: null, memberCount: 2 }])
      if (url.endsWith('/courses')) return Promise.resolve([{ id: 'c1', code: 'ADV-1', title: 'Advanced', status: 'Published' }])
      return Promise.resolve([])
    })
  })

  it('enrolls a whole cohort and explains each person’s outcome', async () => {
    render(<CohortsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Class A' }))
    expect(await screen.findByText('Members (2)')).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText('Course'), 'c1')
    await userEvent.click(screen.getByRole('button', { name: 'Enroll cohort' }))
    expect(callsTo('/cohorts/k1/enroll', 'POST')[0][1].body).toBe(JSON.stringify({ courseId: 'c1', overridePrerequisites: false }))
    const status = await screen.findByRole('status')
    expect(status).toHaveTextContent('1 enrolled · 1 blocked')
    expect(status).toHaveTextContent('needs Basics')
    expect(within(status).getByText('Missing a prerequisite')).toBeInTheDocument()
  })

  it('lists cohorts as rows and opens nothing by default', async () => {
    render(<CohortsPage />)
    const rows = within(await screen.findByRole('list', { name: 'Cohorts' })).getAllByRole('listitem')
    expect(rows).toHaveLength(1)
    expect(rows[0]).toHaveTextContent('Morning batch')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('opens a New cohort panel, marks the name required and blocks an empty submit', async () => {
    render(<CohortsPage />)
    await userEvent.click(await screen.findByRole('button', { name: /New cohort/ }))
    expect(screen.getByRole('dialog', { name: 'New cohort' })).toBeInTheDocument()
    expect(screen.getByText('Name')).toHaveClass('after:text-red-500')
    await userEvent.click(screen.getByRole('button', { name: 'Create cohort' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a name')
    expect(callsTo('/cohorts', 'POST')).toHaveLength(0)
  })

  it('creates a cohort with the same request body and then shows its details', async () => {
    render(<CohortsPage />)
    await userEvent.click(await screen.findByRole('button', { name: /New cohort/ }))
    await userEvent.type(screen.getByLabelText('Name'), 'Class A')
    await userEvent.click(screen.getByRole('button', { name: 'Create cohort' }))
    expect(callsTo('/cohorts', 'POST')[0][1].body).toBe(JSON.stringify({ name: 'Class A', description: '', startDateAd: null, endDateAd: null }))
    expect(await screen.findByText('Members (0)')).toBeInTheDocument()
  })

  it('cannot enroll until a course is chosen', async () => {
    render(<CohortsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Class A' }))
    await screen.findByText('Members (2)')
    expect(screen.getByRole('button', { name: 'Enroll cohort' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Send invitations' })).toBeDisabled()
  })
})

// ---------- invitations ----------
describe('invitation helpers', () => {
  const now = new Date('2026-10-01T12:00:00Z')
  it('describes time left', () => {
    expect(expiresIn('2026-09-30T00:00:00Z', now)).toBe('expired')
    expect(expiresIn('2026-10-01T20:00:00Z', now)).toBe('expires today')
    expect(expiresIn('2026-10-04T12:00:00Z', now)).toBe('3 days left')
  })
  it('explains the acceptance outcome', () => {
    expect(acceptedMessage({ outcome: 'Enrolled', courseTitle: 'Maths' })).toBe('You are enrolled in Maths.')
    expect(acceptedMessage({ outcome: 'Waitlisted', courseTitle: 'Maths' })).toMatch(/waitlist/)
    expect(acceptedMessage({ outcome: 'AlreadyEnrolled', courseTitle: 'Maths' })).toMatch(/already/)
  })
})

describe('InvitationsPage', () => {
  const mine = [{ id: 'i1', courseId: 'c1', courseCode: 'MAT-1', courseTitle: 'Maths', invitedBy: 'Ms Rai', message: 'Please join', createdAtUtc: '2026-10-01T00:00:00Z', expiresAtUtc: new Date(Date.now() + 5 * 86400000).toISOString() }]

  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path?: string, options?: { method?: string }) => {
      const url = String(path)
      if (url.endsWith('/invitations/i1/accept')) return Promise.resolve({ outcome: 'Waitlisted', courseId: 'c1', courseTitle: 'Maths', enrollmentId: 'e' })
      if (url.endsWith('/invitations') && options?.method === 'POST') return Promise.resolve({ id: 'i9', token: 'one-time-token-value', expiresAtUtc: '2026-10-15T00:00:00Z' })
      if (url.endsWith('/invitations/mine')) return Promise.resolve(mine)
      if (url.includes('/invitations')) return Promise.resolve([])
      if (url.endsWith('/courses')) return Promise.resolve([{ id: 'c1', code: 'MAT-1', title: 'Maths', status: 'Published' }])
      return Promise.resolve(undefined)
    })
  })

  it('lets a learner accept an invitation and tells them the outcome', async () => {
    permissions = ['enrollment.read']
    render(<InvitationsPage />)
    expect(await screen.findByText('Maths')).toBeInTheDocument()
    expect(screen.getByText('Please join')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /New invitation/ })).not.toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'My invitations' })).getAllByRole('listitem')).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: /Accept invitation to Maths/ }))
    expect(await screen.findByRole('status')).toHaveTextContent('Maths is full, so you are on the waitlist')
  })

  it('accepts with a pasted code', async () => {
    permissions = ['enrollment.read']
    render(<InvitationsPage />)
    await userEvent.type(await screen.findByLabelText('Invitation code'), '  abc123  ')
    await userEvent.click(screen.getByRole('button', { name: 'Use code' }))
    expect(callsTo('/invitations/accept', 'POST')[0][1].body).toBe(JSON.stringify({ token: 'abc123' }))
  })

  it('shows staff the one-time code after sending', async () => {
    permissions = ['enrollment.read', 'enrollment.manage']
    render(<InvitationsPage />)
    await userEvent.click(await screen.findByRole('button', { name: /New invitation/ }))
    expect(screen.getByRole('dialog', { name: 'New invitation' })).toBeInTheDocument()
    expect(screen.getByText('Email address')).toHaveClass('after:text-red-500')
    await userEvent.selectOptions(await screen.findByLabelText('Course'), 'c1')
    await userEvent.type(screen.getByLabelText('Email address'), 'new@x.test')
    await userEvent.click(screen.getByRole('button', { name: 'Send invitation' }))
    expect(await screen.findByText('one-time-token-value')).toBeInTheDocument()
    expect(screen.getByText(/shown only once/)).toBeInTheDocument()
    expect(callsTo('/invitations', 'POST')[0][1].body).toBe(JSON.stringify({ courseId: 'c1', email: 'new@x.test', message: '', expiresInDays: 14 }))
  })

  it('blocks an empty invitation before sending', async () => {
    permissions = ['enrollment.read', 'enrollment.manage']
    render(<InvitationsPage />)
    await userEvent.click(await screen.findByRole('button', { name: /New invitation/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Send invitation' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Choose a course')
    expect(callsTo('/invitations', 'POST')).toHaveLength(0)
  })
})
