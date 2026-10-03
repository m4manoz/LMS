import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import type { CalendarMode } from './calendar'

const storageKey = 'lms-calendar-mode'
const CalendarContext = createContext<{ mode: CalendarMode; setMode: (mode: CalendarMode) => void } | null>(null)

export function CalendarProvider({ children }: { children: ReactNode }) {
  const [mode, setMode] = useState<CalendarMode>(() => localStorage.getItem(storageKey) === 'BS' ? 'BS' : 'AD')
  useEffect(() => localStorage.setItem(storageKey, mode), [mode])
  const value = useMemo(() => ({ mode, setMode }), [mode])
  return <CalendarContext.Provider value={value}>{children}</CalendarContext.Provider>
}

export function useCalendarSettings() {
  const value = useContext(CalendarContext)
  if (!value) throw new Error('useCalendarSettings must be used inside CalendarProvider')
  return value
}

