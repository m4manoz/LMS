import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import VideoLibraryPage from './VideoLibraryPage'
import VideoAiSettingsPanel, { validateServiceAddress } from './VideoAiSettingsPanel'
import { InsightsManager, StudyAids, TranscriptManager, TranscriptView } from './VideoAiPanels'
import { clock, currentLine, filterLines, validateQuestions, validateQuizForm, type Insight, type Transcript } from '@/lib/videoAi'
import type { VideoItem } from '@/lib/video'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

const video = (over: Partial<VideoItem> = {}): VideoItem => ({
  id: 'v1', courseId: 'c1', courseTitle: 'Algebra', lessonId: null, title: 'Intro to limits', description: null, type: 'Uploaded', status: 'Ready', statusMessage: null,
  contentType: 'video/mp4', sizeBytes: 1024, durationSeconds: 600, externalUrl: null, createdBy: 'Tara', createdAtUtc: '2026-10-01T10:00:00Z', myProgress: null, ...over,
})
const lines = [
  { index: 0, startSeconds: 0, endSeconds: 5, text: 'A limit describes behaviour near a point.' },
  { index: 1, startSeconds: 5, endSeconds: 10, text: 'Continuity means the limit equals the value.' },
  { index: 2, startSeconds: 3725, endSeconds: 3730, text: 'Derivatives come from limits.' },
]
const ready = (segments = lines): Transcript => ({ status: 'Ready', source: 'Manual', language: 'en', provider: null, statusMessage: null, segments })
const none: Transcript = { status: 'None', source: null, language: null, provider: null, statusMessage: null, segments: null }
const calls = (fragment: string, method?: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || call[1]?.method === method))
const summary = (over: Partial<Insight> = {}): Insight => ({ id: 'i1', kind: 'Summary', content: 'Overview.\n- [0:05] Continuity is explained.', questions: null, published: false, provider: 'Local', model: 'extractive-v1', updatedAtUtc: '2026-10-02T10:00:00Z', ...over })
const quiz = (over: Partial<Insight> = {}): Insight => ({
  id: 'i2', kind: 'Questions', content: null, published: false, provider: 'OpenAiCompatible', model: 'gpt-4o-mini', updatedAtUtc: '2026-10-02T10:00:00Z',
  questions: [{ question: 'What does a limit describe?', options: ['Behaviour near a point', 'The area'], answerIndex: 0, timestampSeconds: 0, explanation: 'Said at the start.' }], ...over,
})

describe('helpers', () => {
  it('shows moments as minutes and seconds, with hours when needed', () => {
    expect(clock(0)).toBe('0:00'); expect(clock(754.9)).toBe('12:34'); expect(clock(3725)).toBe('1:02:05'); expect(clock(-3)).toBe('0:00')
  })
  it('finds the line being said at a moment', () => {
    expect(currentLine(lines, 0)).toBe(0); expect(currentLine(lines, 4.9)).toBe(0); expect(currentLine(lines, 5)).toBe(1); expect(currentLine(lines, 9999)).toBe(2)
    expect(currentLine([{ index: 0, startSeconds: 2, endSeconds: 3, text: 'x' }], 1)).toBe(-1)
  })
  it('filters lines by every word typed, ignoring case', () => {
    expect(filterLines(lines, '').length).toBe(3)
    expect(filterLines(lines, 'LIMIT').map((line) => line.index)).toEqual([0, 1, 2])
    expect(filterLines(lines, 'continuity value').map((line) => line.index)).toEqual([1])
    expect(filterLines(lines, 'zebra')).toEqual([])
  })
  it('checks questions the way the server does', () => {
    const good = { question: 'Why?', options: ['a', 'b'], answerIndex: 1, timestampSeconds: null, explanation: null }
    expect(validateQuestions([good])).toBeNull()
    expect(validateQuestions([])).toMatch(/at least one/)
    expect(validateQuestions([{ ...good, question: ' ' }])).toMatch(/Question 1/)
    expect(validateQuestions([{ ...good, options: ['a'] }])).toMatch(/between 2 and 6/)
    expect(validateQuestions([{ ...good, options: ['a', 'A'] }])).toMatch(/different/)
    expect(validateQuestions([{ ...good, answerIndex: 2 }])).toMatch(/correct/)
  })
  it('accepts a blank or clean https service address only', () => {
    expect(validateServiceAddress('')).toBeNull(); expect(validateServiceAddress('https://api.openai.com/v1')).toBeNull()
    for (const bad of ['http://x.example.org', 'not a link', 'https://u:p@x.example.org', 'https://x.example.org/?a=1']) expect(validateServiceAddress(bad), bad).not.toBeNull()
  })
})

