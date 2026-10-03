import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CalendarPage from './CalendarPage'
import { ApiError } from '@/lib/api'
import { convertAdToBsLocal, convertBsToAdLocal, daysInBsMonth } from '@/lib/calendar'
import { buildMonthGrid, bsMonthOf, shiftMonth, startOfDay } from '@/lib/calendarGrid'
import { CalendarProvider } from '@/lib/calendarSettings'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

const adDate = (bs: string) => { const [y, m, d] = convertBsToAdLocal(bs)!.split('-').map(Number); return new Date(y, m - 1, d, 12) }

describe('buildMonthGrid', () => {
  it('AD: six whole weeks starting on a Sunday, covering the month, with no second calendar', () => {
    const grid = buildMonthGrid('AD', new Date(2026, 9, 14))
    expect(grid.cells).toHaveLength(42)
    expect(grid.cells[0].date.getDay()).toBe(0)
    expect(grid.title).toMatch(/2026/)
    expect(grid.cells.filter((cell) => cell.inMonth)).toHaveLength(31)
    expect(grid.cells.every((cell) => cell.secondary === null)).toBe(true)
  })

  it('BS: a BS month on its own weeks, titled in BS, with the day numbers in BS and the AD day small', () => {
    const anchor = adDate('2083-01-10')
    const grid = buildMonthGrid('BS', anchor)
    expect(grid.title).toBe('Baisakh 2083 BS')
    expect(grid.cells.length % 7).toBe(0)
    expect(grid.cells[0].date.getDay()).toBe(0)
    const inMonth = grid.cells.filter((cell) => cell.inMonth)
    expect(inMonth).toHaveLength(daysInBsMonth(2083, 1))
    expect(inMonth[0].label).toBe('1')
    expect(inMonth[inMonth.length - 1].label).toBe(String(daysInBsMonth(2083, 1)))
    expect(inMonth[0].key).toBe(convertBsToAdLocal('2083-01-01'))
    expect(inMonth[0].secondary).toBe(String(Number(convertBsToAdLocal('2083-01-01')!.split('-')[2])))
    // days outside the month still carry their own BS numbers
    expect(grid.cells.filter((cell) => !cell.inMonth).every((cell) => Number(cell.label) >= 1)).toBe(true)
  })

  it('BS: from and to span the whole visible grid so every cell gets its items', () => {
    const grid = buildMonthGrid('BS', adDate('2083-06-15'))
    expect(grid.from.getTime()).toBe(grid.cells[0].date.getTime())
    expect(grid.to.getTime()).toBe(grid.cells[grid.cells.length - 1].date.getTime())
  })

  it('cells are unique days one after another', () => {
    for (const mode of ['AD', 'BS'] as const) {
      const cells = buildMonthGrid(mode, adDate('2082-10-05')).cells
      expect(new Set(cells.map((cell) => cell.key)).size).toBe(cells.length)
      for (let i = 1; i < cells.length; i += 1) expect(cells[i].date.getTime() - cells[i - 1].date.getTime()).toBeGreaterThanOrEqual(23 * 3600_000)
    }
  })
})

describe('shiftMonth', () => {
  it('AD moves by calendar months across a year end', () => {
    const next = shiftMonth('AD', new Date(2026, 11, 20), 1)
    expect([next.getFullYear(), next.getMonth()]).toEqual([2027, 0])
    const back = shiftMonth('AD', new Date(2026, 0, 20), -1)
    expect([back.getFullYear(), back.getMonth()]).toEqual([2025, 11])
  })

  it('BS moves by BS months, rolling over the BS year (Chaitra to Baisakh)', () => {
    const chaitra = adDate('2082-12-10')
    expect(bsMonthOf(shiftMonth('BS', chaitra, 1))).toEqual({ year: 2083, month: 1 })
    expect(bsMonthOf(shiftMonth('BS', adDate('2083-01-10'), -1))).toEqual({ year: 2082, month: 12 })
    expect(bsMonthOf(shiftMonth('BS', adDate('2083-05-10'), 3))).toEqual({ year: 2083, month: 8 })
  })

  it('stays put at the end of the supported BS range', () => {
    const first = new Date(1913, 3, 14, 12)
    expect(shiftMonth('BS', first, -2).getTime()).toBe(first.getTime())
  })
})

describe('startOfDay', () => {
  it('is local midnight, optionally some days ahead', () => {
    const base = new Date(2026, 9, 14, 15, 30)
    expect(startOfDay(base).getHours()).toBe(0)
    expect(startOfDay(base, 30).getDate()).toBe(13) // 14 Oct + 30 days = 13 Nov
  })
})

// ---------- the page ----------
const item = (id: string, title: string, when: Date) => ({ id, kind: 'assignment', title, courseTitle: null, startAtUtc: null, dueAtUtc: when.toISOString(), status: 'Todo', target: 'assignments', count: null })
const feed = (...items: object[]) => ({ fromUtc: '', toUtc: '', items })
const midMonth = (offset: number) => { const now = new Date(); return new Date(now.getFullYear(), now.getMonth() + offset, 15, 12) }

const deferred = <T,>() => { let resolve!: (value: T) => void; const promise = new Promise<T>((r) => { resolve = r }); return { promise, resolve } }

