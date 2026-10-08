import { useEffect, useMemo, useState } from 'react'
import { Search } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import type { PublicCategory, PublicCourse } from '@/lib/publicApi'
import { catalogRoute, siteHref, sortKeys, sortLabels, type SiteRoute, type SortKey } from '@/lib/siteRoutes'
import { cn } from '@/lib/utils'
import { CourseCard } from './parts'

type CatalogRoute = Extract<SiteRoute, { page: 'courses' }>

/** The courses that match the words typed (every word must appear) and the chosen category, in the chosen order. */
export function filterCourses(courses: PublicCourse[], { category, q, sort }: { category: string | null; q: string; sort: SortKey }): PublicCourse[] {
  const words = q.trim().toLowerCase().split(/\s+/).filter(Boolean)
  const found = courses.filter((course) => (category === null || course.categoryId === category)
    && words.every((word) => `${course.title} ${course.code} ${course.summary} ${course.category ?? ''} ${course.teacher ?? ''}`.toLowerCase().includes(word)))
  if (sort === 'title') return [...found].sort((a, b) => a.title.localeCompare(b.title))
  if (sort === 'rating') {
    // Rated courses first, best first; among equals the one more people rated; unrated courses keep their (newest first) order at the end.
    return [...found].sort((a, b) => (b.ratingAverage ?? -1) - (a.ratingAverage ?? -1) || (b.ratingCount ?? 0) - (a.ratingCount ?? 0))
  }
  return found   // the server sends courses newest first
}

/** Every course of the organization, with search, categories and ordering. The filters live in the address, so a filtered list can be shared. */
export default function CatalogPage({ courses, categories, route, onRoute, onOpen }: {
  courses: PublicCourse[]; categories: PublicCategory[]; route: CatalogRoute
  onRoute: (route: SiteRoute, replace?: boolean) => void; onOpen: (course: PublicCourse) => void
}) {
  const [text, setText] = useState(route.q)
  useEffect(() => setText(route.q), [route.q])
  // Typing updates the address a moment after the last key, replacing rather than adding history entries.
  useEffect(() => {
    if (text === route.q) return
    const timer = window.setTimeout(() => onRoute({ ...route, q: text }, true), 250)
    return () => window.clearTimeout(timer)
  }, [text])

  const shown = useMemo(() => filterCourses(courses, { category: route.category, q: text, sort: route.sort }), [courses, route.category, route.sort, text])
  const chosen = categories.find((item) => item.id === route.category)
  const filtering = route.category !== null || text.trim() !== ''

  return (
    <main className="mx-auto flex max-w-6xl flex-col gap-6 px-4 py-10">
      <nav aria-label="Breadcrumb" className="text-sm text-muted-foreground"><a className="hover:underline" href={siteHref({ page: 'home' })}>Home</a> / <span aria-current="page">Courses</span></nav>
      <div>
        <h1 className="text-3xl font-bold tracking-tight">{chosen ? chosen.name : 'All courses'}</h1>
        <p className="text-muted-foreground">Find a course, read what it covers and apply.</p>
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <div className="relative min-w-60 flex-1 sm:max-w-md">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
          <Input type="search" aria-label="Search the catalog" className="h-10 pl-9" placeholder="Search by title, topic or teacher" value={text} onChange={(event) => setText(event.target.value)} />
        </div>
        <label className="flex items-center gap-2 text-sm">Sort by
          <Select aria-label="Sort courses" className="w-44" value={route.sort} onChange={(event) => onRoute({ ...route, sort: event.target.value as SortKey })}>
            {sortKeys.map((key) => <option key={key} value={key}>{sortLabels[key]}</option>)}
          </Select>
        </label>
      </div>

      {categories.length > 0 ? (
        <div role="group" aria-label="Categories" className="flex flex-wrap gap-2">
          <button type="button" aria-pressed={route.category === null} onClick={() => onRoute({ ...route, category: null })} className={chip(route.category === null)}>All</button>
          {categories.map((item) => <button key={item.id} type="button" aria-pressed={route.category === item.id} onClick={() => onRoute({ ...route, category: item.id })} className={chip(route.category === item.id)}>{item.name} <small className="opacity-70">{item.courses}</small></button>)}
        </div>
      ) : null}

      <div className="flex flex-wrap items-center justify-between gap-2">
        <p role="status" className="text-sm text-muted-foreground">{shown.length} course{shown.length === 1 ? '' : 's'}{chosen ? ` in ${chosen.name}` : ''}{text.trim() ? ` for “${text.trim()}”` : ''}</p>
        {filtering ? <button type="button" className="text-sm font-medium text-primary hover:underline" onClick={() => { setText(''); onRoute(catalogRoute({ sort: route.sort })) }}>Clear filters</button> : null}
      </div>

      {courses.length === 0 ? <p className="rounded-lg border border-dashed border-border p-8 text-center text-muted-foreground">No courses are open yet. Please check back soon.</p>
        : shown.length === 0 ? <p className="rounded-lg border border-dashed border-border p-8 text-center text-muted-foreground">No course matches. Try another word, or clear the filters to see everything.</p>
          : <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">{shown.map((course) => <div key={course.id} className="flex [&>button]:w-full"><CourseCard course={course} onOpen={onOpen} /></div>)}</div>}
    </main>
  )
}

const chip = (active: boolean) => cn('rounded-full border px-4 py-1.5 text-sm font-medium transition', active ? 'border-primary bg-primary text-primary-foreground' : 'border-border bg-card hover:border-primary hover:text-primary')
