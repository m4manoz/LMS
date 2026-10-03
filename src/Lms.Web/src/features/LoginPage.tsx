import { useState } from 'react'
import { GraduationCap } from 'lucide-react'
import { ErrorBanner, Field, FormLayout } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import type { PublicOrganization } from '@/lib/publicApi'

const lastOrganizationKey = 'lms-last-organization'
const rememberedOrganization = () => { try { return localStorage.getItem(lastOrganizationKey) ?? '' } catch { return '' } }

export default function LoginPage({ onJoin, onForgot, onBack, initialTenant, locked }: { onJoin?: () => void; onForgot?: () => void; onBack?: () => void; initialTenant?: string; locked?: PublicOrganization } = {}) {
  const { login } = useAuth()
  const [tenantSlug, setTenantSlug] = useState(locked?.slug ?? initialTenant ?? rememberedOrganization())
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const problem = !tenantSlug.trim() ? 'Enter your organization.' : !email.trim() ? 'Enter your email.' : !password ? 'Enter your password.' : null
    if (problem) { setError(problem); return }
    setBusy(true)
    setError(null)
    try {
      await login(tenantSlug, email, password)
      try { localStorage.setItem(lastOrganizationKey, tenantSlug.trim()) } catch { /* private mode: nothing to remember */ }
    } catch (exception) {
      setError(exception instanceof ApiError ? exception.message : 'Unable to sign in.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-4 text-foreground">
      <Card className="w-full max-w-md">
        <CardHeader>
          <div className="mb-2 flex h-10 w-10 items-center justify-center rounded-lg bg-primary text-primary-foreground">
            <GraduationCap className="h-5 w-5" />
          </div>
          <CardTitle className="text-xl">Sign in to your workspace</CardTitle>
          <CardDescription>{locked ? `Use your ${locked.name} account to continue.` : 'Use your organization account to continue.'}</CardDescription>
        </CardHeader>
        <CardContent>
          <FormLayout className="gap-4" onSubmit={submit}>
            <ErrorBanner message={error} />
            {locked ? null : <Field id="tenant" label="Organization (tenant slug)" required><Input id="tenant" value={tenantSlug} onChange={(e) => setTenantSlug(e.target.value)} /></Field>}
            <Field id="email" label="Email" required><Input id="email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
            <Field id="password" label="Password" required><Input id="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
            <Button type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</Button>
            {onForgot ? <button type="button" className="text-sm text-muted-foreground underline" onClick={onForgot}>Forgot your password?</button> : null}
            {onBack ? <button type="button" className="text-sm text-muted-foreground underline" onClick={onBack}>Back to the home page</button> : null}
            {onJoin ? <button type="button" className="text-sm text-muted-foreground underline" onClick={onJoin}>Join with an invitation</button> : null}
          </FormLayout>
        </CardContent>
      </Card>
    </div>
  )
}