describe('validateQuizForm', () => {
  const ok = { title: 'Quiz', points: '2', attempts: '3', minutes: '' }
  it('accepts whole numbers within the limits, and no time limit', () => {
    expect(validateQuizForm(ok)).toBeNull()
    expect(validateQuizForm({ ...ok, minutes: '45' })).toBeNull()
  })
  it('refuses anything outside them, with a reason', () => {
    expect(validateQuizForm({ ...ok, title: 'x'.repeat(251) })).toMatch(/250/)
    for (const points of ['0', '101', 'two', '1.5', '']) expect(validateQuizForm({ ...ok, points }), points).toMatch(/Points/)
    for (const attempts of ['0', '21', 'x']) expect(validateQuizForm({ ...ok, attempts }), attempts).toMatch(/Attempts/)
    for (const minutes of ['0', '1441', 'x']) expect(validateQuizForm({ ...ok, minutes }), minutes).toMatch(/time limit/)
  })
})

describe('TranscriptView', () => {
  it('lists the lines with times, marks the one being said, and jumps when one is chosen', async () => {
    const seek = vi.fn()
    render(<TranscriptView transcript={ready()} time={6} onSeek={seek} />)
    const list = screen.getByRole('list', { name: 'Transcript' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(3)
    expect(within(list).getAllByRole('listitem')[1]).toHaveAttribute('aria-current', 'true')
    expect(within(list).getByText('1:02:05')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: /Derivatives come from limits/ }))
    expect(seek).toHaveBeenCalledWith(3725)
  })
  it('narrows to the lines with the words typed', async () => {
    render(<TranscriptView transcript={ready()} time={0} onSeek={() => undefined} />)
    await userEvent.type(screen.getByLabelText('Search this transcript'), 'continuity')
    expect(within(screen.getByRole('list', { name: 'Transcript' })).getAllByRole('listitem')).toHaveLength(1)
    await userEvent.clear(screen.getByLabelText('Search this transcript')); await userEvent.type(screen.getByLabelText('Search this transcript'), 'zebra')
    expect(screen.getByText('No line has those words.')).toBeInTheDocument()
  })
})

describe('StudyAids (what learners see)', () => {
  beforeEach(() => request.mockReset())

  it('shows nothing when nothing is published', async () => {
    request.mockResolvedValue([])
    const { container } = render(<StudyAids video={video()} onSeek={() => undefined} />)
    await waitFor(() => expect(calls('/insights')).toHaveLength(1))
    expect(container).toBeEmptyDOMElement()
  })

  it('shows the summary with times that jump, and practice questions that mark the answer and say how many were right', async () => {
    request.mockResolvedValue([summary({ published: true }), quiz({ published: true })])
    const seek = vi.fn()
    render(<StudyAids video={video()} onSeek={seek} />)
    const overview = await screen.findByRole('region', { name: 'Summary' })
    await userEvent.click(within(overview).getByRole('button', { name: 'Go to 0:05' }))
    expect(seek).toHaveBeenCalledWith(5)

    const practice = screen.getByRole('region', { name: 'Practice questions' })
    await userEvent.click(within(practice).getByLabelText('The area'))
    expect(within(practice).getByRole('status')).toHaveTextContent(/Not quite — the answer is “Behaviour near a point”/)
    expect(within(practice).getByText('0 of 1 correct')).toBeInTheDocument()
    expect(within(practice).getByLabelText('Behaviour near a point')).toBeDisabled()                 // one try per question
    await userEvent.click(within(practice).getByRole('button', { name: /Watch this part \(0:00\)/ }))
    expect(seek).toHaveBeenCalledWith(0)
  })

  it('counts a right answer as correct', async () => {
    request.mockResolvedValue([quiz({ published: true })])
    render(<StudyAids video={video()} onSeek={() => undefined} />)
    await userEvent.click(await screen.findByLabelText('Behaviour near a point'))
    expect(screen.getByText('1 of 1 correct')).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent(/^Correct\./)
  })
})

