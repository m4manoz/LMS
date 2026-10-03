import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ApiError, apiRequest, downloadFile } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import GradeScalesEditor from './GradeScalesEditor'
import GradebookSetup from './GradebookSetup'
import { cn } from '@/lib/utils'

type Cell = { itemId: string; status: 'Graded' | 'Pending' | 'Missing'; score: number | null; maxPoints: number; percent: number | null; isLate: boolean }
type Item = { id: string; kind: string; title: string; maxPoints: number; dueAtUtc: string | null; categoryId?: string | null }
type BookCategory = { id: string; name: string; weightPercent: number }
type Learner = { userId: string; name: string; email: string; cells: Cell[]; earnedPoints: number; possiblePoints: number; overallPercent: number | null; categoryPercents?: (number | null)[]; letter?: string | null; passed?: boolean | null }
type CourseBook = { courseId: string; courseCode: string; courseTitle: string; items: Item[]; itemAverages: (number | null)[]; learners: Learner[]; classAverage: number | null; categories?: BookCategory[]; weighted?: boolean; scaleName?: string; passPercent?: number }
type MyRow = { kind: string; title: string; status: string; score: number | null; maxPoints: number; percent: number | null; isLate: boolean; feedback: string | null; categoryName?: string | null }
type MyCategoryResult = { name: string; weightPercent: number; percent: number | null }
type MyCourse = { courseId: string; courseCode: string; courseTitle: string; rows: MyRow[]; earnedPoints: number; possiblePoints: number; overallPercent: number | null; letter?: string | null; passed?: boolean | null; weighted?: boolean; categories?: MyCategoryResult[]; scaleName?: string }
type Course = { id: string; code: string; title: string; status: string }

/** Colour of a percentage so strong and weak results are easy to scan. */
export function percentTone(percent: number | null): string {
  if (percent === null) return 'text-muted-foreground'
  if (percent >= 80) return 'text-emerald-400'
  if (percent >= 50) return 'text-foreground'
  return 'text-destructive'
}

export const formatPercent = (value: number | null) => (value === null ? '—' : `${value % 1 === 0 ? value : value.toFixed(1)}%`)

export default function GradebookPage() {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('grade.manage') ?? false
  const [error, setError] = useState<string | null>(null)

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <div>
        <h2 className="text-2xl font-semibold">Gradebook</h2>
        <p className="text-sm text-muted-foreground">
          {canManage ? 'Every learner’s results across assignments and assessments.' : 'Your results across assignments and assessments.'}
        </p>
      </div>
      {error ? <div role="alert" className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">{error}</div> : null}
      {canManage ? <ClassBook onError={setError} /> : <MyGrades onError={setError} />}
    </section>
  )
}

