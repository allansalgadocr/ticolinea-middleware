import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, KeyRound, Plus, ShieldCheck } from 'lucide-react'
import { Shell } from '../components/Shell'
import { Button, Field, Modal, Pill, Toggle } from '../components/ui'
import { api } from '../api'
import { errorMessage, Spinner, useAuth } from '../auth'
import type { AdminUser, UserRole } from '../types'

const roleLabel: Record<UserRole, string> = {
  owner: 'Propietario',
  operator: 'Operador',
}

export function Users() {
  const { user: me } = useAuth()
  const [list, setList] = useState<AdminUser[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [creating, setCreating] = useState(false)
  const [draft, setDraft] = useState({ username: '', displayName: '', role: 'operator' as UserRole, password: '' })
  const [resetting, setResetting] = useState<AdminUser | null>(null)
  const [newPassword, setNewPassword] = useState('')
  const [actionError, setActionError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setList(await api.users())
    } catch (e) {
      setLoadError(errorMessage(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const create = async () => {
    setActionError(null)
    setBusy(true)
    try {
      await api.createUser(draft)
      setDraft({ username: '', displayName: '', role: 'operator', password: '' })
      setCreating(false)
      await load()
    } catch (e) {
      setActionError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const resetPassword = async () => {
    if (!resetting) return
    setActionError(null)
    setBusy(true)
    try {
      await api.setUserPassword(resetting.id, newPassword)
      setResetting(null)
      setNewPassword('')
    } catch (e) {
      setActionError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const toggle = async (u: AdminUser, enabled: boolean) => {
    setActionError(null)
    // Optimistic: the row reflects the intent immediately, and a failure
    // reloads the truth from the server rather than leaving a stale switch.
    setList((prev) => prev.map((x) => (x.id === u.id ? { ...x, enabled } : x)))
    try {
      await api.setUserEnabled(u.id, enabled)
    } catch (e) {
      setActionError(errorMessage(e))
      await load()
    }
  }

  const relative = (iso: string | null) =>
    iso
      ? new Date(iso).toLocaleString('es-CR', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
      : 'Nunca'

  return (
    <Shell
      title="Usuarios"
      subtitle={loading ? 'Cargando…' : `${list.length} cuentas con acceso a esta consola`}
      actions={
        <Button variant="primary" onClick={() => { setActionError(null); setCreating(true) }}>
          <Plus size={15} /> Nuevo usuario
        </Button>
      }
    >
      {(loadError || actionError) && (
        <p role="alert" className="mb-4 flex items-center gap-2 rounded-lg border border-danger/30 bg-danger/8 px-4 py-3 text-[13px] text-danger">
          <AlertCircle size={15} /> {loadError ?? actionError}
          {loadError && <Button size="sm" onClick={() => void load()} className="ml-auto">Reintentar</Button>}
        </p>
      )}

      <div className="panel overflow-hidden">
        {loading ? (
          <div className="grid place-items-center py-16"><Spinner /></div>
        ) : (
          <div className="stagger divide-y divide-line-soft">
            {list.map((u) => (
              <div key={u.id} className="flex flex-wrap items-center gap-3 px-5 py-3.5 transition-colors hover:bg-surface-2/60">
                <div className="grid size-9 shrink-0 place-items-center rounded-full bg-surface-2 font-display text-[13px] font-bold text-tx-2 ring-1 ring-line">
                  {u.displayName.slice(0, 1).toUpperCase()}
                </div>

                <div className="min-w-0 flex-1">
                  <p className="flex items-center gap-2 truncate text-[14px] font-semibold">
                    {u.displayName}
                    {u.isSeed && <Pill tone="mint">inicial</Pill>}
                    {u.id === me?.id && <Pill>usted</Pill>}
                  </p>
                  <p className="mt-0.5 font-mono text-[11px] text-tx-3">
                    {u.username} · último acceso {relative(u.lastLogin)}
                  </p>
                </div>

                <span className="hidden items-center gap-1.5 text-[13px] text-tx-2 sm:flex">
                  <ShieldCheck size={14} className={u.role === 'owner' ? 'text-mint' : 'text-tx-3'} />
                  {roleLabel[u.role]}
                </span>

                <Button
                  size="sm"
                  variant="subtle"
                  onClick={() => { setActionError(null); setNewPassword(''); setResetting(u) }}
                  aria-label={`Restablecer contraseña de ${u.username}`}
                >
                  <KeyRound size={14} />
                </Button>

                {/* The seed account cannot be disabled — locking it out would
                    leave the node with no way back in. The API enforces this too. */}
                <Toggle
                  on={u.enabled}
                  label={`Activo: ${u.username}`}
                  onChange={(v) => !u.isSeed && void toggle(u, v)}
                />
              </div>
            ))}
          </div>
        )}
      </div>

      <Modal
        open={creating}
        title="Nuevo usuario"
        subtitle="La cuenta existe solo en este nodo."
        onClose={() => setCreating(false)}
        footer={
          <>
            <Button onClick={() => setCreating(false)} disabled={busy}>Cancelar</Button>
            <Button variant="primary" onClick={() => void create()} disabled={busy}>
              {busy ? <Spinner size={14} /> : 'Crear usuario'}
            </Button>
          </>
        }
      >
        <div className="space-y-4">
          {actionError && (
            <p role="alert" className="flex items-start gap-2 rounded-lg border border-danger/30 bg-danger/8 px-3 py-2.5 text-[13px] text-danger">
              <AlertCircle size={15} className="mt-px shrink-0" /> {actionError}
            </p>
          )}

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Usuario">
              <input
                className="field !font-mono !text-[12px]"
                value={draft.username}
                onChange={(e) => setDraft({ ...draft, username: e.target.value })}
                placeholder="operaciones"
              />
            </Field>
            <Field label="Nombre visible">
              <input
                className="field"
                value={draft.displayName}
                onChange={(e) => setDraft({ ...draft, displayName: e.target.value })}
                placeholder="Operaciones LS"
              />
            </Field>
          </div>

          <Field label="Contraseña" hint="Mínimo 12 caracteres. Se almacena con hash, nunca en texto plano.">
            <input
              className="field"
              type="password"
              value={draft.password}
              onChange={(e) => setDraft({ ...draft, password: e.target.value })}
              placeholder="••••••••••••"
            />
          </Field>

          <Field label="Rol">
            <select
              className="field cursor-pointer"
              value={draft.role}
              onChange={(e) => setDraft({ ...draft, role: e.target.value as UserRole })}
            >
              <option value="operator">Operador — edita canales y categorías</option>
              <option value="owner">Propietario — además administra usuarios</option>
            </select>
          </Field>
        </div>
      </Modal>

      <Modal
        open={!!resetting}
        title="Restablecer contraseña"
        subtitle={resetting ? `Se cerrarán las sesiones abiertas de ${resetting.username}.` : undefined}
        onClose={() => setResetting(null)}
        footer={
          <>
            <Button onClick={() => setResetting(null)} disabled={busy}>Cancelar</Button>
            <Button variant="primary" onClick={() => void resetPassword()} disabled={busy}>
              {busy ? <Spinner size={14} /> : 'Guardar'}
            </Button>
          </>
        }
      >
        <div className="space-y-4">
          {actionError && (
            <p role="alert" className="flex items-start gap-2 rounded-lg border border-danger/30 bg-danger/8 px-3 py-2.5 text-[13px] text-danger">
              <AlertCircle size={15} className="mt-px shrink-0" /> {actionError}
            </p>
          )}
          <Field label="Nueva contraseña" hint="Mínimo 12 caracteres.">
            <input
              className="field"
              type="password"
              autoFocus
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              placeholder="••••••••••••"
            />
          </Field>
        </div>
      </Modal>
    </Shell>
  )
}
