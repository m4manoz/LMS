import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Maximize2, Minimize2, X } from 'lucide-react'
import { Button } from '@/components/ui/button'

type Props = { open: boolean; label: string; onClose: () => void; children: ReactNode; /** Takes the whole width of the screen (for a live class) and offers a browser full screen button. */ wide?: boolean }

/** A panel that slides over the page from the right. Click the dimmed area, press Escape or use the close button to dismiss it. */
export default function SidePanel({ open, label, onClose, children, wide = false }: Props) {
  const panel = useRef<HTMLDivElement>(null)
  const [fullscreen, setFullscreen] = useState(false)
  useEffect(() => {
    const changed = () => setFullscreen(document.fullscreenElement === panel.current)
    document.addEventListener('fullscreenchange', changed)
    return () => document.removeEventListener('fullscreenchange', changed)
  }, [])
  function toggleFullscreen() {
    if (document.fullscreenElement) void document.exitFullscreen?.()
    else void panel.current?.requestFullscreen?.().catch(() => undefined)
  }

  // The page re-renders on every keystroke and hands over a new onClose each time, so keep the latest one in a ref.
  // Listening and focusing must depend on `open` alone, or focus would be pulled out of the form being typed in.
  const close = useRef(onClose)
  useEffect(() => { close.current = onClose })

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') close.current() }
    document.addEventListener('keydown', onKey)
    panel.current?.focus()
    return () => document.removeEventListener('keydown', onKey)
  }, [open])

  if (!open) return null
  return (
    <div className="fixed inset-0 z-50 flex justify-end">
      <div className="absolute inset-0 bg-black/60" data-testid="side-panel-backdrop" onClick={onClose} aria-hidden />
      <div ref={panel} role="dialog" aria-modal="true" aria-label={label} tabIndex={-1}
        className={`relative flex h-full w-full flex-col bg-background shadow-2xl outline-none ${wide ? 'max-w-none' : 'max-w-4xl border-l border-border'}`}>
        <div className="flex shrink-0 items-center justify-between gap-3 border-b border-border px-5 py-3">
          <span className="text-sm font-medium text-muted-foreground">{label}</span>
          <div className="flex items-center gap-1">
            {wide && document.fullscreenEnabled ? <Button type="button" variant="ghost" size="sm" aria-label={fullscreen ? 'Exit full screen' : 'Full screen'} onClick={toggleFullscreen}>{fullscreen ? <Minimize2 className="h-4 w-4" /> : <Maximize2 className="h-4 w-4" />}</Button> : null}
            <Button type="button" variant="ghost" size="sm" aria-label="Close panel" onClick={onClose}><X className="h-4 w-4" /></Button>
          </div>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain p-5">{children}</div>
      </div>
    </div>
  )
}
