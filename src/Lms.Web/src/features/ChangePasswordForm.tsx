import { useState } from 'react'
import { ErrorBanner, Field, FormActions, FormLayout, FormSection, NoticeBanner } from '@/components/form'
import { Input } from '@/components/ui/input'
import { apiRequest } from '@/lib/api'

const empty = { current: '', next: '', confirm: '' }

/** A signed-in person chooses a new password. Other devices are signed out; this one stays signed in. */
export default function ChangePasswordForm() {
  const [form, setForm] = useState(empty)
  const [errors, setErrors] = useState<Partial<Record<keyof typeof empty, string>>>({})
  const [problem, setProblem] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setDone(null); setProblem(null)
    const found: typeof errors = {}
    if (!form.current) found.current = 'Enter your current password.'
    if (form.next.length < 8) found.next = 'Use at least 8 characters.'
    else if (form.next === form.current) found.next = 'Choose a password different from the current one.'
    if (form.confirm !== form.next) found.confirm = 'The passwords do not match.'
    setErrors(found)
    if (Object.keys(found).length > 0) return
    setBusy(true)
    try {
      await apiRequest('/api/v1/tenant/me/password', { method: 'POST', body: JSON.stringify({ currentPassword: form.current, newPassword: form.next }) })
      setForm(empty)
      setDone('Your password was changed. You were signed out on your other devices.')
    } catch (exception) {
      setProblem(exception instanceof Error ? exception.message : 'Unable to change the password.')
    } finally { setBusy(false) }
  }

  const set = (key: keyof typeof empty) => (event: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: event.target.value })

  return (
    <FormLayout onSubmit={submit} noValidate>
      <FormSection title="Change password" description="You stay signed in here. Anyone signed in with the old password on another device is signed out." divider={false}>
        <ErrorBanner message={problem} />
        <NoticeBanner message={done} />
        <Field id="current-password" label="Current password" required error={errors.current}>
          <Input id="current-password" type="password" autoComplete="current-password" value={form.current} onChange={set('current')} />
        </Field>
        <Field id="new-password" label="New password" required error={errors.next} hint="At least 8 characters.">
          <Input id="new-password" type="password" autoComplete="new-password" maxLength={200} value={form.next} onChange={set('next')} />
        </Field>
        <Field id="confirm-password" label="Confirm new password" required error={errors.confirm}>
          <Input id="confirm-password" type="password" autoComplete="new-password" maxLength={200} value={form.confirm} onChange={set('confirm')} />
        </Field>
      </FormSection>
      <FormActions busy={busy} submitLabel="Change password" busyLabel="Changing…" />
    </FormLayout>
  )
}