describe('TranscriptManager', () => {
  beforeEach(() => { request.mockReset(); vi.spyOn(window, 'confirm').mockReturnValue(true) })
  const setup = (transcript: Transcript, item = video()) => {
    const change = vi.fn()
    render(<TranscriptManager video={item} transcript={transcript} onChange={change} />)
    return change
  }

  it('says when there is no transcript, and saves a pasted one with its language', async () => {
    request.mockResolvedValue(ready())
    const change = setup(none)
    expect(screen.getByText('No transcript yet.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Save transcript' }))
    expect(await screen.findByText(/Paste the transcript/)).toBeInTheDocument()
    expect(calls('/transcript', 'POST')).toHaveLength(0)
    await userEvent.click(screen.getByLabelText(/Transcript/, { selector: 'textarea' }))
    await userEvent.paste('WEBVTT\n\n00:00:00.000 --> 00:00:04.000\nHello.')
    await userEvent.type(screen.getByLabelText('Language'), 'en')
    await userEvent.click(screen.getByRole('button', { name: 'Save transcript' }))
    await waitFor(() => expect(calls('/v1/transcript', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/v1/transcript', 'POST')[0][1].body)).toEqual({ text: 'WEBVTT\n\n00:00:00.000 --> 00:00:04.000\nHello.', language: 'en' })
    await waitFor(() => expect(change).toHaveBeenCalled())
    expect(await screen.findByText('Transcript saved.')).toBeInTheDocument()
  })

  it('shows the server message when the transcript cannot be read', async () => {
    const { ApiError } = await import('@/lib/api')
    request.mockImplementation(() => Promise.resolve().then(() => { throw new ApiError('No timed lines were found.', 400) }))
    setup(none)
    await userEvent.click(screen.getByLabelText(/Transcript/, { selector: 'textarea' }))
    await userEvent.paste('words')
    await userEvent.click(screen.getByRole('button', { name: 'Save transcript' }))
    expect(await screen.findByText('No timed lines were found.')).toBeInTheDocument()
  })

  it('asks for an automatic transcript for uploaded videos only, and shows a failure with its reason', async () => {
    request.mockResolvedValue({ ...none, status: 'Queued' })
    const change = setup({ ...none, status: 'Failed', statusMessage: 'No speech-to-text service is set up.' })
    expect(screen.getByRole('alert')).toHaveTextContent('No speech-to-text service is set up.')
    await userEvent.click(screen.getByRole('button', { name: 'Make the transcript again' }))
    await waitFor(() => expect(calls('/transcript/generate', 'POST')).toHaveLength(1))
    await waitFor(() => expect(change).toHaveBeenCalledWith(expect.objectContaining({ status: 'Queued' })))
  })

  it('does not offer automatic transcripts for a linked video', () => {
    setup(none, video({ type: 'External' }))
    expect(screen.queryByRole('button', { name: /Make the transcript/ })).toBeNull()
  })

  it('shows a transcript being made and checks back for it', async () => {
    request.mockResolvedValue(ready())
    const change = setup({ ...none, status: 'Processing' })
    expect(screen.getByRole('status')).toHaveTextContent(/being made/)
    expect(screen.queryByRole('button', { name: 'Delete transcript' })).toBeNull()
    await waitFor(() => expect(change).toHaveBeenCalledWith(expect.objectContaining({ status: 'Ready' })), { timeout: 6000 })
  })

  it('deletes the transcript after confirming', async () => {
    request.mockResolvedValue(null)
    const change = setup(ready())
    expect(screen.getByTestId('transcript-state')).toHaveTextContent('3 lines · added by hand · en')
    await userEvent.click(screen.getByRole('button', { name: 'Delete transcript' }))
    await waitFor(() => expect(calls('/transcript', 'DELETE')).toHaveLength(1))
    await waitFor(() => expect(change).toHaveBeenCalledWith(expect.objectContaining({ status: 'None' })))
  })
})

const topLevel = () => Array.from(screen.getByRole('list', { name: 'Summaries and questions' }).children) as HTMLElement[]   // not the questions inside a set

describe('InsightsManager', () => {
  beforeEach(() => { request.mockReset(); vi.spyOn(window, 'confirm').mockReturnValue(true) })

  it('needs a transcript before anything can be written', async () => {
    request.mockResolvedValue([])
    render(<InsightsManager video={video()} hasTranscript={false} />)
    expect(await screen.findByText('Nothing written yet.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Write a summary' })).toBeDisabled()
    expect(screen.getByText(/Add a transcript first/)).toBeInTheDocument()
  })

  it('writes a draft, which is published, edited and deleted', async () => {
    request.mockImplementation((path: string, options?: { method?: string; body?: string }) => {
      if (path.endsWith('/insights') && !options?.method) return Promise.resolve([])
      if (path.endsWith('/insights') && options?.method === 'POST') return Promise.resolve(JSON.parse(options.body!).kind === 'Summary' ? summary() : quiz())
      if (path.endsWith('/publish')) return Promise.resolve(summary({ published: JSON.parse(options!.body!).published }))
      if (options?.method === 'PUT') return Promise.resolve(summary({ content: JSON.parse(options.body!).content }))
      return Promise.resolve(null)
    })
    render(<InsightsManager video={video()} hasTranscript />)
    await screen.findByText('Nothing written yet.')
    await userEvent.click(screen.getByRole('button', { name: 'Write a summary' }))
    expect(await screen.findByText(/written as a draft/)).toBeInTheDocument()
    const list = screen.getByRole('list', { name: 'Summaries and questions' })
    expect(within(list).getByText('Draft')).toBeInTheDocument()
    expect(within(list).getByText('picked from the transcript')).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Questions'), '8')
    await userEvent.click(screen.getByRole('button', { name: 'Write practice questions' }))
    await waitFor(() => expect(topLevel()).toHaveLength(2))
    expect(JSON.parse(calls('/insights', 'POST')[1][1].body)).toEqual({ kind: 'Questions', count: 8 })
    expect(screen.getByText(/written by gpt-4o-mini/)).toBeInTheDocument()

    const first = topLevel()[0]
    await userEvent.click(within(first).getByRole('button', { name: 'Publish to learners' }))
    expect(await within(first).findByText('Published')).toBeInTheDocument()
    expect(JSON.parse(calls('/i1/publish', 'POST')[0][1].body)).toEqual({ published: true })

    await userEvent.click(within(first).getByRole('button', { name: 'Edit' }))
    const box = within(first).getByLabelText(/Summary/)
    await userEvent.clear(box); await userEvent.type(box, 'My own words.')
    await userEvent.click(within(first).getByRole('button', { name: 'Save changes' }))
    expect(await within(first).findByText('My own words.')).toBeInTheDocument()

    await userEvent.click(within(first).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(calls('/i1', 'DELETE')).toHaveLength(1))
    await waitFor(() => expect(topLevel()).toHaveLength(1))
  })

  it('shows the server message when nothing can be written', async () => {
    const { ApiError } = await import('@/lib/api')
    request.mockImplementation((path: string, options?: { method?: string }) => options?.method === 'POST'
      ? Promise.resolve().then(() => { throw new ApiError('The transcript is too short to write questions from.', 422) })
      : Promise.resolve([]))
    render(<InsightsManager video={video()} hasTranscript />)
    await userEvent.click(await screen.findByRole('button', { name: 'Write practice questions' }))
    expect(await screen.findByText('The transcript is too short to write questions from.')).toBeInTheDocument()
  })

  it('checks edited questions before sending them', async () => {
    request.mockImplementation((_path: string, options?: { method?: string }) => Promise.resolve(options?.method ? quiz() : [quiz()]))
    render(<InsightsManager video={video()} hasTranscript />)
    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }))
    const answers = screen.getByLabelText(/Answers/)
    await userEvent.clear(answers); await userEvent.type(answers, 'only one')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByText(/between 2 and 6 answers/)).toBeInTheDocument()
    expect(calls('/i2', 'PUT')).toHaveLength(0)
  })
})

