import { useEffect, useState, type ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Progress } from '@/components/ui/progress'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useAuth } from '@/lib/auth'
import AssessmentsPage from './AssessmentsPage'
import AssignmentsPage from './AssignmentsPage'
import { CourseAnnouncements, CourseComingUp, CourseGrades } from './CourseHomePanels'
import CourseRatingPanel from './CourseRatingPanel'
import ForumsPage from './ForumsPage'
import LiveClassesPage from './LiveClassesPage'
import MessagesPage from './MessagesPage'
import VideoLibraryPage from './VideoLibraryPage'

export type CourseHomeTab = 'overview' | 'lessons' | 'assessments' | 'assignments' | 'live' | 'videos' | 'discussion' | 'grades'

type Props = {
  course: { id: string; code: string; title: string; description?: string | null }
  enrollment: { status: string; progressPercent: number }
  busy?: boolean
  onWithdraw: () => void
  /** The lessons of the course and the lesson being read (kept by the page that opens the course). */
  lessons: ReactNode
  /** The lesson to carry on with, for the Continue button. */
  continueLesson?: string | null
  initialTab?: CourseHomeTab
}

/**
 * Everything a learner does in one course, in one place: the lessons, the assessments and assignments, the live classes and videos, the discussion
 * and chat, and the grades and certificate. Each tab shows the existing page for just this course. A tab only appears when the person may use it.
 */
export default function CourseHome({ course, enrollment, busy = false, onWithdraw, lessons, continueLesson, initialTab = 'overview' }: Props) {
  const { session } = useAuth()
  const can = (permission: string) => session?.permissions.includes(permission) ?? false
  const [tab, setTab] = useState<string>(initialTab)
  const [talk, setTalk] = useState('forum')
  useEffect(() => { setTab(initialTab); setTalk('forum') }, [course.id, initialTab])

  const tabs: { id: CourseHomeTab; label: string; show: boolean }[] = [
    { id: 'overview', label: 'Overview', show: true },
    { id: 'lessons', label: 'Lessons', show: true },
    { id: 'assessments', label: 'Assessments', show: can('assessment.read') },
    { id: 'assignments', label: 'Assignments', show: can('assessment.read') },
    { id: 'live', label: 'Live classes', show: can('liveclass.read') },
    { id: 'videos', label: 'Videos', show: can('course.read') },
    { id: 'discussion', label: 'Discussion', show: can('collaboration.read') },
    { id: 'grades', label: 'Grades and certificate', show: can('grade.read') || can('certificate.read') },
  ]

  return (
    <div className="flex flex-col gap-4">
      <Card>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <div>
              <p className="text-xs text-muted-foreground">{course.code} · {enrollment.progressPercent}% complete</p>
              <CardTitle className="text-xl">{course.title}</CardTitle>
            </div>
            <div className="flex items-center gap-2">
              <Badge>{enrollment.status}</Badge>
              {enrollment.status === 'Active' ? <Button variant="softDestructive" size="sm" disabled={busy} onClick={onWithdraw}>Withdraw</Button> : null}
            </div>
          </div>
          <CardDescription>{course.description || 'Continue where you left off.'}</CardDescription>
        </CardHeader>
        <CardContent><Progress value={enrollment.progressPercent} /></CardContent>
      </Card>

      <Tabs value={tab} onValueChange={setTab}>
        <TabsList className="h-auto flex-wrap justify-start">
          {tabs.filter((item) => item.show).map((item) => <TabsTrigger key={item.id} value={item.id}>{item.label}</TabsTrigger>)}
        </TabsList>

        <TabsContent value="overview">
          <div className="flex flex-col gap-4">
            <div className="flex flex-wrap items-center gap-3 rounded-md border border-border p-4">
              <div className="min-w-0 flex-1">
                <h3 className="text-base font-semibold">{enrollment.progressPercent >= 100 ? 'You have finished this course' : 'Carry on learning'}</h3>
                <p className="text-sm text-muted-foreground">{continueLesson ? `Next: ${continueLesson}` : 'Open the lessons to start.'}</p>
              </div>
              <Button type="button" onClick={() => setTab('lessons')}>{enrollment.progressPercent >= 100 ? 'Review the lessons' : 'Continue'}</Button>
            </div>
            <CourseComingUp courseId={course.id} onGo={setTab} />
            <CourseAnnouncements courseId={course.id} limit={3} />
            <CourseRatingPanel key={course.id} courseId={course.id} />
          </div>
        </TabsContent>

        <TabsContent value="lessons">{lessons}</TabsContent>
        <TabsContent value="assessments"><AssessmentsPage embedded courseId={course.id} /></TabsContent>
        <TabsContent value="assignments"><AssignmentsPage embedded courseId={course.id} /></TabsContent>
        <TabsContent value="live"><LiveClassesPage embedded courseId={course.id} /></TabsContent>
        <TabsContent value="videos"><VideoLibraryPage embedded courseId={course.id} /></TabsContent>
        <TabsContent value="discussion">
          <Tabs value={talk} onValueChange={setTalk}>
            <TabsList>
              <TabsTrigger value="forum">Forum</TabsTrigger>
              <TabsTrigger value="chat">Course chat</TabsTrigger>
              <TabsTrigger value="news">Announcements</TabsTrigger>
            </TabsList>
            <TabsContent value="forum"><ForumsPage embedded courseId={course.id} /></TabsContent>
            <TabsContent value="chat"><MessagesPage courseId={course.id} /></TabsContent>
            <TabsContent value="news"><CourseAnnouncements courseId={course.id} /></TabsContent>
          </Tabs>
        </TabsContent>
        <TabsContent value="grades"><CourseGrades courseId={course.id} /></TabsContent>
      </Tabs>
    </div>
  )
}
