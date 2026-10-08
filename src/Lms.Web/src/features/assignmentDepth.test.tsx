import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AssignmentGroupsPanel from './AssignmentGroupsPanel'
import AssignmentSimilarityPanel from './AssignmentSimilarityPanel'
import AssignmentsPage from './AssignmentsPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args), downloadFile: vi.fn() }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, user: { id: 'u1' } } }) }))

const calls = (method: string, fragment = '') => request.mock.calls.filter((call) => (call[1]?.method ?? 'GET') === method && String(call[0]).includes(fragment))
const bodyOf = (call: unknown[]) => JSON.parse((call[1] as { body: string }).body)

const criteria = [
  { id: 'k1', name: 'Content', levels: [{ label: 'Full', points: 6 }, { label: 'Little', points: 0 }] },
  { id: 'k2', name: 'Clarity', levels: [{ label: 'Clear', points: 4 }, { label: 'Muddled', points: 1 }] },
]
const rubric = { id: 'r1', courseId: 'c1', name: 'Report rubric', totalPoints: 10, usedByQuestions: 0, criteria }
const course = { id: 'c1', code: 'ENG-1', title: 'English', status: 'Published' }
const base = {
  id: 'a1', courseId: 'c1', courseTitle: 'English', title: 'Team project', instructions: 'Build it.', maxPoints: 10, dueAtUtc: null, allowLate: false, latePenaltyPercent: 0,
  status: 'Published', submissionCount: 0, gradedCount: 0, mySubmission: null,
}
const submission = (over: object) => ({ id: 's', assignmentId: 'a1', learnerUserId: 'x', learnerName: 'X', textResponse: 'Work', fileName: null, fileSizeBytes: null, submissionCount: 1, isLate: false, status: 'Submitted', scorePoints: null, finalPoints: null, feedback: null, submittedAtUtc: '2026-01-01T00:00:00Z', gradedAtUtc: null, ...over })

function serve(assignment: object, submissions: unknown[] = []) {
  request.mockImplementation((path: string, init?: { method?: string }) => {
    if (init?.method) return Promise.resolve({})
    if (path === '/api/v1/tenant/courses') return Promise.resolve([course])
    if (path.endsWith('/rubrics')) return Promise.resolve([rubric])
    if (path.endsWith('/submissions')) return Promise.resolve(submissions)
    if (path.endsWith('/groups')) return Promise.resolve({ groups: [], unassigned: [] })
    return Promise.resolve([assignment])
  })
}

beforeEach(() => { request.mockReset(); permissions = [] })

describe('creating a rubric or group assignment', () => {
  it('lets the author pick a rubric, which sets the points, and mark group work', async () => {
    permissions = ['assessment.read', 'assessment.manage']
    serve(base)
    render(<AssignmentsPage />)
    await screen.findByText('Team project')
    await userEvent.click(screen.getByRole('button', { name: 'New assignment' }))
    await userEvent.selectOptions(screen.getByLabelText('Course', { selector: '#new-assignment-course' }), 'c1')
    await userEvent.type(screen.getByLabelText('Title'), 'Report')
    await userEvent.selectOptions(await screen.findByLabelText('Scoring rubric'), 'r1')
    expect(screen.getByLabelText('Maximum points')).toBeDisabled()
    expect(screen.getByLabelText('Maximum points')).toHaveValue(10)
    await userEvent.click(screen.getByLabelText(/Group work/))
    await userEvent.click(screen.getByRole('button', { name: 'Create draft' }))
    await waitFor(() => expect(calls('POST', '/api/v1/tenant/assignments')).toHaveLength(1))
    expect(bodyOf(calls('POST', '/api/v1/tenant/assignments')[0])).toMatchObject({ courseId: 'c1', title: 'Report', maxPoints: 10, rubricId: 'r1', isGroup: true })
    expect(await screen.findByText(/form the groups and publish it/)).toBeInTheDocument()
  })
})

