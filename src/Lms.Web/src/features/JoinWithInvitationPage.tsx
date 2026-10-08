import { useEffect, useState } from 'react'
import { GraduationCap } from 'lucide-react'
import { ErrorBanner, Field, FormLayout } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { clearInviteHash, type InviteLink } from '@/lib/inviteLink'

export type Preview = { organizationName: string; tenantSlug: string; courseTitle: string; email: string; invitedBy: string; message: string | null; expiresAtUtc: string; hasAccount: boolean; isMember?: boolean }
type Registered = { email: string; courseId: string; courseTitle: string; outcome: string; message: string | null }

const publicRequest = <T,>(slug: string, path: string, body: object) =>
  apiRequest<T>(`/api/v1/tenant/invitations/public/${path}`, { method: 'POST', headers: { 'X-Tenant-Slug': slug }, body: JSON.stringify(body) })

/** A person with no account yet: confirm the invitation, choose a password, and land signed in and enrolled. */
export default function JoinWithInvitationPage({ link, onBack }: { link: InviteLink | null; onBack: () => void }) {
  const { login } = useAuth()
  const [tenant, setTenant] = useState(link?.tenant ?? '')
  const [token, setToken] = useState(link?.token ?? '')
  const [preview, setPreview] = useState<Preview | null>(null)
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function check(slug = tenant, code = token) {
    setBusy(true); setError(null)
    try { setPreview(await publicRequest<Preview>(slug.trim(), 'lookup', { token: code.trim() })) }
    catch (exception) { setPreview(null); setError(exception instanceof ApiError ? exception.message : 'Unable to check the invitation.') }
    finally { setBusy(false) }
  }
  useEffect(() => { if (link) void check(link.tenant, link.token) }, [])

  async function join(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!preview) return
    // Someone with an account in another organization joins with that account's password instead of choosing a new one.
    const joinsExisting = preview.hasAccount && preview.isMember === false
    if (joinsExisting) { if (!password) { setError('Enter the password of your existing account.'); return } }
    else {
      if (!name.trim()) { setError('Enter your name.'); return }
      if (password.length < 8) { setError('Choose a password of at least 8 characters.'); return }
    }
    setBusy(true); setError(null)
    try {
      await publicRequest<Registered>(preview.tenantSlug, 'register', joinsExisting ? { token: token.trim(), password } : { token: token.trim(), displayName: name, password })
      clearInviteHash()
      await login(preview.tenantSlug, preview.email, password)
    } catch (exception) { setError(exception instanceof ApiError ? exception.message : 'Unable to create your account.') }
    finally { setBusy(false) }
  }

  const leave = () => { clearInviteHash(); onBack() }

  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-4 text-foreground">
      <Card className="w-full max-w-md">
        <CardHeader>
          <div className="mb-2 flex h-10 w-10 items-center justify-center rounded-lg bg-primary text-primary-foreground"><GraduationCap className="h-5 w-5" /></div>
          <CardTitle className="text-xl">{preview ? `Join ${preview.courseTitle}` : 'Join with an invitation'}</CardTitle>
          <CardDescription>{preview ? `${preview.invitedBy} invited you to ${preview.courseTitle} at ${preview.organizationName}.` : 'Enter the organization and the invitation code you were sent.'}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          {!preview ? (
            <FormLayout className="gap-4" onSubmit={(event) => { event.preventDefault(); if (!tenant.trim()) { setError('Enter the organization.'); return } if (!token.trim()) { setError('Enter the invitation code.'); return } void check() }}>
              <ErrorBanner message={error} />
              <Field id="join-tenant" label="Organization (tenant slug)" required><Input id="join-tenant" value={tenant} onChange={(e) => setTenant(e.target.value)} /></Field>
              <Field id="join-code" label="Invitation code" required><Input id="join-code" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} /></Field>
              <Button type="submit" disabled={busy}>{busy ? 'Checking…' : 'Continue'}</Button>
            </FormLayout>
          ) : preview.hasAccount && preview.isMember !== false ? (
            <div className="flex flex-col gap-3 text-sm">
              <ErrorBanner message={error} />
              <p>There is already an account for <strong>{preview.email}</strong>. Sign in with it, then open <em>Invitations</em> to accept.</p>
              <Button onClick={leave}>Go to sign in</Button>
            </div>
          ) : preview.hasAccount ? (
            <FormLayout className="gap-4" onSubmit={join}>
              <ErrorBanner message={error} />
              {preview.message ? <p className="whitespace-pre-wrap rounded-md border border-border p-3 text-sm">{preview.message}</p> : null}
              <p className="text-sm">You already have an account for <strong>{preview.email}</strong> with another organization. Enter its password to join {preview.organizationName} with it.</p>
              <Field id="join-email" label="Email"><Input id="join-email" value={preview.email} readOnly /></Field>
              <Field id="join-existing-password" label="Your existing password" required><Input id="join-existing-password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
              <Button type="submit" disabled={busy}>{busy ? 'Joining…' : 'Join with my account'}</Button>
              <small className="text-muted-foreground">Forgotten it? Reset it from the sign-in page of your other organization first.</small>
            </FormLayout>
          ) : (
            <FormLayout className="gap-4" onSubmit={join}>
              <ErrorBanner message={error} />
              {preview.message ? <p className="whitespace-pre-wrap rounded-md border border-border p-3 text-sm">{preview.message}</p> : null}
              <Field id="join-email" label="Email"><Input id="join-email" value={preview.email} readOnly /></Field>
              <Field id="join-name" label="Your name" required><Input id="join-name" autoComplete="name" maxLength={200} value={name} onChange={(e) => setName(e.target.value)} /></Field>
              <Field id="join-password" label="Choose a password" required hint="At least 8 characters."><Input id="join-password" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
              <Button type="submit" disabled={busy}>{busy ? 'Creating your account…' : 'Create account and join'}</Button>
            </FormLayout>
          )}
          {!(preview?.hasAccount && preview.isMember !== false) ? <button type="button" className="text-sm text-muted-foreground underline" onClick={leave}>Back to sign in</button> : null}
        </CardContent>
      </Card>
    </div>
  )
}
