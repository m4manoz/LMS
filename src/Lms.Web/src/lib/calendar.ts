export type CalendarMode = 'AD' | 'BS'

export type BsCalendarDay = {
  day: number
  bs: string
  ad: string
  isToday: boolean
}

export type BsCalendarMonth = {
  year: number
  month: number
  monthName: string
  daysInMonth: number
  firstDayOfWeek: number
  days: BsCalendarDay[]
}

const monthNames = ['Baisakh', 'Jestha', 'Asadh', 'Shrawan', 'Bhadra', 'Ashwin', 'Kartik', 'Mangsir', 'Poush', 'Magh', 'Falgun', 'Chaitra']
const encodedMonthLengths = [
  5315258, 5314490, 9459438, 8673005, 5315258, 5315066, 9459438, 8673005,
  5315258, 5314298, 9459438, 5327594, 5315258, 5314298, 9459438, 5327594,
  5315258, 5314286, 9459438, 5315306, 5315258, 5314286, 8673006, 5315306,
  5315258, 5265134, 8673006, 5315258, 5315258, 9459438, 8673005, 5315258,
  5314298, 9459438, 8673005, 5315258, 5314298, 9459438, 8473322, 5315258,
  5314298, 9459438, 5327594, 5315258, 5314298, 9459438, 5327594, 5315258,
  5314286, 8673006, 5315306, 5315258, 5265134, 8673006, 5315306, 5315258,
  9459438, 8673005, 5315258, 5314490, 9459438, 8673005, 5315258, 5314298,
  9459438, 8473325, 5315258, 5314298, 9459438, 5327594, 5315258, 5314298,
  9459438, 5327594, 5315258, 5314286, 9459438, 5315306, 5315258, 5265134,
  8673006, 5315306, 5315258, 5265134, 8673006, 5315258, 5314490, 9459438,
  8673005, 5315258, 5314298, 9459438, 8669933, 5315258, 5314298, 9459438,
  8473322, 5315258, 5314298, 9459438, 5327594, 5315258, 5314286, 9459438,
  5315306, 5315258, 5265134, 8673006, 5315306, 5315258, 5265134, 8673006,
  5315258, 5315258, 5527226, 5528046, 5527277, 5528250, 5528057, 5527277,
  5527277,
]
const gregorianEpoch = Date.UTC(1913, 3, 13)

export function daysInBsMonth(year: number, month: number): number {
  const encoded = encodedMonthLengths[year - 1970]
  return encoded === undefined || month < 1 || month > 12 ? 0 : 29 + ((encoded >> ((month - 1) * 2)) & 3)
}

export function convertAdToBsLocal(value: string): string | null {
  const match = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(value)
  if (!match) return null
  const year = Number(match[1]); const month = Number(match[2]); const day = Number(match[3])
  const utc = Date.UTC(year, month - 1, day)
  const parsed = new Date(utc)
  if (parsed.getUTCFullYear() !== year || parsed.getUTCMonth() !== month - 1 || parsed.getUTCDate() !== day) return null
  let remaining = Math.round((utc - gregorianEpoch) / 86_400_000) + 1
  if (remaining < 1) return null
  for (let bsYear = 1970; bsYear <= 1970 + encodedMonthLengths.length - 1; bsYear += 1) {
    for (let bsMonth = 1; bsMonth <= 12; bsMonth += 1) {
      const days = daysInBsMonth(bsYear, bsMonth)
      if (remaining <= days) return `${bsYear}-${String(bsMonth).padStart(2, '0')}-${String(remaining).padStart(2, '0')}`
      remaining -= days
    }
  }
  return null
}

export function convertBsToAdLocal(value: string): string | null {
  const match = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(value.replace(/[/.]/g, '-').replace(/\s+/g, ''))
  if (!match) return null
  const year = Number(match[1]); const month = Number(match[2]); const day = Number(match[3])
  const selectedDays = daysInBsMonth(year, month)
  if (!selectedDays || day < 1 || day > selectedDays) return null
  let offset = day - 1
  for (let bsYear = 1970; bsYear < year; bsYear += 1) for (let bsMonth = 1; bsMonth <= 12; bsMonth += 1) offset += daysInBsMonth(bsYear, bsMonth)
  for (let bsMonth = 1; bsMonth < month; bsMonth += 1) offset += daysInBsMonth(year, bsMonth)
  const date = new Date(gregorianEpoch + offset * 86_400_000)
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}-${String(date.getUTCDate()).padStart(2, '0')}`
}

export function getBsCalendarMonth(year: number, month: number, today = new Date()): BsCalendarMonth | null {
  const days = daysInBsMonth(year, month)
  if (!days) return null
  const firstAd = convertBsToAdLocal(`${year}-${String(month).padStart(2, '0')}-01`)
  if (!firstAd) return null
  const firstDayOfWeek = new Date(`${firstAd}T00:00:00Z`).getUTCDay()
  const todayAd = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`
  return {
    year, month, monthName: monthNames[month - 1], daysInMonth: days, firstDayOfWeek,
    days: Array.from({ length: days }, (_, index) => {
      const day = index + 1
      const bs = `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`
      const ad = convertBsToAdLocal(bs)!
      return { day, bs, ad, isToday: ad === todayAd }
    }),
  }
}

export function todayBs(): { year: number; month: number; day: number } {
  const parsed = convertAdToBsLocal(new Date().toISOString().slice(0, 10)) ?? '2082-01-01'
  const [year, month, day] = parsed.split('-').map(Number)
  return { year, month, day }
}

