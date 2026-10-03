import { useCallback, useEffect, useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { EmptyState, ErrorBanner, ListRow, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ApiError, apiRequest } from '@/lib/api'
import { dayKey, describeStatus, describeWhen, isCompleted, itemDate, kindLabels, kindMarker, statusVariant, type TaskFeed, type TaskItem } from '@/lib/tasks'

type Group = { title: string; items: TaskItem[] }

/** Splits open work into Overdue / Today / This week / Later / No deadline. */
export function groupOpenTasks(items: TaskItem[], now = new Date()): Group[] {
  const today = dayKey(now)
  const weekEnd = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 7)
  const groups: Group[] = [
    { title: 'Overdue', items: [] }, { title: 'Today', items: [] }, { title: 'This week', items: [] },
    { title: 'Later', items: [] }, { title: 'No deadline', items: [] },
  ]
  for (const item of items) {
    const date = itemDate(item)
    if (item.status === 'Overdue') groups[0].items.push(item)
    else if (!date) groups[4].items.push(item)
    else if (dayKey(date) === today) groups[1].items.push(item)
    else if (date < weekEnd) groups[2].items.push(item)
    else groups[3].items.push(item)
  }
  return groups.filter((group) => group.items.length > 0)
}

/** Only the kinds of work that actually appear, in a fixed order, so the filter never offers an empty choice. */
export function kindsPresent(items: TaskItem[]): TaskItem['kind'][] {
  return (Object.keys(kindLabels) as TaskItem['kind'][]).filter((kind) => items.some((item) => item.kind === kind))
}

export default function MyTasksPage({ onNavigate }: { onNavigate: (menuId: string) => void }) {
  const [items, setItems] = useState<TaskItem[] | null>(null)
  const [kind, setKind] = useState<'all' | TaskItem['kind']>('all')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true); setError(null)
    try { setItems((await apiRequest<TaskFeed>('/api/v1/tenant/tasks')).items) }
    catch (exception) { setError(exception instanceof ApiError ? exception.message : 'Unable to load your tasks.') }
    finally { setLoading(false) }
  }, [])
  useEffect(() => { void load() }, [load])

  // A class that has already ended is history, not something to do (it stays on the calendar).
  const all = (items ?? []).filter((item) => !(item.kind === 'live-class' && item.status === 'Ended'))
  const kinds = kindsPresent(all)
  const matching = all.filter((item) => kind === 'all' || item.kind === kind)
  const open = matching.filter((item) => !isCompleted(item))
  const done = matching.filter(isCompleted)
  const groups = groupOpenTasks(open)
  const overdue = all.filter((item) => item.status === 'Overdue' && !isCompleted(item)).length

  const row = (item: TaskItem) => (
    <ListRow key={item.id} columns="sm:grid-cols-[minmax(0,1fr)_auto_auto]">
      <div className="flex min-w-0 items-center gap-3">
        <span aria-hidden className={`h-8 w-1 shrink-0 rounded-full ${kindMarker[item.kind]}`} />
        <span className="min-w-0 flex-1">
          <strong className="block truncate">{item.title}</strong>
          <small className="text-muted-foreground">{kindLabels[item.kind]}{item.courseTitle ? ` · ${item.courseTitle}` : ''} · {describeWhen(item)}</small>
        </span>
      </div>
      <div><Badge variant={statusVariant(item)}>{describeStatus(item)}</Badge></div>
      <div className="flex justify-end"><Button variant="soft" size="sm" aria-label={`Open ${item.title}`} onClick={() => onNavigate(item.target)}>Open</Button></div>
    </ListRow>
  )

  return (
    <section className="flex flex-col gap-4 text-foreground" aria-busy={loading}>
      <PageHeader title="My tasks" description="Everything that needs your attention, soonest first."
        actions={<>
          {overdue > 0 ? <Badge variant="destructive">{overdue} overdue</Badge> : null}
          <Button variant="outline" size="sm" disabled={loading} onClick={() => void load()}><RefreshCw className="mr-1 h-4 w-4" aria-hidden />Refresh</Button>
        </>} />
      <ErrorBanner message={error} />
      {items === null && !error ? <p className="text-sm text-muted-foreground">Loading your tasks…</p> : null}
      {items !== null ? (
        <>
          {kinds.length > 1 ? (
            <div className="flex flex-wrap gap-1.5" role="group" aria-label="Filter by type">
              <Button type="button" size="sm" variant={kind === 'all' ? 'secondary' : 'outline'} aria-pressed={kind === 'all'} onClick={() => setKind('all')}>All</Button>
              {kinds.map((item) => <Button key={item} type="button" size="sm" variant={kind === item ? 'secondary' : 'outline'} aria-pressed={kind === item} onClick={() => setKind(item)}>{kindLabels[item]}</Button>)}
            </div>
          ) : null}
          <Tabs defaultValue="todo">
            <TabsList>
              <TabsTrigger value="todo">To do ({open.length})</TabsTrigger>
              <TabsTrigger value="completed">Completed ({done.length})</TabsTrigger>
            </TabsList>
            <TabsContent value="todo">
              {groups.length === 0 ? <EmptyState>You are all caught up.</EmptyState> : groups.map((group) => (
                <Card key={group.title}>
                  <CardHeader><CardTitle className={group.title === 'Overdue' ? 'text-destructive' : undefined}>{group.title} ({group.items.length})</CardTitle></CardHeader>
                  <CardContent><RowList label={group.title}>{group.items.map(row)}</RowList></CardContent>
                </Card>
              ))}
            </TabsContent>
            <TabsContent value="completed">
              {done.length === 0 ? <EmptyState>Nothing completed in this period yet.</EmptyState> : <RowList label="Completed">{done.map(row)}</RowList>}
            </TabsContent>
          </Tabs>
        </>
      ) : null}
    </section>
  )
}
