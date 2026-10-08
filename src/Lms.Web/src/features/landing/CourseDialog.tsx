import { useEffect, useState } from 'react'
import { CalendarDays, CheckCircle2, UserRound } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { ErrorBanner, Field, FormActions, FormLayout, FormSection } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api'
import { applyForCourse, getPublicCourse, validateApplication, type ApplicationForm, type PublicCourse, type PublicCourseDetail } from '@/lib/publicApi'
import { cn } from '@/lib/utils'
import { RatingLabel, Stars, coverFor, seatsLabel, whenLabel } from './parts'

const empty: ApplicationForm = { fullName: '', email: '', phone: '', message: '', website: '' }

/** One course in full, with the form to apply for it. Anyone can apply; staff decide, and approved people get an invitation by email. Shown in a panel or on a page of its own. */
export function CourseView({ organization, course, onClose, onLogin }: { organization: string; course: PublicCourse; onClose: () => void; onLogin: () => void }) {
  const [detail, setDetail] = useState<PublicCourseDetail | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [form, setForm] = useState<ApplicationForm>(empty)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [thanks, setThanks] = useState<string | null>(null)

  useEffect(() => {
    setDetail(null); setLoadError(null); setForm(empty); setProblem(null); setThanks(null)
    if (!course) return
    let current = true
    getPublicCourse(organization, course.id)
      .then((result) => { if (current) setDetail(result) })
      .catch((exception) => { if (current) setLoadError(exception instanceof ApiError ? exception.message : 'The course details could not be loaded.') })
    return () => { current = false }
  }, [course?.id, organization])   

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!course) return
    const message = validateApplication(form)
    setProblem(message)
    if (message) return
    setBusy(true)
    try { setThanks((await applyForCourse(organization, course.id, form)).message) }
    catch (exception) { setProblem(exception instanceof ApiError ? exception.message : 'Your application could not be sent. Please try again.') }
    finally { setBusy(false) }
  }

  const seats = seatsLabel(detail?.seatsLeft ?? course?.seatsLeft ?? null)
  const set = (field: keyof ApplicationForm) => (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setForm((current) => ({ ...current, [field]: event.target.value }))

  return (
        <div className="flex flex-col gap-6">
          <div className={cn('flex flex-col gap-2 rounded-xl bg-gradient-to-br p-6 text-white', coverFor(course.category ?? course.title))}>
            <span className="w-fit rounded bg-black/25 px-2 py-0.5 text-xs font-medium">{course.category ?? 'Course'}</span>
            <h2 className="text-2xl font-bold">{course.title}</h2>
            <p className="text-sm text-white/85">{course.code}{course.teacher ? ` · Taught by ${course.teacher}` : ''}</p>
          </div>

          <div className="flex flex-wrap items-center gap-3 text-sm">
            <Badge variant="secondary" className="gap-1"><CalendarDays className="h-3.5 w-3.5" aria-hidden />{whenLabel(course)}</Badge>
            {seats ? <Badge variant="outline" className="gap-1"><UserRound className="h-3.5 w-3.5" aria-hidden />{seats}</Badge> : null}
            <RatingLabel average={detail?.rating?.average ?? course.ratingAverage} count={detail?.rating?.count ?? course.ratingCount} />
          </div>

          <ErrorBanner message={loadError} />
          {detail === null && !loadError ? <p className="text-sm text-muted-foreground">Loading the course…</p> : null}
          {detail?.description ? <section aria-label="About this course"><h3 className="mb-1 text-lg font-semibold">About this course</h3><p className="whitespace-pre-line text-sm text-muted-foreground">{detail.description}</p></section> : null}
          {detail && detail.modules.length > 0 ? (
            <section aria-label="What you will learn" className="flex flex-col gap-2">
              <h3 className="text-lg font-semibold">What you will learn</h3>
              <div className="divide-y divide-border rounded-lg border border-border">
                {detail.modules.map((module, index) => (
                  <details key={`${module.title}-${index}`} className="group px-4 py-3" open={index === 0}>
                    <summary className="flex cursor-pointer list-none items-center justify-between gap-3 text-sm font-medium [&::-webkit-details-marker]:hidden">
                      <span>{module.title}</span><small className="text-muted-foreground">{module.lessons.length} lesson{module.lessons.length === 1 ? '' : 's'}</small>
                    </summary>
                    <ul className="mt-2 flex flex-col gap-1 text-sm text-muted-foreground">
                      {module.lessons.map((lesson, position) => <li key={`${lesson}-${position}`} className="flex gap-2"><CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-hidden />{lesson}</li>)}
                    </ul>
                  </details>
                ))}
              </div>
            </section>
          ) : null}

          {detail?.rating && detail.rating.count > 0 ? (
            <section aria-label="What learners say" className="flex flex-col gap-3">
              <h3 className="text-lg font-semibold">What learners say</h3>
              <div className="flex flex-wrap items-center gap-6">
                <div className="flex flex-col items-start gap-1">
                  <strong className="text-4xl">{detail.rating.average?.toFixed(1)}</strong>
                  <Stars value={detail.rating.average ?? 0} />
                  <small className="text-muted-foreground">{detail.rating.count} rating{detail.rating.count === 1 ? '' : 's'}</small>
                </div>
                <ol className="flex min-w-48 flex-1 flex-col gap-1" aria-label="Ratings by stars">
                  {[5, 4, 3, 2, 1].map((star) => {
                    const total = detail.rating!.distribution[star - 1] ?? 0
                    return (
                      <li key={star} className="flex items-center gap-2 text-xs text-muted-foreground">
                        <span className="w-12">{star} star{star === 1 ? '' : 's'}</span>
                        <span className="h-2 flex-1 overflow-hidden rounded bg-muted"><span className="block h-full bg-amber-400" style={{ width: `${Math.round((total * 100) / detail.rating!.count)}%` }} /></span>
                        <span className="w-6 text-right">{total}</span>
                      </li>
                    )
                  })}
                </ol>
              </div>
              {detail.reviews && detail.reviews.length > 0 ? (
                <ul className="flex flex-col gap-3" aria-label="Reviews">
                  {detail.reviews.map((review, index) => (
                    <li key={`${review.author}-${index}`} className="rounded-lg border border-border p-3 text-sm">
                      <div className="flex items-center gap-2"><Stars value={review.stars} /><span className="sr-only">{review.stars} out of 5.</span><strong>{review.author}</strong></div>
                      <p className="mt-1 whitespace-pre-line text-muted-foreground">{review.text}</p>
                    </li>
                  ))}
                </ul>
              ) : null}
            </section>
          ) : null}

          {thanks ? (
            <section role="status" aria-label="Application sent" className="flex flex-col gap-2 rounded-xl border border-primary/40 bg-primary/5 p-5">
              <h3 className="flex items-center gap-2 text-lg font-semibold"><CheckCircle2 className="h-5 w-5 text-primary" aria-hidden />Application sent</h3>
              <p className="text-sm">{thanks}</p>
              <div><Button type="button" variant="outline" onClick={onClose}>Back to courses</Button></div>
            </section>
          ) : (
            <FormLayout onSubmit={submit} noValidate>
              <ErrorBanner message={problem} />
              <FormSection title="Apply for this course" description="Tell us who you are. We review your application and email you an invitation to create your account and join the course.">
                <Field id="apply-name" label="Full name" required><Input id="apply-name" autoComplete="name" maxLength={120} value={form.fullName} onChange={set('fullName')} /></Field>
                <Field id="apply-email" label="Email" required hint="Your invitation is sent here."><Input id="apply-email" type="email" autoComplete="email" maxLength={200} value={form.email} onChange={set('email')} /></Field>
                <Field id="apply-phone" label="Phone"><Input id="apply-phone" type="tel" autoComplete="tel" maxLength={40} value={form.phone} onChange={set('phone')} /></Field>
                <Field id="apply-message" label="Why do you want to take this course?"><Textarea id="apply-message" rows={3} maxLength={1000} value={form.message} onChange={set('message')} /></Field>
                {/* A box people never see: only a program filling in every field will touch it. */}
                <div aria-hidden className="absolute left-[-9999px] h-0 w-0 overflow-hidden"><label>Website<input tabIndex={-1} autoComplete="off" value={form.website} onChange={set('website')} /></label></div>
              </FormSection>
              <FormActions busy={busy} submitLabel="Apply now" busyLabel="Sending…" />
              <p className="text-sm text-muted-foreground">Already have an account? <button type="button" className="font-medium text-primary hover:underline" onClick={onLogin}>Log in</button></p>
            </FormLayout>
          )}
        </div>
  )
}

/** The same course in a panel that slides over the page. */
export default function CourseDialog({ organization, course, onClose, onLogin }: { organization: string; course: PublicCourse | null; onClose: () => void; onLogin: () => void }) {
  return (
    <SidePanel open={course !== null} label={course?.title ?? 'Course'} onClose={onClose}>
      {course ? <CourseView organization={organization} course={course} onClose={onClose} onLogin={onLogin} /> : null}
    </SidePanel>
  )
}
