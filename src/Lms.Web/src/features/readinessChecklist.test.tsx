import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { readiness } from './courseAuthoring'
import { ReadinessChecklist } from './ReadinessChecklist'

const mod = (title: string, lessons: number) => ({ id: title, title, description: null, displayOrder: 1, lessons: Array.from({ length: lessons }, (_, index) => ({ id: `${title}${index}`, title: `L${index}`, displayOrder: index })) })

describe('ReadinessChecklist', () => {
  it('separates what is still to do from what is done, and counts the required items', () => {
    render(<ReadinessChecklist checks={readiness([mod('Basics', 2), mod('Formulas (lessons still to come)', 0)], null)} onGo={vi.fn()} />)
    expect(screen.getByRole('status')).toHaveTextContent('1 of 2 required items done')
    const todo = screen.getByRole('region', { name: 'Still to do' })
    expect(within(todo).getByText('Still to do (2)')).toBeInTheDocument()
    expect(within(todo).getByText('Every module has a lesson')).toBeInTheDocument()
    expect(within(todo).getByText(/Add a lesson to “Formulas \(lessons still to come\)”/)).toBeInTheDocument()
    expect(within(todo).getByText('Required')).toBeInTheDocument()
    expect(within(todo).getByText('Optional')).toBeInTheDocument()                         // the description
    const done = screen.getByRole('region', { name: 'Done' })
    expect(within(done).getByText('Has at least one module')).toBeInTheDocument()
    expect(within(done).queryByText('Every module has a lesson')).toBeNull()               // never in both groups
  })

  it('sends the person to the tab where each item is finished', async () => {
    const onGo = vi.fn()
    render(<ReadinessChecklist checks={readiness([mod('Formulas', 0)], null)} onGo={onGo} />)
    await userEvent.click(screen.getByRole('button', { name: /Go to Outline to finish: Every module has a lesson/ }))
    expect(onGo).toHaveBeenLastCalledWith('outline')
    await userEvent.click(screen.getByRole('button', { name: /Go to Details to finish: Has a description/ }))
    expect(onGo).toHaveBeenLastCalledWith('details')
  })

  it('says it is ready when the required items are done, even if an optional one is not', () => {
    render(<ReadinessChecklist checks={readiness([mod('Basics', 1)], null)} onGo={vi.fn()} />)
    expect(screen.getByRole('status')).toHaveTextContent('2 of 2 required items done — ready to submit')
    expect(within(screen.getByRole('region', { name: 'Still to do' })).getByText('Has a description')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Done' })).getAllByRole('listitem')).toHaveLength(2)
  })

  it('shows only the done group when everything is finished, and only the to do group when nothing is', () => {
    const { rerender } = render(<ReadinessChecklist checks={readiness([mod('Basics', 1)], 'A description')} />)
    expect(screen.queryByRole('region', { name: 'Still to do' })).toBeNull()
    expect(screen.queryByRole('button')).toBeNull()
    rerender(<ReadinessChecklist checks={readiness([], null)} />)
    expect(screen.queryByRole('region', { name: 'Done' })).toBeNull()
    expect(screen.getByRole('status')).toHaveTextContent('0 of 2 required items done')
  })

  it('offers no buttons when it is only showing the list', () => {
    render(<ReadinessChecklist checks={readiness([], null)} />)
    expect(screen.queryByRole('button')).toBeNull()
  })
})