describe('making a graded quiz', () => {
  beforeEach(() => { request.mockReset() })
  const created = { assessmentId: 'a1', title: 'Limits check', status: 'Draft', questions: 1, totalPoints: 3, courseId: 'c1' }

  function serve(items: Insight[]) {
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path.endsWith('/quiz') && options?.method === 'POST') return Promise.resolve(created)
      if (path.endsWith('/insights') && !options?.method) return Promise.resolve(items)
      return Promise.resolve(null)
    })
  }

  it('is offered only for published practice questions that have no quiz yet', async () => {
    serve([quiz({ published: false }), quiz({ id: 'i3', published: true, quizAssessmentId: 'a0' }), summary({ id: 'i4', published: true })])
    render(<InsightsManager video={video()} hasTranscript />)
    await screen.findByRole('list', { name: 'Summaries and questions' })
    expect(screen.queryByRole('button', { name: 'Make a graded quiz' })).toBeNull()
    expect(screen.getByText(/A graded quiz was made from these questions/)).toBeInTheDocument()
  })

  it('sends the choices, then says where the quiz is and what state it is in', async () => {
    serve([quiz({ published: true })])
    render(<InsightsManager video={video()} hasTranscript />)
    await userEvent.click(await screen.findByRole('button', { name: 'Make a graded quiz' }))
    const title = screen.getByLabelText('Title')
    await userEvent.clear(title); await userEvent.type(title, 'Limits check')
    const points = screen.getByLabelText(/Points per question/)
    await userEvent.clear(points); await userEvent.type(points, '3')
    await userEvent.type(screen.getByLabelText(/Time limit/), '15')
    await userEvent.click(screen.getByRole('button', { name: 'Make the quiz' }))
    await waitFor(() => expect(calls('/i2/quiz', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/i2/quiz', 'POST')[0][1].body)).toEqual({ title: 'Limits check', pointsPerQuestion: 3, attemptLimit: 1, timeLimitMinutes: 15, publish: false })
    expect(await screen.findByText(/Quiz “Limits check” made with 1 questions \(3 points\)\. It is a draft: publish it under Assessments/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Make a graded quiz' })).toBeNull()      // one quiz per set
  })

  it('checks the numbers before sending and shows the server message when it fails', async () => {
    const { ApiError } = await import('@/lib/api')
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/quiz')
      ? Promise.resolve().then(() => { throw new ApiError('The course must be published before its quiz can be.', 409) })
      : Promise.resolve(options?.method ? null : [quiz({ published: true })]))
    render(<InsightsManager video={video()} hasTranscript />)
    await userEvent.click(await screen.findByRole('button', { name: 'Make a graded quiz' }))
    await userEvent.clear(screen.getByLabelText(/Attempts allowed/)); await userEvent.type(screen.getByLabelText(/Attempts allowed/), '0')
    await userEvent.click(screen.getByRole('button', { name: 'Make the quiz' }))
    expect(await screen.findByText(/Attempts must be/)).toBeInTheDocument()
    expect(calls('/quiz', 'POST')).toHaveLength(0)
    await userEvent.clear(screen.getByLabelText(/Attempts allowed/)); await userEvent.type(screen.getByLabelText(/Attempts allowed/), '1')
    await userEvent.click(screen.getByRole('checkbox'))
    await userEvent.click(screen.getByRole('button', { name: 'Make the quiz' }))
    expect(await screen.findByText('The course must be published before its quiz can be.')).toBeInTheDocument()
    expect(JSON.parse(calls('/quiz', 'POST')[0][1].body).publish).toBe(true)
  })
})

describe('VideoAiSettingsPanel', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((_path: string, options?: { method?: string; body?: string }) => Promise.resolve(options?.method === 'PUT'
      ? { provider: 'OpenAiCompatible', baseUrl: 'https://api.openai.com/v1', apiKeySet: true, transcriptionModel: 'whisper-1', chatModel: 'gpt-4o-mini', autoTranscribe: true, conversionAvailable: false }
      : { provider: 'Local', baseUrl: 'https://api.openai.com/v1', apiKeySet: false, transcriptionModel: 'whisper-1', chatModel: 'gpt-4o-mini', autoTranscribe: false, conversionAvailable: false }))
  })

  it('offers the two choices and asks for service details only for the AI service', async () => {
    render(<VideoAiSettingsPanel />)
    const group = await screen.findByRole('radiogroup', { name: 'Video AI provider' })
    expect(within(group).getAllByRole('radio')).toHaveLength(2)
    expect(within(group).getByRole('radio', { name: /Built in/ })).toBeChecked()
    expect(screen.queryByLabelText(/API key/)).toBeNull()
    await userEvent.click(within(group).getByRole('radio', { name: /An AI service/ }))
    expect(screen.getByLabelText(/API key/)).toBeInTheDocument()
    expect(screen.getByText(/FFmpeg\) is not set up/)).toBeInTheDocument()
  })

  it('needs a key the first time, sends the details, and never shows a saved key', async () => {
    render(<VideoAiSettingsPanel />)
    await userEvent.click(await screen.findByRole('radio', { name: /An AI service/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByText('Enter the API key.')).toBeInTheDocument()
    expect(calls('/video-ai', 'PUT')).toHaveLength(0)
    await userEvent.type(screen.getByLabelText(/API key/), 'sk-secret-value')
    await userEvent.click(screen.getByRole('checkbox'))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(calls('/video-ai', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('/video-ai', 'PUT')[0][1].body)).toMatchObject({ provider: 'OpenAiCompatible', apiKey: 'sk-secret-value', autoTranscribe: true, transcriptionModel: 'whisper-1' })
    expect(await screen.findByText(/A key is saved/)).toBeInTheDocument()
    expect((screen.getByLabelText(/API key/) as HTMLInputElement).value).toBe('')
  })

  it('does not send an address that is not valid', async () => {
    render(<VideoAiSettingsPanel />)
    await userEvent.click(await screen.findByRole('radio', { name: /An AI service/ }))
    await userEvent.clear(screen.getByLabelText(/Service address/)); await userEvent.type(screen.getByLabelText(/Service address/), 'http://insecure.example.org')
    await userEvent.type(screen.getByLabelText(/API key/), 'k')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByText(/Use an https address/)).toBeInTheDocument()
    expect(calls('/video-ai', 'PUT')).toHaveLength(0)
  })
})

