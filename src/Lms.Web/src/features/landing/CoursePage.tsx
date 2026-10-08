import { useEffect } from 'react'
import type { PublicCourse } from '@/lib/publicApi'
import { siteHref } from '@/lib/siteRoutes'
import { CourseView } from './CourseDialog'
import { CourseCard } from './parts'

/** What to suggest after a course: others in its category first, then the newest, never the course itself. */
export function relatedCourses(courses: PublicCourse[], course: PublicCourse, limit = 4): PublicCourse[] {
  const others = courses.filter((item) => item.id !== course.id)
  const same = course.categoryId ? others.filter((item) => item.categoryId === course.categoryId) : []
  return [...same, ...others.filter((item) => !same.includes(item))].slice(0, limit)
}

/** One course on a page of its own, with its address, so it can be shared. Applying works exactly as in the panel. */
export default function CoursePage({ organization, organizationName, courses, courseId, onOpen, onBack, onLogin }: {
  organization: string; organizationName: string; courses: PublicCourse[]; courseId: string
  onOpen: (course: PublicCourse) => void; onBack: () => void; onLogin: () => void
}) {
  const course = courses.find((item) => item.id === courseId) ?? null
  // The tab says which course this is, and goes back to the organization's name when the visitor leaves.
  useEffect(() => {
    const before = document.title
    document.title = course ? `${course.title} · ${organizationName}` : organizationName
    return () => { document.title = before }
  }, [course, organizationName])

  if (!course) {
    return (
      <main className="mx-auto flex max-w-3xl flex-col items-start gap-4 px-4 py-20">
        <h1 className="text-3xl font-bold">That course is not available</h1>
        <p className="text-muted-foreground">It may have been closed or moved. The courses that are open are all in the catalog.</p>
        <a className="font-medium text-primary hover:underline" href={siteHref({ page: 'courses', category: null, q: '', sort: 'newest' })}>See all courses</a>
      </main>
    )
  }

  const related = relatedCourses(courses, course)
  return (
    <main className="mx-auto flex max-w-4xl flex-col gap-10 px-4 py-10">
      <nav aria-label="Breadcrumb" className="text-sm text-muted-foreground">
        <a className="hover:underline" href={siteHref({ page: 'home' })}>Home</a> / <a className="hover:underline" href={siteHref({ page: 'courses', category: null, q: '', sort: 'newest' })}>Courses</a> / <span aria-current="page">{course.title}</span>
      </nav>
      <CourseView key={course.id} organization={organization} course={course} onClose={onBack} onLogin={onLogin} />
      {related.length > 0 ? (
        <section aria-label="More courses" className="flex flex-col gap-3">
          <h2 className="text-xl font-semibold">{course.categoryId && related.some((item) => item.categoryId === course.categoryId) ? `More in ${course.category}` : 'More courses'}</h2>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">{related.map((item) => <div key={item.id} className="flex [&>button]:w-full"><CourseCard course={item} onOpen={onOpen} /></div>)}</div>
        </section>
      ) : null}
    </main>
  )
}
