import { useEffect, useState, type ReactNode } from 'react'
import { Bell, MessageSquare, ChevronDown, ChevronRight, GraduationCap, LogOut, Menu, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { apiRequest, type StoredSession } from '@/lib/api'
import { canViewMenuItem, menuGroups } from '@/lib/navigation'
import { subscribeMessages } from '@/lib/liveMessages'
import { NOTIFICATIONS_CHANGED } from '@/lib/notifications'
import { cn } from '@/lib/utils'

type Props = {
  session: StoredSession
  activeMenu: string
  onNavigate: (id: string) => void
  onLogout: () => void
  calendarMode: 'AD' | 'BS'
  onCalendarModeChange: (mode: 'AD' | 'BS') => void
  children: ReactNode
}

export default function AppShell({ session, activeMenu, onNavigate, onLogout, calendarMode, onCalendarModeChange, children }: Props) {
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({})
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [unread, setUnread] = useState(0)
  const [unreadMessages, setUnreadMessages] = useState(0)
  const canSeeMessages = session.permissions.includes('collaboration.read')
  const canSeeNotifications = session.permissions.includes('notification.read')

  // Poll quietly for unread notifications; failures simply leave the last known count.
  useEffect(() => {
    if (!canSeeNotifications) return undefined
    let active = true
    const load = () => apiRequest<unknown[]>('/api/v1/tenant/notifications?unread=true')
      .then((items) => { if (active) setUnread(items.length) }).catch(() => undefined)
    void load()
    const timer = window.setInterval(load, 60000)
    // Reading notifications in the inbox updates the bell at once instead of waiting for the next poll.
    window.addEventListener(NOTIFICATIONS_CHANGED, load)
    return () => { active = false; window.clearInterval(timer); window.removeEventListener(NOTIFICATIONS_CHANGED, load) }
  }, [canSeeNotifications, session.accessToken])

  useEffect(() => {
    if (!canSeeMessages) return undefined
    let active = true
    const load = () => apiRequest<{ count: number }>('/api/v1/tenant/messages/unread-count')
      .then((result) => { if (active) setUnreadMessages(result.count) }).catch(() => undefined)
    void load()
    const timer = window.setInterval(load, 60000)
    // The badge changes the moment a message arrives or is deleted, instead of waiting for the next poll.
    const stopLive = subscribeMessages(() => { void load() })
    return () => { active = false; window.clearInterval(timer); stopLive() }
  }, [canSeeMessages, session.accessToken])

  function navigate(id: string) {
    onNavigate(id)
    setDrawerOpen(false)
  }

  const navigation = (
    <>
          {menuGroups.filter((group) => group.submenus.some((submenu) => submenu.items.some((item) => canViewMenuItem(item, session.role, session.permissions)))).map((group) => (
            <div key={group.label} className="mb-4">
              <p className="px-2 pb-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{group.label}</p>
              {group.submenus.map((submenu) => {
                const items = submenu.items.filter((item) => canViewMenuItem(item, session.role, session.permissions))
                if (items.length === 0) return null
                const key = `${group.label}:${submenu.label}`
                const isCollapsed = collapsed[key] ?? false
                return (
                  <div key={key} className="mb-1">
                    <button
                      type="button"
                      aria-expanded={!isCollapsed}
                      className="flex w-full items-center gap-1 px-2 py-1 text-xs text-muted-foreground hover:text-foreground"
                      onClick={() => setCollapsed((current) => ({ ...current, [key]: !isCollapsed }))}
                    >
                      {isCollapsed ? <ChevronRight className="h-3 w-3" /> : <ChevronDown className="h-3 w-3" />}
                      {submenu.label}
                    </button>
                    {isCollapsed
                      ? null
                      : items.map((item) => (
                          <button
                            key={item.id}
                            type="button"
                            aria-current={activeMenu === item.id ? 'page' : undefined}
                            onClick={() => navigate(item.id)}
                            className={cn(
                              'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-muted',
                              activeMenu === item.id && 'bg-muted font-medium text-primary',
                            )}
                          >
                            <span aria-hidden className="w-5 text-center">{item.icon}</span>
                            {item.label}
                          </button>
                        ))}
                  </div>
                )
              })}
            </div>
          ))}
    </>
  )

  return (
    <div className="flex h-dvh flex-col overflow-hidden bg-background text-foreground">
      <header className="flex items-center gap-3 border-b border-border px-4 py-3">
        <Button variant="ghost" size="icon" className="md:hidden" aria-label="Open menu" onClick={() => setDrawerOpen(true)}>
          <Menu className="h-5 w-5" />
        </Button>
        <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary text-primary-foreground">
          <GraduationCap className="h-5 w-5" />
        </div>
        <div className="min-w-0 flex-1">
          <h1 className="truncate text-base font-semibold">{session.tenant.name}</h1>
          <p className="text-xs text-muted-foreground">{session.role}</p>
        </div>
        <select
          aria-label="Calendar"
          className="h-9 rounded-md border border-input bg-card px-2 text-sm"
          value={calendarMode}
          onChange={(event) => onCalendarModeChange(event.target.value as 'AD' | 'BS')}
        >
          <option value="AD">AD calendar</option>
          <option value="BS">BS calendar</option>
        </select>
        {canSeeMessages ? (
          <Button variant="outline" size="icon" className="relative" aria-label={unreadMessages > 0 ? `Messages, ${unreadMessages} unread` : 'Messages'} onClick={() => onNavigate('messages')}>
            <MessageSquare className="h-4 w-4" />
            {unreadMessages > 0 ? (
              <span className="absolute -right-1.5 -top-1.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-destructive px-1 text-[10px] font-semibold text-destructive-foreground">
                {unreadMessages > 99 ? '99+' : unreadMessages}
              </span>
            ) : null}
          </Button>
        ) : null}
        {canSeeNotifications ? (
          <Button variant="outline" size="icon" className="relative" aria-label={unread > 0 ? `Notifications, ${unread} unread` : 'Notifications'} onClick={() => onNavigate('notifications')}>
            <Bell className="h-4 w-4" />
            {unread > 0 ? (
              <span className="absolute -right-1.5 -top-1.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-destructive px-1 text-[10px] font-semibold text-destructive-foreground">
                {unread > 99 ? '99+' : unread}
              </span>
            ) : null}
          </Button>
        ) : null}
        <Button variant="outline" size="sm" onClick={onLogout}>
          <LogOut className="h-4 w-4" />
          Sign out
        </Button>
      </header>
      <div className="flex min-h-0 flex-1">
        <nav aria-label="Primary" className="hidden w-64 shrink-0 overflow-y-auto overscroll-contain border-r border-border p-3 md:block">
          {navigation}
        </nav>
        <main className="min-h-0 min-w-0 flex-1 overflow-y-auto overscroll-contain p-4 md:p-6">{children}</main>
      </div>
      {drawerOpen ? (
        <div className="fixed inset-0 z-50 md:hidden" role="dialog" aria-modal="true" aria-label="Navigation menu">
          <button type="button" aria-label="Close menu" className="absolute inset-0 bg-black/60" onClick={() => setDrawerOpen(false)} />
          <nav aria-label="Primary mobile" className="absolute inset-y-0 left-0 w-72 max-w-[85vw] overflow-y-auto border-r border-border bg-background p-3">
            <div className="mb-3 flex justify-end">
              <Button variant="ghost" size="icon" aria-label="Close menu" onClick={() => setDrawerOpen(false)}>
                <X className="h-5 w-5" />
              </Button>
            </div>
            {navigation}
          </nav>
        </div>
      ) : null}
    </div>
  )
}