describe('the video library with transcripts', () => {
  beforeEach(() => { request.mockReset(); vi.useRealTimers() })

  function serve(opts: { transcript?: Transcript; hits?: unknown[]; insights?: Insight[] }) {
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path === '/api/v1/tenant/videos' && !options?.method) return Promise.resolve([video()])
      if (path === '/api/v1/tenant/videos/usage') return Promise.resolve({ count: 1, totalBytes: 1 })
      if (path === '/api/v1/tenant/courses') return Promise.resolve([])
      if (path.includes('/videos/search')) return Promise.resolve(opts.hits ?? [])
      if (path.endsWith('/transcript')) return Promise.resolve(opts.transcript ?? none)
      if (path.endsWith('/insights')) return Promise.resolve(opts.insights ?? [])
      if (path.endsWith('/link')) return Promise.resolve({ kind: 'stream', url: '/stream?token=t', expiresAtUtc: null, embeddable: false })
      return Promise.resolve(null)
    })
  }

  it('searches inside what is said and opens the video at that moment', async () => {
    permissions = ['course.read']
    serve({ hits: [{ videoId: 'v1', videoTitle: 'Intro to limits', courseTitle: 'Algebra', startSeconds: 28, text: 'the limit of a difference quotient' }], transcript: ready() })
    render(<VideoLibraryPage />)
    await userEvent.type(await screen.findByLabelText('Search videos'), 'quotient')
    const found = await screen.findByRole('region', { name: 'Found in what is said' }, { timeout: 3000 })
    expect(within(found).getByText('the limit of a difference quotient')).toBeInTheDocument()
    expect(calls('/videos/search?q=quotient')).toHaveLength(1)
    await userEvent.click(within(found).getByRole('button', { name: 'Watch Intro to limits from 0:28' }))
    const panel = await screen.findByRole('dialog', { name: 'Intro to limits' })
    expect(await within(panel).findByRole('list', { name: 'Transcript' })).toBeInTheDocument()   // the transcript sits beside the video
  })

  it('shows learners the transcript and what staff published, without the staff tabs', async () => {
    permissions = ['course.read']
    serve({ transcript: ready(), insights: [summary({ published: true })] })
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Intro to limits' }).catch(() => screen.findByRole('button', { name: 'Watch' })))
    const panel = await screen.findByRole('dialog', { name: 'Intro to limits' })
    expect(await within(panel).findByRole('list', { name: 'Transcript' })).toBeInTheDocument()
    expect(await within(panel).findByRole('region', { name: 'Summary' })).toBeInTheDocument()
    expect(within(panel).queryByRole('tab', { name: 'Transcript' })).toBeNull()
  })

  it('gives staff Transcript and Study aids tabs', async () => {
    permissions = ['course.read', 'course.manage']
    serve({ transcript: none })
    render(<VideoLibraryPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Intro to limits' }))
    const panel = await screen.findByRole('dialog', { name: 'Intro to limits' })
    await userEvent.click(within(panel).getByRole('tab', { name: 'Transcript' }))
    expect(await within(panel).findByText('No transcript yet.')).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('tab', { name: 'Study aids' }))
    expect(await within(panel).findByText(/Add a transcript first/)).toBeInTheDocument()
  })
})
