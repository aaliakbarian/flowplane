import { useEffect, useState } from 'react'
import { type ProviderApi, type Validation, safeMessage } from './api'

export function ProviderValidation({ api, id, onFailure }: { api: ProviderApi; id: string; onFailure(error: unknown): void }) {
  const [result, setResult] = useState<Validation | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [alive] = useState(() => ({ current: true }))
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [alive])
  async function validate() {
    setBusy(true); setResult(null); setError('')
    try { const reply = await api.validate(id); if (alive.current) setResult(reply.data) }
    catch (failure) { if (alive.current) { setError(safeMessage(failure)); onFailure(failure) } }
    finally { if (alive.current) setBusy(false) }
  }
  return <section aria-label="Provider validation"><h3>Validation</h3>
    <button type="button" disabled={busy} onClick={() => void validate()}>Validate provider</button>
    {busy && <p role="status">Validating…</p>}{error && <p role="alert">{error}</p>}
    {result && <><p>Validation: {['Succeeded', 'Failed', 'Unavailable'][result.outcome] ?? 'Unavailable'}</p>
      <ul>{result.checks.map((check) => <li key={check.code}>{check.code}: {['Succeeded', 'Failed', 'Unavailable'][check.status] ?? 'Unavailable'}</li>)}</ul>
      <ul>{result.capabilities.map((capability) => <li key={capability.code}>{capability.code}: {['Available', 'Unavailable', 'Unknown'][capability.availability] ?? 'Unknown'}</li>)}</ul></>}
    <p>Positive execution permission does not prove package execution.</p>
  </section>
}
