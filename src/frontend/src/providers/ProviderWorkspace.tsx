import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, ProviderApi, type Provider, safeMessage } from './api'
import { ProviderForm } from './ProviderForm'
import { ProviderValidation } from './ProviderValidation'
import { DiscoveryRefresh } from './DiscoveryRefresh'
import { InventoryBrowser } from './InventoryBrowser'
import './providers.css'

export function ProviderWorkspace({ token, subject, onUnauthorized }: { token: string; subject: string; onUnauthorized(): void }) {
  const api = useMemo(() => new ProviderApi(token), [token])
  const [rows, setRows] = useState<Provider[]>([])
  const [offset, setOffset] = useState(0)
  const [selection, setSelection] = useState<string | null>(null)
  const [denied, setDenied] = useState(false)
  const [authorized, setAuthorized] = useState(false)
  const [error, setError] = useState('')
  const [reload, setReload] = useState(0)
  const failure = useCallback((value: unknown) => {
    if (value instanceof ApiError && (value.status === 401 || value.status === 403)) {
      setRows([]); setSelection(null); setDenied(true)
      if (value.status === 401) onUnauthorized()
    }
  }, [onUnauthorized])
  useEffect(() => {
    const controller = new AbortController()
    setRows([]); setError(''); setAuthorized(false)
    void api.list(offset, controller.signal).then((reply) => { if (!controller.signal.aborted) {
      if (!Array.isArray(reply.data)) throw new ApiError(502)
      setRows(reply.data); setDenied(false); setAuthorized(true)
    } }).catch((value: unknown) => { if (!controller.signal.aborted) { failure(value); setError(safeMessage(value)) } })
    return () => controller.abort()
  }, [api, offset, reload, failure])
  if (denied) return <section><h2>Provider workspace</h2><p role="alert">You are signed in but are not an authorized operator, or access has been revoked.</p><button onClick={() => setReload(reload + 1)}>Recheck access</button></section>
  if (!authorized) return <section><h2>Provider workspace</h2><p role={error ? 'alert' : 'status'}>{error || 'Checking operator access…'}</p></section>
  return <section className="provider-workspace" aria-label="Provider workspace"><h2>SSIS providers</h2>
    {error && <p role="alert">{error}</p>}
    <nav aria-label="Provider selection"><button onClick={() => setSelection('new')}>New provider</button><button onClick={() => { setSelection(null); setReload(reload + 1) }}>Reload providers</button>
      {rows.map((row) => <button key={row.id} onClick={() => setSelection(row.id)}>{row.displayName}</button>)}
      {offset > 0 && <button onClick={() => { setSelection(null); setOffset(offset - 50) }}>Previous providers</button>}
      {rows.length === 50 && <button onClick={() => { setSelection(null); setOffset(offset + 50) }}>More providers</button>}
    </nav>
    {selection === 'new' && <ProviderForm key="new" api={api} onFailure={failure} onSaved={(id) => { setSelection(id); setReload(reload + 1) }} />}
    {selection && selection !== 'new' && <SelectedProvider key={`${subject}:${selection}`} id={selection} api={api} subject={subject} onFailure={failure} />}
  </section>
}
function SelectedProvider({ id, api, subject, onFailure }: { id: string; api: ProviderApi; subject: string; onFailure(error: unknown): void }) {
  const [value, setValue] = useState<{ data: Provider; etag: string } | null>(null)
  const [reload, setReload] = useState(0)
  const [error, setError] = useState('')
  useEffect(() => {
    const controller = new AbortController(); setValue(null); setError('')
    void api.get(id, controller.signal).then((reply) => { if (!controller.signal.aborted) setValue(reply) }).catch((failure: unknown) => { if (!controller.signal.aborted) { setError(safeMessage(failure)); onFailure(failure) } })
    return () => controller.abort()
  }, [api, id, reload, onFailure])
  if (error) return <p role="alert">{error}</p>
  if (!value) return <p role="status">Loading provider…</p>
  return <ProviderPanel key={value.etag} api={api} provider={value.data} etag={value.etag} subject={subject} onFailure={onFailure} onSaved={() => setReload(reload + 1)} />
}
function ProviderPanel({ api, provider, etag, subject, onFailure, onSaved }: { api: ProviderApi; provider: Provider; etag: string; subject: string; onFailure(error: unknown): void; onSaved(): void }) {
  const [inventoryEpoch, setInventoryEpoch] = useState(0)
  const invalidate = useCallback(() => setInventoryEpoch((epoch) => epoch + 1), [])
  return <article><h2>{provider.displayName}</h2>
    <ProviderForm api={api} provider={provider} etag={etag} onSaved={onSaved} onFailure={onFailure} />
    <ProviderValidation api={api} id={provider.id} onFailure={onFailure} />
    <DiscoveryRefresh api={api} id={provider.id} etag={etag} subject={subject} onFailure={onFailure} onInvalidate={invalidate} />
    <InventoryBrowser key={inventoryEpoch} api={api} id={provider.id} onFailure={onFailure} />
  </article>
}
