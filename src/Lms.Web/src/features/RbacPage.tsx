import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { ApiError, apiRequest } from '../lib/api'
import { useAuth } from '../lib/auth'
import { groupFlat, PermissionPicker, PermissionSummary, type PermissionModule } from './PermissionGroups'

type Role = { id: string; code: string; name: string; isSystemRole: boolean; permissions: string[] }
type User = { id: string; email: string; displayName: string; userStatus: string; membershipStatus: string; roleCode: string; roleName: string; roleCodes?: string[] }
type AuditEvent = { id: string; actorUserId: string | null; action: string; resourceType: string; resourceId: string | null; detailsJson: string; ipAddress: string | null; createdAtUtc: string }

const defaultPermissions = ['course.read', 'liveclass.read', 'collaboration.read']

export default function RbacPage({ initialTab = 'users' }: { initialTab?: 'users' | 'roles' | 'audit' }) {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('role.manage') ?? false
  const canManageUsers = session?.permissions.includes('user.manage') ?? false
  const canAudit = session?.permissions.includes('security.read') ?? false
  const [roles, setRoles] = useState<Role[]>([])
  const [users, setUsers] = useState<User[]>([])
  const [permissions, setPermissions] = useState<string[]>([])
  const [catalog, setCatalog] = useState<PermissionModule[] | null>(null)
  const [editingRole, setEditingRole] = useState<Role | null>(null)
  const [audit, setAudit] = useState<AuditEvent[]>([])
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [selectedPermissions, setSelectedPermissions] = useState<string[]>(defaultPermissions)
  const [creatingRole, setCreatingRole] = useState(false)
  const [creatingUser, setCreatingUser] = useState(false)
  const [viewRole, setViewRole] = useState<Role | null>(null)
  const [userEmail, setUserEmail] = useState('')
  const [userName, setUserName] = useState('')
  const [userPassword, setUserPassword] = useState('')
  const [userRole, setUserRole] = useState('')
  const [userSearch, setUserSearch] = useState('')
  const [roleSearch, setRoleSearch] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [tab, setTab] = useState<string>(initialTab)
  const [error, setError] = useState<string | null>(null)

  async function refresh() {
    try {
      const requests: Promise<unknown>[] = [apiRequest<Role[]>('/api/v1/tenant/roles'), apiRequest<User[]>('/api/v1/tenant/users'), apiRequest<string[]>('/api/v1/tenant/security/permissions')]
      if (canAudit) requests.push(apiRequest<AuditEvent[]>('/api/v1/tenant/security/audit-events'))
      const values = await Promise.all(requests)
      // The same permissions grouped by module with plain names; if that list cannot be read the codes are grouped by their first word.
      apiRequest<PermissionModule[]>('/api/v1/tenant/security/permission-catalog').then((list) => setCatalog(Array.isArray(list) ? list : null)).catch(() => setCatalog(null))
      setRoles(values[0] as Role[]); setUsers(values[1] as User[]); setPermissions(values[2] as string[]); if (canAudit) setAudit(values[3] as AuditEvent[])
    } catch (exception) { setError(readError(exception, 'Unable to load access control data.')) }
  }

  useEffect(() => { void refresh() }, [canAudit])

  const modules = catalog ?? groupFlat(permissions)
  const openRole = () => { setEditingRole(null); setCode(''); setName(''); setSelectedPermissions(defaultPermissions); setProblem(null); setNotice(null); setCreatingRole(true) }
  const openEdit = (role: Role) => { setEditingRole(role); setCode(role.code); setName(role.name); setSelectedPermissions(role.permissions); setProblem(null); setNotice(null); setViewRole(null); setCreatingRole(true) }
  const closeRole = () => { setCreatingRole(false); setEditingRole(null) }
  const openUser = () => { setUserEmail(''); setUserName(''); setUserPassword(''); setUserRole(''); setProblem(null); setNotice(null); setCreatingUser(true) }
  const closeUser = () => setCreatingUser(false)

  async function createRole(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!code.trim()) { setProblem('Enter a role code.'); return }
    if (!name.trim()) { setProblem('Enter a role name.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      if (editingRole) await apiRequest(`/api/v1/tenant/roles/${editingRole.id}`, { method: 'PUT', body: JSON.stringify({ code, name, permissions: selectedPermissions }) })
      else await apiRequest('/api/v1/tenant/roles', { method: 'POST', body: JSON.stringify({ code, name, permissions: selectedPermissions }) })
      setCreatingRole(false); setNotice(`Role “${name}” ${editingRole ? 'saved' : 'created'}.`); setEditingRole(null); await refresh(); setTab('roles')
    }
    catch (exception) { setProblem(readError(exception, editingRole ? 'Unable to save the role.' : 'Unable to create the role.')) }
    finally { setBusy(false) }
  }

  async function createUser(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!/^\S+@\S+\.\S+$/.test(userEmail.trim())) { setProblem('Enter a valid email address.'); return }
    if (!userName.trim()) { setProblem('Enter the display name.'); return }
    if (userPassword.length < 8 || userPassword.length > 200) { setProblem('Password must contain between 8 and 200 characters.'); return }
    if (!userRole) { setProblem('Choose a role.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      await apiRequest('/api/v1/tenant/users', { method: 'POST', body: JSON.stringify({ email: userEmail.trim(), displayName: userName.trim(), password: userPassword, roleCode: userRole }) })
      setCreatingUser(false); setNotice(`User “${userName.trim()}” created.`); await refresh(); setTab('users')
    } catch (exception) { setProblem(readError(exception, 'Unable to create the user.')) }
    finally { setBusy(false) }
  }

  async function assignRole(userId: string, roleId: string) {
    if (!roleId) return
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(`/api/v1/tenant/users/${userId}/role`, { method: 'PUT', body: JSON.stringify({ roleId }) }); await refresh() }
    catch (exception) { setError(readError(exception, 'Unable to assign the role.')) }
    finally { setBusy(false) }
  }

  const userNeedle = userSearch.trim().toLowerCase()
  const shownUsers = users.filter(user => !userNeedle || `${user.displayName} ${user.email} ${(user.roleCodes ?? [user.roleCode]).join(' ')}`.toLowerCase().includes(userNeedle))
  const roleNeedle = roleSearch.trim().toLowerCase()
  const shownRoles = roles.filter(role => !roleNeedle || `${role.name} ${role.code}`.toLowerCase().includes(roleNeedle))

  const primary = tab === 'users' && canManageUsers
    ? <Button onClick={openUser}><Plus className="mr-1 h-4 w-4" aria-hidden />New user</Button>
    : tab === 'roles' && canManage ? <Button onClick={openRole}><Plus className="mr-1 h-4 w-4" aria-hidden />New role</Button> : null

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Access control" description="Permissions are tenant-scoped and enforced by the API. A new role takes effect when the user signs in again."
        actions={<><Button variant="outline" onClick={() => void refresh()}>Refresh</Button>{primary}</>} />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList>
          <TabsTrigger value="users">Users ({users.length})</TabsTrigger>
          <TabsTrigger value="roles">Roles ({roles.length})</TabsTrigger>
          {canAudit ? <TabsTrigger value="audit">Audit trail</TabsTrigger> : null}
        </TabsList>

        <TabsContent value="users">
          <div className="flex flex-col gap-4">
            <Input className="max-w-sm" type="search" placeholder="Search users" aria-label="Search users" value={userSearch} onChange={event => setUserSearch(event.target.value)} />
            {users.length === 0 ? <EmptyState>No users yet.</EmptyState> : shownUsers.length === 0 ? <EmptyState>No users match.</EmptyState> : (
              <RowList label="Users">
                {shownUsers.map(user => {
                  const assignedRoles = user.roleCodes ?? [user.roleCode]
                  return (
                    <ListRow key={user.id} columns="md:grid-cols-[minmax(0,2fr)_110px_minmax(0,1fr)_170px]">
                      <div className="min-w-0"><strong className="block truncate">{user.displayName}</strong><small className="text-muted-foreground">{user.email}</small></div>
                      <div><Badge variant={user.membershipStatus === 'Active' ? 'default' : 'secondary'}>{user.membershipStatus}</Badge></div>
                      <div className="hidden min-w-0 truncate text-muted-foreground md:block">{assignedRoles.join(', ')}</div>
                      <div className="flex justify-end">
                        {canManage ? (
                          <Select className="w-40" aria-label={`Add role for ${user.displayName}`} value="" disabled={busy} onChange={event => void assignRole(user.id, event.target.value)}>
                            <option value="">Add role…</option>
                            {roles.filter(role => !assignedRoles.includes(role.code)).map(role => <option key={role.id} value={role.id}>{role.name}</option>)}
                          </Select>
                        ) : <Badge>{user.roleName}</Badge>}
                      </div>
                    </ListRow>
                  )
                })}
              </RowList>
            )}
          </div>
        </TabsContent>

        <TabsContent value="roles">
          <div className="flex flex-col gap-4">
            <Input className="max-w-sm" type="search" placeholder="Search roles" aria-label="Search roles" value={roleSearch} onChange={event => setRoleSearch(event.target.value)} />
            {roles.length === 0 ? <EmptyState>No roles yet.</EmptyState> : shownRoles.length === 0 ? <EmptyState>No roles match.</EmptyState> : (
              <RowList label="Roles">
                {shownRoles.map(role => (
                  <ListRow key={role.id} selected={viewRole?.id === role.id} columns="md:grid-cols-[minmax(0,2fr)_130px_150px_auto]">
                    <div className="min-w-0"><strong className="block truncate">{role.name}</strong><small className="text-muted-foreground">{role.code}</small></div>
                    <div><Badge variant={role.isSystemRole ? 'secondary' : 'default'}>{role.isSystemRole ? 'System role' : 'Custom role'}</Badge></div>
                    <div className="hidden text-muted-foreground md:block">{role.permissions.length} permissions</div>
                    <div className="flex justify-end"><Button variant="secondary" size="sm" aria-label={`View details for ${role.name}`} onClick={() => setViewRole(role)}>View details</Button></div>
                  </ListRow>
                ))}
              </RowList>
            )}
          </div>
        </TabsContent>

        {canAudit ? (
          <TabsContent value="audit">
            {audit.length === 0 ? <EmptyState>No security events recorded yet.</EmptyState> : (
              <RowList label="Security audit trail">
                {audit.map(item => (
                  <ListRow key={item.id}>
                    <div className="min-w-0">
                      <strong className="block truncate">{item.action}</strong>
                      <small className="text-muted-foreground">{item.resourceType}{item.resourceId ? ` · ${item.resourceId}` : ''} · {formatDate(item.createdAtUtc)}</small>
                    </div>
                  </ListRow>
                ))}
              </RowList>
            )}
          </TabsContent>
        ) : null}
      </Tabs>

      <SidePanel open={viewRole !== null} label={viewRole ? `Role: ${viewRole.name}` : 'Role'} onClose={() => setViewRole(null)}>
        {viewRole ? (
          <div className="flex max-w-3xl flex-col gap-4">
            <div><h3 className="text-lg font-semibold">{viewRole.name}</h3><p className="text-sm text-muted-foreground">{viewRole.code} · {viewRole.isSystemRole ? 'System role' : 'Custom role'}</p></div>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h4 className="text-sm font-semibold">Permissions ({viewRole.permissions.length})</h4>
              {canManage && !viewRole.isSystemRole ? <Button type="button" size="sm" variant="secondary" onClick={() => openEdit(viewRole)}>Edit role</Button> : null}
            </div>
            {viewRole.permissions.length === 0 ? <EmptyState>This role grants no permissions.</EmptyState> : <PermissionSummary modules={modules} granted={viewRole.permissions} />}
            {viewRole.isSystemRole ? <p className="text-sm text-muted-foreground">System roles cannot be edited. Create a custom role to give a different set of permissions.</p> : null}
          </div>
        ) : null}
      </SidePanel>

      <SidePanel open={creatingRole} label={editingRole ? 'Edit role' : 'New role'} onClose={closeRole}>
        <FormLayout onSubmit={createRole}>
          <ErrorBanner message={problem} />
          <FormSection title="About the role" description="Pick the permissions the role grants, module by module. Changes take effect when the people with this role sign in again.">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field id="role-code" label="Role code" required><Input id="role-code" value={code} onChange={event => setCode(event.target.value)} placeholder="ROLE_CODE" maxLength={80} /></Field>
              <Field id="role-name" label="Role name" required><Input id="role-name" value={name} onChange={event => setName(event.target.value)} placeholder="Role name" maxLength={150} /></Field>
            </div>
          </FormSection>
          <FormSection title="Permissions">
            <p className="text-sm text-muted-foreground">{selectedPermissions.length} permission{selectedPermissions.length === 1 ? '' : 's'} chosen.</p>
            <PermissionPicker modules={modules} selected={selectedPermissions} onChange={setSelectedPermissions} />
          </FormSection>
          <FormActions busy={busy} submitLabel={editingRole ? 'Save role' : 'Create role'} onCancel={closeRole} />
        </FormLayout>
      </SidePanel>

      <SidePanel open={creatingUser} label="New user" onClose={closeUser}>
        <FormLayout onSubmit={createUser}>
          <ErrorBanner message={problem} />
          <FormSection title="About the user" description="The user signs in with this email and password.">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field id="user-name" label="Display name" required><Input id="user-name" value={userName} onChange={event => setUserName(event.target.value)} maxLength={150} /></Field>
              <Field id="user-email" label="Email" required><Input id="user-email" type="email" value={userEmail} onChange={event => setUserEmail(event.target.value)} /></Field>
            </div>
            <Field id="user-password" label="Password" required hint="Between 8 and 200 characters."><Input id="user-password" type="password" autoComplete="new-password" value={userPassword} onChange={event => setUserPassword(event.target.value)} /></Field>
          </FormSection>
          <FormSection title="Access">
            <Field id="user-role" label="Role" required>
              <Select id="user-role" value={userRole} onChange={event => setUserRole(event.target.value)}>
                <option value="">Choose a role…</option>
                {roles.map(role => <option key={role.id} value={role.code}>{role.name}</option>)}
              </Select>
            </Field>
          </FormSection>
          <FormActions busy={busy} submitLabel="Create user" onCancel={closeUser} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}

function formatDate(value: string) { return new Date(value).toLocaleString() }
function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
