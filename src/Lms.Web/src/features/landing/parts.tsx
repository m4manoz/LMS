import { useEffect, useRef, useState } from 'react'
import { Award, BookOpen, CalendarDays, CheckCircle2, ChevronLeft, ChevronRight, Clock, Globe, GraduationCap, Laptop, ShieldCheck, Star, UserRound, Users, Video } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { landingImageUrl, type LandingBanner, type LandingFaq, type LandingFooterGroup, type PublicCourse } from '@/lib/publicApi'

const icons = { video: Video, book: BookOpen, award: Award, users: Users, clock: Clock, shield: ShieldCheck, globe: Globe, laptop: Laptop, check: CheckCircle2, star: Star } as const
export const iconFor = (name: string) => (icons as Record<string, typeof Star>)[name] ?? Star

/** Five stars filled up to the rating (to the nearest half). Decoration only: the words that go with it carry the meaning. */
export function Stars({ value, className }: { value: number; className?: string }) {
  return (
    <span className={cn('inline-flex', className)} aria-hidden>
      {[1, 2, 3, 4, 5].map((position) => <Star key={position} className={cn('h-3.5 w-3.5', value >= position - 0.25 ? 'fill-amber-400 text-amber-400' : 'text-muted-foreground/40')} />)}
    </span>
  )
}

/** "★★★★☆ 4.2 (31)" with a spoken version; nothing at all for a course nobody has rated. */
export function RatingLabel({ average, count }: { average: number | null | undefined; count: number | undefined }) {
  if (!count || average === null || average === undefined) return null
  return (
    <span className="inline-flex items-center gap-1 text-xs text-muted-foreground" aria-label={`Rated ${average.toFixed(1)} out of 5 by ${count} ${count === 1 ? 'learner' : 'learners'}`}>
      <Stars value={average} /><span className="font-medium text-foreground">{average.toFixed(1)}</span><span>({count})</span>
    </span>
  )
}

const covers = [
  'from-sky-500 to-indigo-600', 'from-emerald-500 to-teal-700', 'from-violet-500 to-fuchsia-600', 'from-amber-500 to-orange-600',
  'from-rose-500 to-pink-700', 'from-cyan-500 to-blue-700', 'from-lime-500 to-green-700', 'from-slate-600 to-slate-800',
]
/** The same words always give the same colours, so a course keeps its look. */
export function coverFor(seed: string): string {
  let hash = 0
  for (const char of seed) hash = (hash * 31 + char.charCodeAt(0)) >>> 0
  return covers[hash % covers.length]
}

const bannerThemes: Record<string, string> = {
  blue: 'from-blue-600 to-indigo-700', green: 'from-emerald-600 to-teal-700', purple: 'from-violet-600 to-fuchsia-700', amber: 'from-amber-500 to-orange-600', dark: 'from-slate-800 to-slate-950',
}

const dateFormat = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
/** "Starts Oct 12, 2026", "Open enrollment", or "Ended" for a course whose dates are over. */
export function whenLabel(course: Pick<PublicCourse, 'startDate' | 'endDate'>, today = new Date()): string {
  const day = (value: string) => new Date(`${value}T00:00:00`)
  if (course.endDate && day(course.endDate) < today) return 'Ended'
  if (course.startDate && day(course.startDate) > today) return `Starts ${dateFormat.format(day(course.startDate))}`
  return 'Open enrollment'
}

export function seatsLabel(seatsLeft: number | null): string | null {
  if (seatsLeft === null) return null
  if (seatsLeft === 0) return 'Full: join the waitlist'
  return seatsLeft <= 10 ? `${seatsLeft} seat${seatsLeft === 1 ? '' : 's'} left` : null
}

