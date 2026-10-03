import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { ErrorBanner, Field, FormActions, FormLayout, FormSection } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, apiRequest } from '@/lib/api'

type Band = { minPercent: number; label: string; points: number | null }
type Scale = { id: string | null; name: string; isDefault: boolean; builtIn: boolean; bands: Band[] }
type Draft = { id: string | null; name: string; isDefault: boolean; bands: { minPercent: string; label: string; points: string }[] }

const standard: Draft['bands'] = [
  { minPercent: '90', label: 'A', points: '4' }, { minPercent: '80', label: 'B', points: '3' }, { minPercent: '70', label: 'C', points: '2' },
  { minPercent: '60', label: 'D', points: '1' }, { minPercent: '0', label: 'F', points: '0' },
]

/** The same rules the server enforces, so mistakes show up while typing. Returns null when the bands are fine. */
export function bandsError(bands: Draft['bands']): string | null {
  if (bands.length === 0) return 'Add at least one band.'
  if (bands.length > 12) return 'A scale can have at most 12 bands.'
  const mins = bands.map((band) => Number(band.minPercent))
  if (bands.some((band) => band.minPercent.trim() === '') || mins.some((min) => Number.isNaN(min) || min < 0 || min > 100)) return 'Each minimum must be a number from 0 to 100.'
  if (new Set(mins).size !== mins.length) return 'Two bands cannot start at the same percentage.'
  if (!mins.includes(0)) return 'The lowest band must start at 0 so every score gets a grade.'
  if (bands.some((band) => !band.label.trim() || band.label.trim().length > 20)) return 'Each band needs a label of 1 to 20 characters.'
  if (new Set(bands.map((band) => band.label.trim().toLowerCase())).size !== bands.length) return 'Band labels must be different.'
  if (bands.some((band) => band.points.trim() !== '' && (Number(band.points) < 0 || Number(band.points) > 10))) return 'Grade points must be between 0 and 10.'
  return null
}

const toDraft = (scale: Scale): Draft => ({ id: scale.id, name: scale.name, isDefault: scale.isDefault, bands: scale.bands.map((band) => ({ minPercent: String(band.minPercent), label: band.label, points: band.points === null ? '' : String(band.points) })) })

