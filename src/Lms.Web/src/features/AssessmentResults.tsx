import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { EmptyState, ListRow, RowList } from '@/components/form'
import { apiRequest } from '@/lib/api'

export type MyResult = {
  assessmentId: string
  assessmentTitle: string
  attempt: { id: string; attemptNumber: number; status: string; scorePoints: number; possiblePoints: number; percentage: number | null; startedAtUtc: string; submittedAtUtc: string | null; gradedAtUtc: string | null }
}

/** What to show for an attempt's result: the score once it is marked, otherwise where it stands. */
export function resultLabel(attempt: MyResult['attempt']): string {
  if (attempt.status === 'InProgress') return 'In progress'
  if (attempt.status === 'Graded' || attempt.percentage !== null) return `${attempt.scorePoints} / ${attempt.possiblePoints}${attempt.percentage !== null ? ` · ${Math.round(attempt.percentage)}%` : ''}`
  return 'Waiting to be marked'
}

/** A learner's own attempts at the course's assessments, newest first. `reloadKey` makes it read again (after an attempt is submitted). */
export default function AssessmentResults({ courseId, reloadKey = 0 }: { courseId: string; reloadKey?: number }) {
  const [results, setResults] = useState<MyResult[] | null>(null)
  useEffect(() => {
    let active = true
    apiRequest<MyResult[]>(`/api/v1/tenant/courses/${courseId}/assessment-results`).then((list) => { if (active) setResults(Array.isArray(list) ? list : []) }).catch(() => { if (active) setResults([]) })
    return () => { active = false }
  }, [courseId, reloadKey])
  if (results === null) return null
  return (
    <section aria-label="My results" className="flex flex-col gap-2">
      <h3 className="text-base font-semibold">My results</h3>
      {results.length === 0 ? <EmptyState>You have not taken an assessment in this course yet.</EmptyState> : (
        <RowList label="My assessment results">
          {results.map((item) => (
            <ListRow key={item.attempt.id} columns="sm:grid-cols-[minmax(0,2fr)_90px_minmax(0,1fr)_130px]">
              <div className="min-w-0"><strong className="block truncate">{item.assessmentTitle}</strong><small className="text-muted-foreground">Attempt {item.attempt.attemptNumber} · {new Date(item.attempt.startedAtUtc).toLocaleDateString()}</small></div>
              <div><Badge variant={item.attempt.status === 'Graded' ? 'default' : 'secondary'}>{item.attempt.status === 'InProgress' ? 'In progress' : item.attempt.status}</Badge></div>
              <div className="text-sm">{resultLabel(item.attempt)}</div>
              <div className="text-sm text-muted-foreground">{item.attempt.submittedAtUtc ? new Date(item.attempt.submittedAtUtc).toLocaleString() : ''}</div>
            </ListRow>
          ))}
        </RowList>
      )}
    </section>
  )
}