export function CourseCard({ course, onOpen }: { course: PublicCourse; onOpen: (course: PublicCourse) => void }) {
  const seats = seatsLabel(course.seatsLeft)
  return (
    <button type="button" onClick={() => onOpen(course)} aria-label={`${course.title}. View details and apply.`}
      className="group flex w-64 shrink-0 snap-start flex-col overflow-hidden rounded-xl border border-border bg-card text-left shadow-sm transition hover:-translate-y-0.5 hover:shadow-lg focus-visible:outline-2 focus-visible:outline-primary sm:w-72">
      <div className={cn('relative flex h-32 items-end bg-gradient-to-br p-3 text-white', coverFor(course.category ?? course.title))} aria-hidden>
        <GraduationCap className="absolute right-3 top-3 h-7 w-7 opacity-40" />
        <span className="rounded bg-black/25 px-2 py-0.5 text-xs font-medium">{course.category ?? 'Course'}</span>
      </div>
      <div className="flex flex-1 flex-col gap-1.5 p-4">
        <span className="text-xs text-muted-foreground">{course.teacher ?? 'Course'}</span>
        <strong className="line-clamp-2 text-base leading-snug group-hover:text-primary">{course.title}</strong>
        <RatingLabel average={course.ratingAverage} count={course.ratingCount} />
        {course.summary ? <p className="line-clamp-2 text-sm text-muted-foreground">{course.summary}</p> : null}
        <div className="mt-auto flex flex-wrap items-center gap-x-3 gap-y-1 pt-2 text-xs text-muted-foreground">
          <span className="inline-flex items-center gap-1"><CalendarDays className="h-3.5 w-3.5" aria-hidden />{whenLabel(course)}</span>
          {seats ? <span className="inline-flex items-center gap-1 font-medium text-foreground"><UserRound className="h-3.5 w-3.5" aria-hidden />{seats}</span> : null}
        </div>
      </div>
    </button>
  )
}

/** A heading and a row of cards that scrolls sideways, with buttons for people who do not scroll by touch. */
export function CourseRow({ title, subtitle, courses, onOpen }: { title: string; subtitle?: string | null; courses: PublicCourse[]; onOpen: (course: PublicCourse) => void }) {
  const track = useRef<HTMLDivElement>(null)
  const scroll = (direction: 1 | -1) => track.current?.scrollBy?.({ left: direction * 320, behavior: 'smooth' })
  return (
    <section aria-label={title} className="flex flex-col gap-3">
      <div className="flex items-end justify-between gap-3">
        <div><h3 className="text-xl font-semibold">{title}</h3>{subtitle ? <p className="text-sm text-muted-foreground">{subtitle}</p> : null}</div>
        <div className="hidden gap-1.5 sm:flex">
          <Button type="button" variant="outline" size="icon" aria-label={`Scroll ${title} back`} onClick={() => scroll(-1)}><ChevronLeft className="h-4 w-4" /></Button>
          <Button type="button" variant="outline" size="icon" aria-label={`Scroll ${title} forward`} onClick={() => scroll(1)}><ChevronRight className="h-4 w-4" /></Button>
        </div>
      </div>
      <div ref={track} className="flex snap-x gap-4 overflow-x-auto pb-3 [scrollbar-width:thin]">
        {courses.map((course) => <CourseCard key={course.id} course={course} onOpen={onOpen} />)}
      </div>
    </section>
  )
}

