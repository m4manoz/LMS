import { useCallback, useEffect, useState } from 'react'
import { EmptyState, ErrorBanner, ListRow, NoticeBanner, RowList } from '@/components/form'
import { Button } from '@/components/ui/button'
import { ApiError, apiRequest } from '@/lib/api'
import { notificationsChanged, notificationTarget, timeAgo } from '@/lib/notifications'
import { cn } from '@/lib/utils'

export type InboxItem = { id: string; templateCode: string; subject: string; body: string; createdAtUtc: string; readAtUtc?: string | null }

/** The server returns at most this many of the newest notifications. */
export const INBOX_LIMIT = 100

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)

export default function NotificationInbox({ onNavigate }: { onNavigate?: (menuId: string) => void }) {
  const [items, setItems] = useState<InboxItem[] | null>(null)
  const [filter, setFilter] = useState<'all' | 'unread'>('all')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    setError(null)
    try { setItems(await apiRequest<InboxItem[]>('/api/v1/tenant/notifications')) }
    catch (exception) { setError(readError(exception, 'Unable to load your notifications.')) }
  }, [])
  useEffect(() => { void load() }, [load])

  const unread = (items ?? []).filter((item) => !item.readAtUtc).length
  const shown = (items ?? []).filter((item) => filter === 'all' || !item.readAtUtc)

  async function markRead(id: string) {
    setError(null); setNotice(null)
    try {
      await apiRequest(`/api/v1/tenant/notifications/${id}/read`, { method: 'POST' })
      setItems((current) => (current ?? []).map((item) => (item.id === id ? { ...item, readAtUtc: new Date().toISOString() } : item)))
      notificationsChanged()
    } catch (exception) { setError(readError(exception, 'Unable to mark the notification as read.')) }
  }

  async function markAllRead() {
    setBusy(true); setError(null); setNotice(null)
    try {
      const result = await apiRequest<{ marked: number }>('/api/v1/tenant/notifications/read-all', { method: 'POST' })
      const now = new Date().toISOString()
      setItems((current) => (current ?? []).map((item) => ({ ...item, readAtUtc: item.readAtUtc ?? now })))
      setNotice(result.marked === 1 ? '1 notification marked as read.' : `${result.marked} notifications marked as read.`)
      notificationsChanged()
    } catch (exception) { setError(readError(exception, 'Unable to mark the notifications as read.')) }
    finally { setBusy(false) }
  }

  async function open(item: InboxItem, target: string) {
    if (!item.readAtUtc) await markRead(item.id)
    onNavigate?.(target)
  }

  return (
    <div className="flex flex-col gap-4">
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex gap-1.5" role="group" aria-label="Show">
          <Button type="button" size="sm" variant={filter === 'all' ? 'secondary' : 'outline'} aria-pressed={filter === 'all'} onClick={() => setFilter('all')}>All <span className="text-muted-foreground">{items?.length ?? 0}</span></Button>
          <Button type="button" size="sm" variant={filter === 'unread' ? 'secondary' : 'outline'} aria-pressed={filter === 'unread'} onClick={() => setFilter('unread')}>Unread <span className="text-muted-foreground">{unread}</span></Button>
        </div>
        <Button type="button" variant="outline" size="sm" disabled={busy || unread === 0} onClick={() => void markAllRead()}>Mark all as read</Button>
      </div>

      {items === null && !error ? <p className="text-sm text-muted-foreground">Loading your notifications…</p> : null}
      {items !== null && shown.length === 0 ? <EmptyState>{filter === 'unread' ? 'You are all caught up. Nothing unread.' : 'No notifications yet.'}</EmptyState> : null}
      {shown.length > 0 ? (
        <RowList label="Notifications">
          {shown.map((item) => {
            const target = notificationTarget(item.templateCode)
            const isUnread = !item.readAtUtc
            return (
              <ListRow key={item.id} columns="sm:grid-cols-[minmax(0,1fr)_auto]">
                <div className="flex min-w-0 items-start gap-3">
                  <span aria-hidden className={cn('mt-1.5 h-2 w-2 shrink-0 rounded-full', isUnread ? 'bg-primary' : 'bg-transparent')} />
                  <div className="min-w-0">
                    <strong className={cn('block', !isUnread && 'font-medium text-muted-foreground')}>{item.subject}{isUnread ? <span className="sr-only"> (unread)</span> : null}</strong>
                    <p className="text-muted-foreground">{item.body}</p>
                    <small className="text-muted-foreground" title={new Date(item.createdAtUtc).toLocaleString()}>{timeAgo(item.createdAtUtc)}</small>
                  </div>
                </div>
                <div className="flex shrink-0 items-center justify-end gap-2">
                  {target && onNavigate ? <Button type="button" variant="soft" size="sm" aria-label={`Open: ${item.subject}`} onClick={() => void open(item, target)}>Open</Button> : null}
                  {isUnread ? <Button type="button" variant="outline" size="sm" aria-label={`Mark as read: ${item.subject}`} onClick={() => void markRead(item.id)}>Mark read</Button> : null}
                </div>
              </ListRow>
            )
          })}
        </RowList>
      ) : null}
      {(items?.length ?? 0) >= INBOX_LIMIT ? <p className="text-xs text-muted-foreground">Showing your latest {INBOX_LIMIT} notifications.</p> : null}
    </div>
  )
}
