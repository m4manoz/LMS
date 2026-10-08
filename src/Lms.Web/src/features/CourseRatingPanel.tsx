import { useCallback, useEffect, useState } from 'react'
import { Star } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Textarea } from '@/components/ui/textarea'
import { ErrorBanner, NoticeBanner } from '@/components/form'
import { ApiError, apiRequest } from '@/lib/api'
import { cn } from '@/lib/utils'

type Mine = { rating: { stars: number; review: string | null; isHidden: boolean; updatedAtUtc: string } | null; cannotRateBecause: string | null }

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

/** A learner rates the course they are taking: 1 to 5 stars and, if they like, a few words. They can change or withdraw it any time. */
export default function CourseRatingPanel({ courseId }: { courseId: string }) {
  const [mine, setMine] = useState<Mine | null>(null)
  const [stars, setStars] = useState(0)
  const [review, setReview] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const base = `/api/v1/tenant/courses/${courseId}/rating`

  const load = useCallback(async () => {
    try {
      const result = await apiRequest<Mine>(base)
      setMine(result)
      setStars(result?.rating?.stars ?? 0); setReview(result?.rating?.review ?? '')
    } catch (exception) { setError(readError(exception, 'Unable to load your rating.')) }
  }, [base])
  useEffect(() => { setMine(null); setNotice(null); setError(null); void load() }, [load])

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (stars < 1) return setError('Choose from 1 to 5 stars.')
    if (review.length > 1000) return setError('A review can have at most 1000 characters.')
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(base, { method: 'PUT', body: JSON.stringify({ stars, review: review.trim() || null }) }); setNotice('Thank you. Your rating is saved.'); await load() }
    catch (exception) { setError(readError(exception, 'Unable to save your rating.')) } finally { setBusy(false) }
  }

  async function withdraw() {
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(base, { method: 'DELETE' }); setNotice('Your rating was removed.'); await load() }
    catch (exception) { setError(readError(exception, 'Unable to remove your rating.')) } finally { setBusy(false) }
  }

  // Someone who cannot rate (a guardian, a learner who has not begun) is not shown a form they cannot use.
  if (mine === null) return error ? <ErrorBanner message={error} /> : null
  if (!mine.rating && mine.cannotRateBecause) return (
    <Card>
      <CardHeader><CardTitle>Rate this course</CardTitle><CardDescription>{mine.cannotRateBecause}</CardDescription></CardHeader>
    </Card>
  )

  return (
    <Card>
      <CardHeader>
        <CardTitle>{mine.rating ? 'Your rating' : 'Rate this course'}</CardTitle>
        <CardDescription>Your stars and words help other learners choose. Your name appears as a first name and initial.</CardDescription>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-3" onSubmit={(event) => void save(event)} noValidate>
          <ErrorBanner message={error} />
          <NoticeBanner message={notice} />
          {mine.rating?.isHidden ? <p role="status" className="rounded-md border border-border px-3 py-2 text-sm">Staff have hidden your rating from the public page.</p> : null}
          <div role="radiogroup" aria-label="Stars" className="flex gap-1">
            {[1, 2, 3, 4, 5].map((value) => (
              <label key={value} className="cursor-pointer" title={`${value} star${value === 1 ? '' : 's'}`}>
                <input type="radio" name={`stars-${courseId}`} className="sr-only" aria-label={`${value} star${value === 1 ? '' : 's'}`} checked={stars === value} onChange={() => setStars(value)} />
                <Star className={cn('h-7 w-7 transition-colors', value <= stars ? 'fill-amber-400 text-amber-400' : 'text-muted-foreground/50 hover:text-amber-400')} aria-hidden />
              </label>
            ))}
          </div>
          <Textarea aria-label="Your review" rows={3} maxLength={1000} placeholder="What did you think? (optional)" value={review} onChange={(event) => setReview(event.target.value)} />
          <div className="flex flex-wrap gap-2">
            <Button type="submit" disabled={busy}>{mine.rating ? 'Update rating' : 'Save rating'}</Button>
            {mine.rating ? <Button type="button" variant="outline" disabled={busy} onClick={() => void withdraw()}>Remove my rating</Button> : null}
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
