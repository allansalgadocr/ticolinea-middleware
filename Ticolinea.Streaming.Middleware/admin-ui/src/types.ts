// Mirrors the node's console API payloads (ASP.NET Core camel-cases these).
// After the seed this node owns its catalog outright, so these are the node's
// own tables — streams_tl / stream_categories / node_admin_users.

export interface Category {
  id: number
  name: string
  order: number
  channelCount: number
}

export interface Channel {
  id: number
  name: string
  source: string
  logo: string
  categoryId: number | null
  order: number
  epgId: string
  enabled: boolean
  /** false = created locally in this console; true = arrived in the original seed. */
  seeded: boolean
}

export type UserRole = 'owner' | 'operator'

export interface AdminUser {
  id: number
  username: string
  displayName: string
  role: UserRole
  enabled: boolean
  lastLogin: string | null
  /** The bootstrap account created at install time; cannot be disabled. */
  isSeed: boolean
  isOwner: boolean
}
