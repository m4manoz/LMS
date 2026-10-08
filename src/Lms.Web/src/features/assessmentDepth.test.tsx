import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AssessmentAccommodationsPanel from './AssessmentAccommodationsPanel'
import AssessmentAttemptPanel from './AssessmentAttemptPanel'
import AssessmentGradingPanel from './AssessmentGradingPanel'
import AssessmentQuestionsPanel from './AssessmentQuestionsPanel'
import AssessmentRubricsPanel from './AssessmentRubricsPanel'
import AssessmentsPage from './AssessmentsPage'
import type { Attempt, AssessmentDetail, Rubric } from '@/lib/assessments'
import { timeLeft } from '@/lib/assessments'

const request = vi.fn()
const download = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args), downloadFile: (...args: unknown[]) => download(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't' } }) }))

const calls = (method: string, fragment = '') => request.mock.calls.filter((call) => (call[1]?.method ?? 'GET') === method && String(call[0]).includes(fragment))
const bodyOf = (call: unknown[]) => JSON.parse((call[1] as { body: string }).body)

const rubric: Rubric = {
  id: 'r1', courseId: 'c1', name: 'Essay rubric', totalPoints: 10, usedByQuestions: 0,
  criteria: [
    { id: 'k1', name: 'Argument', description: 'Convincing?', levels: [{ label: 'Strong', points: 6 }, { label: 'Weak', points: 0 }] },
    { id: 'k2', name: 'Grammar', levels: [{ label: 'Clean', points: 4 }, { label: 'Errors', points: 1 }] },
  ],
}

function attemptWith(overrides: Partial<Attempt['attempt']> = {}, questions: Attempt['questions'] = []): Attempt {
  return {
    attempt: { id: 'a1', learnerUserId: 'u1', attemptNumber: 1, status: 'InProgress', scorePoints: 0, possiblePoints: 10, percentage: null, learnerName: 'Lena', ...overrides },
    assessmentTitle: 'Midterm', instructions: null, questions, teacherFeedback: null, expiresAtUtc: null,
  }
}
const q = (partial: Partial<Attempt['questions'][number]>): Attempt['questions'][number] => ({ id: 'q', type: 'MultipleChoice', prompt: 'P', options: [], correctAnswers: [], displayOrder: 1, points: 1, answers: [], scorePoints: 0, ...partial })

beforeEach(() => { request.mockReset(); download.mockReset(); request.mockResolvedValue(null); download.mockResolvedValue(undefined); permissions = [] })

describe('timeLeft', () => {
  it('counts down and stops at zero', () => {
    const now = new Date('2026-01-01T10:00:00Z').getTime()
    expect(timeLeft('2026-01-01T10:12:05Z', now)).toBe('12:05')
    expect(timeLeft('2026-01-01T10:00:09Z', now)).toBe('0:09')
    expect(timeLeft('2026-01-01T09:59:59Z', now)).toBeNull()
    expect(timeLeft(null, now)).toBeNull()
  })
})

