import { ApiError } from './api'

// What an organization's public front page is made of. The shapes follow the server's.
export type LandingHero = { title: string; subtitle: string; primaryLabel: string; primaryLink: string; searchPlaceholder: string }
export type LandingBanner = { id: string; title: string; text: string; buttonLabel: string; link: string; theme: string }
export type LandingLink = { label: string; link: string }
export type LandingRow = { id: string; title: string; subtitle: string | null; mode: string; categoryId: string | null; courseIds: string[]; limit: number }
export type LandingFeature = { title: string; text: string; icon: string }
export type LandingStat = { value: string; label: string }
export type LandingTestimonial = { name: string; role: string | null; quote: string }
export type LandingFaq = { question: string; answer: string }
export type LandingFooterLink = { label: string; url: string }
export type LandingFooterGroup = { title: string; links: LandingFooterLink[] }

export type LandingContent = {
  hero: LandingHero
  banners: LandingBanner[]
  intents: { title: string; items: LandingLink[] }
  rows: LandingRow[]
  showCategories: boolean
  categoriesTitle: string
  featuresTitle: string
  features: LandingFeature[]
  stats: LandingStat[]
  testimonialsTitle: string
  testimonials: LandingTestimonial[]
  faqTitle: string
  faq: LandingFaq[]
  footerAbout: string
  footerGroups: LandingFooterGroup[]
  copyright: string
}

export type PublicOrganization = { slug: string; name: string }
export type PublicCourse = { id: string; code: string; title: string; summary: string; categoryId: string | null; category: string | null; teacher: string | null; startDate: string | null; endDate: string | null; seatsLeft: number | null }
export type PublicCategory = { id: string; name: string; courses: number }
export type PublicRow = { id: string; title: string; subtitle: string | null; courseIds: string[] }
export type PublicLanding = { organization: PublicOrganization; content: LandingContent; courses: PublicCourse[]; categories: PublicCategory[]; rows: PublicRow[] }
export type PublicCourseDetail = { id: string; code: string; title: string; description: string | null; category: string | null; teacher: string | null; startDate: string | null; endDate: string | null; seatsLeft: number | null; modules: { title: string; lessons: string[] }[] }
export type ApplicationForm = { fullName: string; email: string; phone: string; message: string; website: string }

/** The content management screen's view: the content, whether it is still the default, and the choices the editor offers. */
export type LandingEditorData = { content: LandingContent; isDefault: boolean; updatedAtUtc: string | null; slug: string; themes: string[]; modes: string[]; icons: string[] }

/** A request that needs no sign-in: the public front page. */
async function publicRequest<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body) headers.set('Content-Type', 'application/json')
  const response = await fetch(path, { ...init, headers })
  const text = await response.text()
  let parsed: unknown
  try { parsed = text ? JSON.parse(text) : null } catch { parsed = text }
  if (!response.ok) {
    const message = typeof parsed === 'object' && parsed !== null && 'message' in parsed ? String((parsed as { message: unknown }).message) : `Request failed with status ${response.status}.`
    throw new ApiError(message, response.status)
  }
  return parsed as T
}

const base = (slug: string) => `/api/v1/public/${encodeURIComponent(slug)}`
/** What this website is: one organization's own site (its address says which), or the shared portal where people name their organization. */
export type SiteInfo = { mode: 'tenant'; organization: PublicOrganization } | { mode: 'portal'; organization: null }
export const getSite = (host: string) => publicRequest<SiteInfo>(`/api/v1/public/site?host=${encodeURIComponent(host)}`)
export const getDefaultOrganization = () => publicRequest<PublicOrganization>('/api/v1/public/organizations/default')
export const getLanding = (slug: string) => publicRequest<PublicLanding>(`${base(slug)}/landing`)
export const getPublicCourse = (slug: string, courseId: string) => publicRequest<PublicCourseDetail>(`${base(slug)}/courses/${courseId}`)
export const applyForCourse = (slug: string, courseId: string, form: ApplicationForm) =>
  publicRequest<{ message: string }>(`${base(slug)}/courses/${courseId}/applications`, { method: 'POST', body: JSON.stringify({ fullName: form.fullName.trim(), email: form.email.trim(), phone: form.phone.trim() || null, message: form.message.trim() || null, website: form.website }) })

/** A link that is safe to put in a button: a section of this page, a path in this app, a mail link or an https address. Mirrors the server. */
export function isSafeLink(value: string | null | undefined): boolean {
  const text = (value ?? '').trim()
  if (!text || text.length > 500) return false
  if (text.startsWith('#')) return text.length > 1
  if (text.startsWith('/')) return !text.startsWith('//') && !text.includes('\\')
  if (/^mailto:/i.test(text)) return text.length > 7
  try { const url = new URL(text); return url.protocol === 'https:' && !url.username && !url.password && !!url.hostname } catch { return false }
}

/** The first problem with an application form, or null when it can be sent. */
export function validateApplication(form: ApplicationForm): string | null {
  const name = form.fullName.trim()
  if (name.length < 2 || name.length > 120) return 'Enter your name (2 to 120 characters).'
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.email.trim())) return 'Enter a valid email address.'
  if (form.phone.trim().length > 40) return 'The phone number must be 40 characters or fewer.'
  if (form.message.trim().length > 1000) return 'The message must be 1000 characters or fewer.'
  return null
}
