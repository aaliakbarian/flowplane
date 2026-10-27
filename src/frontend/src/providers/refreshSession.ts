export interface PendingRefresh { providerId: string; key: string; etag: string }
const prefix = 'flowplane.refresh.v1:'
const storageKey = (subject: string, id: string) => `${prefix}${encodeURIComponent(subject)}:${encodeURIComponent(id)}`
export function readPending(subject: string, id: string): PendingRefresh | null {
  const raw = sessionStorage.getItem(storageKey(subject, id))
  if (raw === null) return null
  const value = JSON.parse(raw) as Partial<PendingRefresh>
  if (value.providerId !== id || typeof value.key !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value.key) || typeof value.etag !== 'string' || !/^"[1-9]\d{0,18}"$/.test(value.etag)) throw new Error('Refresh recovery unavailable')
  return { providerId: id, key: value.key, etag: value.etag }
}
export function writePending(subject: string, value: PendingRefresh) {
  // Explicit projection: no token, password, provider settings or metadata.
  sessionStorage.setItem(storageKey(subject, value.providerId), JSON.stringify({ providerId: value.providerId, key: value.key, etag: value.etag }))
}
export function clearPending(subject: string, id: string) { sessionStorage.removeItem(storageKey(subject, id)) }
export function clearRefreshRecovery() {
  for (const key of Object.keys(sessionStorage)) if (key.startsWith(prefix)) sessionStorage.removeItem(key)
}
