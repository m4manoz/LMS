import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, type APIRequestContext, type Page } from '@playwright/test'

export const API = 'http://localhost:5299'
export const PLATFORM_KEY = 'local-development-only-change-me' // the development default from appsettings.json
export const TENANT = 'e2e'
export const ADMIN = { email: 'admin@e2e.test', password: 'Admin-pass-123' }
export const LEARNER = { name: 'Lena Learner', email: 'lena@e2e.test', password: 'Learner-pass-123' }

/** Creates the organization and its administrator through the platform API (idempotent). */
export async function provision(request: APIRequestContext) {
  const headers = { 'X-Platform-Key': PLATFORM_KEY }
  const tenant = await request.post(`${API}/api/v1/platform/tenants`, { headers, data: { name: 'E2E Academy', slug: TENANT } })
  expect([201, 409]).toContain(tenant.status())
  const admin = await request.post(`${API}/api/v1/platform/tenants/${TENANT}/bootstrap-admin`, { headers, data: { email: ADMIN.email, displayName: 'E2E Admin', password: ADMIN.password } })
  expect([201, 409]).toContain(admin.status())
}

/** From the landing page to the sign-in form. */
export async function openLogin(page: Page) {
  await page.goto('/')
  await page.getByRole('banner').getByRole('button', { name: 'Log in' }).click()
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
}

export async function signIn(page: Page, who: { email: string; password: string }) {
  await openLogin(page)
  await page.getByLabel('Organization (tenant slug)').fill(TENANT)
  await page.getByLabel('Email').fill(who.email)
  await page.getByLabel('Password').fill(who.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()
}

export async function signOut(page: Page) {
  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()      // back on the landing page
}

/** Opens a page from the left menu by its label. */
export async function openMenu(page: Page, label: string | RegExp) {
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('button', { name: label, exact: typeof label === 'string' }).click()
}

/** Records script errors and server errors so a screen that opens "fine" but is broken still fails the test. */
export function watchForProblems(page: Page) {
  const problems: string[] = []
  page.on('pageerror', (error) => problems.push(`script error: ${error.message}`))
  page.on('response', (response) => { if (response.status() >= 500) problems.push(`${response.status()} ${response.request().method()} ${response.url()}`) })
  return problems
}

export const TEACHER = { name: 'Tara Teacher', email: 'tara@e2e.test', password: 'Teacher-pass-123' }

/** Calls the API directly as the organization's administrator, for set-up the browser test should not have to click through. */
export async function adminApi(request: APIRequestContext) {
  const login = await request.post(`${API}/api/v1/auth/login`, { data: { tenantSlug: TENANT, email: ADMIN.email, password: ADMIN.password } })
  expect(login.ok()).toBeTruthy()
  const headers = { 'X-Tenant-Slug': TENANT, Authorization: `Bearer ${(await login.json()).accessToken}` }
  return {
    get: async (path: string) => (await request.get(`${API}${path}`, { headers })).json(),
    post: async (path: string, data: object) => request.post(`${API}${path}`, { headers, data }),
    delete: async (path: string) => request.delete(`${API}${path}`, { headers }),
    put: async (path: string, data: object) => request.put(`${API}${path}`, { headers, data }),
  }
}

/** A value for a datetime-local input, `minutes` from now, in the browser's local time. */
export function localInput(minutes: number) {
  const date = new Date(Date.now() + minutes * 60_000)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/** Calls the API directly as any signed-in person. */
export async function apiAs(request: APIRequestContext, who: { email: string; password: string }) {
  const login = await request.post(`${API}/api/v1/auth/login`, { data: { tenantSlug: TENANT, email: who.email, password: who.password } })
  expect(login.ok()).toBeTruthy()
  const headers = { 'X-Tenant-Slug': TENANT, Authorization: `Bearer ${(await login.json()).accessToken}` }
  return {
    get: async (path: string) => (await request.get(`${API}${path}`, { headers })).json(),
    post: async (path: string, data: object) => request.post(`${API}${path}`, { headers, data }),
    delete: async (path: string) => request.delete(`${API}${path}`, { headers }),
  }
}

/** A real 8-second MP4 (picture and sound), small enough to keep in the repository. */
export const SAMPLE_VIDEO = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'sample.mp4')
export const CONVERTS_VIDEOS = process.env.E2E_NO_FFMPEG !== '1'

/** Uploads a video through the API as the given person (a multipart form, like the browser sends). Returns the new video. */
export async function uploadVideo(request: APIRequestContext, who: { email: string; password: string }, courseId: string, title: string, file: Buffer) {
  const login = await request.post(`${API}/api/v1/auth/login`, { data: { tenantSlug: TENANT, email: who.email, password: who.password } })
  expect(login.ok()).toBeTruthy()
  const headers = { 'X-Tenant-Slug': TENANT, Authorization: `Bearer ${(await login.json()).accessToken}` }
  const response = await request.post(`${API}/api/v1/tenant/videos`, { headers, multipart: { courseId, title, file: { name: 'lecture.mp4', mimeType: 'video/mp4', buffer: file } } })
  expect(response.status()).toBe(201)
  return response.json()
}
