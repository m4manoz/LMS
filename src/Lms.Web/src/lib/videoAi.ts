export type Segment = { index: number; startSeconds: number; endSeconds: number; text: string }

export type Transcript = {
  status: 'None' | 'Queued' | 'Processing' | 'Ready' | 'Failed'
  source: string | null
  language: string | null
  provider: string | null
  statusMessage: string | null
  segments: Segment[] | null
}

export type PracticeQuestion = { question: string; options: string[]; answerIndex: number; timestampSeconds: number | null; explanation: string | null }

export type Insight = {
  id: string
  kind: 'Summary' | 'Questions'
  content: string | null
  questions: PracticeQuestion[] | null
  published: boolean
  provider: string
  model: string | null
  updatedAtUtc: string
  quizAssessmentId?: string | null
}

export type QuizCreated = { assessmentId: string; title: string; status: string; questions: number; totalPoints: number; courseId: string }

/** The first problem with the "make a quiz" form, or null when it can be sent. Mirrors the server's limits. */
export function validateQuizForm(values: { title: string; points: string; attempts: string; minutes: string }): string | null {
  if (values.title.trim().length > 250) return 'The title must be 250 characters or fewer.'
  const whole = (text: string) => /^\d+$/.test(text.trim())
  if (!whole(values.points) || Number(values.points) < 1 || Number(values.points) > 100) return 'Points per question must be a whole number from 1 to 100.'
  if (!whole(values.attempts) || Number(values.attempts) < 1 || Number(values.attempts) > 20) return 'Attempts must be a whole number from 1 to 20.'
  if (values.minutes.trim() && (!whole(values.minutes) || Number(values.minutes) < 1 || Number(values.minutes) > 1440)) return 'The time limit must be a whole number of minutes from 1 to 1440, or empty for no limit.'
  return null
}

export type SearchHit = { videoId: string; videoTitle: string; courseTitle: string; startSeconds: number; text: string }

/** 754 -> "12:34", 3723 -> "1:02:03". A moment in a video, as shown next to a transcript line. */
export function clock(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds))
  const hours = Math.floor(whole / 3600)
  const minutes = Math.floor((whole % 3600) / 60)
  const rest = String(whole % 60).padStart(2, '0')
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`
}

/** The line being said at this moment: the last one that has started. -1 before the first line. */
export function currentLine(segments: Segment[], seconds: number): number {
  let found = -1
  for (let i = 0; i < segments.length; i++) {
    if (segments[i].startSeconds <= seconds + 0.05) found = i
    else break
  }
  return found
}

/** Lines containing every word typed, ignoring case. An empty search keeps all of them. */
export function filterLines(segments: Segment[], search: string): Segment[] {
  const words = search.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) return segments
  return segments.filter((line) => words.every((word) => line.text.toLowerCase().includes(word)))
}

/** The first problem with a set of questions being edited, or null when it can be saved. Mirrors the server's rules. */
export function validateQuestions(questions: PracticeQuestion[]): string | null {
  if (questions.length === 0) return 'Add at least one question.'
  for (const [index, item] of questions.entries()) {
    const label = `Question ${index + 1}`
    if (!item.question.trim() || item.question.length > 500) return `${label}: write the question in 500 characters or fewer.`
    const options = item.options.map((option) => option.trim())
    if (options.length < 2 || options.length > 6) return `${label}: give between 2 and 6 answers.`
    if (options.some((option) => !option || option.length > 300)) return `${label}: each answer needs 1 to 300 characters.`
    if (new Set(options.map((option) => option.toLowerCase())).size !== options.length) return `${label}: the answers must be different from each other.`
    if (item.answerIndex < 0 || item.answerIndex >= options.length) return `${label}: mark which answer is correct.`
  }
  return null
}
