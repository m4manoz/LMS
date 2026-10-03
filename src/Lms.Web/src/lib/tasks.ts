export type TaskItem = {
  id: string
  kind: 'assignment' | 'assessment' | 'live-class' | 'grading'
  title: string
  courseTitle: string | null
  startAtUtc: string | null
  dueAtUtc: string | null
  status: string
  target: string
  count: number | null
}

export type TaskFeed = { fromUtc: string; toUtc: string; items: TaskItem[] }

export const kindLabels: Record<TaskItem['kind'], string> = {
  assignment: 'Assignment',
  assessment: 'Assessment',
  'live-class': 'Live class',
  grading: 'To grade',
}

/** Colour of the small marker that identifies the kind of item (not a status). */
export const kindMarker: Record<TaskItem['kind'], string> = {
  assignment: 'bg-amber-400',
  assessment: 'bg-emerald-400',
  'live-class': 'bg-sky-400',
  grading: 'bg-violet-400',
}

/** 'Ended' is a live class whose time has passed: nothing is left to do about it. */
const completedStatuses = new Set(['Submitted', 'Graded', 'Done', 'Ended'])

export const isCompleted = (item: TaskItem) => completedStatuses.has(item.status)

/** The moment the item belongs to on a calendar: a class start, otherwise the deadline. */
export const itemDate = (item: TaskItem): Date | null => {
  const value = item.startAtUtc ?? item.dueAtUtc
  return value ? new Date(value) : null
}

/** Local calendar day as YYYY-MM-DD (what the user sees, not UTC). */
export const dayKey = (date: Date) =>
  `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`

export function describeStatus(item: TaskItem): string {
  if (item.kind === 'grading') return `${item.count ?? 0} to grade`
  if (item.status === 'InProgress') return 'In progress'
  if (item.status === 'Todo') return 'To do'
  return item.status
}

export function statusVariant(item: TaskItem): 'default' | 'secondary' | 'outline' | 'destructive' {
  if (item.status === 'Overdue') return 'destructive'
  if (isCompleted(item)) return 'secondary'
  if (item.status === 'Live' || item.status === 'ToGrade') return 'default'
  return 'outline'
}

export function describeWhen(item: TaskItem): string {
  const date = itemDate(item)
  if (!date) return 'No deadline'
  if (item.kind === 'live-class') return date.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
  return `Due ${date.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}`
}