function ClassBook({ onError }: { onError: (message: string | null) => void }) {
  const [courses, setCourses] = useState<Course[]>([])
  const [courseId, setCourseId] = useState('')
  const [book, setBook] = useState<CourseBook | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    apiRequest<Course[]>('/api/v1/tenant/courses').then((items) => {
      const published = items.filter((course) => course.status === 'Published')
      setCourses(published)
      if (published[0]) setCourseId(published[0].id)
    }).catch((exception) => onError(readError(exception, 'Unable to load courses.')))
  }, [])

  const reload = useCallback(async () => {
    if (!courseId) { setBook(null); return }
    onError(null)
    try { setBook(await apiRequest<CourseBook>(`/api/v1/tenant/gradebook/courses/${courseId}`)) }
    catch (exception) { onError(readError(exception, 'Unable to load the gradebook.')) }
  }, [courseId])
  useEffect(() => { void reload() }, [reload])

  async function exportCsv() {
    if (!book) return
    setBusy(true); onError(null)
    try { await downloadFile(`/api/v1/tenant/gradebook/courses/${book.courseId}/export.csv`, `gradebook-${book.courseCode}.csv`) }
    catch (exception) { onError(readError(exception, 'Unable to export the gradebook.')) }
    finally { setBusy(false) }
  }

  return (
    <Tabs defaultValue="class">
      <TabsList>
        <TabsTrigger value="class">Class gradebook</TabsTrigger>
        <TabsTrigger value="setup">Grading setup</TabsTrigger>
        <TabsTrigger value="scales">Grade scales</TabsTrigger>
      </TabsList>
      <TabsContent value="setup">
        {courseId ? <GradebookSetup courseId={courseId} onSaved={() => void reload()} /> : <Card><CardContent className="pt-5 text-sm text-muted-foreground">Choose a course on the Class gradebook tab first.</CardContent></Card>}
      </TabsContent>
      <TabsContent value="scales"><GradeScalesEditor /></TabsContent>
      <TabsContent value="class">
        <Card>
          <CardContent className="flex flex-wrap items-end justify-between gap-3 pt-5">
            <div className="flex min-w-60 flex-col gap-1.5">
              <Label htmlFor="gradebook-course">Course</Label>
              <Select id="gradebook-course" value={courseId} onChange={(e) => setCourseId(e.target.value)}>
                <option value="">Choose a published course</option>
                {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
              </Select>
            </div>
            <Button variant="outline" disabled={!book || busy} onClick={() => void exportCsv()}>Export CSV</Button>
          </CardContent>
        </Card>

        {!book ? (
          <Card><CardContent className="pt-5 text-sm text-muted-foreground">Choose a course to see its grades.</CardContent></Card>
        ) : book.items.length === 0 ? (
          <Card><CardContent className="pt-5 text-sm text-muted-foreground">This course has no published assignments or assessments yet.</CardContent></Card>
        ) : (
          <Card>
            <CardHeader>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <CardTitle>{book.courseTitle}</CardTitle>
                <Badge variant="outline">Class average {formatPercent(book.classAverage)}</Badge>
              </div>
              <CardDescription>
                {book.weighted
                  ? `Weighted by category (${(book.categories ?? []).map((category) => `${category.name} ${category.weightPercent}%`).join(', ')}). A category with no graded work yet is left out until it has some.`
                  : 'Overall = points earned ÷ points possible across graded work.'}{' '}
                Pending work is submitted but not graded; Missing means nothing was submitted. Grades use the “{book.scaleName ?? 'Standard'}” scale; pass mark {book.passPercent ?? 50}%.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <div className="overflow-x-auto rounded-md border border-border">
                <table className="w-full min-w-max border-collapse text-sm">
                  <thead>
                    <tr className="bg-muted/40 text-left">
                      <th scope="col" className="sticky left-0 z-10 min-w-44 bg-card px-3 py-2 font-medium">Learner</th>
                      {book.items.map((item) => (
                        <th key={item.id} scope="col" className="min-w-28 px-3 py-2 font-medium">
                          <span className="block max-w-40 truncate">{item.title}</span>
                          <small className="font-normal text-muted-foreground">{item.kind === 'assignment' ? 'Assignment' : 'Assessment'} · {item.maxPoints} pts</small>
                        </th>
                      ))}
                      {(book.categories ?? []).map((category) => <th key={category.id} scope="col" className="min-w-24 border-l border-border px-3 py-2 font-medium">{category.name}<small className="block font-normal text-muted-foreground">{category.weightPercent}%</small></th>)}
                      <th scope="col" className="min-w-24 border-l border-border px-3 py-2 font-medium">Overall</th>
                      <th scope="col" className="min-w-24 px-3 py-2 font-medium">Grade</th>
                    </tr>
                  </thead>
                  <tbody>
                    {book.learners.length === 0 ? (
                      <tr><td colSpan={book.items.length + (book.categories ?? []).length + 3} className="px-3 py-4 text-muted-foreground">No learners are enrolled yet.</td></tr>
                    ) : book.learners.map((learner) => (
                      <tr key={learner.userId} className="border-t border-border">
                        <th scope="row" className="sticky left-0 z-10 bg-card px-3 py-2 text-left font-medium">
                          <span className="block">{learner.name}</span>
                          <small className="font-normal text-muted-foreground">{learner.email}</small>
                        </th>
                        {learner.cells.map((cell) => (
                          <td key={cell.itemId} className="px-3 py-2">
                            {cell.status === 'Graded' ? (
                              <span className={cn('font-medium', percentTone(cell.percent))}>
                                {cell.score}<span className="text-muted-foreground"> / {cell.maxPoints}</span>
                                {cell.isLate ? <span className="ml-1.5 text-xs text-destructive">late</span> : null}
                              </span>
                            ) : cell.status === 'Pending' ? <span className="text-xs text-amber-400">Pending</span> : <span className="text-muted-foreground">—</span>}
                          </td>
                        ))}
                        {(book.categories ?? []).map((category, index) => <td key={category.id} className={cn('border-l border-border px-3 py-2', percentTone(learner.categoryPercents?.[index] ?? null))}>{formatPercent(learner.categoryPercents?.[index] ?? null)}</td>)}
                        <td className={cn('border-l border-border px-3 py-2 font-semibold', percentTone(learner.overallPercent))}>{formatPercent(learner.overallPercent)}</td>
                        <td className="px-3 py-2">
                          {learner.letter ? <span className="flex items-center gap-1.5"><strong>{learner.letter}</strong>{learner.passed === false ? <Badge variant="destructive">Below pass</Badge> : null}</span> : <span className="text-muted-foreground">—</span>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot>
                    <tr className="border-t-2 border-border bg-muted/40">
                      <th scope="row" className="sticky left-0 z-10 bg-card px-3 py-2 text-left font-medium">Class average</th>
                      {book.itemAverages.map((average, index) => <td key={book.items[index].id} className={cn('px-3 py-2 font-medium', percentTone(average))}>{formatPercent(average)}</td>)}
                      {(book.categories ?? []).map((category) => <td key={category.id} className="border-l border-border px-3 py-2" />)}
                      <td className={cn('border-l border-border px-3 py-2 font-semibold', percentTone(book.classAverage))}>{formatPercent(book.classAverage)}</td>
                      <td className="px-3 py-2" />
                    </tr>
                  </tfoot>
                </table>
              </div>
            </CardContent>
          </Card>
        )}
      </TabsContent>
    </Tabs>
  )
}

function MyGrades({ onError }: { onError: (message: string | null) => void }) {
  const [courses, setCourses] = useState<MyCourse[] | null>(null)

  useEffect(() => {
    apiRequest<MyCourse[]>('/api/v1/tenant/gradebook/me').then(setCourses)
      .catch((exception) => onError(readError(exception, 'Unable to load your grades.')))
  }, [])

  return (
    <Tabs defaultValue="mine">
      <TabsList><TabsTrigger value="mine">My grades</TabsTrigger></TabsList>
      <TabsContent value="mine">
        {courses === null ? <p className="text-sm text-muted-foreground">Loading your grades…</p> : courses.length === 0 ? (
          <Card><CardContent className="pt-5 text-sm text-muted-foreground">You are not enrolled in a course with graded work yet.</CardContent></Card>
        ) : courses.map((course) => (
          <Card key={course.courseId}>
            <CardHeader>
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div>
                  <p className="text-xs text-muted-foreground">{course.courseCode}</p>
                  <CardTitle>{course.courseTitle}</CardTitle>
                </div>
                <div className="text-right">
                  <strong className={cn('text-2xl', percentTone(course.overallPercent))}>{formatPercent(course.overallPercent)}{course.letter ? ` · ${course.letter}` : ''}</strong>
                  <small className="block text-muted-foreground">{course.earnedPoints} / {course.possiblePoints} graded points</small>
                  {course.passed === null || course.passed === undefined ? null : <Badge variant={course.passed ? 'default' : 'destructive'}>{course.passed ? 'Passing' : 'Below pass mark'}</Badge>}
                </div>
              </div>
              {course.weighted && course.categories && course.categories.length > 0 ? (
                <ul className="mt-2 flex flex-wrap gap-2 text-sm">
                  {course.categories.map((category) => (
                    <li key={category.name} className="rounded-md border border-border px-2.5 py-1">
                      <strong>{category.name}</strong> <span className="text-muted-foreground">{category.weightPercent}% of grade</span> · <span className={percentTone(category.percent)}>{formatPercent(category.percent)}</span>
                    </li>
                  ))}
                </ul>
              ) : null}
            </CardHeader>
            <CardContent className="flex flex-col gap-2">
              {course.rows.length === 0 ? <p className="text-sm text-muted-foreground">No graded work in this course yet.</p> : course.rows.map((row, index) => (
                <div key={`${row.title}-${index}`} className="flex flex-col gap-1 rounded-md border border-border px-3 py-2 text-sm">
                  <div className="flex items-center justify-between gap-3">
                    <span className="min-w-0"><strong className="block truncate">{row.title}</strong><small className="text-muted-foreground">{row.kind === 'assignment' ? 'Assignment' : 'Assessment'}{row.categoryName ? ` · ${row.categoryName}` : ''}</small></span>
                    <span className="flex items-center gap-2">
                      {row.isLate ? <Badge variant="destructive">Late</Badge> : null}
                      {row.status === 'Graded'
                        ? <strong className={percentTone(row.percent)}>{row.score} / {row.maxPoints} · {formatPercent(row.percent)}</strong>
                        : <Badge variant={row.status === 'Pending' ? 'default' : 'secondary'}>{row.status === 'Missing' ? 'Not submitted' : 'Awaiting grade'}</Badge>}
                    </span>
                  </div>
                  {row.feedback ? <p className="text-muted-foreground">Feedback: {row.feedback}</p> : null}
                </div>
              ))}
            </CardContent>
          </Card>
        ))}
      </TabsContent>
    </Tabs>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
