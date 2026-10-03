import { useState } from 'react'
import { KeyRound } from 'lucide-react'
import { ErrorBanner, Field, FormLayout } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, apiRequest } from '@/lib/api'
import { clearInviteHash, type InviteLink } from '@/lib/inviteLink'

type Step = 'request' | 'sent' | 'code' | 'done'

/** Forgotten password: ask for a code by email, then choose a new password with it. */
export default function PasswordResetPage({ link, onBack }: { link: InviteLink | null; onBack: () => void }) {
  const [step, setStep] = useState<Step>(link ? 'code' : 'request')
  const [tenant, setTenant] = useState(link?.tenant ?? '')
  const [email, setEmail] = useState('')
  const [code, setCode] = useState(link?.token ?? '')
  const [password, setPassword] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function run(action: () => Promise<void>, failure: string) {
    setBusy(true); setError(null)
    try { await action() } catch (exception) { setError(exception instanceof ApiError ? exception.message : failure) } finally { setBusy(false) }
  }

  const request = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!tenant.trim()) { setError('Enter the organization.'); return }
    if (!email.trim()) { setError('Enter your email.'); return }
    return run(async () => {
      const result = await apiRequest<{ message: string }>('/api/v1/auth/password-reset/request', { method: 'POST', body: JSON.stringify({ tenantSlug: tenant.trim(), email: email.trim() }) })
      setMessage(result.message); setStep('sent')
    }, 'Unable to send the reset email.')
  }

  const confirm = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!tenant.trim()) { setError('Enter the organization.'); return }
    if (!code.trim()) { setError('Enter the reset code.'); return }
    if (password.length < 8) { setError('Choose a password of at least 8 characters.'); return }
    return run(async () => {
      await apiRequest('/api/v1/auth/password-reset/confirm', { method: 'POST', body: JSON.stringify({ tenantSlug: tenant.trim(), token: code.trim(), newPassword: password }) })
      clearInviteHash(); setStep('done')
    }, 'Unable to reset the password.')
  }

  const leave = () => { clearInviteHash(); onBack() }

  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-4 text-foreground">
      <Card className="w-full max-w-md">
        <CardHeader>
          <div className="mb-2 flex h-10 w-10 items-center justify-center rounded-lg bg-primary text-primary-foreground"><KeyRound className="h-5 w-5" /></div>
          <CardTitle className="text-xl">{step === 'done' ? 'Password changed' : 'Reset your password'}</CardTitle>
          <CardDescription>
            {step === 'request' ? 'Enter your organization and email address and we will send you a code.' : step === 'sent' ? 'Check your email.' : step === 'code' ? 'Enter the code from the email and choose a new password.' : 'You can now sign in with your new password.'}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          {step === 'request' ? (
            <FormLayout className="gap-4" onSubmit={request}>
              <ErrorBanner message={error} />
              <Field id="reset-tenant" label="Organization (tenant slug)" required><Input id="reset-tenant" value={tenant} onChange={(e) => setTenant(e.target.value)} /></Field>
              <Field id="reset-email" label="Email" required><Input id="reset-email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
              <Button type="submit" disabled={busy}>{busy ? 'Sending…' : 'Send reset code'}</Button>
              <button type="button" className="text-sm text-muted-foreground underline" onClick={() => setStep('code')}>I already have a code</button>
            </FormLayout>
          ) : null}

          {step === 'sent' ? (
            <div className="flex flex-col gap-3 text-sm">
              <ErrorBanner message={error} />
              <p role="status">{message}</p>
              <Button onClick={() => setStep('code')}>I have the code</Button>
            </div>
          ) : null}

          {step === 'code' ? (
            <FormLayout className="gap-4" onSubmit={confirm}>
              <ErrorBanner message={error} />
              <Field id="reset-tenant2" label="Organization (tenant slug)" required><Input id="reset-tenant2" value={tenant} onChange={(e) => setTenant(e.target.value)} /></Field>
              <Field id="reset-code" label="Reset code" required><Input id="reset-code" autoComplete="off" value={code} onChange={(e) => setCode(e.target.value)} /></Field>
              <Field id="reset-password" label="New password" required hint="At least 8 characters. You will be signed out of every device."><Input id="reset-password" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
              <Button type="submit" disabled={busy}>{busy ? 'Saving…' : 'Change password'}</Button>
            </FormLayout>
          ) : null}

          {step === 'done' ? <Button onClick={leave}>Go to sign in</Button> : <button type="button" className="text-sm text-muted-foreground underline" onClick={leave}>Back to sign in</button>}
        </CardContent>
      </Card>
    </div>
  )
}
