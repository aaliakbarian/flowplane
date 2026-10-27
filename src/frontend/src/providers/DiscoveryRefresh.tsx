import { useEffect, useRef, useState } from 'react'
import { ApiError, type ProviderApi, type Receipt, safeMessage } from './api'
import { clearPending, readPending, writePending, type PendingRefresh } from './refreshSession'
import { uuidv7 } from './uuidv7'

const terminal = new Set(['Published','Failed','Unavailable','Abandoned'])
const phases = new Set([...terminal, 'Accepted','Scanning','Preparing','Publishing','PublicationUncertain'])
export function DiscoveryRefresh({ api, id, etag, subject, onFailure, onInvalidate }: { api: ProviderApi; id: string; etag: string; subject: string; onFailure(error: unknown): void; onInvalidate(): void }) {
  const [initial] = useState(() => { try { return { value: readPending(subject, id), blocked: false } } catch { return { value: null, blocked: true } } })
  const [pending, setPending] = useState<PendingRefresh | null>(initial.value)
  const [receipt, setReceipt] = useState<Receipt | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [rejected, setRejected] = useState(false)
  const [retryAt, setRetryAt] = useState(0)
  const [now, setNow] = useState(Date.now())
  const active = useRef(true)
  const finished = receipt !== null && terminal.has(receipt.status)
  useEffect(() => { active.current = true; const timer = setInterval(() => setNow(Date.now()), 1000); return () => { active.current = false; clearInterval(timer) } }, [])
  function accept(value: Receipt, expected: PendingRefresh) {
    if (value.refreshId !== expected.key || value.providerInstanceId !== id || !phases.has(value.status)) throw new ApiError(502)
    setReceipt(value); setError(''); setRejected(false)
  }
  useEffect(() => {
    if (!pending || busy || finished) return
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout>
    async function poll() {
      try {
        const reply = await api.status(id, pending!.key, controller.signal)
        if (controller.signal.aborted) return
        accept(reply.data, pending!)
        if (reply.data.status === 'Published') onInvalidate()
        if (!terminal.has(reply.data.status)) timer = setTimeout(() => void poll(), reply.retryAfterMs)
      } catch (failure) {
        if (controller.signal.aborted) return
        onFailure(failure); setError(safeMessage(failure))
        if (!(failure instanceof ApiError && [401,403,404].includes(failure.status)))
          timer = setTimeout(() => void poll(), failure instanceof ApiError ? failure.retryAfterMs : 2000)
      }
    }
    timer = setTimeout(() => void poll(), Math.max(0, retryAt - Date.now()))
    return () => { controller.abort(); clearTimeout(timer) }
    // Serial observation respects Retry-After even across nonterminal phase changes.
    // Only GET observation is aborted; accepted backend work is never cancelled.
  }, [api, id, pending, busy, finished, retryAt, onFailure, onInvalidate])
  async function admit() {
    if (busy || Date.now() < retryAt || initial.blocked) return
    let operation = pending
    const firstAttempt = !operation || finished
    setBusy(true); setError(''); setRejected(false); onInvalidate()
    try {
      if (!operation || receipt && terminal.has(receipt.status)) {
        operation = { providerId: id, key: uuidv7(), etag }
        writePending(subject, operation) // Persist BEFORE any POST, or fail without submitting.
        setPending(operation); setReceipt(null)
      }
      const reply = await api.admit(id, operation.etag, operation.key)
      if (active.current) { accept(reply.data, operation); setRetryAt(Date.now() + reply.retryAfterMs) }
    } catch (failure) {
      if (active.current) {
        onFailure(failure); setError(safeMessage(failure))
        if (failure instanceof ApiError) {
          setRetryAt(Date.now() + failure.retryAfterMs)
          // A later policy/precondition rejection does NOT prove an earlier POST
          // was never accepted. Reloaded or retried keys must remain recoverable.
          setRejected(firstAttempt && [400,404,409,412,428,429].includes(failure.status) && failure.code !== 'DiscoveryKeyConflict')
        }
      }
    } finally { if (active.current) setBusy(false) }
  }
  const elapsed = receipt ? Math.max(0, Math.floor((now - Date.parse(receipt.acceptedAtUtc)) / 1000)) : 0
  return <section aria-label="Discovery refresh"><h3>Discovery refresh</h3>
    {initial.blocked ? <p role="alert">Refresh recovery is unavailable. Contact the operator before submitting another request.</p> : <>
      {(!pending || receipt && terminal.has(receipt.status)) && <button disabled={busy || now < retryAt} onClick={() => void admit()}>Start discovery</button>}
      {pending && !receipt && <button disabled={busy || now < retryAt} onClick={() => void admit()}>Retry admission</button>}
      {rejected && pending && !receipt && <button disabled={busy || now < retryAt} onClick={() => { clearPending(subject, id); setPending(null); setRejected(false); setError('') }}>Discard rejected request</button>}
    </>}
    {pending && !receipt && <p>Admission not yet confirmed. Retries reuse the same request key.</p>}
    {receipt && <><p role="status">{receipt.status}</p><p>Elapsed: {Number.isFinite(elapsed) ? elapsed : 0} seconds</p>
      <p>Provisional observations: {Number.isSafeInteger(receipt.observations) ? receipt.observations : 'large count'}. Provisional candidates: {Number.isSafeInteger(receipt.candidates) ? receipt.candidates : 'large count'}.</p></>}
    {error && <p role="alert">{error}</p>}
    <p>Closing this page does not cancel accepted work. Counts are not percentages or an authorized inventory.</p>
  </section>
}
