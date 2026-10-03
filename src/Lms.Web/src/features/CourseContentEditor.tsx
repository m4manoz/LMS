import { useCallback, useEffect, useState } from 'react'
import { ArrowDown, ArrowUp } from 'lucide-react'
import { ErrorBanner, Field, FormActions, FormLayout, FormSection } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { BlockView, type Block } from '@/components/LessonBlocks'

type BlockType = Block['type']
type Module = { id: string; title: string; lessons: { id: string; title: string }[] }
type Draft = { type: BlockType; title: string; text: string; language: string; url: string; caption: string; file: File | null }

export const blockTypes: { value: BlockType; label: string }[] = [
  { value: 'Text', label: 'Text' }, { value: 'Code', label: 'Code' }, { value: 'Link', label: 'Link' }, { value: 'Embed', label: 'Video or page embed' },
  { value: 'Image', label: 'Image' }, { value: 'Pdf', label: 'PDF document' }, { value: 'Video', label: 'Video file' }, { value: 'Audio', label: 'Audio file' }, { value: 'Download', label: 'Downloadable file' },
]

/** Which inputs each block type needs. */
export function blockFields(type: BlockType) {
  return {
    text: type === 'Text' || type === 'Code',
    language: type === 'Code',
    url: type === 'Link' || type === 'Embed',
    file: ['Image', 'Pdf', 'Video', 'Audio', 'Download'].includes(type),
    accept: { Image: 'image/png,image/jpeg,image/gif,image/webp', Pdf: 'application/pdf', Video: 'video/mp4,video/webm,video/ogg', Audio: 'audio/mpeg,audio/ogg,audio/wav,audio/mp4,audio/webm' }[type as 'Image'] as string | undefined,
  }
}

/** The first problem that stops a block from being sent, or null. File blocks are checked on add only (an edit never re-uploads). */
export function validateDraft(draft: Draft, needsFile: boolean): string | null {
  const fields = blockFields(draft.type)
  if (fields.text && !draft.text.trim()) return draft.type === 'Code' ? 'Enter the code.' : 'Enter the text.'
  if (fields.url && !draft.url.trim()) return draft.type === 'Embed' ? 'Enter the embed link.' : 'Enter the link.'
  if (fields.file && needsFile && !draft.file) return 'Choose a file to upload.'
  return null
}

const emptyDraft = (type: BlockType = 'Text'): Draft => ({ type, title: '', text: '', language: '', url: '', caption: '', file: null })

/** Text-only blocks are sent as JSON; file blocks as multipart so the file arrives with its block. */
export function toBody(draft: Draft): FormData | string {
  if (!blockFields(draft.type).file) return JSON.stringify({ type: draft.type, title: draft.title, text: draft.text, language: draft.language, url: draft.url, caption: draft.caption })
  const form = new FormData()
  form.set('type', draft.type); form.set('title', draft.title); form.set('caption', draft.caption)
  if (draft.file) form.set('file', draft.file)
  return form
}