describe('a learner on group work', () => {
  const open = async () => { render(<AssignmentsPage />); await userEvent.click(await screen.findByRole('button', { name: 'View details for Team project' })) }

  it('sees their group, and the rubric they will be marked by', async () => {
    permissions = ['assessment.read', 'assessment.attempt']
    serve({ ...base, isGroup: true, myGroup: { id: 'g1', name: 'Team A', members: ['Lena', 'Otto'] }, rubricId: 'r1', rubric: { id: 'r1', name: 'Report rubric', totalPoints: 10, criteria } })
    await open()
    expect(await screen.findByText(/Your group:/)).toHaveTextContent('Team A — Lena, Otto')
    expect(screen.getByText(/How this is marked: Report rubric \(10 points\)/)).toBeInTheDocument()
    expect(screen.getByLabelText('Written response')).toBeInTheDocument()
    expect(screen.getByText(/This counts for your whole group/)).toBeInTheDocument()
  })

  it('is told to wait when not in a group yet and cannot submit', async () => {
    permissions = ['assessment.read', 'assessment.attempt']
    serve({ ...base, isGroup: true, myGroup: null })
    await open()
    expect(await screen.findByText(/not been placed in a group yet/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Written response')).toBeNull()
  })
})

describe('grading', () => {
  const open = async () => {
    render(<AssignmentsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Team project' }))
    await userEvent.click(await screen.findByRole('tab', { name: 'Grading' }))
  }

  it('scores a rubric assignment criterion by criterion and sends the criteria, not a typed score', async () => {
    permissions = ['assessment.read', 'assessment.manage', 'grade.manage']
    serve({ ...base, rubricId: 'r1', rubric: { id: 'r1', name: 'Report rubric', totalPoints: 10, criteria } }, [submission({ id: 's1', learnerName: 'Lena' })])
    await open()
    expect(screen.queryByLabelText('Score')).toBeNull()
    await userEvent.click(await screen.findByRole('button', { name: 'Grade' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose a level for “Content”.')
    expect(calls('POST')).toHaveLength(0)

    await userEvent.click(within(screen.getByRole('radiogroup', { name: 'Content' })).getByLabelText('Full (6)'))
    await userEvent.click(within(screen.getByRole('radiogroup', { name: 'Clarity' })).getByLabelText('Muddled (1)'))
    expect(screen.getByText('Score: 7 of 10')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Feedback'), 'Good')
    await userEvent.click(screen.getByRole('button', { name: 'Grade' }))
    await waitFor(() => expect(calls('POST', '/grade')).toHaveLength(1))
    expect(bodyOf(calls('POST', '/grade')[0])).toEqual({ scorePoints: 0, feedback: 'Good', criterionScores: [{ criterionId: 'k1', points: 6 }, { criterionId: 'k2', points: 1 }] })
  })

  it('shows a group once, naming its members, and grades it as one', async () => {
    permissions = ['assessment.read', 'assessment.manage', 'grade.manage']
    serve({ ...base, isGroup: true }, [
      submission({ id: 's1', learnerName: 'Lena', groupId: 'g1', groupName: 'Team A' }),
      submission({ id: 's2', learnerName: 'Otto', groupId: 'g1', groupName: 'Team A' }),
      submission({ id: 's3', learnerName: 'Cleo' }),
    ])
    await open()
    expect(await screen.findByText('Team A')).toBeInTheDocument()
    expect(screen.getAllByText('Team A')).toHaveLength(1)
    expect(screen.getByText(/Group of 2: Lena, Otto\. One grade for all\./)).toBeInTheDocument()
    expect(screen.getByText('Cleo')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Grade' })).toHaveLength(2)
  })

  it('offers the copied-work check to graders', async () => {
    permissions = ['assessment.read', 'assessment.manage', 'grade.manage']
    serve(base, [])
    await open()
    expect(await screen.findByRole('region', { name: 'Copied work check' })).toBeInTheDocument()
  })
})

describe('AssignmentSimilarityPanel', () => {
  const report = {
    submissions: 4, compared: 3, tooShort: 1, filesNotRead: 1, threshold: 30,
    pairs: [{ firstSubmissionId: 's1', firstName: 'Lena', secondSubmissionId: 's2', secondName: 'Otto', percent: 96, sharedPhrases: 31, examples: ['the industrial revolution changed the way'] }],
  }

  it('runs the check with the chosen threshold and shows each pair with what they share', async () => {
    request.mockResolvedValue(report)
    render(<AssignmentSimilarityPanel assignmentId="a1" />)
    const threshold = screen.getByLabelText(/at least this alike/)
    await userEvent.clear(threshold)
    await userEvent.type(threshold, '50')
    await userEvent.click(screen.getByRole('button', { name: 'Run the check' }))
    expect(await screen.findByText(/Lena and Otto/)).toBeInTheDocument()
    expect(request).toHaveBeenCalledWith('/api/v1/tenant/assignments/a1/similarity?threshold=50')
    expect(screen.getByText(/96% alike · 31 shared phrases/)).toBeInTheDocument()
    expect(screen.getByText('“the industrial revolution changed the way”')).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Compared 3 of 4 submissions; 1 too short to compare; 1 attached file could not be read')
  })

  it('says so when nothing is alike and refuses a silly threshold', async () => {
    request.mockResolvedValue({ ...report, pairs: [], tooShort: 0, filesNotRead: 0 })
    render(<AssignmentSimilarityPanel assignmentId="a1" />)
    await userEvent.clear(screen.getByLabelText(/at least this alike/))
    await userEvent.type(screen.getByLabelText(/at least this alike/), '2')
    await userEvent.click(screen.getByRole('button', { name: 'Run the check' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('5% to 100%')
    expect(request).not.toHaveBeenCalled()
    await userEvent.clear(screen.getByLabelText(/at least this alike/))
    await userEvent.type(screen.getByLabelText(/at least this alike/), '30')
    await userEvent.click(screen.getByRole('button', { name: 'Run the check' }))
    expect(await screen.findByText('No pairs are 30% alike or more.')).toBeInTheDocument()
  })
})

describe('AssignmentGroupsPanel', () => {
  const overview = {
    groups: [{ id: 'g1', name: 'Team A', hasSubmitted: false, members: [{ userId: 'u1', name: 'Lena' }] }, { id: 'g2', name: 'Team B', hasSubmitted: true, members: [{ userId: 'u3', name: 'Cleo' }] }],
    unassigned: [{ userId: 'u2', name: 'Otto' }, { userId: 'u4', name: 'Pia' }],
  }
  beforeEach(() => { request.mockImplementation((path: string, init?: { method?: string }) => Promise.resolve(init?.method ? null : overview)); vi.spyOn(window, 'confirm').mockReturnValue(true) })

  it('lists groups and who is left, and locks a group that has submitted', async () => {
    render(<AssignmentGroupsPanel assignmentId="a1" />)
    expect(await screen.findByText('Team A')).toBeInTheDocument()
    expect(screen.getByText(/Not in a group yet: Otto, Pia\./)).toBeInTheDocument()
    expect(screen.getByText('Team B · submitted')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit group Team B' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Delete group Team B' })).toBeDisabled()
  })

  it('creates a group from unassigned learners after checking the form', async () => {
    render(<AssignmentGroupsPanel assignmentId="a1" />)
    await screen.findByText('Team A')
    await userEvent.click(screen.getByRole('button', { name: 'Create group' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a name for the group.')
    await userEvent.type(screen.getByLabelText('Group name'), 'Team C')
    await userEvent.click(screen.getByRole('button', { name: 'Create group' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose at least one learner.')
    await userEvent.click(screen.getByLabelText('Otto'))
    await userEvent.click(screen.getByLabelText('Pia'))
    await userEvent.click(screen.getByRole('button', { name: 'Create group' }))
    await waitFor(() => expect(calls('POST', '/groups')).toHaveLength(1))
    expect(bodyOf(calls('POST', '/groups')[0])).toEqual({ name: 'Team C', memberUserIds: ['u2', 'u4'] })
  })

  it('edits a group, offering its own members as well as the unassigned, and deletes one', async () => {
    render(<AssignmentGroupsPanel assignmentId="a1" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Edit group Team A' }))
    const members = screen.getByRole('group', { name: 'Members' })
    expect(within(members).getAllByRole('checkbox').map((box) => (box as HTMLInputElement).checked)).toEqual([true, false, false])   // Lena (in it), Otto, Pia
    await userEvent.click(within(members).getByLabelText('Otto'))
    await userEvent.click(screen.getByRole('button', { name: 'Save group' }))
    await waitFor(() => expect(calls('PUT', '/groups/g1')).toHaveLength(1))
    expect(bodyOf(calls('PUT', '/groups/g1')[0])).toEqual({ name: 'Team A', memberUserIds: ['u1', 'u2'] })

    await userEvent.click(screen.getByRole('button', { name: 'Delete group Team A' }))
    await waitFor(() => expect(calls('DELETE', '/groups/g1')).toHaveLength(1))
  })
})
