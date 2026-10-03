import { useEffect, useState } from 'react'
import { BookOpen, ClipboardCheck, Video } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { apiRequest, type StoredSession } from '@/lib/api'

type Shortcut = { id: string; title: string; description: string; permission: string; icon: typeof BookOpen }

const shortcuts: Shortcut[] = [
  { id: 'learning', title: 'My learning', description: 'Resume your enrolled courses.', permission: 'enrollment.read', icon: BookOpen },
  { id: 'live', title: 'Live classes', description: 'Join or schedule online sessions.', permission: 'liveclass.read', icon: Video },
  { id: 'quizzes', title: 'Assessments', description: 'Take quizzes and review grades.', permission: 'assessment.read', icon: ClipboardCheck },
]

type DashboardData = {
  learning: { activeEnrollments: number; completedEnrollments: number }
  upcomingSessions: { id: string; title: string; startAtUtc: string; endAtUtc: string; status: string }[]
  teaching: { attemptsToGrade: number } | null
  admin: { activeEnrollments: number; publishedCourses: number } | null
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <Card>
      <CardHeader>
        <CardDescription>{label}</CardDescription>
        <CardTitle className="text-3xl">{value}</CardTitle>
      </CardHeader>
    </Card>
  )
}

export default function DashboardPage({ session, onNavigate }: { session: StoredSession; onNavigate: (id: string) => void }) {
  const [data, setData] = useState<DashboardData | null>(null)
  const [error, setError] = useState(false)
  useEffect(() => {
    apiRequest<DashboardData>('/api/v1/tenant/dashboard').then(setData).catch(() => setError(true))
  }, [])
  const available = shortcuts.filter((item) => session.permissions.includes(item.permission))
  return (
    <div className="flex flex-col gap-6">
      <div>
        <p className="text-sm text-muted-foreground">Signed in as {session.role}</p>
        <h2 className="text-2xl font-semibold">Welcome, {session.user.displayName}</h2>
      </div>
      {error ? <p role="alert" className="text-sm text-destructive">Dashboard data could not be loaded.</p> : null}
      {data ? (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <Stat label="Courses in progress" value={data.learning.activeEnrollments} />
          <Stat label="Courses completed" value={data.learning.completedEnrollments} />
          {data.teaching ? <Stat label="Attempts to grade" value={data.teaching.attemptsToGrade} /> : null}
          {data.admin ? <Stat label="Active enrollments (all)" value={data.admin.activeEnrollments} /> : null}
          {data.admin ? <Stat label="Published courses" value={data.admin.publishedCourses} /> : null}
        </div>
      ) : null}
      {data && data.upcomingSessions.length > 0 ? (
        <Card>
          <CardHeader>
            <CardTitle>Upcoming live classes</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-2">
            {data.upcomingSessions.map((item) => (
              <button key={item.id} type="button" onClick={() => onNavigate('live')} className="flex items-center justify-between rounded-md border border-border px-3 py-2 text-left text-sm hover:bg-muted">
                <span className="font-medium">{item.title}</span>
                <span className="text-muted-foreground">{new Date(item.startAtUtc).toLocaleString()}</span>
              </button>
            ))}
          </CardContent>
        </Card>
      ) : null}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {available.map(({ id, title, description, icon: Icon }) => (
          <button key={id} type="button" className="text-left" onClick={() => onNavigate(id)}>
            <Card className="h-full transition-colors hover:border-primary">
              <CardHeader>
                <Icon className="mb-2 h-5 w-5 text-primary" />
                <CardTitle>{title}</CardTitle>
                <CardDescription>{description}</CardDescription>
              </CardHeader>
            </Card>
          </button>
        ))}
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Your access</CardTitle>
          <CardDescription>Permissions granted by your roles.</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          {session.permissions.map((permission) => (
            <span key={permission} className="rounded-full bg-muted px-2.5 py-0.5 text-xs text-muted-foreground">{permission}</span>
          ))}
        </CardContent>
      </Card>
    </div>
  )
}
