import { useState } from 'react'
import { Field } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { ApiError, apiRequest } from '@/lib/api'
import { landingImageUrl, type LandingPicture } from '@/lib/publicApi'

const TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif']
const MAX_BYTES = 3 * 1024 * 1024

/** The first problem with a picture chosen for upload, or null. The server checks again; this saves a round trip. */
export function pictureProblem(file: Pick<File, 'type' | 'size'>): string | null {
  if (!TYPES.includes(file.type)) return 'Upload a PNG, JPEG, WebP or GIF picture.'
  if (file.size > MAX_BYTES) return 'Pictures must be 3 MB or smaller.'
  return null
}

/** Picks the picture for one spot on the page: reuse one already uploaded, upload a new one, or show none. */
export default function PictureField({ id, label, hint, value, pictures, slug, onChange, onUploaded }: {
  id: string; label: string; hint?: string; value: string; pictures: LandingPicture[]; slug: string
  onChange: (id: string) => void; onUploaded: (picture: LandingPicture) => void
}) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  async function upload(file: File | undefined) {
    if (!file) return
    const found = pictureProblem(file)
    setProblem(found)
    if (found) return
    setBusy(true)
    try {
      const body = new FormData()
      body.append('file', file)
      const picture = await apiRequest<LandingPicture>('/api/v1/tenant/landing/images', { method: 'POST', body })
      onUploaded(picture); onChange(picture.id)
    } catch (exception) { setProblem(exception instanceof ApiError ? exception.message : 'The picture could not be uploaded.') }
    finally { setBusy(false) }
  }

  return (
    <Field id={id} label={label} hint={hint} error={problem} className="sm:col-span-2">
      <div className="flex flex-wrap items-center gap-3">
        {value ? <img src={landingImageUrl(slug, value)} alt={`${label} preview`} className="h-16 w-auto max-w-[10rem] rounded border border-border object-contain" /> : <span className="flex h-16 w-24 items-center justify-center rounded border border-dashed border-border text-xs text-muted-foreground">No picture</span>}
        <div className="flex min-w-48 flex-1 flex-col gap-2">
          <Select id={id} value={value} onChange={(event) => onChange(event.target.value)}>
            <option value="">No picture</option>
            {pictures.map((picture) => <option key={picture.id} value={picture.id}>{picture.fileName}</option>)}
          </Select>
          <div className="flex flex-wrap items-center gap-2">
            <Input aria-label={`Upload a new picture for ${label}`} type="file" accept={TYPES.join(',')} disabled={busy} className="max-w-xs" onChange={(event) => { void upload(event.target.files?.[0]); event.target.value = '' }} />
            {value ? <Button type="button" variant="outline" size="sm" onClick={() => onChange('')}>Remove from the page</Button> : null}
          </div>
        </div>
      </div>
    </Field>
  )
}
