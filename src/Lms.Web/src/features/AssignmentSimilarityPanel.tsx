import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { EmptyState, ErrorBanner } from '@/components/form'
import { ApiError, apiRequest } from '@/lib/api'

type Pair = { firstSubmissionId: string; firstName: string; secondSubmissionId: string; secondName: string; percent: number; sharedPhrases: number; examples: string[] }
export type SimilarityReport = { submissions: number; compared: number; tooShort: number; filesNotRead: number; threshold: number; pairs: Pair[] }

/** Looks for work that was copied between learners. It lists pairs for a teacher to read side by side; it does not decide anything. */
export default function AssignmentSimilarityPanel({ assignmentId }: { assignmentId: string }) {
  const [threshold, setThreshold] = useState('30')
  const [report, setReport] = useState<SimilarityReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function run() {
    const value = Number(threshold)
    if (!Number.isInteger(value) || value < 5 || value > 100) return setError('Show pairs that are at least 5% to 100% alike.')
    setBusy(true); setError(null)
    try { setReport(await apiRequest<SimilarityReport>(`/api/v1/tenant/assignments/${assignmentId}/similarity?threshold=${value}`)) }
    catch (exception) { setError(exception instanceof ApiError ? exception.message : 'Unable to run the check.') } finally { setBusy(false) }
  }

  return (
    <section className="flex flex-col gap-3 rounded-md border border-border p-3" aria-label="Copied work check">
      <div>
        <h4 className="text-sm font-semibold">Check for copied work</h4>
        <p className="text-xs text-muted-foreground">Compares what learners wrote (and plain-text files) with each other. Wording from the instructions is ignored, and teammates in a group are not compared. It does not search the internet, so read any pair it shows before concluding anything.</p>
      </div>
      <div className="flex flex-wrap items-end gap-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="similarity-threshold">Show pairs at least this alike (%)</Label>
          <Input id="similarity-threshold" className="w-24" type="number" min="5" max="100" value={threshold} onChange={(event) => setThreshold(event.target.value)} />
        </div>
        <Button variant="secondary" disabled={busy} onClick={() => void run()}>{busy ? 'Checking…' : 'Run the check'}</Button>
      </div>
      <ErrorBanner message={error} />
      {report ? (
        <div className="flex flex-col gap-2 text-sm">
          <p className="text-muted-foreground" role="status">
            Compared {report.compared} of {report.submissions} submission{report.submissions === 1 ? '' : 's'}
            {report.tooShort > 0 ? `; ${report.tooShort} too short to compare` : ''}
            {report.filesNotRead > 0 ? `; ${report.filesNotRead} attached file${report.filesNotRead === 1 ? '' : 's'} could not be read (only plain text is checked)` : ''}.
          </p>
          {report.pairs.length === 0 ? <EmptyState>No pairs are {report.threshold}% alike or more.</EmptyState> : (
            <ol className="flex flex-col gap-2" aria-label="Similar pairs">
              {report.pairs.map((pair) => (
                <li key={`${pair.firstSubmissionId}-${pair.secondSubmissionId}`} className="rounded-md border border-border p-3">
                  <strong>{pair.firstName} and {pair.secondName}</strong> <span className="text-muted-foreground">· {pair.percent}% alike · {pair.sharedPhrases} shared phrases</span>
                  <ul className="mt-1 list-disc pl-5 text-muted-foreground">
                    {pair.examples.map((example) => <li key={example}>“{example}”</li>)}
                  </ul>
                </li>
              ))}
            </ol>
          )}
        </div>
      ) : null}
    </section>
  )
}
