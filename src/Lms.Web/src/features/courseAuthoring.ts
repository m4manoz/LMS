import type { VersionInfo } from './CourseVersionPanel'

export type Course = {
  id: string
  code: string
  slug: string
  title: string
  description?: string | null
  status: string
  startDateAd?: string | null
  endDateAd?: string | null
  capacity?: number | null
  categoryId?: string | null
  categoryName?: string | null
  currentVersionId?: string | null
  publishedAtUtc?: string | null
}

export type Lesson = { id: string; title: string; summary?: string | null; contentHtml?: string | null; displayOrder: number; completeWhenVideosWatched?: boolean }
export type Module = { id: string; title: string; description?: string | null; displayOrder: number; lessons: Lesson[] }
export type CourseDetail = {
  course: Course
  currentVersion?: VersionInfo | null
  draftVersion?: VersionInfo | null
  viewingDraft?: boolean
  modules: Module[]
  workflow?: { id: string; eventType: string; fromStatus?: string | null; toStatus: string; notes?: string | null; createdAtUtc: string }[]
}

export type CourseFormValues = { code: string; title: string; description: string; startDateAd: string; endDateAd: string; capacity: string; categoryId: string }
export type CategoryOption = { id: string; name: string }

export const statusLabel = (status: string) => (status === 'InReview' ? 'In review' : status)

/** Published reads as live, a draft as neutral, and a course waiting for review stands out. */
export const statusVariant = (status: string) => (status === 'Published' ? 'default' : status === 'InReview' ? 'outline' : 'secondary') as 'default' | 'outline' | 'secondary'

export const STATUS_FILTERS = ['All', 'Draft', 'InReview', 'Published', 'Archived'] as const

/** Search by title or code, optionally narrowed to one status and one category ('none' means uncategorised). */
export function filterCourses<T extends Pick<Course, 'title' | 'code' | 'status'> & { categoryId?: string | null }>(courses: T[], search: string, status: string, category = 'All'): T[] {
  const needle = search.trim().toLowerCase()
  return courses.filter((course) => (status === 'All' || course.status === status) && (category === 'All' || (category === 'none' ? !course.categoryId : course.categoryId === category)) && (!needle || course.title.toLowerCase().includes(needle) || course.code.toLowerCase().includes(needle)))
}

export type Check = { id: string; label: string; ok: boolean; hint: string; required: boolean }

/** What is still missing before a course is worth sending for review. */
export function readiness(modules: Module[], description?: string | null): Check[] {
  const empty = modules.filter((module) => module.lessons.length === 0)
  return [
    { id: 'modules', label: 'Has at least one module', ok: modules.length > 0, hint: 'Add a module on the Outline tab.', required: true },
    { id: 'lessons', label: 'Every module has a lesson', ok: modules.length > 0 && empty.length === 0, hint: empty.length > 0 ? `Add a lesson to ${empty.map((module) => `“${module.title}”`).join(', ')}.` : 'Add modules first.', required: true },
    { id: 'description', label: 'Has a description', ok: !!description?.trim(), hint: 'Learners see this in the catalog. Add it on the Details tab.', required: false },
  ]
}

export const isReady = (checks: Check[]) => checks.filter((check) => check.required).every((check) => check.ok)

export const countLessons = (modules: Module[]) => modules.reduce((total, module) => total + module.lessons.length, 0)

export const emptyForm = (): CourseFormValues => ({ code: '', title: '', description: '', startDateAd: '', endDateAd: '', capacity: '', categoryId: '' })

export const formFrom = (course: Course): CourseFormValues => ({
  code: course.code, title: course.title, description: course.description ?? '', startDateAd: course.startDateAd ?? '', endDateAd: course.endDateAd ?? '', capacity: course.capacity ? String(course.capacity) : '', categoryId: course.categoryId ?? '',
})

/** The request body for creating or updating a course; blank optional fields mean "not set". */
export const toPayload = (values: CourseFormValues) => ({
  code: values.code.trim(), title: values.title.trim(), description: values.description.trim() || null,
  startDateAd: values.startDateAd || null, endDateAd: values.endDateAd || null, capacity: values.capacity.trim() ? Number(values.capacity) : null, categoryId: values.categoryId || null,
})

/** A message for the first problem with the form, or null when it can be sent. */
export function validateForm(values: CourseFormValues, creating: boolean): string | null {
  if (creating && !values.code.trim()) return 'Enter a course code.'
  if (!values.title.trim()) return 'Enter a title.'
  if (values.startDateAd && values.endDateAd && values.startDateAd > values.endDateAd) return 'The end date must be on or after the start date.'
  if (values.capacity.trim() && !(Number.isInteger(Number(values.capacity)) && Number(values.capacity) >= 1 && Number(values.capacity) <= 1_000_000)) return 'Capacity must be a whole number from 1 to 1,000,000, or empty for unlimited.'
  return null
}
