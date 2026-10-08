import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { countOf, groupFlat, PermissionPicker, PermissionSummary, setModule, type PermissionModule } from './PermissionGroups'
import RbacPage from './RbacPage'

const modules: PermissionModule[] = [
  { module: 'courses', title: 'Courses and content', permissions: [
    { code: 'course.read', label: 'See courses', description: 'Browse courses.' },
    { code: 'course.manage', label: 'Create and edit courses', description: 'Build courses.' },
    { code: 'course.publish', label: 'Publish courses', description: 'Make a course visible.' }] },
  { module: 'reports', title: 'Reports', permissions: [
    { code: 'report.read', label: 'See reports', description: 'Open reports.' },
    { code: 'report.export', label: 'Export reports', description: 'Download data.' }] },
]

describe('permission helpers', () => {
  it('groups plain codes by the word before the dot when no grouped list is available', () => {
    const grouped = groupFlat(['report.read', 'course.read', 'course.manage'])
    expect(grouped.map((item) => item.title)).toEqual(['Course', 'Report'])
    expect(grouped[0].permissions.map((item) => item.code)).toEqual(['course.manage', 'course.read'])
  })
  it('counts what is chosen in a module', () => {
    expect(countOf(modules[0], ['course.read', 'report.read', 'course.publish'])).toBe(2)
    expect(countOf(modules[1], [])).toBe(0)
  })
  it('chooses or clears a whole module without touching the others or repeating a choice', () => {
    expect(setModule(modules[0], ['report.read', 'course.read'], true)).toEqual(['report.read', 'course.read', 'course.manage', 'course.publish'])
    expect(setModule(modules[0], ['report.read', 'course.read', 'course.manage'], false)).toEqual(['report.read'])
  })
})

describe('PermissionPicker', () => {
  it('shows each module with how many are chosen, and a name, a description and the code for each permission', () => {
    render(<PermissionPicker modules={modules} selected={['course.read']} onChange={vi.fn()} />)
    expect(screen.getByText('Courses and content')).toBeInTheDocument()
    expect(screen.getByText('1 of 3 chosen')).toBeInTheDocument()
    expect(screen.getByText('0 of 2 chosen')).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: /See courses/ })).toBeChecked()
    expect(screen.getByText('Browse courses.')).toBeInTheDocument()
    expect(screen.getByText('course.publish')).toBeInTheDocument()
  })
  it('ticks and unticks one permission', async () => {
    const onChange = vi.fn()
    render(<PermissionPicker modules={modules} selected={['course.read']} onChange={onChange} />)
    await userEvent.click(screen.getByRole('checkbox', { name: /Publish courses/ }))
    expect(onChange).toHaveBeenLastCalledWith(['course.read', 'course.publish'])
    await userEvent.click(screen.getByRole('checkbox', { name: /See courses/ }))
    expect(onChange).toHaveBeenLastCalledWith([])
  })
  it('chooses or clears a whole module, and offers only what makes sense', async () => {
    const onChange = vi.fn()
    render(<PermissionPicker modules={modules} selected={['course.read']} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Choose everything in Courses and content' }))
    expect(onChange).toHaveBeenLastCalledWith(['course.read', 'course.manage', 'course.publish'])
    await userEvent.click(screen.getByRole('button', { name: 'Clear everything in Courses and content' }))
    expect(onChange).toHaveBeenLastCalledWith([])
    expect(screen.getByRole('button', { name: 'Clear everything in Reports' })).toBeDisabled()          // nothing chosen there
  })
  it('can be shown but not changed', () => {
    render(<PermissionPicker modules={modules} selected={[]} onChange={vi.fn()} disabled />)
    expect(screen.getByRole('checkbox', { name: /See courses/ })).toBeDisabled()
  })
})

describe('PermissionSummary', () => {
  it('lists only the modules a role gives something in, with what it allows', () => {
    render(<PermissionSummary modules={modules} granted={['course.read', 'course.publish']} />)
    const section = screen.getByRole('region', { name: 'Courses and content' })
    expect(within(section).getByText('2 of 3')).toBeInTheDocument()
    expect(within(section).getByText('See courses')).toBeInTheDocument()
    expect(within(section).queryByText('Create and edit courses')).toBeNull()
    expect(screen.queryByRole('region', { name: 'Reports' })).toBeNull()
  })
  it('shows a permission it does not know at the end instead of hiding it', () => {
    render(<PermissionSummary modules={modules} granted={['course.read', 'old.thing']} />)
    expect(within(screen.getByRole('region', { name: 'Other' })).getByText('old.thing')).toBeInTheDocument()
  })
})