/** The banners at the top: one at a time with arrows and dots; they turn over by themselves unless the visitor asked for less motion or is using them. */
export function BannerCarousel({ banners, onLink, slug }: { banners: LandingBanner[]; onLink: (link: string) => void; slug?: string }) {
  const [index, setIndex] = useState(0)
  const [paused, setPaused] = useState(false)
  const count = banners.length
  useEffect(() => {
    if (count < 2 || paused || (typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches)) return
    const timer = window.setInterval(() => setIndex((current) => (current + 1) % count), 7000)
    return () => window.clearInterval(timer)
  }, [count, paused])
  if (count === 0) return null
  const at = Math.min(index, count - 1)
  const banner = banners[at]
  const go = (next: number) => setIndex((next + count) % count)
  return (
    <section aria-label="Featured" aria-roledescription="carousel" className="relative" onMouseEnter={() => setPaused(true)} onMouseLeave={() => setPaused(false)} onFocus={() => setPaused(true)} onBlur={() => setPaused(false)}>
      <div className={cn('rounded-2xl bg-gradient-to-br p-8 text-white sm:p-10', bannerThemes[banner.theme] ?? bannerThemes.blue)} aria-live={paused ? 'polite' : 'off'} aria-label={`${at + 1} of ${count}`} role="group">
        <div className="flex items-center justify-between gap-8">
          <div className="flex max-w-2xl flex-col items-start gap-3">
            <h2 className="text-2xl font-bold leading-tight sm:text-3xl">{banner.title}</h2>
            {banner.text ? <p className="text-base text-white/90">{banner.text}</p> : null}
            {banner.buttonLabel && banner.link ? <Button type="button" className="mt-2 bg-white text-slate-900 hover:bg-white/90" onClick={() => onLink(banner.link)}>{banner.buttonLabel}</Button> : null}
          </div>
          {banner.imageId && slug ? <img src={landingImageUrl(slug, banner.imageId)} alt="" loading="lazy" className="hidden h-40 w-auto max-w-[40%] shrink-0 rounded-xl object-cover sm:block" /> : null}
        </div>
      </div>
      {count > 1 ? (
        <div className="mt-3 flex items-center justify-center gap-3">
          <Button type="button" variant="ghost" size="icon" aria-label="Previous banner" onClick={() => go(at - 1)}><ChevronLeft className="h-4 w-4" /></Button>
          <div className="flex gap-2">
            {banners.map((item, position) => <button key={item.id} type="button" aria-label={`Show banner ${position + 1}: ${item.title}`} aria-current={position === at ? 'true' : undefined} onClick={() => setIndex(position)} className={cn('h-2 rounded-full transition-all', position === at ? 'w-6 bg-primary' : 'w-2 bg-muted-foreground/40 hover:bg-muted-foreground')} />)}
          </div>
          <Button type="button" variant="ghost" size="icon" aria-label="Next banner" onClick={() => go(at + 1)}><ChevronRight className="h-4 w-4" /></Button>
        </div>
      ) : null}
    </section>
  )
}

export function FaqList({ items }: { items: LandingFaq[] }) {
  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col divide-y divide-border rounded-xl border border-border bg-card">
      {items.map((item) => (
        <details key={item.question} className="group px-5 py-4">
          <summary className="flex cursor-pointer list-none items-center justify-between gap-4 font-medium [&::-webkit-details-marker]:hidden">
            {item.question}<ChevronRight className="h-4 w-4 shrink-0 transition-transform group-open:rotate-90" aria-hidden />
          </summary>
          <p className="mt-3 whitespace-pre-line text-sm text-muted-foreground">{item.answer}</p>
        </details>
      ))}
    </div>
  )
}

export function SiteFooter({ name, about, groups, copyright, onLink, logoUrl }: { name: string; about: string; groups: LandingFooterGroup[]; copyright: string; onLink: (link: string) => void; logoUrl?: string }) {
  return (
    <footer className="border-t border-border bg-muted/30">
      <div className="mx-auto flex max-w-6xl flex-wrap gap-x-12 gap-y-8 px-4 py-10">
        <div className="flex min-w-60 flex-1 basis-72 flex-col gap-2">
          <span className="flex items-center gap-2 font-semibold">
            {logoUrl ? <img src={logoUrl} alt="" className="h-8 w-auto max-w-[8rem] rounded object-contain" /> : <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-primary text-primary-foreground"><GraduationCap className="h-4 w-4" /></span>}{name}
          </span>
          {about ? <p className="max-w-sm text-sm text-muted-foreground">{about}</p> : null}
        </div>
        {groups.map((group) => (
          <nav key={group.title} aria-label={group.title} className="flex min-w-36 flex-col gap-2 text-sm">
            <strong>{group.title}</strong>
            {group.links.map((link) => <button key={link.label} type="button" className="w-fit text-left text-muted-foreground hover:text-foreground hover:underline" onClick={() => onLink(link.url)}>{link.label}</button>)}
          </nav>
        ))}
      </div>
      {copyright ? <div className="border-t border-border py-4 text-center text-xs text-muted-foreground">{copyright}</div> : null}
    </footer>
  )
}