function renderPage(mode: 'AD' | 'BS' = 'AD') {
  localStorage.setItem('lms-calendar-mode', mode)
  return render(<CalendarProvider><CalendarPage onNavigate={vi.fn()} /></CalendarProvider>)
}

describe('CalendarPage', () => {
  beforeEach(() => { request.mockReset(); localStorage.clear() })

  it('shows a loading indicator while the month loads', async () => {
    const pending = deferred<ReturnType<typeof feed>>()
    request.mockReturnValue(pending.promise)
    renderPage()
    expect(await screen.findByRole('status')).toHaveTextContent('Loading')
    await act(async () => { pending.resolve(feed()) })
    await waitFor(() => expect(screen.queryByRole('status')).toBeNull())
  })

  it('ignores a slow answer for a month the person has already left', async () => {
    const slow = deferred<ReturnType<typeof feed>>()
    const fast = deferred<ReturnType<typeof feed>>()
    request.mockReturnValueOnce(slow.promise).mockReturnValueOnce(fast.promise)
    renderPage()
    await userEvent.click(screen.getByRole('tab', { name: 'Agenda' }))
    await userEvent.click(screen.getByRole('button', { name: 'Next month' }))
    await act(async () => { fast.resolve(feed(item('b', 'Next month task', midMonth(1)))) })
    await act(async () => { slow.resolve(feed(item('a', 'Old month task', midMonth(0)))) })   // arrives last, but is stale
    expect(await screen.findByText('Next month task')).toBeInTheDocument()
    expect(screen.queryByText('Old month task')).toBeNull()
  })

  it('clears an error as soon as the next load starts, and shows it only while it applies', async () => {
    let fail = true
    request.mockImplementation(() => {
      if (fail) { fail = false; return Promise.resolve().then(() => { throw new ApiError('The calendar service is down.', 503) }) }
      return Promise.resolve(feed())
    })
    renderPage()
    expect(await screen.findByRole('alert')).toHaveTextContent('The calendar service is down.')
    await userEvent.click(screen.getByRole('button', { name: 'Next month' }))
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
  })

  it('AD: titles the month in AD and shows BS numbers small', async () => {
    request.mockResolvedValue(feed())
    renderPage('AD')
    const now = new Date()
    expect(await screen.findByText(now.toLocaleString([], { month: 'long', year: 'numeric' }))).toBeInTheDocument()
    expect(screen.queryByText(/ BS$/)).toBeNull()
  })

  it('BS: titles the month in BS, steps by BS months, and asks for the whole visible grid', async () => {
    request.mockResolvedValue(feed())
    renderPage('BS')
    const bs = convertAdToBsLocal(new Date().toISOString().slice(0, 10))!.split('-').map(Number)
    const title = await screen.findByText(/BS$/)
    expect(title.textContent).toContain(String(bs[0]))

    await waitFor(() => expect(request).toHaveBeenCalled())
    const first = new URL(`http://x${String(request.mock.calls[0][0])}`)
    const from = new Date(first.searchParams.get('from')!); const to = new Date(first.searchParams.get('to')!)
    expect((to.getTime() - from.getTime()) / 86_400_000).toBeGreaterThanOrEqual(27)   // a whole BS month of weeks

    const before = title.textContent
    await userEvent.click(screen.getByRole('button', { name: 'Next month' }))
    await waitFor(() => expect(screen.getByText(/BS$/).textContent).not.toBe(before))
  })

  it('"Next 30 days" lists what is coming up from today, whatever month is on screen', async () => {
    request.mockResolvedValue(feed(item('u', 'Soon task', new Date(Date.now() + 3 * 86_400_000))))
    renderPage()
    await userEvent.click(screen.getByRole('button', { name: 'Next month' }))   // browse away first
    request.mockClear()
    await userEvent.click(screen.getByRole('tab', { name: /Next 30 days/ }))
    expect(await screen.findByText('Soon task')).toBeInTheDocument()
    const url = new URL(`http://x${String(request.mock.calls[0][0])}`)
    const from = new Date(url.searchParams.get('from')!); const to = new Date(url.searchParams.get('to')!)
    expect(from.getTime()).toBe(startOfDay(new Date()).getTime())
    expect(Math.round((to.getTime() - from.getTime()) / 86_400_000)).toBe(30)
    expect(screen.queryByRole('button', { name: 'Next month' })).toBeNull()      // month controls do not apply here
  })

  it('opens the right screen from an item', async () => {
    const onNavigate = vi.fn()
    request.mockResolvedValue(feed(item('x', 'Essay', new Date())))
    localStorage.setItem('lms-calendar-mode', 'AD')
    render(<CalendarProvider><CalendarPage onNavigate={onNavigate} /></CalendarProvider>)
    await userEvent.click(await screen.findByRole('button', { name: 'Open Essay' }))
    expect(onNavigate).toHaveBeenCalledWith('assignments')
  })

  it('says so when a period is empty', async () => {
    request.mockResolvedValue(feed())
    renderPage()
    await userEvent.click(await screen.findByRole('tab', { name: 'Agenda' }))
    expect(await screen.findByText(/Nothing scheduled in/)).toBeInTheDocument()
  })
})
