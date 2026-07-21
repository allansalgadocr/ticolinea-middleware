import { type ReactNode } from 'react'
import { NavLink, useNavigate } from 'react-router-dom'
import { LayoutGrid, LogOut, Radio, Tags, Users2 } from 'lucide-react'
import { useNodeInfo } from '../config'
import { useAuth } from '../auth'

const nav = [
  { to: '/canales', label: 'Canales', icon: Radio, ownerOnly: false },
  { to: '/categorias', label: 'Categorías', icon: Tags, ownerOnly: false },
  { to: '/usuarios', label: 'Usuarios', icon: Users2, ownerOnly: true },
]

export function Shell({ title, subtitle, actions, children }: {
  title: string
  subtitle: ReactNode
  actions?: ReactNode
  children: ReactNode
}) {
  const navigate = useNavigate()
  const node = useNodeInfo()
  const { user, logout } = useAuth()

  // Operators never see the Usuarios link; RequireOwner also guards the route,
  // so hiding it is presentation, not the access control itself.
  const links = nav.filter((n) => !n.ownerOnly || user?.role === 'owner')

  return (
    <div className="relative z-10 flex min-h-screen">
      <aside className="fixed inset-y-0 left-0 hidden w-[236px] flex-col border-r border-line-soft bg-ink-2/60 backdrop-blur-sm lg:flex">
        <div className="px-5 pt-6 pb-5">
          <div className="flex items-center gap-2.5">
            <div className="grid size-8 place-items-center rounded-lg bg-mint/12 ring-1 ring-mint/25">
              <LayoutGrid size={15} className="text-mint" />
            </div>
            <div className="leading-tight">
              <p className="font-display text-[15px] font-extrabold tracking-tight">Consola</p>
              <p className="font-mono text-[10px] tracking-wider text-tx-3 uppercase">nodo · {node.provider}</p>
            </div>
          </div>
        </div>

        <nav className="flex flex-col gap-0.5 px-3">
          {links.map(({ to, label, icon: Icon }) => (
            <NavLink
              key={to}
              to={to}
              className={({ isActive }) =>
                `group relative flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm font-medium transition-colors duration-150 ${
                  isActive ? 'bg-surface-2 text-tx' : 'text-tx-2 hover:bg-surface/70 hover:text-tx'
                }`
              }
            >
              {({ isActive }) => (
                <>
                  {isActive && (
                    <span className="absolute top-1/2 -left-3 h-5 w-[2.5px] -translate-y-1/2 rounded-r-full bg-mint" />
                  )}
                  <Icon size={16} className={isActive ? 'text-mint' : 'text-tx-3 group-hover:text-tx-2'} />
                  {label}
                </>
              )}
            </NavLink>
          ))}
        </nav>

        <div className="mt-auto px-5 pb-5">
          {/* The autonomy note is load-bearing: this node no longer tracks the
              panel, and the owner must never wonder who owns a change. */}
          <div className="panel mb-3 !rounded-xl px-3 py-2.5">
            <p className="font-mono text-[10px] tracking-wider text-tx-3 uppercase">Catálogo</p>
            <p className="mt-1 text-[12px] leading-snug text-tx-2">Administrado localmente</p>
          </div>

          {user && (
            <p className="mb-2 truncate px-1 text-[12px] text-tx-3">
              <span className="text-tx-2">{user.displayName}</span>
              <span className="font-mono"> · {user.role === 'owner' ? 'propietario' : 'operador'}</span>
            </p>
          )}

          <button
            onClick={async () => {
              await logout()
              navigate('/', { replace: true })
            }}
            className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-[13px] font-medium text-tx-3 transition-colors hover:bg-surface-2 hover:text-tx"
          >
            <LogOut size={15} /> Cerrar sesión
          </button>
        </div>
      </aside>

      <main className="flex-1 lg:pl-[236px]">
        <header className="sticky top-0 z-20 border-b border-line-soft bg-ink/85 px-6 py-5 backdrop-blur-md lg:px-9">
          <div className="mx-auto flex max-w-6xl flex-wrap items-end justify-between gap-4">
            <div>
              <h1 className="font-display text-[26px] leading-none font-extrabold tracking-[-0.02em]">{title}</h1>
              <p className="mt-1.5 text-[13px] text-tx-3">{subtitle}</p>
            </div>
            {actions && <div className="flex items-center gap-2">{actions}</div>}
          </div>
        </header>
        <div className="mx-auto max-w-6xl px-6 py-7 lg:px-9">{children}</div>
      </main>
    </div>
  )
}
