import { useEffect, useState } from 'react'
import { EmptyState, ErrorBanner, Field, PageHeader } from '@/components/form'
import { Select } from '@/components/ui/select'
import { ApiError, apiRequest } from '@/lib/api'
import CourseEnrollmentPanel from './CourseEnrollmentPanel'

type Course = { id: string; code: string; title: string; status: string }

/** The place to put people in courses: choose the course, then choose the people. The same panel is on each course's Enrollment tab. */
export default function EnrollLearnersPage() {
  const [courses, setCourses] = useState<Course[] | null>(null)
  const [courseId, setCourseId] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    apiRequest<Course[]>('/api/v1/tenant/courses')
      .then((list) => {
        const usable = (Array.isArray(list) ? list : []).filter((course) => course.status !== 'Archived')
        setCourses(usable)
        // Published courses first: only those can have learners enrolled.
        const first = usable.find((course) => course.status === 'Published') ?? usable[0]
        if (first) setCourseId(first.id)
      })
      .catch((exception) => { setCourses([]); setError(exception instanceof ApiError ? exception.message : 'Unable to load the courses.') })
  }, [])

  const selected = courses?.find((course) => course.id === courseId)

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Enroll learners" description="Choose a course, then choose the people to put in it. Learners see a course, and its live classes, once they are enrolled in it." />
      <ErrorBanner message={error} />
      {courses === null ? <p className="text-sm text-muted-foreground">Loading the courses…</p> : courses.length === 0 ? <EmptyState>There are no courses yet. Create and publish one under Courses → Course authoring.</EmptyState> : (
        <>
          <Field id="enroll-course" label="Course" className="max-w-md">
            <Select id="enroll-course" value={courseId} onChange={(event) => setCourseId(event.target.value)}>
              {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}{course.status === 'Published' ? '' : ` (${course.status.toLowerCase()})`}</option>)}
            </Select>
          </Field>
          {selected ? <CourseEnrollmentPanel key={selected.id} courseId={selected.id} published={selected.status === 'Published'} /> : null}
        </>
      )}
    </section>
  )
}
