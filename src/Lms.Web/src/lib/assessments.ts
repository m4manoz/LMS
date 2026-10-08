export type Course = { id: string; code: string; title: string; status: string }

export type Assessment = {
  id: string
  courseId: string
  title: string
  instructions?: string | null
  status: string
  timeLimitMinutes?: number | null
  attemptLimit: number
  questionCount: number
  shuffleQuestions?: boolean
  shuffleOptions?: boolean
  currentVersion?: number
  draftVersion?: number | null
  opensAtUtc?: string | null
  dueAtUtc?: string | null
}

export type Question = {
  id: string
  type: string
  prompt: string
  options: string[]
  correctAnswers: string[]
  displayOrder: number
  points: number
  pool?: string | null
  rubricId?: string | null
  rubricName?: string | null
}

export type Pool = { name: string; drawCount: number; questionCount: number }
export type MyAccommodation = { extraTimePercent: number; extraAttempts: number }

export type AssessmentDetail = {
  assessment: Assessment
  questions: Question[]
  pools?: Pool[] | null
  editingDraftVersion?: boolean
  effectiveTimeLimitMinutes?: number | null
  effectiveAttemptLimit?: number | null
  attemptsUsed?: number | null
  accommodation?: MyAccommodation | null
}

export type RubricLevel = { label: string; points: number; description?: string | null }
export type RubricCriterion = { id: string; name: string; description?: string | null; levels: RubricLevel[] }
export type Rubric = { id: string; courseId: string; name: string; totalPoints: number; criteria: RubricCriterion[]; usedByQuestions: number }
export type CriterionScore = { criterionId: string; name: string; maxPoints: number; points: number }

export type AttemptQuestion = {
  id: string
  type: string
  prompt: string
  options: string[]
  correctAnswers: string[]
  displayOrder: number
  points: number
  answers: string[]
  scorePoints: number
  isCorrect?: boolean | null
  feedback?: string | null
  text?: string | null
  file?: { fileName: string; sizeBytes: number } | null
  rubric?: { id: string; name: string; totalPoints: number; criteria: RubricCriterion[] } | null
  rubricScores?: CriterionScore[] | null
}

export type AttemptSummary = {
  id: string
  learnerUserId: string
  attemptNumber: number
  status: string
  scorePoints: number
  possiblePoints: number
  percentage?: number | null
  learnerName?: string | null
  version?: number
}

export type Attempt = {
  attempt: AttemptSummary
  assessmentTitle: string
  instructions?: string | null
  timeLimitMinutes?: number | null
  questions: AttemptQuestion[]
  teacherFeedback?: string | null
  expiresAtUtc?: string | null
}

/** An ISO time as the value of a datetime-local field (the person's own clock), or '' when there is none. */
export function toLocalInput(iso: string | null | undefined): string {
  if (!iso) return ''
  const date = new Date(iso)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/** What a datetime-local field holds as an ISO time, or null when it is empty. */
export function fromLocalInput(value: string): string | null {
  if (!value.trim()) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

/** Where an assessment stands against its dates right now. */
export function availability(assessment: Pick<Assessment, 'opensAtUtc' | 'dueAtUtc'>, now = Date.now()): 'not-open' | 'open' | 'closed' {
  if (assessment.opensAtUtc && new Date(assessment.opensAtUtc).getTime() > now) return 'not-open'
  if (assessment.dueAtUtc && new Date(assessment.dueAtUtc).getTime() <= now) return 'closed'
  return 'open'
}

export const choiceTypes = ['MultipleChoice', 'MultipleResponse', 'TrueFalse']
export const manualTypes = ['Essay', 'FileUpload']
export const rubricTypes = ['Essay', 'FileUpload']

export function splitCsv(value: string) {
  return value.split(',').map((item) => item.trim()).filter(Boolean)
}

/** "12:05" for the time left, or null once it has run out. */
export function timeLeft(expiresAtUtc: string | null | undefined, now: number): string | null {
  if (!expiresAtUtc) return null
  const seconds = Math.floor((new Date(expiresAtUtc).getTime() - now) / 1000)
  if (seconds <= 0) return null
  const minutes = Math.floor(seconds / 60)
  return `${minutes}:${String(seconds % 60).padStart(2, '0')}`
}

export function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

export const readableType: Record<string, string> = {
  MultipleChoice: 'Multiple choice', MultipleResponse: 'Multiple response', TrueFalse: 'True or false', ShortAnswer: 'Short answer', Essay: 'Essay', FileUpload: 'File upload',
}
