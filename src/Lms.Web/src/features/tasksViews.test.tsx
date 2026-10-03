import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MyTasksPage, { groupOpenTasks } from './MyTasksPage'
import { itemsByDay, monthGrid } from './CalendarPage'
import { dayKey, type TaskItem } from '@/lib/tasks'

const item = (overrides: Partial<TaskItem>): TaskItem => ({
  id: 'x', kind: 'assignment', title: 'Task', courseTitle: 'English', startAtUtc: null, dueAtUtc: null, status: 'Todo', target: 'assignments', count: null, ...overrides,
})

describe('groupOpenTasks', () => {
  const now = new Date(2026, 9, 15, 12, 0, 0) // 15 Oct 2026, local noon

  it('puts overdue first and buckets the rest by day', () => {
    const groups = groupOpenTasks([
      item({ id: 'a', title: 'Late', status: 'Overdue', dueAtUtc: new Date(2026, 9, 10).toISOString() }),
      item({ id: 'b', title: 'Today', dueAtUtc: new Date(2026, 9, 15, 18).toISOString() }),
      item({ id: 'c', title: 'Soon', dueAtUtc: new Date(2026, 9, 18).toISOString() }),
      item({ id: 'd', title: 'Far', dueAtUtc: new Date(2026, 10, 30).toISOString() }),
      item({ id: 'e', title: 'Quiz', kind: 'assessment', dueAtUtc: null }),
    ], now)
    expect(groups.map((group) => group.title)).toEqual(['Overdue', 'Today', 'This week', 'Later', 'No deadline'])
    expect(groups[0].items[0].title).toBe('Late')
  })

  it('omits empty groups', () => {
    expect(groupOpenTasks([], now)).toEqual([])
  })
})

describe('calendar helpers', () => {
  it('builds a 42-day grid that starts on a Sunday and contains the whole month', () => {
    const grid = monthGrid(new Date(2026, 1, 10)) // February 2026
    expect(grid).toHaveLength(42)
    expect(grid[0].getDay()).toBe(0)
    expect(grid.some((date) => dayKey(date) === '2026-02-01')).toBe(true)
    expect(grid.some((date) => dayKey(date) === '2026-02-28')).toBe(true)
  })

  it('groups dated items by local day and ignores undated ones', () => {
    const map = itemsByDay([
      item({ id: 'a', dueAtUtc: new Date(2026, 9, 15, 9).toISOString() }),
      item({ id: 'b', startAtUtc: new Date(2026, 9, 15, 17).toISOString() }),
      item({ id: 'c', dueAtUtc: null }),
    ])
    expect(map.get('2026-10-15')).toHaveLength(2)
    expect(map.size).toBe(1)
  })
})

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

describe('MyTasksPage', () => {
  beforeEach(() => request.mockReset())

  it('lists open work and opens the right module', async () => {
    const onNavigate = vi.fn()
    request.mockResolvedValue({ fromUtc: '', toUtc: '', items: [
      item({ id: 'a', title: 'Essay one', dueAtUtc: new Date(Date.now() + 86400000).toISOString() }),
      item({ id: 'b', title: 'Old essay', status: 'Graded', dueAtUtc: new Date(Date.now() - 86400000).toISOString() }),
    ] })
    render(<MyTasksPage onNavigate={onNavigate} />)
    expect(await screen.findByText('Essay one')).toBeInTheDocument()
    expect(screen.queryByText('Old essay')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Open Essay one' }))
    expect(onNavigate).toHaveBeenCalledWith('assignments')
    expect(screen.getByRole('tab', { name: 'Completed (1)' })).toBeInTheDocument()
  })

  it('shows an all-caught-up message when nothing is open', async () => {
    request.mockResolvedValue({ fromUtc: '', toUtc: '', items: [] })
    render(<MyTasksPage onNavigate={vi.fn()} />)
    expect(await screen.findByText('You are all caught up.')).toBeInTheDocument()
  })
})
