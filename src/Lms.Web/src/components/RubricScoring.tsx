import type { RubricCriterion } from '@/lib/assessments'

/** The points chosen for each criterion, keyed by criterion id (a criterion not yet scored is absent). */
export type CriterionMarks = Record<string, number | undefined>

export const markedTotal = (criteria: RubricCriterion[], marks: CriterionMarks) => criteria.reduce((sum, criterion) => sum + (marks[criterion.id] ?? 0), 0)
export const firstUnscored = (criteria: RubricCriterion[], marks: CriterionMarks) => criteria.find((criterion) => marks[criterion.id] === undefined)

/** One group of level choices per criterion, with the running total. A grader picks a level for every criterion. */
export default function RubricScoring({ criteria, marks, onChange, disabled = false, idPrefix }: { criteria: RubricCriterion[]; marks: CriterionMarks; onChange: (marks: CriterionMarks) => void; disabled?: boolean; idPrefix: string }) {
  const total = criteria.reduce((sum, criterion) => sum + Math.max(0, ...criterion.levels.map((level) => level.points)), 0)
  return (
    <div className="flex flex-col gap-3">
      {criteria.map((criterion) => (
        <div key={criterion.id} role="radiogroup" aria-label={criterion.name} className="flex flex-col gap-1">
          <span className="font-medium">{criterion.name}{criterion.description ? <span className="font-normal text-muted-foreground"> — {criterion.description}</span> : null}</span>
          <div className="flex flex-wrap gap-3">
            {criterion.levels.map((level) => (
              <label key={level.label} className="flex items-center gap-1.5">
                <input type="radio" name={`${idPrefix}-${criterion.id}`} disabled={disabled} checked={marks[criterion.id] === level.points} onChange={() => onChange({ ...marks, [criterion.id]: level.points })} />
                {level.label} ({level.points})
              </label>
            ))}
          </div>
        </div>
      ))}
      <span className="text-muted-foreground">Score: {markedTotal(criteria, marks)} of {total}</span>
    </div>
  )
}
