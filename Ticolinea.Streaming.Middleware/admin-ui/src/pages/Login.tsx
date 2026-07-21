import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AlertCircle, ArrowRight, LayoutGrid, ShieldCheck } from 'lucide-react'
import { Button } from '../components/ui'
import { errorMessage, FullPageSpinner, Spinner, useAuth } from '../auth'
import { useNodeInfo } from '../config'

export function Login() {
  const navigate = useNavigate()
  const node = useNodeInfo()
  const { user, loading, login } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // An already-authenticated session skips the form entirely.
  useEffect(() => {
    if (!loading && user) navigate('/canales', { replace: true })
  }, [loading, user, navigate])

  if (loading) return <FullPageSpinner />

  const submit = async () => {
    setError(null)
    setBusy(true)
    try {
      await login(username.trim(), password)
      navigate('/canales', { replace: true })
    } catch (err) {
      setError(errorMessage(err))
      setPassword('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="relative z-10 grid min-h-screen lg:grid-cols-[1.05fr_1fr]">
      {/* Identity side — states plainly which node you are about to change. */}
      <section className="relative hidden flex-col justify-between overflow-hidden border-r border-line-soft p-10 lg:flex">
        <div
          aria-hidden
          className="pointer-events-none absolute -top-24 -left-24 size-[520px] rounded-full opacity-45 blur-3xl"
          style={{ background: 'radial-gradient(circle, rgba(45,212,191,0.20), transparent 65%)' }}
        />
        <div className="relative flex items-center gap-2.5">
          <div className="grid size-8 place-items-center rounded-lg bg-mint/12 ring-1 ring-mint/25">
            <LayoutGrid size={15} className="text-mint" />
          </div>
          <p className="font-display text-[15px] font-extrabold tracking-tight">Ticolinea</p>
        </div>

        <div className="relative max-w-md">
          <p className="font-mono text-[11px] tracking-[0.22em] text-mint uppercase">Consola del nodo</p>
          <h1 className="mt-4 font-display text-[54px] leading-[0.94] font-extrabold tracking-[-0.035em]">
            {node.displayName}
          </h1>
          <p className="mt-5 text-[15px] leading-relaxed text-tx-2">
            Administre los canales y categorías de este nodo. Los cambios se aplican
            únicamente aquí y no se sobrescriben desde el panel central.
          </p>
          <div className="mt-7 flex items-center gap-2 text-[13px] text-tx-3">
            <ShieldCheck size={15} className="text-mint" />
            Catálogo administrado localmente
          </div>
        </div>

        <p className="relative font-mono text-[10px] tracking-wider text-tx-3 uppercase">consola del nodo</p>
      </section>

      {/* Form side */}
      <section className="flex items-center justify-center p-6 sm:p-10">
        <form
          className="stagger w-full max-w-[370px]"
          onSubmit={(e) => {
            e.preventDefault()
            void submit()
          }}
        >
          <h2 className="font-display text-[28px] leading-none font-extrabold tracking-[-0.02em]">Iniciar sesión</h2>
          <p className="mt-2 text-[13px] text-tx-3">Ingrese con su usuario de administrador.</p>

          <div className="mt-7 space-y-4">
            <label className="block">
              <span className="label">Usuario</span>
              <input
                className="field"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoComplete="username"
                autoFocus
                required
              />
            </label>
            <label className="block">
              <span className="label">Contraseña</span>
              <input
                className="field"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                placeholder="••••••••••"
                autoComplete="current-password"
                required
              />
            </label>
          </div>

          {error && (
            <p
              role="alert"
              className="mt-4 flex items-start gap-2 rounded-lg border border-danger/30 bg-danger/8 px-3 py-2.5 text-[13px] text-danger"
            >
              <AlertCircle size={15} className="mt-px shrink-0" />
              {error}
            </p>
          )}

          <Button variant="primary" type="submit" disabled={busy} className="mt-6 w-full !py-2.5">
            {busy ? <Spinner size={15} /> : <>Entrar <ArrowRight size={15} /></>}
          </Button>

          <p className="mt-6 text-center font-mono text-[11px] leading-relaxed text-tx-3">
            Acceso restringido al operador del nodo.
          </p>
        </form>
      </section>
    </div>
  )
}
