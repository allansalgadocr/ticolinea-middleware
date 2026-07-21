import { useCallback, useEffect, useState } from 'react'
import { AlertCircle, Pencil, Plus, Tags, Trash2 } from 'lucide-react'
import { Shell } from '../components/Shell'
import { Button, EmptyState, Field, Modal } from '../components/ui'
import { api } from '../api'
import { errorMessage, Spinner } from '../auth'
import type { Category } from '../types'

export function Categories() {
  const [list, setList] = useState<Category[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [draft, setDraft] = useState<{ id: number | null; name: string } | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<Category | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setList(await api.categories())
    } catch (e) {
      setLoadError(errorMessage(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const save = async () => {
    if (!draft) return
    setActionError(null)
    setBusy(true)
    try {
      if (draft.id == null) await api.createCategory(draft.name)
      else await api.renameCategory(draft.id, draft.name)
      setDraft(null)
      await load()
    } catch (e) {
      setActionError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!confirmDelete) return
    setActionError(null)
    setBusy(true)
    try {
      await api.deleteCategory(confirmDelete.id)
      setConfirmDelete(null)
      await load()
    } catch (e) {
      setActionError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Shell
      title="Categorías"
      subtitle={loading ? 'Cargando…' : `${list.length} categorías · agrupan los canales en la lista de reproducción`}
      actions={
        <Button variant="primary" onClick={() => { setActionError(null); setDraft({ id: null, name: '' }) }}>
          <Plus size={15} /> Nueva categoría
        </Button>
      }
    >
      {loadError && (
        <p role="alert" className="mb-4 flex items-center gap-2 rounded-lg border border-danger/30 bg-danger/8 px-4 py-3 text-[13px] text-danger">
          <AlertCircle size={15} /> {loadError}
          <Button size="sm" onClick={() => void load()} className="ml-auto">Reintentar</Button>
        </p>
      )}

      <div className="panel overflow-hidden">
        {loading && <div className="grid place-items-center py-16"><Spinner /></div>}

        {!loading && list.length === 0 && (
          <EmptyState
            icon={<Tags size={26} />}
            title="Sin categorías"
            body="Las categorías agrupan los canales en la lista. Cree la primera para empezar."
          />
        )}

        {!loading && list.length > 0 && (
          <div className="stagger divide-y divide-line-soft">
            {list.map((c) => (
              <div key={c.id} className="flex items-center gap-3 px-5 py-3.5 transition-colors hover:bg-surface-2/60">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[14px] font-semibold">{c.name}</p>
                  <p className="mt-0.5 font-mono text-[11px] text-tx-3">
                    {c.channelCount} {c.channelCount === 1 ? 'canal' : 'canales'}
                  </p>
                </div>

                {/* Always visible, not hover-revealed: the owner is not a power
                    user and a hidden affordance reads as "no action available". */}
                <div className="flex shrink-0 items-center gap-1">
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => { setActionError(null); setDraft({ id: c.id, name: c.name }) }}
                    aria-label={`Editar ${c.name}`}
                  >
                    <Pencil size={14} /> Editar
                  </Button>
                  <Button
                    size="sm"
                    variant="subtle"
                    onClick={() => { setActionError(null); setConfirmDelete(c) }}
                    aria-label={`Eliminar ${c.name}`}
                  >
                    <Trash2 size={14} />
                  </Button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <Modal
        open={!!draft}
        title={draft?.id != null ? 'Editar categoría' : 'Nueva categoría'}
        subtitle="El nombre aparece como grupo en la lista de reproducción (group-title)."
        onClose={() => setDraft(null)}
        footer={
          <>
            <Button onClick={() => setDraft(null)} disabled={busy}>Cancelar</Button>
            <Button variant="primary" onClick={() => void save()} disabled={busy}>
              {busy ? <Spinner size={14} /> : 'Guardar'}
            </Button>
          </>
        }
      >
        {draft && (
          <div className="space-y-4">
            {actionError && (
              <p role="alert" className="flex items-start gap-2 rounded-lg border border-danger/30 bg-danger/8 px-3 py-2.5 text-[13px] text-danger">
                <AlertCircle size={15} className="mt-px shrink-0" /> {actionError}
              </p>
            )}
            <Field label="Nombre">
              <input
                className="field"
                autoFocus
                value={draft.name}
                onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                placeholder="Deportes"
              />
            </Field>
          </div>
        )}
      </Modal>

      {/* Deleting a category orphans its channels' id_categoria, which drops them
          from the playlist join — so the count is stated before confirming. */}
      <Modal
        open={!!confirmDelete}
        title="Eliminar categoría"
        onClose={() => setConfirmDelete(null)}
        footer={
          <>
            <Button onClick={() => setConfirmDelete(null)} disabled={busy}>Cancelar</Button>
            <Button variant="danger" onClick={() => void remove()} disabled={busy}>
              {busy ? <Spinner size={14} /> : 'Eliminar'}
            </Button>
          </>
        }
      >
        {confirmDelete && (
          <div className="space-y-3">
            {actionError && (
              <p role="alert" className="flex items-start gap-2 rounded-lg border border-danger/30 bg-danger/8 px-3 py-2.5 text-[13px] text-danger">
                <AlertCircle size={15} className="mt-px shrink-0" /> {actionError}
              </p>
            )}
            <p className="text-[14px] leading-relaxed text-tx-2">
              ¿Eliminar <span className="font-semibold text-tx">{confirmDelete.name}</span>?
              {confirmDelete.channelCount > 0 && (
                <>
                  {' '}
                  <span className="text-warn">
                    {confirmDelete.channelCount}{' '}
                    {confirmDelete.channelCount === 1 ? 'canal quedará' : 'canales quedarán'} sin categoría y no{' '}
                    {confirmDelete.channelCount === 1 ? 'aparecerá' : 'aparecerán'} en la lista de reproducción
                  </span>{' '}
                  hasta reasignarlos.
                </>
              )}
            </p>
          </div>
        )}
      </Modal>
    </Shell>
  )
}