export default function GradeScalesEditor() {
  const [scales, setScales] = useState<Scale[]>([])
  const [draft, setDraft] = useState<Draft | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [nameProblem, setNameProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { setScales(await apiRequest<Scale[]>('/api/v1/tenant/gradebook/scales')) }
    catch (exception) { setError(readError(exception, 'Unable to load the grade scales.')) }
  }, [])
  useEffect(() => { void load() }, [load])

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!draft) return
    if (!draft.name.trim()) { setNameProblem('Enter a name for the scale.'); return }
    setBusy(true); setError(null)
    try {
      const body = JSON.stringify({
        name: draft.name, isDefault: draft.isDefault,
        bands: draft.bands.map((band) => ({ minPercent: Number(band.minPercent), label: band.label, points: band.points.trim() === '' ? null : Number(band.points) })),
      })
      await apiRequest(draft.id ? `/api/v1/tenant/gradebook/scales/${draft.id}` : '/api/v1/tenant/gradebook/scales', { method: draft.id ? 'PUT' : 'POST', body })
      setDraft(null); await load()
    } catch (exception) { setError(readError(exception, 'Unable to save the scale.')) }
    finally { setBusy(false) }
  }

  async function remove(scale: Scale) {
    if (!window.confirm(`Delete the scale "${scale.name}"?`)) return
    setBusy(true); setError(null)
    try { await apiRequest(`/api/v1/tenant/gradebook/scales/${scale.id}`, { method: 'DELETE' }); await load() }
    catch (exception) { setError(readError(exception, 'Unable to delete the scale.')) }
    finally { setBusy(false) }
  }

  const startDraft = (next: Draft) => { setNameProblem(null); setDraft(next) }
  const problem = draft ? bandsError(draft.bands) : null
  const setBand = (index: number, patch: Partial<Draft['bands'][number]>) => draft && setDraft({ ...draft, bands: draft.bands.map((band, i) => (i === index ? { ...band, ...patch } : band)) })

  return (
    <div className="flex flex-col gap-4">
      <ErrorBanner message={error} />
      {draft ? (
        <FormLayout onSubmit={save}>
          <ErrorBanner message={nameProblem ?? problem} />
          <FormSection title={draft.id ? 'Edit scale' : 'New scale'} description="Scores at or above a band’s minimum earn its label. The lowest band must start at 0.">
            <Field id="scale-name" label="Name" required className="max-w-sm">
              <Input id="scale-name" value={draft.name} maxLength={100} onChange={(e) => { setNameProblem(null); setDraft({ ...draft, name: e.target.value }) }} />
            </Field>
          </FormSection>
          <FormSection title="Bands">
            {draft.bands.map((band, index) => (
              <div key={index} className="flex flex-wrap items-end gap-2">
                <Field id={`band-min-${index}`} label="From %" required className="w-28"><Input id={`band-min-${index}`} type="number" min="0" max="100" step="0.5" value={band.minPercent} onChange={(e) => setBand(index, { minPercent: e.target.value })} /></Field>
                <Field id={`band-label-${index}`} label="Label" required className="w-32"><Input id={`band-label-${index}`} value={band.label} maxLength={20} onChange={(e) => setBand(index, { label: e.target.value })} /></Field>
                <Field id={`band-points-${index}`} label="Points" className="w-28"><Input id={`band-points-${index}`} type="number" min="0" max="10" step="0.1" value={band.points} placeholder="optional" onChange={(e) => setBand(index, { points: e.target.value })} /></Field>
                <Button type="button" variant="softDestructive" size="sm" aria-label={`Remove band ${index + 1}`} onClick={() => setDraft({ ...draft, bands: draft.bands.filter((_, i) => i !== index) })}>Remove</Button>
              </div>
            ))}
            <div><Button type="button" variant="secondary" size="sm" disabled={draft.bands.length >= 12} onClick={() => setDraft({ ...draft, bands: [...draft.bands, { minPercent: '', label: '', points: '' }] })}>Add band</Button></div>
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={draft.isDefault} onChange={(e) => setDraft({ ...draft, isDefault: e.target.checked })} />Use as the organization default</label>
          </FormSection>
          <FormActions busy={busy} disabled={problem !== null} submitLabel="Save scale" onCancel={() => setDraft(null)} />
        </FormLayout>
      ) : (
        <div><Button onClick={() => startDraft({ id: null, name: '', isDefault: false, bands: standard.map((band) => ({ ...band })) })}>New scale</Button></div>
      )}

      {scales.map((scale) => (
        <Card key={scale.id ?? 'built-in'}>
          <CardHeader>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <CardTitle className="text-base">{scale.name}</CardTitle>
              <span className="flex items-center gap-2">
                {scale.isDefault ? <Badge>Default</Badge> : null}
                {scale.builtIn ? <Badge variant="outline">Built in</Badge> : (
                  <>
                    <Button variant="soft" size="sm" onClick={() => startDraft(toDraft(scale))}>Edit</Button>
                    <Button variant="softDestructive" size="sm" disabled={busy} onClick={() => void remove(scale)}>Delete</Button>
                  </>
                )}
              </span>
            </div>
          </CardHeader>
          <CardContent className="flex flex-wrap gap-2">
            {scale.bands.map((band) => (
              <span key={band.label} className="rounded-md border border-border px-2.5 py-1 text-sm"><strong>{band.label}</strong> <span className="text-muted-foreground">from {band.minPercent}%{band.points !== null ? ` · ${band.points} pts` : ''}</span></span>
            ))}
          </CardContent>
        </Card>
      ))}
    </div>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
