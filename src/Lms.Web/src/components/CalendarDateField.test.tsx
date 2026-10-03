import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CalendarDateField from './CalendarDateField'
import { CalendarProvider } from '@/lib/calendarSettings'

const renderField = (props: Partial<React.ComponentProps<typeof CalendarDateField>> = {}, mode: 'AD' | 'BS' = 'AD') => {
  localStorage.setItem('lms-calendar-mode', mode)
  const onChange = vi.fn()
  render(<CalendarProvider><CalendarDateField id="start" label="Available from" value="" onChange={onChange} {...props} /></CalendarProvider>)
  return onChange
}

describe('CalendarDateField', () => {
  beforeEach(() => localStorage.clear())

  it('keeps the label plain, with the active calendar shown beside it', () => {
    renderField()
    expect(screen.getByLabelText('Available from')).toHaveAttribute('type', 'date')
    expect(screen.getByText('AD')).toBeInTheDocument()
  })

  it('does not offer its own AD/BS switch: the calendar is chosen once in the page header', () => {
    renderField()
    expect(screen.queryByRole('combobox')).toBeNull()
    expect(screen.queryByLabelText('Calendar mode')).toBeNull()
  })

  it('marks a required field with the shared red asterisk, without changing its accessible name', () => {
    renderField({ required: true })
    expect(screen.getByText('Available from')).toHaveClass('after:text-red-500')
    expect(screen.getByLabelText('Available from')).toBeRequired()
  })

  it('does not mark an optional field', () => {
    renderField()
    expect(screen.getByText('Available from')).not.toHaveClass('after:text-red-500')
  })

  it('reports a date typed in the AD calendar', async () => {
    const onChange = renderField()
    await userEvent.type(screen.getByLabelText('Available from'), '2026-05-01')
    expect(onChange).toHaveBeenLastCalledWith('2026-05-01')
  })

  it('shows the BS picker in the page flow, so a scrolling panel cannot clip it, and stores the date as AD', async () => {
    const onChange = renderField({}, 'BS')
    expect(screen.getByText('BS')).toBeInTheDocument()
    await userEvent.click(screen.getByLabelText('Available from'))
    const picker = await screen.findByRole('dialog', { name: 'Available from calendar' })
    expect(picker).not.toHaveClass('absolute')
    await userEvent.click(within15(picker))
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChange.mock.calls[0][0]).toMatch(/^\d{4}-\d{2}-\d{2}$/)      // an AD date, whatever BS day was picked
    expect(screen.queryByRole('dialog', { name: 'Available from calendar' })).toBeNull() // closes after choosing
  })

  it('is disabled with the form section around it', () => {
    renderField({ disabled: true })
    expect(screen.getByLabelText('Available from')).toBeDisabled()
  })
})

/** The button for day 15 inside the picker (every BS month has one). */
function within15(picker: HTMLElement) {
  const button = Array.from(picker.querySelectorAll('button')).find((item) => item.textContent === '15')
  if (!button) throw new Error('day 15 not found')
  return button
}
