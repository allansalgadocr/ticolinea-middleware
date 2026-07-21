import type { AdminUser, Category, Channel } from './types'

const BASE = '/api/console'

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message)
  }
}

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(BASE + path, {
    // The session lives in an HttpOnly cookie, so every call must send it.
    credentials: 'same-origin',
    headers: init?.body ? { 'Content-Type': 'application/json' } : undefined,
    ...init,
  })

  if (res.status === 204) return undefined as T

  if (!res.ok) {
    // Controllers answer errors as { message }; fall back to the status text
    // when a proxy or an unhandled exception returns something else.
    let message = res.statusText
    try {
      const body = await res.json()
      if (body?.message) message = body.message
    } catch {
      /* non-JSON error body */
    }
    throw new ApiError(message, res.status)
  }

  return (await res.json()) as T
}

export const api = {
  me: () => req<AdminUser>('/auth/me'),
  login: (username: string, password: string) =>
    req<AdminUser>('/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }),
  logout: () => req<void>('/auth/logout', { method: 'POST' }),

  channels: () => req<Channel[]>('/channels'),
  createChannel: (c: ChannelPayload) => req<Channel>('/channels', { method: 'POST', body: JSON.stringify(c) }),
  updateChannel: (id: number, c: ChannelPayload) =>
    req<{ sourceChanged: boolean; restarted: boolean }>(`/channels/${id}`, { method: 'PUT', body: JSON.stringify(c) }),
  deleteChannel: (id: number) => req<void>(`/channels/${id}`, { method: 'DELETE' }),

  categories: () => req<Category[]>('/categories'),
  createCategory: (name: string) => req<Category>('/categories', { method: 'POST', body: JSON.stringify({ name }) }),
  renameCategory: (id: number, name: string) =>
    req<void>(`/categories/${id}`, { method: 'PUT', body: JSON.stringify({ name }) }),
  deleteCategory: (id: number) => req<void>(`/categories/${id}`, { method: 'DELETE' }),

  users: () => req<AdminUser[]>('/users'),
  createUser: (u: NewUserPayload) => req<AdminUser>('/users', { method: 'POST', body: JSON.stringify(u) }),
  setUserEnabled: (id: number, enabled: boolean) =>
    req<void>(`/users/${id}/enabled`, { method: 'POST', body: JSON.stringify(enabled) }),
  setUserPassword: (id: number, password: string) =>
    req<void>(`/users/${id}/password`, { method: 'POST', body: JSON.stringify({ password }) }),
}

export interface ChannelPayload {
  name: string
  source: string
  logo: string
  categoryId: number | null
  epgId: string
  enabled: boolean
}

export interface NewUserPayload {
  username: string
  displayName: string
  password: string
  role: string
}