export default function CourseContentEditor({ courseId, modules, editable }: { courseId: string; modules: Module[]; editable: boolean }) {
  const lessons = modules.flatMap((module) => module.lessons.map((lesson) => ({ ...lesson, moduleTitle: module.title })))
  const [lessonId, setLessonId] = useState(lessons[0]?.id ?? '')
  const [blocks, setBlocks] = useState<Block[]>([])
  const [draft, setDraft] = useState<Draft>(emptyDraft())
  const [editingId, setEditingId] = useState<string | null>(null)
  const [edit, setEdit] = useState<Draft>(emptyDraft())
  const [error, setError] = useState<string | null>(null)
  const [addProblem, setAddProblem] = useState<string | null>(null)
  const [editProblem, setEditProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const base = `/api/v1/tenant/courses/${courseId}/lessons/${lessonId}/blocks`

  useEffect(() => { if (!lessons.some((lesson) => lesson.id === lessonId)) setLessonId(lessons[0]?.id ?? '') }, [modules])

  const load = useCallback(async () => {
    if (!lessonId) { setBlocks([]); return }
    try { setBlocks(await apiRequest<Block[]>(`/api/v1/tenant/courses/${courseId}/lessons/${lessonId}/blocks`)) }
    catch (exception) { setError(readError(exception, 'Unable to load the lesson content.')) }
  }, [courseId, lessonId])
  useEffect(() => { setEditingId(null); void load() }, [load])

  async function run(action: () => Promise<void>, failure: string) {
    setBusy(true); setError(null)
    try { await action(); await load() } catch (exception) { setError(readError(exception, failure)) } finally { setBusy(false) }
  }

  const add = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const problem = validateDraft(draft, true)
    setAddProblem(problem)
    if (problem) return
    return run(async () => { await apiRequest(base, { method: 'POST', body: toBody(draft) }); setDraft(emptyDraft(draft.type)) }, 'Unable to add the block.')
  }

  const save = (event: React.FormEvent<HTMLFormElement>, block: Block) => {
    event.preventDefault()
    const problem = validateDraft(edit, false)
    setEditProblem(problem)
    if (problem) return
    return run(async () => {
    await apiRequest(`${base}/${block.id}`, { method: 'PUT', body: JSON.stringify({ title: edit.title, text: edit.text, language: edit.language, url: edit.url, caption: edit.caption }) })
    setEditingId(null)
  }, 'Unable to save the block.')
  }

  const remove = (block: Block) => {
    if (!window.confirm('Delete this block? This cannot be undone.')) return
    return run(() => apiRequest(`${base}/${block.id}`, { method: 'DELETE' }).then(() => undefined), 'Unable to delete the block.')
  }

  const move = (index: number, offset: -1 | 1) => {
    const ids = blocks.map((block) => block.id)
    ;[ids[index], ids[index + offset]] = [ids[index + offset], ids[index]]
    return run(() => apiRequest(`${base}/order`, { method: 'PUT', body: JSON.stringify({ blockIds: ids }) }).then(() => undefined), 'Unable to reorder the blocks.')
  }

  const startEdit = (block: Block) => {
    setEditingId(block.id)
    setEditProblem(null)
    setEdit({ type: block.type, title: block.title ?? '', text: block.text ?? '', language: block.language ?? '', url: block.url ?? '', caption: block.caption ?? '', file: null })
  }

  if (lessons.length === 0) {
    return <Card><CardContent className="pt-5 text-sm text-muted-foreground">Add a module and a lesson on the Outline tab first. Content is added to lessons.</CardContent></Card>
  }

  const fields = blockFields(draft.type)
  return (
    <div className="flex flex-col gap-4">
      <ErrorBanner message={error} />
      <Card>
        <CardContent className="max-w-md pt-5">
          <Field id="content-lesson" label="Lesson">
            <Select id="content-lesson" value={lessonId} onChange={(e) => setLessonId(e.target.value)}>
              {modules.map((module) => (
                <optgroup key={module.id} label={module.title}>
                  {module.lessons.map((lesson) => <option key={lesson.id} value={lesson.id}>{lesson.title}</option>)}
                </optgroup>
              ))}
            </Select>
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Lesson content ({blocks.length})</CardTitle>
          <CardDescription>{editable ? 'Blocks appear to learners in this order.' : 'This course is no longer a draft, so its content is read-only.'}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {blocks.length === 0 ? <p className="text-sm text-muted-foreground">No content yet.</p> : blocks.map((block, index) => (
            <div key={block.id} className="flex flex-col gap-3 rounded-md border border-border p-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="flex items-center gap-2 text-sm"><span className="flex h-6 w-6 items-center justify-center rounded-full bg-muted text-xs">{index + 1}</span><Badge variant="outline">{blockTypes.find((t) => t.value === block.type)?.label}</Badge></span>
                {editable ? (
                  <span className="flex flex-wrap gap-1.5">
                    <Button variant="soft" size="icon" aria-label={`Move block ${index + 1} up`} disabled={busy || index === 0} onClick={() => void move(index, -1)}><ArrowUp className="h-4 w-4" /></Button>
                    <Button variant="soft" size="icon" aria-label={`Move block ${index + 1} down`} disabled={busy || index === blocks.length - 1} onClick={() => void move(index, 1)}><ArrowDown className="h-4 w-4" /></Button>
                    {!blockFields(block.type).file ? <Button variant="soft" size="sm" disabled={busy} onClick={() => startEdit(block)}>Edit</Button> : null}
                    <Button variant="softDestructive" size="sm" disabled={busy} onClick={() => void remove(block)}>Delete</Button>
                  </span>
                ) : null}
              </div>
              {editingId === block.id ? (
                <FormLayout onSubmit={(event) => void save(event, block)}>
                  <ErrorBanner message={editProblem} />
                  <FormSection title="Edit block">
                    <BlockFields draft={edit} onChange={setEdit} idPrefix={`edit-${block.id}`} showFile={false} />
                  </FormSection>
                  <FormActions busy={busy} submitLabel="Save changes" onCancel={() => setEditingId(null)} />
                </FormLayout>
              ) : <BlockView block={block} />}
            </div>
          ))}
        </CardContent>
      </Card>

      {editable ? (
        <Card>
          <CardContent className="pt-5">
            <FormLayout onSubmit={add}>
              <ErrorBanner message={addProblem} />
              <FormSection title="Add a block" description="Add text, code, links, embeds or files. Uploads are checked and may be up to 100 MB.">
                <Field id="block-type" label="Type" className="max-w-xs">
                  <Select id="block-type" value={draft.type} onChange={(e) => { setAddProblem(null); setDraft(emptyDraft(e.target.value as BlockType)) }}>
                    {blockTypes.map((type) => <option key={type.value} value={type.value}>{type.label}</option>)}
                  </Select>
                </Field>
                <BlockFields draft={draft} onChange={setDraft} idPrefix="new-block" showFile />
              </FormSection>
              <FormActions busy={busy} disabled={fields.file && !draft.file} submitLabel="Add block" busyLabel="Adding…" />
            </FormLayout>
          </CardContent>
        </Card>
      ) : null}
    </div>
  )
}

