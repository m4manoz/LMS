import { useEffect, useMemo, useState } from 'react'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { EmptyState, ErrorBanner, ListRow, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ApiError, apiRequest } from '@/lib/api'
import { buildMonthGrid, shiftMonth, startOfDay, WEEKDAYS } from '@/lib/calendarGrid'
import { useCalendarSettings } from '@/lib/calendarSettings'
import { dayKey, describeStatus, describeWhen, itemDate, kindLabels, kindMarker, statusVariant, type TaskFeed, type TaskItem } from '@/lib/tasks'
import { cn } from '@/lib/utils'

/** The 6-week grid (Sunday first) that contains the given month, as plain dates (AD). */
export function monthGrid(cursor: Date): Date[] {
  return buildMonthGrid('AD', cursor).cells.map((cell) => cell.date)
}

/** Groups dated items by local day. Items without a date are left out. */
export function itemsByDay(items: TaskItem[]): Map<string, TaskItem[]> {
  const map = new Map<string, TaskItem[]>()
  for (const item of items) {
    const date = itemDate(item)
    if (!date) continue
    const key = dayKey(date)
    map.set(key, [...(map.get(key) ?? []), item])
  }
  return map
}

const UPCOMING_DAYS = 30

/** Loads the task feed for a range. The caller ignores the result if it has been superseded. */
async function loadFeed(from: Date, to: Date): Promise<TaskItem[]> {
  const feed = await apiRequest<TaskFeed>(`/api/v1/tenant/tasks?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`)
  return feed.items
}

const endOfDay = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate(), 23, 59, 59)
const readError = (exception: unknown) => (exception instanceof ApiError ? exception.message : 'Unable to load the calendar.')

