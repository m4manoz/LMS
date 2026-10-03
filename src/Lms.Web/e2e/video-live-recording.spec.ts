import fs from 'node:fs'
import { expect, test } from '@playwright/test'
import { CONVERTS_VIDEOS, LEARNER, TEACHER, adminApi, apiAs, localInput, openMenu, provision, signIn } from './helpers'

// Needs LiveKit with its recording service: scripts/livekit-dev.ps1, then E2E_EGRESS=1 for the test run
// (so the API is told to record). Runs after smoke.spec.ts, which creates the published course and enrols the learner.
test.beforeAll(async ({ request }) => { await provision(request) })

test('a class held in LiveKit is recorded and the recording arrives in the video library, converted for streaming', async ({ browser, request }) => {
  test.skip(process.env.E2E_EGRESS !== '1', 'set E2E_EGRESS=1 and start scripts/livekit-dev.ps1')
  test.skip(!(await request.get('http://localhost:7880').then((response) => response.ok(), () => false)), 'no LiveKit server on localhost:7880')
  test.setTimeout(300_000)

  const admin = await adminApi(request)
  const course = (await admin.get('/api/v1/tenant/courses')).find((item: { code: string }) => item.code === 'E2E-101')
  test.skip(!course, 'run smoke.spec.ts first: it creates the course')
  const saved = await admin.put('/api/v1/tenant/integrations/live-classes', { provider: 'LiveKit', liveKitUrl: 'ws://localhost:7880', liveKitApiKey: 'devkey', liveKitApiSecret: 'secret' })
  expect(saved.ok()).toBeTruthy()

  const context = await browser.newContext({ baseURL: 'http://localhost:5273', permissions: ['camera', 'microphone'] })
  const teacher = await context.newPage()
  await signIn(teacher, TEACHER)
  await openMenu(teacher, 'Schedule class')
  const form = teacher.getByRole('dialog', { name: 'Schedule a class' })
  await form.getByLabel('Title').fill('Recorded class')
  await form.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  await form.getByLabel('Starts').fill(localInput(-5))
  await form.getByLabel('Ends').fill(localInput(55))
  await form.getByRole('button', { name: 'Schedule class' }).click()
  await expect(teacher.getByRole('dialog')).toHaveCount(0)
  await teacher.getByRole('button', { name: 'View details for Recorded class' }).click()
  const details = teacher.getByRole('dialog', { name: 'Recorded class' })

  // Recording cannot start before the host agrees, or before anyone is in the room.
  await details.getByRole('tab', { name: 'Recording' }).click()
  await details.getByRole('button', { name: 'Start recording' }).click()
  await expect(teacher.getByText(/must grant recording consent/)).toBeVisible()
  await details.getByRole('button', { name: 'Give recording consent' }).click()
  await expect(details.getByRole('button', { name: 'Withdraw my consent' })).toBeVisible()
  await details.getByRole('button', { name: 'Start recording' }).click()
  await expect(teacher.getByText(/Nobody is in the class room yet/)).toBeVisible()

  // In the room (a fake camera and microphone), recording starts and everyone is told.
  await details.getByRole('tab', { name: 'Overview' }).click()
  await details.getByRole('button', { name: 'Join class' }).click()
  const room = details.getByRole('region', { name: 'Live class room' })
  await expect(room.getByTestId('room-count')).toHaveText('1 in the class')
  await details.getByRole('tab', { name: 'Recording' }).click()
  await details.getByRole('button', { name: 'Start recording' }).click()
  await expect(details.getByText(/Recording now\./)).toBeVisible({ timeout: 60_000 })
  await expect(room.getByRole('status', { name: 'Recording' })).toBeVisible({ timeout: 30_000 })   // the room shows it too
  await teacher.waitForTimeout(10_000)                                                          // some real footage

  // Stopping saves the file: it comes into the library and is converted for streaming like an upload.
  await details.getByRole('button', { name: 'Stop recording' }).click()
  await expect(details.getByText(/being saved to the video library/)).toBeVisible()
  await expect(details.getByText(/Saved to the video library as a class recording/)).toBeVisible({ timeout: 120_000 })
  expect(fs.readdirSync(`${process.cwd()}/.e2e/egress`).filter((name) => name.endsWith('.mp4'))).toEqual([])   // the shared folder is cleaned up

  const teacherApi = await apiAs(request, TEACHER)
  const video = (await teacherApi.get('/api/v1/tenant/videos')).find((item: { title: string }) => item.title.startsWith('Recorded class (recording '))
  expect(video.type).toBe('LiveRecording')
  expect(video.courseTitle).toBe('Smoke Test Course')
  expect(video.sizeBytes).toBeGreaterThan(10_000)
  expect(video.durationSeconds).toBeGreaterThanOrEqual(5)
  if (CONVERTS_VIDEOS) {
    await expect.poll(async () => (await teacherApi.get(`/api/v1/tenant/videos/${video.id}`)).status, { timeout: 180_000, intervals: [2000] }).toBe('Ready')
    const converted = await teacherApi.get(`/api/v1/tenant/videos/${video.id}`)
    expect(converted.hasStreaming).toBe(true)
    expect(converted.posterUrl).toContain('/poster?token=')

    // An enrolled learner is sent to the streaming version of the recording.
    const lena = await apiAs(request, LEARNER)
    const link = await lena.get(`/api/v1/tenant/videos/${video.id}/link`)
    expect(link.kind).toBe('hls')
    const master = await request.get(`http://localhost:5299${link.url}`)
    expect(master.status()).toBe(200)
    expect(await master.text()).toContain('#EXT-X-STREAM-INF')
  }

  // Close the class and tidy up so the other tests see what they expect.
  await details.getByRole('tab', { name: 'Overview' }).click()
  await details.getByRole('button', { name: 'Leave class' }).click()
  await context.close()
  expect((await teacherApi.delete(`/api/v1/tenant/videos/${video.id}`)).status()).toBe(204)
  expect((await admin.put('/api/v1/tenant/integrations/live-classes', { provider: 'Local' })).ok()).toBeTruthy()
})
