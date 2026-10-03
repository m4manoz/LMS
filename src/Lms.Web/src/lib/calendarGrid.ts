import { convertAdToBsLocal, convertBsToAdLocal, daysInBsMonth, getBsCalendarMonth, type CalendarMode } from './calendar'

export const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

export type GridCell = {
  date: Date
  /** Local calendar day, YYYY-MM-DD (always AD; this is what the server and the rest of the app use). */
  key: string
  /** The number shown large: the AD day in AD mode, the BS day in BS mode. */
  label: string
  /** The other calendar's day number, shown small. */
  secondary: string | null
  inMonth: boolean
}

export type MonthGrid = { title: string; cells: GridCell[]; from: Date; to: Date }

const dayKey = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
/** A local date at midday, so daylight-saving shifts never move it to another day. */
const localDate = (year: number, monthIndex: number, day: number) => new Date(year, monthIndex, day, 12)
const fromKey = (key: string) => { const [y, m, d] = key.split('-').map(Number); return localDate(y, m - 1, d) }

/** BS year and month (1-12) of a date, or null outside the supported range. */
export function bsMonthOf(date: Date): { year: number; month: number } | null {
  const bs = convertAdToBsLocal(dayKey(date))
  if (!bs) return null
  const [year, month] = bs.split('-').map(Number)
  return { year, month }
}

/**
 * The month containing `anchor` as whole weeks starting on Sunday.
 * AD mode is a fixed 6 weeks; BS mode shows as many weeks as that BS month needs.
 */
export function buildMonthGrid(mode: CalendarMode, anchor: Date): MonthGrid {
  if (mode === 'BS') {
    const bs = bsMonthOf(anchor)
    const month = bs ? getBsCalendarMonth(bs.year, bs.month) : null
    if (bs && month) {
      const firstKey = convertBsToAdLocal(`${bs.year}-${String(bs.month).padStart(2, '0')}-01`)!
      const first = fromKey(firstKey)
      const total = Math.ceil((month.firstDayOfWeek + month.daysInMonth) / 7) * 7
      const cells = Array.from({ length: total }, (_, index) => {
        const date = localDate(first.getFullYear(), first.getMonth(), first.getDate() - month.firstDayOfWeek + index)
        const here = bsMonthOf(date)
        const bsDay = convertAdToBsLocal(dayKey(date))?.split('-')[2] ?? ''
        return { date, key: dayKey(date), label: String(Number(bsDay)), secondary: String(date.getDate()), inMonth: here?.year === bs.year && here.month === bs.month }
      })
      return { title: `${month.monthName} ${bs.year} BS`, cells, from: cells[0].date, to: cells[cells.length - 1].date }
    }
    // Outside the supported BS range: fall through to the AD grid rather than showing nothing.
  }
  const first = localDate(anchor.getFullYear(), anchor.getMonth(), 1)
  const cells = Array.from({ length: 42 }, (_, index) => {
    const date = localDate(first.getFullYear(), first.getMonth(), 1 - first.getDay() + index)
    const bsDay = convertAdToBsLocal(dayKey(date))?.split('-')[2]
    return { date, key: dayKey(date), label: String(date.getDate()), secondary: mode === 'AD' || !bsDay ? null : String(Number(bsDay)), inMonth: date.getMonth() === anchor.getMonth() }
  })
  return { title: anchor.toLocaleString([], { month: 'long', year: 'numeric' }), cells, from: cells[0].date, to: cells[41].date }
}

/** A date in the month `delta` months away (in the calendar being shown). Stays put if that month is not supported. */
export function shiftMonth(mode: CalendarMode, anchor: Date, delta: number): Date {
  if (mode === 'BS') {
    const bs = bsMonthOf(anchor)
    if (bs) {
      const index = bs.year * 12 + (bs.month - 1) + delta
      const year = Math.floor(index / 12)
      const month = (index % 12 + 12) % 12 + 1
      if (daysInBsMonth(year, month) > 0) {
        const ad = convertBsToAdLocal(`${year}-${String(month).padStart(2, '0')}-01`)
        if (ad) return fromKey(ad)
      }
      return anchor
    }
  }
  return localDate(anchor.getFullYear(), anchor.getMonth() + delta, 1)
}

/** Midnight at the start of the day `days` from `from`, for building "upcoming" ranges. */
export const startOfDay = (from: Date, days = 0) => new Date(from.getFullYear(), from.getMonth(), from.getDate() + days)