export default function CalendarPage({ onNavigate }: { onNavigate: (menuId: string) => void }) {
  const { mode } = useCalendarSettings()
  const [tab, setTab] = useState('month')
  const [anchor, setAnchor] = useState(() => new Date())
  const [selected, setSelected] = useState(() => dayKey(new Date()))
  const [items, setItems] = useState<TaskItem[]>([])
  const [upcoming, setUpcoming] = useState<TaskItem[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const grid = useMemo(() => buildMonthGrid(mode, anchor), [mode, anchor.getFullYear(), anchor.getMonth(), anchor.getDate()])
  const byDay = useMemo(() => itemsByDay(items), [items])
  const today = dayKey(new Date())

  // The month on screen. If the person moves on before an answer arrives, that answer is thrown away.
  useEffect(() => {
    if (tab === 'upcoming') return undefined
    let current = true
    setLoading(true); setError(null)
    loadFeed(grid.from, endOfDay(grid.to))
      .then((feed) => { if (current) setItems(feed) })
      .catch((exception) => { if (current) setError(readError(exception)) })
      .finally(() => { if (current) setLoading(false) })
    return () => { current = false }
  }, [grid, tab === 'upcoming'])

  // The next 30 days, whatever month is on screen.
  useEffect(() => {
    if (tab !== 'upcoming') return undefined
    let current = true
    setLoading(true); setError(null)
    const now = new Date()
    loadFeed(startOfDay(now), endOfDay(startOfDay(now, UPCOMING_DAYS - 1)))
      .then((feed) => { if (current) setUpcoming(feed) })
      .catch((exception) => { if (current) setError(readError(exception)) })
      .finally(() => { if (current) setLoading(false) })
    return () => { current = false }
  }, [tab])

  const move = (months: number) => { const next = shiftMonth(mode, anchor, months); setAnchor(next); setSelected(dayKey(next)) }
  const goToday = () => { const now = new Date(); setAnchor(now); setSelected(dayKey(now)) }
  const dayItems = byDay.get(selected) ?? []
  const agenda = [...byDay.entries()].sort(([a], [b]) => a.localeCompare(b))
  const upcomingByDay = [...itemsByDay(upcoming).entries()].sort(([a], [b]) => a.localeCompare(b))
  const fullDate = (key: string) => new Date(`${key}T00:00:00`).toLocaleDateString([], { dateStyle: 'full' })

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

  const dayCards = (entries: [string, TaskItem[]][]) => entries.map(([key, list]) => (
    <Card key={key}>
      <CardHeader><CardTitle className="text-base">{fullDate(key)}</CardTitle></CardHeader>
      <CardContent><RowList label={fullDate(key)}>{list.map(row)}</RowList></CardContent>
    </Card>
  ))

  return (
    <section className="flex flex-col gap-4 text-foreground" aria-busy={loading}>
      <PageHeader title="Calendar" description={`Deadlines, live classes and grading work by date. Dates follow your ${mode} calendar setting.`}
        actions={tab === 'upcoming' ? undefined : (
          <>
            <Button variant="outline" size="icon" aria-label="Previous month" onClick={() => move(-1)}><ChevronLeft className="h-4 w-4" /></Button>
            <strong className="min-w-40 text-center text-sm" aria-live="polite">{grid.title}</strong>
            <Button variant="outline" size="icon" aria-label="Next month" onClick={() => move(1)}><ChevronRight className="h-4 w-4" /></Button>
            <Button variant="outline" onClick={goToday}>Today</Button>
          </>
        )} />
      <ErrorBanner message={error} />

      <Tabs value={tab} onValueChange={setTab}>
        <TabsList>
          <TabsTrigger value="month">Month</TabsTrigger>
          <TabsTrigger value="agenda">Agenda</TabsTrigger>
          <TabsTrigger value="upcoming">Next {UPCOMING_DAYS} days</TabsTrigger>
          {loading ? <span role="status" className="ml-auto self-center pb-2 text-xs text-muted-foreground">Loading…</span> : null}
        </TabsList>

        <TabsContent value="month">
          <Card>
            <CardContent className="pt-5">
              <div className="grid grid-cols-7 gap-px overflow-hidden rounded-md border border-border bg-border text-xs">
                {WEEKDAYS.map((day) => <div key={day} className="bg-card px-2 py-1.5 text-center font-medium text-muted-foreground">{day}</div>)}
                {grid.cells.map((cell) => {
                  const list = byDay.get(cell.key) ?? []
                  return (
                    <button
                      key={cell.key}
                      type="button"
                      aria-label={`${cell.date.toDateString()}, ${list.length} item${list.length === 1 ? '' : 's'}`}
                      aria-pressed={selected === cell.key}
                      onClick={() => setSelected(cell.key)}
                      className={cn('flex min-h-24 flex-col gap-1 bg-background p-1.5 text-left hover:bg-muted', !cell.inMonth && 'opacity-45', selected === cell.key && 'ring-2 ring-inset ring-primary')}
                    >
                      <span className="flex items-center justify-between">
                        <span className={cn('flex h-6 w-6 items-center justify-center rounded-full text-xs', cell.key === today && 'bg-primary font-semibold text-primary-foreground')}>{cell.label}</span>
                        {cell.secondary ? <span className="text-[10px] text-muted-foreground">{cell.secondary}{mode === 'BS' ? ' AD' : ' BS'}</span> : null}
                      </span>
                      {list.slice(0, 2).map((item) => (
                        <span key={item.id} className="flex items-center gap-1 truncate rounded bg-muted px-1 py-0.5">
                          <span aria-hidden className={`h-1.5 w-1.5 shrink-0 rounded-full ${kindMarker[item.kind]}`} />
                          <span className="truncate">{item.title}</span>
                        </span>
                      ))}
                      {list.length > 2 ? <span className="text-[10px] text-muted-foreground">+{list.length - 2} more</span> : null}
                    </button>
                  )
                })}
              </div>
              <ul className="mt-3 flex flex-wrap gap-4 text-xs text-muted-foreground">
                {(Object.keys(kindLabels) as TaskItem['kind'][]).map((kind) => (
                  <li key={kind} className="flex items-center gap-1.5"><span aria-hidden className={`h-2 w-2 rounded-full ${kindMarker[kind]}`} />{kindLabels[kind]}</li>
                ))}
              </ul>
            </CardContent>
          </Card>
          <Card>
            <CardHeader><CardTitle>{fullDate(selected)}</CardTitle></CardHeader>
            <CardContent>
              {dayItems.length === 0 ? <EmptyState>Nothing scheduled for this day.</EmptyState> : <RowList label="Items on the selected day">{dayItems.map(row)}</RowList>}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="agenda">
          {agenda.length === 0 ? <EmptyState>{loading ? 'Loading…' : `Nothing scheduled in ${grid.title}.`}</EmptyState> : dayCards(agenda)}
        </TabsContent>

        <TabsContent value="upcoming">
          {upcomingByDay.length === 0 ? <EmptyState>{loading ? 'Loading…' : `Nothing scheduled in the next ${UPCOMING_DAYS} days.`}</EmptyState> : dayCards(upcomingByDay)}
        </TabsContent>
      </Tabs>
    </section>
  )
}
