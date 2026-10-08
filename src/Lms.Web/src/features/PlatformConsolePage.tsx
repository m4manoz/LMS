import { useCallback, useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { ApiError } from '@/lib/api'
import {
  changeTenantStatus, createTenant, getPlatformKey, getTenant, listTenants, renameTenant, setPlatformKey,
  type PlatformTenant, type PlatformTenantDetail,
} from '@/lib/platformApi'

const emptyForm = { name: '', slug: '', adminEmail: '', adminName: '', adminPassword: '' }
const statusTone: Record<string, 'default' | 'secondary' | 'destructive'> = { Active: 'default', Suspended: 'destructive', Archived: 'secondary' }
const readError = (exception: unknown, fallback: string) => (exception instanceof Error && exception.message ? exception.message : fallback)

/**
 * The platform operator's console: every organization on this installation, with its size and status.
 * It is not part of any organization's sign-in; the operator proves who they are with the platform key.
 */
export default function PlatformConsolePage({ onExit }: { onExit?: () => void }) {
  const [key, setKey] = useState(getPlatformKey)
  const [draftKey, setDraftKey] = useState('')
  const [tenants, setTenants] = useState<PlatformTenant[] | null>(null)
  const [q, setQ] = useState('')
  const [status, setStatus] = useState('')
  const [detail, setDetail] = useState<PlatformTenantDetail | null>(null)
  const [renameTo, setRenameTo] = useState('')
  const [creating, setCreating] = useState(false)
  const [form, setForm] = useState(emptyForm)
  const [problem, setProblem] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const signOut = useCallback((message: string | null) => { setPlatformKey(null); setKey(''); setTenants(null); setDetail(null); setError(message) }, [])

  const load = useCallback(async () => {
    if (!key) return
    try { setTenants(await listTenants(key, q, status)) }
    catch (exception) {
      if (exception instanceof ApiError && exception.status === 401) signOut(exception.message)
      else setError(readError(exception, 'Unable to load organizations.'))
    }
  }, [key, q, status, signOut])
  useEffect(() => {
    const timer = window.setTimeout(() => { void load() }, 250)
    return () => window.clearTimeout(timer)
  }, [load])

  async function run(action: () => Promise<void>, failure: string, report: (message: string) => void = setError) {
    setBusy(true); setError(null); setProblem(null); setNotice(null)
    try { await action() } catch (exception) { report(readError(exception, failure)) } finally { setBusy(false) }
  }

  const unlock = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!draftKey.trim()) return
    setPlatformKey(draftKey.trim()); setKey(draftKey.trim()); setDraftKey(''); setError(null)
  }

  const open = (slug: string) => run(async () => {
    const found = await getTenant(key, slug)
    setCreating(false); setDetail(found); setRenameTo(found.name)
  }, 'Unable to open the organization.')

  const change = (tenant: PlatformTenant, action: 'suspend' | 'activate' | 'archive') => {
    const consequence = action === 'activate' ? `Open ${tenant.name} again?`
      : `${action === 'suspend' ? 'Suspend' : 'Archive'} ${tenant.name}? Nobody in it can sign in or use it, and everyone is signed out. Nothing is deleted.`
    if (!window.confirm(consequence)) return
    void run(async () => {
      await changeTenantStatus(key, tenant.slug, action)
      setNotice(`${tenant.name} is now ${action === 'activate' ? 'active' : action === 'suspend' ? 'suspended' : 'archived'}.`)
      await load()
      if (detail?.slug === tenant.slug) setDetail(await getTenant(key, tenant.slug))
    }, 'Unable to change the organization.')
  }

  const rename = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!detail) return
    if (!renameTo.trim()) { setProblem('Enter a name.'); return }
    void run(async () => {
      await renameTenant(key, detail.slug, renameTo.trim())
      setNotice('Name saved.')
      setDetail(await getTenant(key, detail.slug)); await load()
    }, 'Unable to rename the organization.', setProblem)
  }

  const create = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const slug = form.slug.trim().toLowerCase()
    if (!form.name.trim()) { setProblem('Enter the organization’s name.'); return }
    if (!/^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$/.test(slug)) { setProblem('The short name uses 3–63 lowercase letters, numbers and hyphens.'); return }
    if (form.adminEmail.trim() && !form.adminPassword) { setProblem('Enter a password for the first administrator.'); return }
    void run(async () => {
      await createTenant(key, { name: form.name.trim(), slug, adminEmail: form.adminEmail.trim() || undefined, adminName: form.adminName.trim() || undefined, adminPassword: form.adminPassword })
      setCreating(false); setForm(emptyForm); setNotice(`${form.name.trim()} was created.`)
      await load()
    }, 'Unable to create the organization.', setProblem)
  }

  if (!key) {
    return (
      <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-6 px-4">
        <PageHeader title="Platform console" description="Manage the organizations on this installation." />
        <ErrorBanner message={error} />
        <FormLayout onSubmit={unlock} noValidate>
          <Field id="platform-key" label="Platform key" required hint="Kept in this browser tab only.">
            <Input id="platform-key" type="password" autoComplete="off" value={draftKey} onChange={(event) => setDraftKey(event.target.value)} />
          </Field>
          <FormActions submitLabel="Open console" disabled={!draftKey.trim()} onCancel={onExit} />
        </FormLayout>
      </main>
    )
  }

  const panelOpen = creating || detail !== null
  const closePanel = () => { setCreating(false); setDetail(null); setProblem(null) }

  return (
    <main className="mx-auto flex max-w-5xl flex-col gap-5 px-4 py-8">
      <PageHeader
        title="Organizations"
        description="Every organization on this platform. Suspending or archiving keeps all data and signs everyone out."
        actions={<>
          <Button onClick={() => { setForm(emptyForm); setProblem(null); setDetail(null); setCreating(true) }}><Plus aria-hidden className="mr-1 h-4 w-4" />New organization</Button>
          <Button variant="outline" onClick={() => { signOut(null); onExit?.() }}>Close console</Button>
        </>}
      />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      <div className="flex flex-wrap gap-3">
        <Input aria-label="Search organizations" placeholder="Search by name or short name" className="max-w-xs" value={q} onChange={(event) => setQ(event.target.value)} />
        <Select aria-label="Filter by status" className="max-w-[10rem]" value={status} onChange={(event) => setStatus(event.target.value)}>
          <option value="">All statuses</option><option value="Active">Active</option><option value="Suspended">Suspended</option><option value="Archived">Archived</option>
        </Select>
      </div>

      {tenants === null ? <p role="status" className="text-sm text-muted-foreground">Loading…</p>
        : tenants.length === 0 ? <EmptyState>No organizations match.</EmptyState>
          : (
            <RowList label="Organizations">
              {tenants.map((tenant) => (
                <ListRow key={tenant.id} selected={detail?.slug === tenant.slug} columns="md:grid-cols-[minmax(0,2fr)_110px_140px_auto]">
                  <div className="min-w-0">
                    <button type="button" className="truncate text-left font-medium hover:underline" onClick={() => open(tenant.slug)}>{tenant.name}</button>
                    <p className="truncate text-xs text-muted-foreground">{tenant.slug}</p>
                  </div>
                  <Badge variant={statusTone[tenant.status] ?? 'outline'}>{tenant.status}</Badge>
                  <span className="text-xs text-muted-foreground">{tenant.members} people · {tenant.courses} courses</span>
                  <div className="flex gap-2">
                    {tenant.status === 'Active'
                      ? <Button size="sm" variant="outline" disabled={busy} aria-label={`Suspend ${tenant.name}`} onClick={() => change(tenant, 'suspend')}>Suspend</Button>
                      : <Button size="sm" variant="outline" disabled={busy} aria-label={`Activate ${tenant.name}`} onClick={() => change(tenant, 'activate')}>Activate</Button>}
                    {tenant.status !== 'Archived'
                      ? <Button size="sm" variant="outline" disabled={busy} aria-label={`Archive ${tenant.name}`} onClick={() => change(tenant, 'archive')}>Archive</Button> : null}
                  </div>
                </ListRow>
              ))}
            </RowList>
          )}

      <SidePanel open={panelOpen} label={creating ? 'New organization' : detail?.name ?? 'Organization'} onClose={closePanel}>
          {creating ? (
            <FormLayout onSubmit={create} noValidate>
              <ErrorBanner message={problem} />
              <FormSection title="Organization" divider={false}>
                <Field id="org-name" label="Name" required><Input id="org-name" value={form.name} maxLength={200} onChange={(event) => setForm({ ...form, name: event.target.value })} /></Field>
                <Field id="org-slug" label="Short name" required hint="Used to sign in and in its web address. It cannot be changed later."><Input id="org-slug" value={form.slug} maxLength={63} onChange={(event) => setForm({ ...form, slug: event.target.value })} /></Field>
              </FormSection>
              <FormSection title="First administrator" description="Optional. Without one, nobody can sign in yet.">
                <Field id="admin-email" label="Email"><Input id="admin-email" type="email" value={form.adminEmail} onChange={(event) => setForm({ ...form, adminEmail: event.target.value })} /></Field>
                <Field id="admin-name" label="Name"><Input id="admin-name" value={form.adminName} onChange={(event) => setForm({ ...form, adminName: event.target.value })} /></Field>
                <Field id="admin-password" label="Password"><Input id="admin-password" type="password" autoComplete="new-password" value={form.adminPassword} onChange={(event) => setForm({ ...form, adminPassword: event.target.value })} /></Field>
              </FormSection>
              <FormActions busy={busy} submitLabel="Create organization" busyLabel="Creating…" onCancel={closePanel} />
            </FormLayout>
          ) : detail ? (
            <div className="flex flex-col gap-6">
              <dl className="grid grid-cols-2 gap-3 text-sm">
                <div><dt className="text-muted-foreground">Short name</dt><dd>{detail.slug}</dd></div>
                <div><dt className="text-muted-foreground">Status</dt><dd>{detail.status}</dd></div>
                <div><dt className="text-muted-foreground">People</dt><dd>{detail.members}</dd></div>
                <div><dt className="text-muted-foreground">Courses</dt><dd>{detail.courses}</dd></div>
                <div><dt className="text-muted-foreground">Enrollments</dt><dd>{detail.enrollments}</dd></div>
                <div><dt className="text-muted-foreground">Created</dt><dd>{new Date(detail.createdAtUtc).toLocaleDateString()}</dd></div>
              </dl>
              <FormLayout onSubmit={rename} noValidate>
                <ErrorBanner message={problem} />
                <FormSection title="Name" divider={false}>
                  <Field id="rename" label="Organization name" required><Input id="rename" value={renameTo} maxLength={200} onChange={(event) => setRenameTo(event.target.value)} /></Field>
                </FormSection>
                <FormActions busy={busy} submitLabel="Save name" />
              </FormLayout>
              <FormSection title="Administrators">
                {detail.admins.length === 0 ? <p className="text-sm text-muted-foreground">No administrator yet.</p>
                  : <ul className="text-sm">{detail.admins.map((admin) => <li key={admin.userId}>{admin.displayName} · {admin.email}{admin.status !== 'Active' ? ` (${admin.status})` : ''}</li>)}</ul>}
              </FormSection>
              {detail.storageBucket ? (
                <FormSection title="File storage">
                  <p className="text-sm">{detail.ownBucket ? 'Its own bucket' : 'The shared bucket'}: <strong>{detail.storageBucket}</strong></p>
                </FormSection>
              ) : null}
              <FormSection title="Website addresses">
                {detail.domains.length === 0 ? <p className="text-sm text-muted-foreground">None. It is reached through the shared portal.</p>
                  : <ul className="text-sm">{detail.domains.map((host) => <li key={host}>{host}</li>)}</ul>}
              </FormSection>
            </div>
          ) : null}
      </SidePanel>
    </main>
  )
}
