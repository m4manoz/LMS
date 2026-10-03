import fs from 'node:fs'
import http from 'node:http'
import { expect, test } from '@playwright/test'
import { ADMIN, CONVERTS_VIDEOS, LEARNER, SAMPLE_VIDEO, TEACHER, adminApi, apiAs, openMenu, provision, signIn, uploadVideo } from './helpers'

// Real playback (to check that jumping to a moment works) needs Chrome: Playwright's own Chromium has no H.264 decoder.
const CHROME = ['C:/Program Files/Google/Chrome/Application/chrome.exe', '/usr/bin/google-chrome', '/Applications/Google Chrome.app'].some((path) => fs.existsSync(path))
test.use({ channel: 'chrome' })

const AI_PORT = 5399
const requests: { path: string; authorization: string | undefined; body: string }[] = []

// A pretend OpenAI-style service the API talks to, so nothing real is contacted.
const ai = http.createServer((request, response) => {
  const chunks: Buffer[] = []
  request.on('data', (chunk: Buffer) => chunks.push(chunk))
  request.on('end', () => {
    const body = Buffer.concat(chunks).toString('latin1')
    requests.push({ path: request.url ?? '', authorization: request.headers.authorization, body })
    const reply = (status: number, value: unknown) => { response.writeHead(status, { 'content-type': 'application/json' }); response.end(JSON.stringify(value)) }
    if (request.headers.authorization !== 'Bearer sk-e2e') return reply(401, { error: { message: 'Incorrect API key provided.' } })
    if (request.url?.endsWith('/audio/transcriptions')) {
      return reply(200, { language: 'english', duration: 8, segments: [
        { start: 0, end: 4, text: ' Limits describe how a function behaves near a point.' },
        { start: 4, end: 8, text: ' Continuity means the limit equals the value.' },
      ] })
    }
    const wantsQuestions = body.includes('json_object')
    reply(200, { choices: [{ message: { role: 'assistant', content: wantsQuestions
      ? JSON.stringify({ questions: [{ question: 'What does continuity mean?', options: ['The limit equals the value', 'The slope is zero', 'The function is linear'], answerIndex: 0, timestampSeconds: 4, explanation: 'Said at 0:04.' }] })
      : 'Limits and continuity in one paragraph.\n- [0:04] Continuity means the limit equals the value.' } }] })
  })
})

test.beforeAll(async ({ request }) => {
  await provision(request)
  await new Promise<void>((resolve) => ai.listen(AI_PORT, resolve))
})
test.afterAll(async () => { await new Promise((resolve) => ai.close(resolve)) })

