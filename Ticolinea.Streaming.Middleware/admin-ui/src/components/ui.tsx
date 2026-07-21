import { type ReactNode, useEffect } from 'react'
import { X } from 'lucide-react'

type BtnVariant = 'primary' | 'ghost' | 'danger' | 'subtle'

const btnBase =
  'inline-flex items-center justify-center gap-2 rounded-lg text-sm font-semibold transition-all duration-150 active:translate-y-px disabled:opacity-40 disabled:pointer-events-none whitespace-nowrap'

const btnVariants: Record<BtnVariant, string> = {
  // White on the teal fill measures 5.47:1 — the dark theme's light-mint fill
  // with dark text would have been 1.6:1 here, i.e. unreadable.
  primary:
    'bg-mint text-white hover:bg-mint-dim shadow-[0_1px_2px_rgba(16,30,48,0.10),0_6px_16px_-8px_rgba(15,118,110,0.55)]',
  ghost:
    'border border-line bg-surface text-tx-2 hover:text-tx hover:border-[#a9b8c8] hover:bg-surface-2',
  danger: 'border border-danger/40 bg-surface text-danger hover:bg-danger/8',
  subtle: 'text-tx-3 hover:text-tx hover:bg-surface-2',
}

export function Button({
  children,
  variant = 'ghost',
  size = 'md',
  ...rest
}: {
  children: ReactNode
  variant?: BtnVariant
  size?: 'sm' | 'md'
} & React.ButtonHTMLAttributes<HTMLButtonElement>) {
  const sizing = size === 'sm' ? 'px-2.5 py-1.5 text-[13px]' : 'px-3.5 py-2'
  return (
    <button {...rest} className={`${btnBase} ${btnVariants[variant]} ${sizing} ${rest.className ?? ''}`}>
      {children}
    </button>
  )
}

/** On-air indicator. The pulse is reserved for live channels only — if
 *  everything animates, nothing communicates. */
export function StatusDot({ on }: { on: boolean }) {
  return (
    <span
      aria-hidden
      className="inline-block size-1.5 rounded-full"
      style={
        on
          ? { background: 'var(--color-air)', animation: 'pulse-air 2.4s ease-in-out infinite' }
          : { background: 'var(--color-tx-3)' }
      }
    />
  )
}

export function Pill({ children, tone = 'neutral' }: { children: ReactNode; tone?: 'neutral' | 'mint' | 'warn' }) {
  const tones = {
    neutral: 'border-line text-tx-3',
    mint: 'border-mint/30 text-mint bg-mint/8',
    warn: 'border-warn/30 text-warn bg-warn/8',
  }
  return (
    <span className={`inline-flex items-center rounded-md border px-1.5 py-0.5 text-[10px] font-semibold tracking-[0.1em] uppercase ${tones[tone]}`}>
      {children}
    </span>
  )
}

export function Modal({
  open,
  title,
  subtitle,
  onClose,
  children,
  footer,
}: {
  open: boolean
  title: string
  subtitle?: string
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
}) {
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  if (!open) return null
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto p-4 pt-[8vh]">
      {/* An explicit dark scrim, not a tint of the canvas: on a light theme
          `bg-ink/80` would be light-on-light and the dialog would not separate. */}
      <div
        className="fixed inset-0 backdrop-blur-[2px]"
        style={{ background: 'rgba(20, 30, 44, 0.42)', animation: 'rise 0.2s ease-out' }}
        onClick={onClose}
      />
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="panel relative z-10 w-full max-w-xl shadow-[0_24px_60px_-12px_rgba(16,30,48,0.28)]"
        style={{ animation: 'rise 0.28s cubic-bezier(0.22,1,0.36,1)' }}
      >
        <header className="flex items-start justify-between border-b border-line-soft px-6 py-5">
          <div>
            <h2 className="font-display text-lg font-bold tracking-tight">{title}</h2>
            {subtitle && <p className="mt-0.5 text-[13px] text-tx-3">{subtitle}</p>}
          </div>
          <button onClick={onClose} aria-label="Cerrar" className="rounded-md p-1 text-tx-3 transition-colors hover:bg-surface-2 hover:text-tx">
            <X size={17} />
          </button>
        </header>
        <div className="px-6 py-5">{children}</div>
        {footer && <footer className="flex justify-end gap-2 border-t border-line-soft px-6 py-4">{footer}</footer>}
      </div>
    </div>
  )
}

export function Field({
  label,
  hint,
  children,
}: {
  label: string
  hint?: string
  children: ReactNode
}) {
  return (
    <label className="block">
      <span className="label">{label}</span>
      {children}
      {hint && <span className="mt-1.5 block font-mono text-[11px] text-tx-3">{hint}</span>}
    </label>
  )
}

export function Toggle({ on, onChange, label }: { on: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      aria-label={label}
      onClick={() => onChange(!on)}
      className="relative h-[22px] w-[38px] shrink-0 rounded-full border transition-colors duration-200"
      style={{
        background: on ? 'rgba(45,212,191,0.22)' : '#0a0e13',
        borderColor: on ? 'var(--color-mint-dim)' : 'var(--color-line)',
      }}
    >
      <span
        className="absolute top-1/2 size-[14px] -translate-y-1/2 rounded-full transition-all duration-200"
        style={{
          left: on ? 20 : 3,
          background: on ? 'var(--color-mint)' : 'var(--color-tx-3)',
        }}
      />
    </button>
  )
}

/** Transient confirmation. Auto-dismisses; `tone` carries whether the action
 *  fully took effect, which for a source change is the thing the owner needs. */
export function Toast({
  message,
  tone = 'ok',
  onDone,
}: {
  message: string
  tone?: 'ok' | 'warn'
  onDone: () => void
}) {
  useEffect(() => {
    // A warning stays longer — it tells the operator something is still pending.
    const ms = tone === 'warn' ? 9000 : 5000
    const t = setTimeout(onDone, ms)
    return () => clearTimeout(t)
  }, [tone, onDone, message])

  // Solid white ground rather than a translucent tint: over a light canvas a
  // 10%-alpha fill leaves the text floating with almost no separation.
  const tones = {
    ok: 'border-mint/35 bg-surface text-mint',
    warn: 'border-warn/40 bg-surface text-warn',
  }

  return (
    <div
      role="status"
      aria-live="polite"
      className={`fixed right-5 bottom-5 z-[60] max-w-sm rounded-xl border px-4 py-3 text-[13px] leading-snug shadow-[0_16px_36px_-12px_rgba(16,30,48,0.30)] ${tones[tone]}`}
      style={{ animation: 'rise 0.3s cubic-bezier(0.22,1,0.36,1)' }}
    >
      {message}
      <button
        onClick={onDone}
        aria-label="Cerrar aviso"
        className="ml-3 align-middle opacity-60 transition-opacity hover:opacity-100"
      >
        <X size={13} />
      </button>
    </div>
  )
}

export function EmptyState({ icon, title, body }: { icon: ReactNode; title: string; body: string }) {
  return (
    <div className="flex flex-col items-center justify-center px-6 py-16 text-center">
      <div className="mb-3 text-tx-3">{icon}</div>
      <p className="font-display text-base font-bold">{title}</p>
      <p className="mt-1 max-w-sm text-[13px] text-tx-3">{body}</p>
    </div>
  )
}
