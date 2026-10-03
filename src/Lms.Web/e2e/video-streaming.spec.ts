import fs from 'node:fs'
import { expect, test } from '@playwright/test'
import { CONVERTS_VIDEOS, LEARNER, SAMPLE_VIDEO, TEACHER, adminApi, apiAs, openMenu, provision, signIn, uploadVideo } from './helpers'

// Playwright's own Chromium has no H.264 decoder, so real playback needs an installed Chrome.
const CHROME = ['C:/Program Files/Google/Chrome/Application/chrome.exe', '/usr/bin/google-chrome', '/Applications/Google Chrome.app'].some((path) => fs.existsSync(path))
test.use({ channel: 'chrome' })

// Runs after smoke.spec.ts, which creates the published course and enrols the learner.
test.beforeAll(async ({ request }) => { await provision(request) })

test('a converted video streams to a learner in a real browser, piece by piece, with its poster', async ({ page, request }) => {
  test.skip(!CONVERTS_VIDEOS, 'conversion is switched off (E2E_NO_FFMPEG=1)')
  test.skip(!CHROME, 'needs Google Chrome installed')

  const admin = await adminApi(request)
  const courses = await admin.get('/api/v1/tenant/courses')
  const course = courses.find((item: { code: string }) => item.code === 'E2E-101')
  test.skip(!course, 'run smoke.spec.ts first: it creates the course')

  const teacher = await apiAs(request, TEACHER)
  const uploaded = await uploadVideo(request, TEACHER, course.id, 'Streamed lecture', fs.readFileSync(SAMPLE_VIDEO))
  expect(uploaded.status).toBe('Processing')
  await expect.poll(async () => (await teacher.get(`/api/v1/tenant/videos/${uploaded.id}`)).status, { timeout: 120_000, intervals: [1000] }).toBe('Ready')
  const converted = await teacher.get(`/api/v1/tenant/videos/${uploaded.id}`)
  expect(converted.hasStreaming).toBe(true)
  expect(converted.durationSeconds).toBe(8)                  // read from the file by FFmpeg
  expect(converted.posterUrl).toContain('/poster?token=')

  const requested: string[] = []
  page.on('response', (response) => { if (response.url().includes(`/videos/${uploaded.id}/hls/`)) requested.push(`${response.status()} ${new URL(response.url()).pathname.split('/').pop()}`) })
  await signIn(page, LEARNER)
  await openMenu(page, 'Video library')
  const row = page.getByRole('listitem').filter({ hasText: 'Streamed lecture' })
  await expect(row.locator('img')).toBeVisible()
  await row.getByRole('button', { name: 'View details for Streamed lecture' }).click()
  const panel = page.getByRole('dialog', { name: 'Streamed lecture' })
  const player = panel.locator('video')
  await expect(player).toBeVisible()
  await player.evaluate(async (element: HTMLVideoElement) => { element.muted = true; await element.play() })

  // Real playback: time moves forward and the picture has a size, fed by the playlist and its pieces.
  await expect.poll(() => player.evaluate((element: HTMLVideoElement) => element.currentTime), { timeout: 30_000 }).toBeGreaterThan(1)
  expect(await player.evaluate((element: HTMLVideoElement) => element.videoWidth), 'picture width').toBeGreaterThan(0)   // Auto starts on the smallest quality
  expect(requested.join(', '), 'master playlist requested').toMatch(/200 master\.m3u8/)
  expect(requested.join(', '), 'pieces requested').toMatch(/20[06] v\d-seg0000\d\.ts/)   // 206 when the browser asks for part of a piece
  await expect(panel.getByRole('alert')).toHaveCount(0)

  // Several qualities are offered. Choosing the lower one makes the player switch to the 240-pixel pieces.
  const quality = panel.getByLabel('Quality')
  await expect(quality.locator('option')).toHaveText(['Auto', '240p', '360p'])
  await quality.selectOption('360p')
  await expect.poll(() => player.evaluate((element: HTMLVideoElement) => element.videoHeight), { timeout: 30_000 }).toBe(360)
  await quality.selectOption('240p')
  await expect.poll(() => player.evaluate((element: HTMLVideoElement) => element.videoHeight), { timeout: 30_000 }).toBe(240)
  await quality.selectOption('Auto')

  // A learner cannot start conversion.
  const lena = await apiAs(request, LEARNER)
  expect((await lena.post(`/api/v1/tenant/videos/${uploaded.id}/reprocess`, {})).status()).toBe(403)

  // Clean up so the library is as smoke.spec.ts left it.
  expect((await teacher.delete(`/api/v1/tenant/videos/${uploaded.id}`)).status()).toBe(204)
})
