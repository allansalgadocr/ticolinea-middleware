import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertCircle, Plus, Radio, Search, SlidersHorizontal, Trash2 } from 'lucide-react'
import { Shell } from '../components/Shell'
import { Button, EmptyState, Field, Modal, Pill, StatusDot, Toggle } from '../components/ui'
import { api, type ChannelPayload } from '../api'
import { errorMessage, Spinner } from '../auth'
import type { Category, Channel } from '../types'

type Draft = ChannelPayload & { id: number | null; seeded: boolean }

const blank: Draft = {
  id: null, name: '', source: '', logo: '', categoryId: null, epgId: '', enabled: true, seeded: false,
}

export function Channels() {
  const [channels, setChannels] = useState<Channel[]>([])
  const [categories, setCategories] = useState<Category[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [query, setQuery] = useState('')
  const [catFilter, setCatFilter] = useState<number | 'all'>('all')

  const [draft, setDraft] = useState<Draft | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      const [c, cats] = await Promise.all([api.channels(), api.categories()])
      setChannels(c)
      setCategories(cats)
    } catch (e) {
      setLoadError(errorMessage(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const catName = (id: number | null) =>
    id == null ? null : (categories.find((c) => c.id === id)?.name ?? null)

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    return channels
      .filter((c) => (catFilter === 'all' ? true : c.categoryId === catFilter))
      .filter((c) => !q || c.name.toLowerCase().includes(q) || String(c.id).includes(q))
  }, [channels, query, catFilter])

  const save = async () => {
    if (!draft) return
    setSaveError(null)
    setSaving(true)
    const payload: ChannelPayload = {
      name: draft.name,
      source: draft.source,
      logo: draft.logo,
      categoryId: draft.categoryId,
      epgId: draft.epgId,
      enabled: draft.enabled,
    }
    try {
      if (draft.id == null) await api.createChannel(payload)
      else await api.updateChannel(draft.id, payload)
      setDraft(null)
      await load()
    } catch (e) {
      setSaveError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }

  const remove = async () => {
    if (draft?.id == null) return
    setSaveError(null)
    setSaving(true)
    try {
      await api.deleteChannel(draft.id)
      setDraft(null)
      await load()
    } catch (e) {
      setSaveError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }

  const enabledCount = channels.filter((c) => c.enabled).length

  return (
    <Shell
      title="Canales"
      subtitle={loading ? 'Cargando…' : `${channels.length} canales · ${enabledCount} habilitados`}
      actions={
        <Button variant="primary" onClick={() => { setSaveError(null); setDraft({ ...blank }) }}>
          <Plus size={15} /> Nuevo canal
        </Button>
      }
    >
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <div className="relative min-w-[220px] flex-1">
          <Search size={15} className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-tx-3" />
          <input
            className="field !pl-9"
            placeholder="Buscar por nombre o ID…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </div>
        <div className="relative">
          <SlidersHorizontal size={15} className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-tx-3" />
          <select
            className="field !w-auto cursor-pointer !pr-8 !pl-9"
            value={catFilter}
            onChange={(e) => setCatFilter(e.target.value === 'all' ? 'all' : Number(e.target.value))}
          >
            <option value="all">Todas las categorías</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>{c.name}</option>
            ))}
          </select>
        </div>
      </div>

      {loadError && (
        <p role="alert" className="mb-4 flex items-center gap-2 rounded-lg border border-danger/30 bg-danger/8 px-4 py-3 text-[13px] text-danger">
          <AlertCircle size={15} /> {loadError}
          <Button size="sm" onClick={() => void load()} className="ml-auto">Reintentar</Button>
        </p>
      )}

      <div className="panel overflow-hidden">
        <div className="hidden grid-cols-[42px_1fr_150px_120px_92px] gap-3 border-b border-line-soft px-5 py-2.5 md:grid">
          {['', 'Canal', 'Categoría', 'Origen', 'Estado'].map((h) => (
            <span key={h} className="font-mono text-[10px] tracking-[0.14em] text-tx-3 uppercase">{h}</span>
          ))}
        </div>

        {loading && <div className="grid place-items-center py-16"><Spinner /></div>}

        {!loading && shown.length === 0 && (
          <EmptyState
            icon={<Radio size={26} />}
            title={channels.length === 0 ? 'Sin canales' : 'Sin resultados'}
            body={
              channels.length === 0
                ? 'Cree el primer canal con el botón «Nuevo canal».'
                : 'Ningún canal coincide con la búsqueda o el filtro seleccionado.'
            }
          />
        )}

        {!loading && shown.length > 0 && (
          <div className="stagger divide-y divide-line-soft">
            {shown.map((c) => {
              const cat = catName(c.categoryId)
              return (
                <button
                  key={c.id}
                  onClick={() => {
                    setSaveError(null)
                    setDraft({
                      id: c.id, name: c.name, source: c.source, logo: c.logo,
                      categoryId: c.categoryId, epgId: c.epgId, enabled: c.enabled, seeded: c.seeded,
                    })
                  }}
                  className="grid w-full grid-cols-[42px_1fr] items-center gap-3 px-5 py-3 text-left transition-colors duration-150 hover:bg-surface-2/70 md:grid-cols-[42px_1fr_150px_120px_92px]"
                >
                  <span className="font-mono text-[11px] text-tx-3">{c.id}</span>

                  <span className="min-w-0">
                    <span className="flex items-center gap-2">
                      <span className="truncate text-[14px] font-semibold">{c.name}</span>
                      {!c.seeded && <Pill tone="mint">local</Pill>}
                    </span>
                    <span className="mt-0.5 block truncate font-mono text-[11px] text-tx-3">{c.source}</span>
                  </span>

                  {/* A channel with no category is invisible to the playlist —
                      it must look wrong here, not blank. */}
                  <span className="hidden text-[13px] md:block">
                    {cat ?? <span className="text-warn">Sin categoría</span>}
                  </span>

                  <span className="hidden md:block">
                    <Pill tone={c.source.startsWith('srt://') ? 'warn' : 'neutral'}>
                      {c.source.split('://')[0]?.toUpperCase() || '—'}
                    </Pill>
                  </span>

                  <span className="hidden items-center gap-1.5 md:flex">
                    <StatusDot on={c.enabled} />
                    <span className={`text-[12px] font-medium ${c.enabled ? 'text-air' : 'text-tx-3'}`}>
                      {c.enabled ? 'Al aire' : 'Pausado'}
                    </span>
                  </span>
                </button>
              )
            })}
          </div>
        )}
      </div>

      <Modal
        open={!!draft}
        title={draft?.id != null ? `Editar ${draft.name}` : 'Nuevo canal'}
        subtitle={
          draft?.id != null
            ? 'Los cambios se guardan solo en este nodo.'
            : 'El canal se crea localmente y no depende del panel central.'
        }
        onClose={() => setDraft(null)}
        footer={
          <>
            {/* Seeded channels are not deletable — disabling is the reversible
                equivalent, and the API rejects the delete anyway. */}
            {draft?.id != null && !draft.seeded && (
              <Button variant="danger" onClick={() => void remove()} disabled={saving} className="mr-auto">
                <Trash2 size={14} /> Eliminar
              </Button>
            )}
            <Button onClick={() => setDraft(null)} disabled={saving}>Cancelar</Button>
            <Button variant="primary" onClick={() => void save()} disabled={saving}>
              {saving ? <Spinner size={14} /> : 'Guardar cambios'}
            </Button>
          </>
        }
      >
        {draft && (
          <div className="space-y-4">
            {saveError && (
              <p role="alert" className="flex items-start gap-2 rounded-lg border border-danger/30 bg-danger/8 px-3 py-2.5 text-[13px] text-danger">
                <AlertCircle size={15} className="mt-px shrink-0" /> {saveError}
              </p>
            )}

            <Field label="Nombre del canal">
              <input className="field" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} placeholder="Teletica 7" />
            </Field>

            <Field label="Origen del stream" hint="URL que recibe FFmpeg como -i · http, https, srt, rtmp, rtmps, rtsp o udp">
              <input
                className="field !font-mono !text-[12px]"
                value={draft.source}
                onChange={(e) => setDraft({ ...draft, source: e.target.value })}
                placeholder="http://…/stream.m3u8"
              />
            </Field>

            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Categoría">
                <select
                  className="field cursor-pointer"
                  value={draft.categoryId ?? ''}
                  onChange={(e) => setDraft({ ...draft, categoryId: e.target.value === '' ? null : Number(e.target.value) })}
                >
                  <option value="">Sin categoría</option>
                  {categories.map((c) => (
                    <option key={c.id} value={c.id}>{c.name}</option>
                  ))}
                </select>
              </Field>
              <Field label="ID de EPG">
                <input
                  className="field !font-mono !text-[12px]"
                  value={draft.epgId}
                  onChange={(e) => setDraft({ ...draft, epgId: e.target.value })}
                  placeholder="teletica.cr"
                />
              </Field>
            </div>

            <Field label="Logo (URL)">
              <input
                className="field !font-mono !text-[12px]"
                value={draft.logo}
                onChange={(e) => setDraft({ ...draft, logo: e.target.value })}
                placeholder="https://…/logo.png"
              />
            </Field>

            <div className="flex items-center justify-between rounded-lg border border-line-soft bg-ink-2/60 px-4 py-3">
              <div>
                <p className="text-[13px] font-semibold">Habilitado</p>
                <p className="mt-0.5 text-[12px] text-tx-3">Aparece en la lista y FFmpeg lo mantiene activo.</p>
              </div>
              <Toggle on={draft.enabled} label="Habilitado" onChange={(v) => setDraft({ ...draft, enabled: v })} />
            </div>
          </div>
        )}
      </Modal>
    </Shell>
  )
}