// Runs after smoke.spec.ts, which creates the published course and enrols the learner.
test('a video is transcribed, searched by what is said, summarised, and a learner jumps to a spoken moment', async ({ page, browser, request }) => {
  test.skip(!CONVERTS_VIDEOS, 'conversion is switched off (E2E_NO_FFMPEG=1)')
  test.skip(!CHROME, 'needs Google Chrome installed')
  test.setTimeout(240_000)

  const admin = await adminApi(request)
  const course = (await admin.get('/api/v1/tenant/courses')).find((item: { code: string }) => item.code === 'E2E-101')
  test.skip(!course, 'run smoke.spec.ts first: it creates the course')

  // The administrator chooses the service. The form refuses an insecure address; the local test service is set through the API.
  await signIn(page, ADMIN)
  await openMenu(page, 'Integrations')
  await page.getByRole('tab', { name: 'Video AI' }).click()
  await expect(page.getByRole('radio', { name: /Built in/ })).toBeChecked()
  await page.getByRole('radio', { name: /An AI service/ }).check()
  await page.getByLabel(/Service address/).fill('http://insecure.example.org/v1')
  await page.getByLabel(/API key/).fill('sk-e2e')
  await page.getByRole('button', { name: 'Save settings' }).click()
  await expect(page.getByText(/Use an https address/)).toBeVisible()
  const saved = await admin.put('/api/v1/tenant/integrations/video-ai', { provider: 'OpenAiCompatible', baseUrl: `http://localhost:${AI_PORT}/v1`, apiKey: 'sk-e2e' })
  expect(saved.ok()).toBeTruthy()
  expect(await saved.text()).not.toContain('sk-e2e')

  // A teacher uploads a video; once converted she asks for a transcript.
  const teacherApi = await apiAs(request, TEACHER)
  const uploaded = await uploadVideo(request, TEACHER, course.id, 'Spoken lecture', fs.readFileSync(SAMPLE_VIDEO))
  await expect.poll(async () => (await teacherApi.get(`/api/v1/tenant/videos/${uploaded.id}`)).status, { timeout: 120_000, intervals: [1000] }).toBe('Ready')

  const teacherContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const teacher = await teacherContext.newPage()
  await signIn(teacher, TEACHER)
  await openMenu(teacher, 'Video library')
  await teacher.getByRole('button', { name: 'View details for Spoken lecture' }).click()
  const panel = teacher.getByRole('dialog', { name: 'Spoken lecture' })
  await panel.getByRole('tab', { name: 'Transcript' }).click()
  await expect(panel.getByText('No transcript yet.')).toBeVisible()
  await panel.getByRole('button', { name: 'Make the transcript' }).click()
  await expect(panel.getByTestId('transcript-state')).toContainText('2 lines · made from the sound', { timeout: 60_000 })   // the page checks back by itself
  const transcription = requests.find((item) => item.path.endsWith('/audio/transcriptions'))!
  expect(transcription.authorization).toBe('Bearer sk-e2e')
  expect(transcription.body).toContain('whisper-1')
  expect(transcription.body).toContain('ID3')                      // a real mp3 made by FFmpeg from the video's sound

  // Summary and practice questions are written as drafts, checked and published.
  await panel.getByRole('tab', { name: 'Study aids' }).click()
  await panel.getByRole('button', { name: 'Write a summary' }).click()
  await expect(panel.getByText(/written as a draft/)).toBeVisible()
  await panel.getByRole('button', { name: 'Write practice questions' }).click()
  await expect(panel.getByRole('list', { name: 'Summaries and questions' }).locator('> li')).toHaveCount(2)
  const learnerApi = await apiAs(request, LEARNER)
  expect(await learnerApi.get(`/api/v1/tenant/videos/${uploaded.id}/insights`)).toEqual([])      // drafts are not visible to learners
  await panel.getByRole('button', { name: 'Publish to learners' }).first().click()
  await expect(panel.getByText('Published')).toHaveCount(1)
  await panel.getByRole('button', { name: 'Publish to learners' }).first().click()
  await expect(panel.getByText('Published')).toHaveCount(2)

  // The published questions become a graded quiz in the course's assessments.
  await panel.getByRole('button', { name: 'Make a graded quiz' }).click()
  await panel.getByLabel(/Points per question/).fill('2')
  await panel.getByRole('checkbox', { name: /Publish it now/ }).check()
  await panel.getByRole('button', { name: 'Make the quiz' }).click()
  await expect(panel.getByText(/Quiz “Quiz: Spoken lecture” made with 1 questions \(2 points\)\. It is published/)).toBeVisible()
  await expect(panel.getByRole('button', { name: 'Make a graded quiz' })).toHaveCount(0)
  const quizzes = await learnerApi.get(`/api/v1/tenant/courses/${course.id}/assessments`)
  const quiz = quizzes.find((item: { title: string }) => item.title === 'Quiz: Spoken lecture')
  expect(quiz.status).toBe('Published')
  expect(quiz.questionCount).toBe(1)
  const attempt = await (await learnerApi.post(`/api/v1/tenant/assessments/${quiz.id}/attempts`, {})).json()
  expect(attempt.attempt.possiblePoints).toBe(2)
  expect(attempt.questions[0].correctAnswers).toEqual([])                  // the answer is not given away
  await learnerApi.post(`/api/v1/tenant/assessment-attempts/${attempt.attempt.id}/submit`, {})
  expect(requests.some((item) => item.path.endsWith('/chat/completions') && item.body.includes('untrusted'))).toBe(true)

  // Lena searches for a word and is taken to the moment it is said.
  const lenaContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const lena = await lenaContext.newPage()
  await signIn(lena, LEARNER)
  await openMenu(lena, 'Video library')
  await lena.getByLabel('Search videos').fill('continuity')
  const found = lena.getByRole('region', { name: 'Found in what is said' })
  await expect(found.getByText('Continuity means the limit equals the value.')).toBeVisible()
  await found.getByRole('button', { name: 'Watch Spoken lecture from 0:04' }).click()
  const watching = lena.getByRole('dialog', { name: 'Spoken lecture' })
  await expect.poll(() => watching.locator('video').evaluate((element: HTMLVideoElement) => element.currentTime), { timeout: 30_000 }).toBeGreaterThanOrEqual(3.5)

  // The transcript is also offered as captions in the player.
  const captions = watching.locator('video track')
  await expect(captions).toHaveAttribute('kind', 'captions')
  const vtt = await lenaContext.request.get((await captions.getAttribute('src'))!)
  expect(vtt.status()).toBe(200)
  const vttText = await vtt.text()
  expect(vttText).toContain('WEBVTT')
  expect(vttText).toContain('00:00:04.000 --> 00:00:08.000')
  expect(vttText).toContain('Continuity means the limit equals the value.')

  // She reads along, jumps with a line, and tries the practice question.
  const lines = watching.getByRole('list', { name: 'Transcript' })
  await expect(lines.getByRole('listitem')).toHaveCount(2)
  await lines.getByRole('button', { name: /Limits describe/ }).click()
  await expect.poll(() => watching.locator('video').evaluate((element: HTMLVideoElement) => element.currentTime), { timeout: 15_000 }).toBeLessThan(3.5)
  await expect(watching.getByRole('region', { name: 'Summary' })).toContainText('Limits and continuity in one paragraph.')
  const practice = watching.getByRole('region', { name: 'Practice questions' })
  await practice.getByLabel('The slope is zero').check()
  await expect(practice.getByRole('status')).toContainText('Not quite')
  await expect(practice.getByText('0 of 1 correct')).toBeVisible()
  // Staff-only tabs are not offered to her.
  await expect(watching.getByRole('tab')).toHaveCount(0)

  // A learner cannot make transcripts or change the service.
  expect((await learnerApi.post(`/api/v1/tenant/videos/${uploaded.id}/transcript/generate`, {})).status()).toBe(403)

  // Clean up so the library and settings are as smoke.spec.ts left them.
  await lenaContext.close()
  await teacherContext.close()
  expect((await teacherApi.delete(`/api/v1/tenant/videos/${uploaded.id}`)).status()).toBe(204)
  expect((await admin.put('/api/v1/tenant/integrations/video-ai', { provider: 'Local' })).ok()).toBeTruthy()
})