function BlockFields({ draft, onChange, idPrefix, showFile }: { draft: Draft; onChange: (draft: Draft) => void; idPrefix: string; showFile: boolean }) {
  const fields = blockFields(draft.type)
  const set = (patch: Partial<Draft>) => onChange({ ...draft, ...patch })
  return (
    <>
      <Field id={`${idPrefix}-title`} label="Title (optional)">
        <Input id={`${idPrefix}-title`} value={draft.title} maxLength={200} onChange={(e) => set({ title: e.target.value })} />
      </Field>
      {fields.text ? (
        <Field id={`${idPrefix}-text`} label={draft.type === 'Code' ? 'Code' : 'Text'} required>
          <Textarea id={`${idPrefix}-text`} rows={draft.type === 'Code' ? 8 : 5} maxLength={20000} className={draft.type === 'Code' ? 'font-mono' : undefined} value={draft.text} onChange={(e) => set({ text: e.target.value })} />
        </Field>
      ) : null}
      {fields.language ? (
        <Field id={`${idPrefix}-language`} label="Language" className="max-w-xs">
          <Input id={`${idPrefix}-language`} value={draft.language} maxLength={30} placeholder="e.g. python" onChange={(e) => set({ language: e.target.value })} />
        </Field>
      ) : null}
      {fields.url ? (
        <Field id={`${idPrefix}-url`} label={draft.type === 'Embed' ? 'Embed link' : 'Link'} required hint={draft.type === 'Embed' ? 'Use the embed link, such as https://www.youtube.com/embed/… or https://player.vimeo.com/video/…' : undefined}>
          <Input id={`${idPrefix}-url`} type="url" value={draft.url} placeholder="https://" onChange={(e) => set({ url: e.target.value })} />
        </Field>
      ) : null}
      {fields.file && showFile ? (
        <Field id={`${idPrefix}-file`} label="File" required>
          <Input id={`${idPrefix}-file`} type="file" accept={fields.accept} onChange={(e) => set({ file: e.target.files?.[0] ?? null })} />
        </Field>
      ) : null}
      <Field id={`${idPrefix}-caption`} label="Caption (optional)">
        <Input id={`${idPrefix}-caption`} value={draft.caption} maxLength={500} onChange={(e) => set({ caption: e.target.value })} />
      </Field>
    </>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
