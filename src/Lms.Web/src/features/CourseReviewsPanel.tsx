import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorBanner, NoticeBanner } from '@/components/form'
import { ApiError, apiRequest } from '@/lib/api'
import { RatingLabel, Stars } from './landing/parts'

type Row = { id: string; learnerName: string; stars: number; review: string | null; isHidden: boolean; createdAtUtc: string; updatedAtUtc: string }
type Reviews = { summary: { average: number | null; count: number; distribution: number[] }; ratings: Row[] }

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

/** Staff see every rating of a course, with the learner's name, and can hide one that is abusive or spam. Hidden ratings stop counting and are not shown publicly. */
export default function CourseReviewsPanel({ courseId }: { courseId: string }) {
  const [data, setData] = useState<Reviews | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { setData(await apiRequest<Reviews>(`/api/v1/tenant/courses/${courseId}/ratings`)) }
    catch (exception) { setError(readError(exception, 'Unable to load the ratings.')) }
  }, [courseId])
  useEffect(() => { setData(null); setError(null); setNotice(null); void load() }, [load])

  async function change(row: Row, hide: boolean) {
    setBusy(true); setError(null); setNotice(null)
    try {
      await apiRequest(`/api/v1/tenant/ratings/${row.id}/${hide ? 'hide' : 'show'}`, { method: 'POST' })
      setNotice(hide ? `${row.learnerName}'s rating is hidden.` : `${row.learnerName}'s rating is shown again.`)
      await load()
    } catch (exception) { setError(readError(exception, 'Unable to change the rating.')) } finally { setBusy(false) }
  }

  return (
    <section className="flex flex-col gap-3">
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      {data === null ? <p role="status" className="text-sm text-muted-foreground">Loading…</p>
        : data.ratings.length === 0 ? <EmptyState>No learner has rated this course yet.</EmptyState>
          : (
            <>
              <p className="flex items-center gap-2 text-sm"><RatingLabel average={data.summary.average} count={data.summary.count} />{data.summary.count === 0 ? <span className="text-muted-foreground">Every rating is hidden.</span> : null}</p>
              <ul className="flex flex-col gap-2" aria-label="Ratings">
                {data.ratings.map((row) => (
                  <li key={row.id} className="flex flex-wrap items-start justify-between gap-3 rounded-md border border-border p-3 text-sm">
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2"><strong>{row.learnerName}</strong><Stars value={row.stars} /><span className="sr-only">{row.stars} out of 5.</span>{row.isHidden ? <Badge variant="destructive">Hidden</Badge> : null}</div>
                      {row.review ? <p className="mt-1 whitespace-pre-wrap text-muted-foreground">{row.review}</p> : <p className="mt-1 text-muted-foreground">No written review.</p>}
                      <small className="text-muted-foreground">{new Date(row.updatedAtUtc).toLocaleDateString()}</small>
                    </div>
                    <Button size="sm" variant="outline" disabled={busy} aria-label={`${row.isHidden ? 'Show' : 'Hide'} the rating by ${row.learnerName}`} onClick={() => void change(row, !row.isHidden)}>{row.isHidden ? 'Show' : 'Hide'}</Button>
                  </li>
                ))}
              </ul>
            </>
          )}
    </section>
  )
}
