import { useEffect, useRef, useState } from 'react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'
import { convertAdToBsLocal, convertBsToAdLocal, getBsCalendarMonth, todayBs } from '../lib/calendar'
import { useCalendarSettings } from '../lib/calendarSettings'

type CalendarDateFieldProps = {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  required?: boolean
  disabled?: boolean
  hint?: string
}

export default function CalendarDateField({ id, label, value, onChange, required = false, disabled = false, hint }: CalendarDateFieldProps) {
  // The AD/BS choice is one setting for the whole app, changed from the page header, not from each field.
  const { mode } = useCalendarSettings()
  const [open, setOpen] = useState(false)
  const [bsInput, setBsInput] = useState(() => convertAdToBsLocal(value) ?? '')
  const initial = todayBs()
  const selectedBs = convertAdToBsLocal(value)
  const parsedSelected = selectedBs?.split('-').map(Number)
  const [pickerYear, setPickerYear] = useState(parsedSelected?.[0] ?? initial.year)
  const [pickerMonth, setPickerMonth] = useState(parsedSelected?.[1] ?? initial.month)
  const containerRef = useRef<HTMLDivElement>(null)
  const pickerRef = useRef<HTMLDivElement>(null)
  const bsCalendar = mode === 'BS' ? getBsCalendarMonth(pickerYear, pickerMonth) : null

  useEffect(() => {
    setBsInput(convertAdToBsLocal(value) ?? '')
    const parsed = convertAdToBsLocal(value)?.split('-').map(Number)
    if (parsed) { setPickerYear(parsed[0]); setPickerMonth(parsed[1]) }
  }, [value])

  // When the picker opens near the bottom of a scrolling panel, bring it into view.
  useEffect(() => { if (open) pickerRef.current?.scrollIntoView?.({ block: 'nearest' }) }, [open, pickerYear, pickerMonth])

  useEffect(() => {
    if (!open) return undefined
    const close = (event: MouseEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [open])

  function moveMonth(offset: number) {
    const next = pickerMonth + offset
    if (next < 1) { setPickerYear((current) => current - 1); setPickerMonth(12) }
    else if (next > 12) { setPickerYear((current) => current + 1); setPickerMonth(1) }
    else setPickerMonth(next)
  }

  function selectBsDate(bs: string) {
    const ad = convertBsToAdLocal(bs)
    if (!ad) return
    setBsInput(bs)
    onChange(ad)
    setOpen(false)
  }

  return (
    <div className="relative flex flex-col gap-1.5" ref={containerRef}>
      <div className="flex items-center justify-between gap-2">
        <Label htmlFor={id} required={required}>{label}</Label>
        <span className="text-xs text-muted-foreground" title="Change the calendar with the AD/BS control in the page header">{mode}</span>
      </div>
      {mode === 'AD' ? (
        <Input id={id} type="date" value={value} onChange={(event) => onChange(event.target.value)} required={required} disabled={disabled} />
      ) : (
        <div className="relative">
          <Input
            id={id}
            type="text"
            value={bsInput}
            placeholder="YYYY-MM-DD"
            inputMode="numeric"
            onChange={(event) => setBsInput(event.target.value)}
            onFocus={() => setOpen(true)}
            onBlur={() => { const ad = convertBsToAdLocal(bsInput); if (ad) onChange(ad) }}
            onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); const ad = convertBsToAdLocal(bsInput); if (ad) { onChange(ad); setOpen(false) } } if (event.key === 'Escape') setOpen(false) }}
            required={required}
            disabled={disabled}
            aria-haspopup="dialog"
            aria-expanded={open}
          />
          {open && bsCalendar ? (
            // In the flow of the page (not floating), so a scrolling panel or the edge of the screen can never clip it.
            <div
              ref={pickerRef}
              className="mt-1 w-72 max-w-full rounded-lg border border-border bg-card p-3 shadow-sm"
              role="dialog"
              aria-label={`${label} calendar`}
              onMouseDown={(event) => event.preventDefault()}
            >
              <div className="mb-2 flex items-center justify-between">
                <button type="button" className="rounded-md px-2 py-1 hover:bg-muted" onClick={() => moveMonth(-1)} aria-label="Previous month">←</button>
                <strong className="text-sm">{bsCalendar.monthName} {pickerYear}</strong>
                <button type="button" className="rounded-md px-2 py-1 hover:bg-muted" onClick={() => moveMonth(1)} aria-label="Next month">→</button>
              </div>
              <div className="grid grid-cols-7 gap-1 text-center text-xs text-muted-foreground">
                {['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].map((day) => <span key={day}>{day}</span>)}
              </div>
              <div className="mt-1 grid grid-cols-7 gap-1">
                {Array.from({ length: bsCalendar.firstDayOfWeek }, (_, index) => <span key={`empty-${index}`} />)}
                {bsCalendar.days.map((day) => (
                  <button
                    type="button"
                    key={day.bs}
                    onClick={() => selectBsDate(day.bs)}
                    title={`${day.bs} BS · ${day.ad} AD`}
                    className={cn(
                      'rounded-md py-1 text-sm hover:bg-muted',
                      day.isToday && 'border border-primary',
                      day.bs === bsInput && 'bg-primary text-primary-foreground hover:bg-primary',
                    )}
                  >
                    {day.day}
                  </button>
                ))}
              </div>
              <small className="mt-2 block text-xs text-muted-foreground">Stored value: {value || 'not selected'} AD</small>
            </div>
          ) : null}
        </div>
      )}
      {hint ? <small className="text-xs text-muted-foreground">{hint}</small> : null}
    </div>
  )
}