// ---------- on the roles page ----------
const request = vi.fn()
vi.mock('@/lib/api', async () => ({ ...(await vi.importActual<typeof import('@/lib/api')>('@/lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions: ['role.manage', 'user.manage'] } }) }))
vi.mock('../lib/auth', () => ({ useAuth: () => ({ session: { permissions: ['role.manage', 'user.manage'] } }) }))
vi.mock('../lib/api', async () => ({ ...(await vi.importActual<typeof import('../lib/api')>('../lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))

const roles = [
  { id: 'r1', code: 'TENANT_ADMIN', name: 'Tenant Administrator', isSystemRole: true, permissions: ['course.read', 'course.publish', 'report.read'] },
  { id: 'r2', code: 'AUDITOR', name: 'Auditor', isSystemRole: false, permissions: ['course.read'] },
]
const calls = (method: string) => request.mock.calls.filter(([, options]) => (options as { method?: string } | undefined)?.method === method)

describe('roles by module', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path: string, options?: { method?: string; body?: string }) => {
      if (path.endsWith('/roles') && !options?.method) return Promise.resolve(roles)
      if (path.endsWith('/users')) return Promise.resolve([])
      if (path.endsWith('/permission-catalog')) return Promise.resolve(modules)
      if (path.endsWith('/permissions')) return Promise.resolve(['course.read', 'course.manage', 'course.publish', 'report.read', 'report.export'])
      return Promise.resolve({})
    })
  })

  it('shows what a role grants by module', async () => {
    render(<RbacPage initialTab="roles" />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Tenant Administrator' }))
    const panel = screen.getByRole('dialog', { name: 'Role: Tenant Administrator' })
    expect(within(panel).getByRole('region', { name: 'Courses and content' })).toHaveTextContent('Publish courses')
    expect(within(panel).getByRole('region', { name: 'Reports' })).toHaveTextContent('See reports')
    expect(within(panel).queryByRole('button', { name: 'Edit role' })).toBeNull()                       // a system role cannot be edited
    expect(within(panel).getByText(/System roles cannot be edited/)).toBeInTheDocument()
  })

  it('creates a role by choosing permissions module by module', async () => {
    render(<RbacPage initialTab="roles" />)
    await screen.findByText('Auditor')
    await userEvent.click(screen.getByRole('button', { name: 'New role' }))
    const dialog = screen.getByRole('dialog', { name: 'New role' })
    await userEvent.type(within(dialog).getByLabelText('Role code'), 'EDITOR')
    await userEvent.type(within(dialog).getByLabelText('Role name'), 'Editor')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Choose everything in Reports' }))
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /Publish courses/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create role' }))
    const body = JSON.parse((calls('POST')[0][1] as { body: string }).body)
    expect(body.permissions.sort()).toEqual(['collaboration.read', 'course.publish', 'course.read', 'liveclass.read', 'report.export', 'report.read'].sort())
  })

  it('changes a custom role', async () => {
    render(<RbacPage initialTab="roles" />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Auditor' }))
    await userEvent.click(screen.getByRole('button', { name: 'Edit role' }))
    const dialog = screen.getByRole('dialog', { name: 'Edit role' })
    expect(within(dialog).getByLabelText('Role code')).toHaveValue('AUDITOR')
    expect(within(dialog).getByRole('checkbox', { name: /See courses/ })).toBeChecked()
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /See reports/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save role' }))
    expect(calls('PUT')[0][0]).toBe('/api/v1/tenant/roles/r2')
    expect(JSON.parse((calls('PUT')[0][1] as { body: string }).body)).toEqual({ code: 'AUDITOR', name: 'Auditor', permissions: ['course.read', 'report.read'] })
    expect(await screen.findByText('Role “Auditor” saved.')).toBeInTheDocument()
  })

  it('still works, grouped by the first word of each code, when the grouped list cannot be read', async () => {
    const base = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/permission-catalog') ? Promise.reject(new Error('down')) : base(path, options))
    render(<RbacPage initialTab="roles" />)
    await screen.findByText('Auditor')
    await userEvent.click(screen.getByRole('button', { name: 'New role' }))
    const dialog = screen.getByRole('dialog', { name: 'New role' })
    expect(await within(dialog).findByText('Course')).toBeInTheDocument()
    expect(within(dialog).getByRole('checkbox', { name: /course\.publish/ })).toBeInTheDocument()
  })
})
