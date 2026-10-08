import { Check } from 'lucide-react'
import { Button } from '@/components/ui/button'

export type PermissionItem = { code: string; label: string; description: string }
export type PermissionModule = { module: string; title: string; permissions: PermissionItem[] }

/** When the server's grouped list cannot be loaded: the plain codes, grouped by the word before the dot. */
export function groupFlat(codes: string[]): PermissionModule[] {
  const groups = new Map<string, PermissionItem[]>()
  for (const code of [...codes].sort()) {
    const key = code.split('.')[0]
    groups.set(key, [...(groups.get(key) ?? []), { code, label: code, description: '' }])
  }
  return [...groups.entries()].map(([module, permissions]) => ({ module, title: module.charAt(0).toUpperCase() + module.slice(1), permissions }))
}

/** Which of a module's permissions are chosen, as "2 of 5". */
export const countOf = (module: PermissionModule, chosen: string[]) => module.permissions.filter((item) => chosen.includes(item.code)).length

/** Adds every permission of the module to the choice, or takes them all away. */
export function setModule(module: PermissionModule, chosen: string[], on: boolean): string[] {
  const codes = module.permissions.map((item) => item.code)
  return on ? [...chosen, ...codes.filter((code) => !chosen.includes(code))] : chosen.filter((code) => !codes.includes(code))
}

/** Permissions to tick, one module at a time, each with a plain-language name, what it allows and a button to choose or clear the whole module. */
export function PermissionPicker({ modules, selected, onChange, disabled = false }: { modules: PermissionModule[]; selected: string[]; onChange: (next: string[]) => void; disabled?: boolean }) {
  return (
    <div className="flex flex-col gap-2">
      {modules.map((module) => {
        const count = countOf(module, selected)
        return (
          <details key={module.module} open={count > 0} className="rounded-md border border-border">
            <summary className="flex cursor-pointer flex-wrap items-center justify-between gap-2 px-3 py-2 text-sm font-medium">
              <span>{module.title}</span>
              <small className="font-normal text-muted-foreground">{count} of {module.permissions.length} chosen</small>
            </summary>
            <div className="flex flex-col gap-2 border-t border-border p-3">
              <div className="flex gap-2">
                <Button type="button" size="sm" variant="outline" disabled={disabled || count === module.permissions.length} aria-label={`Choose everything in ${module.title}`} onClick={() => onChange(setModule(module, selected, true))}>Choose all</Button>
                <Button type="button" size="sm" variant="outline" disabled={disabled || count === 0} aria-label={`Clear everything in ${module.title}`} onClick={() => onChange(setModule(module, selected, false))}>Clear</Button>
              </div>
              <ul className="flex flex-col gap-1.5">
                {module.permissions.map((item) => (
                  <li key={item.code}>
                    <label className="flex items-start gap-2 text-sm">
                      <input type="checkbox" className="mt-1" disabled={disabled} checked={selected.includes(item.code)} onChange={() => onChange(selected.includes(item.code) ? selected.filter((code) => code !== item.code) : [...selected, item.code])} />
                      <span><span className="block">{item.label}</span>
                        {item.description ? <small className="block text-muted-foreground">{item.description}</small> : null}
                        <small className="font-mono text-xs text-muted-foreground">{item.code}</small></span>
                    </label>
                  </li>
                ))}
              </ul>
            </div>
          </details>
        )
      })}
    </div>
  )
}

/** What a role grants, by module. Modules the role gives nothing in are left out; permissions the list does not know are shown at the end. */
export function PermissionSummary({ modules, granted }: { modules: PermissionModule[]; granted: string[] }) {
  const known = new Set(modules.flatMap((module) => module.permissions.map((item) => item.code)))
  const unknown = granted.filter((code) => !known.has(code))
  const shown = modules.filter((module) => countOf(module, granted) > 0)
  return (
    <div className="flex flex-col gap-3">
      {shown.map((module) => (
        <section key={module.module} aria-label={module.title} className="rounded-md border border-border p-3">
          <h5 className="flex items-baseline justify-between text-sm font-semibold">{module.title}<small className="font-normal text-muted-foreground">{countOf(module, granted)} of {module.permissions.length}</small></h5>
          <ul className="mt-1 flex flex-col gap-1 text-sm">
            {module.permissions.filter((item) => granted.includes(item.code)).map((item) => (
              <li key={item.code} className="flex items-start gap-2"><Check className="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-hidden /><span>{item.label}{item.description ? <small className="block text-muted-foreground">{item.description}</small> : null}</span></li>
            ))}
          </ul>
        </section>
      ))}
      {unknown.length > 0 ? <section aria-label="Other" className="rounded-md border border-border p-3"><h5 className="text-sm font-semibold">Other</h5><ul className="text-sm">{unknown.map((code) => <li key={code}>{code}</li>)}</ul></section> : null}
    </div>
  )
}