describe('AssessmentAttemptPanel', () => {
  const questions = [
    q({ id: 'mc', type: 'MultipleChoice', prompt: 'Pick one', options: ['red', 'blue'], displayOrder: 1 }),
    q({ id: 'mr', type: 'MultipleResponse', prompt: 'Pick some', options: ['a, b', 'c', 'd'], displayOrder: 2 }),
    q({ id: 'sa', type: 'ShortAnswer', prompt: 'Name it', displayOrder: 3 }),
    q({ id: 'es', type: 'Essay', prompt: 'Discuss', displayOrder: 4, points: 10, rubric: { id: 'r1', name: 'Essay rubric', totalPoints: 10, criteria: rubric.criteria } }),
    q({ id: 'fu', type: 'FileUpload', prompt: 'Upload', displayOrder: 5 }),
  ]

  it('shows each question the way it is answered and saves choices, text and files', async () => {
    const onChange = vi.fn()
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path.endsWith('/file') && options?.method === 'POST') return Promise.resolve({ fileName: 'report.pdf', sizeBytes: 2048 })
      if (path.endsWith('/submit')) return Promise.resolve(attemptWith({ status: 'Submitted' }, questions))
      return Promise.resolve(null)
    })
    render(<AssessmentAttemptPanel attempt={attemptWith({}, questions)} canAttempt onChange={onChange} />)

    await userEvent.click(screen.getByLabelText('blue'))
    await userEvent.click(screen.getByLabelText('a, b'))   // an option with a comma stays one option
    await userEvent.click(screen.getByLabelText('d'))
    await userEvent.type(screen.getByLabelText('Answer 3'), 'Kathmandu')
    await userEvent.type(screen.getByLabelText('Answer 4'), 'My essay')
    expect(screen.getByText(/How this is marked: Essay rubric \(10 points\)/)).toBeInTheDocument()

    await userEvent.upload(screen.getByLabelText('Attach a file to question 5'), new File(['x'], 'report.pdf', { type: 'application/pdf' }))
    await waitFor(() => expect(calls('POST', '/answers/fu/file')).toHaveLength(1))
    expect((calls('POST', '/answers/fu/file')[0][1] as { body: FormData }).body).toBeInstanceOf(FormData)
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ questions: expect.arrayContaining([expect.objectContaining({ id: 'fu', file: { fileName: 'report.pdf', sizeBytes: 2048 } })]) }))

    await userEvent.click(screen.getByRole('button', { name: 'Submit attempt' }))
    await waitFor(() => expect(calls('POST', '/submit')).toHaveLength(1))
    const saved = Object.fromEntries(calls('PUT').map((call) => [String(call[0]).split('/answers/')[1], bodyOf(call)]))
    expect(saved.mc).toEqual({ answers: ['blue'], text: null })
    expect(saved.mr).toEqual({ answers: ['a, b', 'd'], text: null })
    expect(saved.sa).toEqual({ answers: [], text: 'Kathmandu' })
    expect(saved.es).toEqual({ answers: [], text: 'My essay' })
  })

  it('lets the learner save without submitting', async () => {
    render(<AssessmentAttemptPanel attempt={attemptWith({}, [questions[0]])} canAttempt onChange={vi.fn()} />)
    await userEvent.click(screen.getByLabelText('red'))
    await userEvent.click(screen.getByRole('button', { name: 'Save answers' }))
    expect(await screen.findByText('Your answers are saved.')).toBeInTheDocument()
    expect(calls('POST', '/submit')).toHaveLength(0)
  })

  it('shows the time left and says so when it has run out', async () => {
    const soon = new Date(Date.now() + 5 * 60_000).toISOString()
    const { unmount } = render(<AssessmentAttemptPanel attempt={{ ...attemptWith({}, [questions[0]]), expiresAtUtc: soon }} canAttempt onChange={vi.fn()} />)
    expect(screen.getByRole('timer')).toHaveTextContent(/[45]:\d\d left/)
    unmount()
    render(<AssessmentAttemptPanel attempt={{ ...attemptWith({}, [questions[0]]), expiresAtUtc: new Date(Date.now() - 1000).toISOString() }} canAttempt onChange={vi.fn()} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Your time is up')
    expect(screen.queryByRole('timer')).toBeNull()
  })

  it('shows scores, rubric scores and feedback once graded, and cannot be edited', async () => {
    const graded = attemptWith({ status: 'Graded', scorePoints: 9, percentage: 90 }, [q({
      id: 'es', type: 'Essay', prompt: 'Discuss', points: 10, scorePoints: 9, text: 'My essay', feedback: 'Well argued',
      rubricScores: [{ criterionId: 'k1', name: 'Argument', maxPoints: 6, points: 6 }, { criterionId: 'k2', name: 'Grammar', maxPoints: 4, points: 3 }],
    }), q({ id: 'fu', type: 'FileUpload', prompt: 'Upload', displayOrder: 2, file: { fileName: 'r.pdf', sizeBytes: 100 } })])
    render(<AssessmentAttemptPanel attempt={graded} canAttempt onChange={vi.fn()} />)
    expect(screen.getByText('90%')).toBeInTheDocument()
    expect(screen.getByText('Argument: 6 of 6')).toBeInTheDocument()
    expect(screen.getByText('Grammar: 3 of 4')).toBeInTheDocument()
    expect(screen.getByText('Feedback: Well argued')).toBeInTheDocument()
    expect(screen.getByLabelText('Answer 1')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Submit attempt' })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Download' }))   // a finished attempt can still be downloaded from
    expect(download).toHaveBeenCalledWith('/api/v1/tenant/assessment-attempts/a1/answers/fu/file', 'r.pdf')
  })
})

