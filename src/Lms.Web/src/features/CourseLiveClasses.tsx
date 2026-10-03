import { useEffect, useState } from 'react'
import { LogIn, Video } from 'lucide-react'
import { EmptyState, ErrorBanner } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ApiError, apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { providerName } from './LiveClassesPage'

type ClassItem = { id: string; courseId: string | null; title: string; description: string | null; provider: string; startAtUtc: string; endAtUtc: string; status: string }

/** The classes of one course, in the order a learner wants them: on now, coming up, then the ones that have happened. */
export function arrangeClasses(items: ClassItem[], courseId: string): { now: ClassItem[]; upcoming: ClassItem[]; past: ClassItem[] } {
  const mine = items.filter((item) => item.courseId === courseId && item.status !== 'Cancelled')
  const open = (item: ClassItem) => item.status === 'Scheduled' || item.status === 'Live'
  const byStart = (a: ClassItem, b: ClassItem) => new Date(a.startAtUtc).getTime() - new Date(b.startAtUtc).getTime()
  return {
    now: mine.filter((item) => item.status === 'Live').sort(byStart),
    upcoming: mine.filter((item) => item.status === 'Scheduled').sort(byStart),
    past: mine.filter((item) => !open(item)).sort((a, b) => byStart(b, a)),
  }
}

const when = (item: ClassItem) => `${new Date(item.startAtUtc).toLocaleString()} – ${new Date(item.endAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`

/** The live classes of a course, shown inside the course. "Open class" goes to the class, where it can be joined. */
export default function CourseLiveClasses({ courseId, onOpen, canSchedule = false }: { courseId: string; onOpen?: (sessionId: string) => void; canSchedule?: boolean }) {
  const { session } = useAuth()
  const allowed = session?.permissions.includes('liveclass.read') ?? false
  const [items, setItems] = useState<ClassItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!allowed) return
    let current = true
    setItems(null); setError(null)
    apiRequest<ClassItem[]>('/api/v1/tenant/live-classes/sessions')
      .then((list) => { if (current) setItems(Array.isArray(list) ? list : []) })
      .catch((exception) => { if (current) { setItems([]); setError(exception instanceof ApiError ? exception.message : 'Unable to load the live classes.') } })
    return () => { current = false }
  }, [courseId, allowed])

  if (!allowed) return <EmptyState>Live classes are not available to your account.</EmptyState>
  if (items === null) return <p className="text-sm text-muted-foreground">Loading the live classes…</p>
  const { now, upcoming, past } = arrangeClasses(items, courseId)
  const none = now.length + upcoming.length + past.length === 0

  const row = (item: ClassItem, open: boolean) => (
    <li key={item.id} className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-border px-3 py-2">
      <div className="min-w-0">
        <strong className="flex items-center gap-2"><Video className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden /><span className="truncate">{item.title}</span></strong>
        <small className="text-muted-foreground">{when(item)}</small>
      </div>
      <div className="flex items-center gap-2">
        <Badge variant={item.status === 'Live' ? 'default' : 'secondary'}>{item.status}</Badge>
        <Badge variant="outline">{providerName(item.provider)}</Badge>
        {onOpen ? <Button type="button" size="sm" variant={open ? 'default' : 'outline'} aria-label={`${open ? 'Open class' : 'View details of'} ${item.title}`} onClick={() => onOpen(item.id)}>{open ? <LogIn className="mr-1.5 h-4 w-4" aria-hidden /> : null}{open ? 'Open class' : 'Details'}</Button> : null}
      </div>
    </li>
  )

  return (
    <div className="flex flex-col gap-4">
      <ErrorBanner message={error} />
      {none ? <EmptyState>{canSchedule ? 'No live classes for this course yet. Schedule one under Classes and learners → Schedule class and choose this course.' : 'No live classes for this course yet.'}</EmptyState> : null}
      {now.length > 0 ? <section aria-label="On now" className="flex flex-col gap-2"><h4 className="text-sm font-semibold">On now</h4><ul className="flex flex-col gap-2">{now.map((item) => row(item, true))}</ul></section> : null}
      {upcoming.length > 0 ? <section aria-label="Coming up" className="flex flex-col gap-2"><h4 className="text-sm font-semibold">Coming up</h4><ul className="flex flex-col gap-2">{upcoming.map((item) => row(item, true))}</ul></section> : null}
      {past.length > 0 ? <section aria-label="Earlier classes" className="flex flex-col gap-2"><h4 className="text-sm font-semibold">Earlier</h4><ul className="flex flex-col gap-2">{past.slice(0, 10).map((item) => row(item, false))}</ul></section> : null}
    </div>
  )
}