describe('AssessmentGradingPanel', () => {
  const summary = { id: 'a1', learnerUserId: 'u1', attemptNumber: 1, status: 'Submitted', scorePoints: 2, possiblePoints: 12, percentage: 16.67, learnerName: 'Lena' }
  const submitted = attemptWith({ status: 'Submitted', scorePoints: 2, possiblePoints: 12 }, [
    q({ id: 'mc', type: 'MultipleChoice', prompt: 'Pick', answers: ['red'], scorePoints: 2, points: 2, isCorrect: true }),
    q({ id: 'es', type: 'Essay', prompt: 'Discuss', displayOrder: 2, points: 10, text: 'Essay words', rubric: { id: 'r1', name: 'Essay rubric', totalPoints: 10, criteria: rubric.criteria } }),
    q({ id: 'fu', type: 'FileUpload', prompt: 'Upload', displayOrder: 3, points: 5, file: { fileName: 'r.pdf', sizeBytes: 100 } }),
  ])

  beforeEach(() => {
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (options?.method === 'POST') return Promise.resolve({})
      if (path.endsWith('/attempts')) return Promise.resolve([summary])
      return Promise.resolve(submitted)
    })
  })

  it('lists attempts by learner and opens one to grade, showing the written answer and file', async () => {
    render(<AssessmentGradingPanel assessmentId="as1" title="Midterm" />)
    expect(await screen.findByText(/Lena · attempt 1/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Grade attempt 1 by Lena' }))
    expect(await screen.findByText('Essay words')).toBeInTheDocument()
    expect(screen.getByText(/r\.pdf/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Download' }))
    expect(download).toHaveBeenCalledWith('/api/v1/tenant/assessment-attempts/a1/answers/fu/file', 'r.pdf')
  })

  it('needs every criterion and every score before it grades, then sends rubric scores and points', async () => {
    render(<AssessmentGradingPanel assessmentId="as1" title="Midterm" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Grade attempt 1 by Lena' }))
    await screen.findByText('Essay words')
    await userEvent.click(screen.getByRole('button', { name: 'Save grade' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose a level for “Argument” in question 2.')

    const argument = screen.getByRole('radiogroup', { name: 'Argument' })
    await userEvent.click(within(argument).getByLabelText('Strong (6)'))
    await userEvent.click(within(screen.getByRole('radiogroup', { name: 'Grammar' })).getByLabelText('Errors (1)'))
    expect(screen.getByText('Score: 7 of 10')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Save grade' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Question 3 is scored from 0 to 5.')

    await userEvent.type(screen.getByLabelText('Score for question 3'), '4')
    await userEvent.type(screen.getByLabelText('Feedback for question 2'), 'Good')
    await userEvent.type(screen.getByLabelText('Overall feedback'), 'Nice work')
    await userEvent.click(screen.getByRole('button', { name: 'Save grade' }))
    await waitFor(() => expect(calls('POST', '/grade')).toHaveLength(1))
    expect(bodyOf(calls('POST', '/grade')[0])).toEqual({
      scorePoints: 0, feedback: 'Nice work',
      answers: [
        { questionId: 'es', criterionScores: [{ criterionId: 'k1', points: 6 }, { criterionId: 'k2', points: 1 }], feedback: 'Good' },
        { questionId: 'fu', scorePoints: 4, feedback: null },
      ],
    })
    expect(await screen.findByText('The attempt was graded and the learner was notified.')).toBeInTheDocument()
  })
})

describe('AssessmentQuestionsPanel', () => {
  const detail = (over: Partial<AssessmentDetail['assessment']>, extra: Partial<AssessmentDetail> = {}): AssessmentDetail => ({
    assessment: { id: 'as1', courseId: 'c1', title: 'Quiz', status: 'Draft', attemptLimit: 1, questionCount: 2, currentVersion: 1, draftVersion: null, ...over },
    questions: [
      { id: 'q1', type: 'MultipleChoice', prompt: 'First', options: ['a', 'b'], correctAnswers: ['a'], displayOrder: 1, points: 2, pool: 'Algebra' },
      { id: 'q2', type: 'Essay', prompt: 'Second', options: [], correctAnswers: [], displayOrder: 2, points: 10, rubricId: 'r1', rubricName: 'Essay rubric' },
    ],
    pools: [{ name: 'Algebra', drawCount: 1, questionCount: 3 }], ...extra,
  })
  beforeEach(() => { request.mockImplementation((path: string) => Promise.resolve(path.endsWith('/rubrics') ? [rubric] : null)) })

  it('edits and deletes questions of a draft, and sends the pool and rubric', async () => {
    const changed = vi.fn().mockResolvedValue(undefined)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<AssessmentQuestionsPanel detail={detail({})} onChanged={changed} />)
    expect(screen.getByText(/pool Algebra/)).toBeInTheDocument()
    expect(screen.getByText('Marked with rubric: Essay rubric')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Edit question 1' }))
    expect(screen.getByRole('heading', { name: 'Edit question 1' })).toBeInTheDocument()
    const prompt = screen.getByLabelText('Prompt')
    await userEvent.clear(prompt)
    await userEvent.type(prompt, 'First, changed')
    await userEvent.click(screen.getByRole('button', { name: 'Save question' }))
    await waitFor(() => expect(calls('PUT', '/questions/q1')).toHaveLength(1))
    expect(bodyOf(calls('PUT', '/questions/q1')[0])).toEqual({ type: 'MultipleChoice', prompt: 'First, changed', options: ['a', 'b'], correctAnswers: ['a'], points: 2, pool: 'Algebra' })

    await userEvent.click(screen.getByRole('button', { name: 'Delete question 2' }))
    await waitFor(() => expect(calls('DELETE', '/questions/q2')).toHaveLength(1))
    expect(changed).toHaveBeenCalled()
  })

  it('takes its points from the chosen rubric', async () => {
    render(<AssessmentQuestionsPanel detail={detail({})} onChanged={vi.fn().mockResolvedValue(undefined)} />)
    await userEvent.selectOptions(screen.getByLabelText('Type'), 'Essay')
    await userEvent.type(screen.getByLabelText('Prompt'), 'Discuss')
    await userEvent.selectOptions(await screen.findByLabelText('Rubric'), 'r1')
    expect(screen.getByLabelText('Points')).toBeDisabled()
    expect(screen.getByLabelText('Points')).toHaveValue(10)
    await userEvent.click(screen.getByRole('button', { name: 'Add question' }))
    await waitFor(() => expect(calls('POST', '/questions')).toHaveLength(1))
    expect(bodyOf(calls('POST', '/questions')[0])).toMatchObject({ type: 'Essay', points: 10, rubricId: 'r1' })
  })

  it('changes a pool’s draw count', async () => {
    const changed = vi.fn().mockResolvedValue(undefined)
    render(<AssessmentQuestionsPanel detail={detail({})} onChanged={changed} />)
    const draw = screen.getByLabelText('Questions to draw from Algebra')
    await userEvent.clear(draw)
    await userEvent.type(draw, '2')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls('PUT', '/pools/Algebra')).toHaveLength(1))
    expect(bodyOf(calls('PUT', '/pools/Algebra')[0])).toEqual({ drawCount: 2 })
  })

  it('does not allow editing a published assessment until a new version is started, which can be published or discarded', async () => {
    const changed = vi.fn().mockResolvedValue(undefined)
    const { rerender } = render(<AssessmentQuestionsPanel detail={detail({ status: 'Published' })} onChanged={changed} />)
    expect(screen.queryByRole('button', { name: 'Edit question 1' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Add question' })).toBeNull()
    expect(screen.getByText(/Version 1 is live/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Start a new version' }))
    await waitFor(() => expect(calls('POST', '/versions')).toHaveLength(1))

    vi.spyOn(window, 'confirm').mockReturnValue(true)
    rerender(<AssessmentQuestionsPanel detail={detail({ status: 'Published', draftVersion: 2 }, { editingDraftVersion: true })} onChanged={changed} />)
    expect(screen.getByRole('button', { name: 'Edit question 1' })).toBeInTheDocument()
    expect(screen.getByText(/editing version 2/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Publish new version' }))
    await waitFor(() => expect(calls('POST', '/publish')).toHaveLength(1))
    await userEvent.click(screen.getByRole('button', { name: 'Discard new version' }))
    await waitFor(() => expect(calls('DELETE', '/versions/draft')).toHaveLength(1))
  })

  it('saves the shuffle settings', async () => {
    render(<AssessmentQuestionsPanel detail={detail({ timeLimitMinutes: 30, attemptLimit: 2 })} onChanged={vi.fn().mockResolvedValue(undefined)} />)
    await userEvent.click(screen.getByLabelText('Shuffle the order of questions for each learner'))
    await userEvent.click(screen.getByLabelText('Shuffle the options of choice questions'))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(calls('PUT', '/assessments/as1')).toHaveLength(1))
    expect(bodyOf(calls('PUT', '/assessments/as1')[0])).toMatchObject({ title: 'Quiz', timeLimitMinutes: 30, attemptLimit: 2, shuffleQuestions: true, shuffleOptions: true })
  })
})

describe('AssessmentRubricsPanel', () => {
  beforeEach(() => { request.mockImplementation((path: string, options?: { method?: string }) => Promise.resolve(options?.method ? null : path.endsWith('/rubrics') ? [rubric, { ...rubric, id: 'r2', name: 'Used rubric', usedByQuestions: 3 }] : null)) })

  it('lists rubrics and only lets an unused one be edited or deleted', async () => {
    render(<AssessmentRubricsPanel courseId="c1" />)
    expect(await screen.findByText('Used rubric')).toBeInTheDocument()
    expect(screen.getByText('Used by 3 questions')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit rubric Essay rubric' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit rubric Used rubric' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Delete rubric Used rubric' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Copy rubric Used rubric' })).toBeInTheDocument()
  })

  it('creates a rubric after checking it, showing the total as criteria change', async () => {
    render(<AssessmentRubricsPanel courseId="c1" />)
    await userEvent.click(await screen.findByRole('button', { name: 'New rubric' }))
    const panel = await screen.findByRole('dialog', { name: 'New rubric' })
    expect(within(panel).getByText('Total: 4 points')).toBeInTheDocument()   // the starting criterion's best level
    await userEvent.click(within(panel).getByRole('button', { name: 'Save rubric' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Enter a name for the rubric.')
    await userEvent.type(within(panel).getByLabelText('Rubric name'), 'Lab report')
    await userEvent.click(within(panel).getByRole('button', { name: 'Save rubric' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Every criterion needs a name.')
    await userEvent.type(within(panel).getByLabelText('Criterion 1 name'), 'Method')
    await userEvent.clear(within(panel).getByLabelText('Criterion 1 level 1 points'))
    await userEvent.type(within(panel).getByLabelText('Criterion 1 level 1 points'), '8')
    expect(within(panel).getByText('Total: 8 points')).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'Save rubric' }))
    await waitFor(() => expect(calls('POST', '/rubrics')).toHaveLength(1))
    const body = bodyOf(calls('POST', '/rubrics')[0])
    expect(body.name).toBe('Lab report')
    expect(body.criteria[0]).toMatchObject({ name: 'Method', levels: [{ label: 'Full marks', points: 8 }, { label: 'Partly', points: 2 }, { label: 'Not shown', points: 0 }] })
  })

  it('refuses two levels with the same points', async () => {
    render(<AssessmentRubricsPanel courseId="c1" />)
    await userEvent.click(await screen.findByRole('button', { name: 'New rubric' }))
    const panel = await screen.findByRole('dialog', { name: 'New rubric' })
    await userEvent.type(within(panel).getByLabelText('Rubric name'), 'X')
    await userEvent.type(within(panel).getByLabelText('Criterion 1 name'), 'Y')
    await userEvent.clear(within(panel).getByLabelText('Criterion 1 level 2 points'))
    await userEvent.type(within(panel).getByLabelText('Criterion 1 level 2 points'), '4')
    await userEvent.click(within(panel).getByRole('button', { name: 'Save rubric' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('must have different points')
    expect(calls('POST', '/rubrics')).toHaveLength(0)
  })
})

describe('AssessmentAccommodationsPanel', () => {
  const roster = [{ learnerUserId: 'u1', name: 'Lena', status: 'Active' }, { learnerUserId: 'u2', name: 'Otto', status: 'Active' }, { learnerUserId: 'u3', name: 'Gone', status: 'Withdrawn' }]
  beforeEach(() => {
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (options?.method) return Promise.resolve(null)
      if (path.endsWith('/accommodations')) return Promise.resolve([{ learnerUserId: 'u1', learnerName: 'Lena', extraTimePercent: 50, extraAttempts: 1, note: 'Support plan' }])
      return Promise.resolve(roster)
    })
  })

  it('lists accommodations and offers only enrolled learners without one', async () => {
    render(<AssessmentAccommodationsPanel courseId="c1" />)
    expect(await screen.findByText('+50% time')).toBeInTheDocument()
    expect(screen.getByText('Support plan')).toBeInTheDocument()
    const options = within(screen.getByLabelText('Learner')).getAllByRole('option').map((option) => option.textContent)
    expect(options).toEqual(['Choose an enrolled learner', 'Otto'])
  })

  it('checks the numbers, then saves and removes', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<AssessmentAccommodationsPanel courseId="c1" />)
    await screen.findByText('+50% time')
    await userEvent.click(screen.getByRole('button', { name: 'Save accommodation' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Choose a learner.')
    await userEvent.selectOptions(screen.getByLabelText('Learner'), 'u2')
    await userEvent.clear(screen.getByLabelText('Extra time (%)'))
    await userEvent.type(screen.getByLabelText('Extra time (%)'), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Save accommodation' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('0 to 200 percent')
    await userEvent.clear(screen.getByLabelText('Extra time (%)'))
    await userEvent.type(screen.getByLabelText('Extra time (%)'), '25')
    await userEvent.click(screen.getByRole('button', { name: 'Save accommodation' }))
    await waitFor(() => expect(calls('PUT', '/accommodations/u2')).toHaveLength(1))
    expect(bodyOf(calls('PUT', '/accommodations/u2')[0])).toEqual({ extraTimePercent: 25, extraAttempts: 0, note: null })

    await userEvent.click(screen.getByRole('button', { name: 'Remove accommodation for Lena' }))
    await waitFor(() => expect(calls('DELETE', '/accommodations/u1')).toHaveLength(1))
  })
})

describe('AssessmentsPage for a learner', () => {
  it('shows the learner’s own limits and accommodation, never the questions, and stops when attempts are used up', async () => {
    permissions = ['assessment.read', 'assessment.attempt']
    const assessment = { id: 'as1', courseId: 'c1', title: 'Timed quiz', status: 'Published', timeLimitMinutes: 10, attemptLimit: 1, questionCount: 4 }
    request.mockImplementation((path: string) => {
      if (path === '/api/v1/tenant/courses') return Promise.resolve([{ id: 'c1', code: 'C1', title: 'Course', status: 'Published' }])
      if (path.endsWith('/assessments')) return Promise.resolve([assessment])
      return Promise.resolve({ assessment, questions: [], effectiveTimeLimitMinutes: 15, effectiveAttemptLimit: 2, attemptsUsed: 2, accommodation: { extraTimePercent: 50, extraAttempts: 1 } })
    })
    render(<AssessmentsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Timed quiz' }))
    expect(await screen.findByText(/4 questions · 15 min · 2 attempts/)).toBeInTheDocument()
    expect(screen.getByText(/50% extra time and 1 extra attempt/)).toBeInTheDocument()
    expect(screen.getByText('You have used every attempt.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start attempt' })).toBeDisabled()
    expect(screen.queryByRole('tab', { name: 'Rubrics' })).toBeNull()
  })

  it('gives staff the Rubrics and Accommodations tabs', async () => {
    permissions = ['assessment.read', 'assessment.manage']
    request.mockImplementation((path: string) => {
      if (path === '/api/v1/tenant/courses') return Promise.resolve([{ id: 'c1', code: 'C1', title: 'Course', status: 'Published' }])
      return Promise.resolve([])
    })
    render(<AssessmentsPage />)
    await userEvent.click(await screen.findByRole('tab', { name: 'Rubrics' }))
    expect(await screen.findByText('No rubrics for this course yet.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Accommodations' }))
    expect(await screen.findByText('No learner has an accommodation in this course.')).toBeInTheDocument()
  })
})
