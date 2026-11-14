import { useEffect, useRef, useState } from 'react'
import { ApiError } from '../providers/api'
import { type AgentApi, type PairingReply, type PairingRequest, pairingMessage } from './api'

const terminal = new Set(['Redeemed', 'Expired', 'Cancelled'])
export function AgentPairing({ api, providerId, providerVersion, displayName, onFailure }: { api: AgentApi; providerId: string; providerVersion: string; displayName: string; onFailure(error: unknown): void }) {
  const [receipt, setReceipt] = useState<PairingReply | null>(null)
  const [code, setCode] = useState<string | null>(null)
  const [pending, setPending] = useState(false)
  const [busy, setBusy] = useState(false)
  const [lostCode, setLostCode] = useState(false)
  const [matched, setMatched] = useState(false)
  const [error, setError] = useState('')
  const [polling, setPolling] = useState(false)
  const [expired, setExpired] = useState(false)
  const lifetime = useRef(new AbortController())
  const request = useRef<PairingRequest | null>(null)
  const grant = useRef<{ agentId: string; grantId: string } | null>(null)
  const inFlight = useRef(false)
  const polls = useRef(0)
  useEffect(() => {
    const controller = new AbortController(); lifetime.current = controller
    request.current = null; grant.current = null; inFlight.current = false; polls.current = 0
    setReceipt(null); setCode(null); setPending(false); setBusy(false); setLostCode(false); setMatched(false); setError(''); setPolling(false); setExpired(false)
    return () => { controller.abort(); request.current = null; grant.current = null }
  }, [api, providerId, providerVersion])
  function fail(value: unknown) {
    setCode(null); setReceipt(null); setMatched(false); setPolling(false); setError(pairingMessage(value))
    if (value instanceof ApiError && [401, 403].includes(value.status)) {
      request.current = null; grant.current = null; setPending(false); setLostCode(false)
    }
    onFailure(value)
  }
  function accept(reply: PairingReply) {
    if (reply.data.providerInstanceId !== providerId || grant.current && (reply.data.grantId !== grant.current.grantId || reply.data.agentId !== grant.current.agentId)) throw new ApiError(502)
    grant.current = { agentId: reply.data.agentId, grantId: reply.data.grantId }
    setReceipt(reply); setMatched(false); setError('')
    if (reply.data.stage !== 'Issued') { setCode(null); setLostCode(false) }
    setPolling(!terminal.has(reply.data.stage))
  }
  async function issue(replace = false) {
    if (inFlight.current || location.protocol !== 'https:') return
    const signal = lifetime.current.signal
    inFlight.current = true; setBusy(true); setError(''); setCode(null)
    try {
      if (replace && grant.current) {
        const existingAgentId = grant.current.agentId
        const expectedAgentVersion = await api.replacementVersion(existingAgentId, providerId, providerVersion, signal)
        if (signal.aborted) return
        request.current = { providerInstanceId: providerId, providerVersion, clientRequestId: crypto.randomUUID(), displayName, existingAgentId, expectedAgentVersion }
        grant.current = null; setReceipt(null); setMatched(false); setPolling(false); setExpired(false); setLostCode(false); polls.current = 0
      }
      request.current ??= { providerInstanceId: providerId, providerVersion, clientRequestId: crypto.randomUUID(), displayName }
      setPending(true)
      const reply = await api.issue(request.current, signal)
      if (signal.aborted) return
      accept(reply); setCode(reply.data.stage === 'Issued' ? reply.pairingCode : null); setLostCode(reply.pairingCode === null)
    } catch (value) { if (!signal.aborted) fail(value) }
    finally { if (!signal.aborted) { inFlight.current = false; setBusy(false) } }
  }
  async function check() {
    if (inFlight.current || !grant.current || expired) return
    const signal = lifetime.current.signal
    const target = grant.current
    inFlight.current = true; setBusy(true)
    try {
      const reply = await api.status(target.agentId, target.grantId, signal)
      if (!signal.aborted) accept(reply)
    } catch (value) { if (!signal.aborted) fail(value) }
    finally { if (!signal.aborted) { inFlight.current = false; setBusy(false) } }
  }
  useEffect(() => {
    if (!polling || !receipt || expired) return
    const timer = setTimeout(() => {
      if (++polls.current > 120) { setPolling(false); setCode(null); setReceipt(null); setMatched(false); setError('Automatic status checks stopped. Check pairing status explicitly to continue.'); return }
      void check()
    }, 5000)
    return () => clearTimeout(timer)
  })
  const expiresAt = receipt?.data.expiresAtUtc
  useEffect(() => {
    if (!expiresAt || receipt && terminal.has(receipt.data.stage)) return
    const timer = setTimeout(() => { setExpired(true); setCode(null); setReceipt(null); setMatched(false); setPolling(false); setError('Pairing display expired. Request a fresh grant explicitly after reviewing the assignment.') }, Math.max(0, Math.min(Date.parse(expiresAt) - Date.now(), 600000)))
    return () => clearTimeout(timer)
  }, [expiresAt, receipt?.data.stage])
  async function confirm() {
    if (inFlight.current || !matched || !receipt?.data.verificationPhrase || receipt.data.stage !== 'Proven' || expired) return
    const signal = lifetime.current.signal
    inFlight.current = true; setBusy(true); setCode(null)
    try { const reply = await api.confirm(receipt, signal); if (!signal.aborted) accept(reply) }
    catch (value) { if (!signal.aborted) fail(value) }
    finally { if (!signal.aborted) { inFlight.current = false; setBusy(false) } }
  }
  return <section aria-label="Agent pairing"><h3>Pair Windows agent</h3>
    <p>Use the installed local setup helper on the intended Windows machine. Pairing does not prove SQL permissions or execution readiness.</p>
    {location.protocol !== 'https:' && <p>Open this operator page over trusted HTTPS before pairing.</p>}
    {!pending && <button disabled={busy || location.protocol !== 'https:'} onClick={() => void issue()}>Request pairing code</button>}
    {pending && !receipt && !grant.current && <button disabled={busy} onClick={() => void issue()}>Retry same pairing request</button>}
    {pending && !receipt && !grant.current && <p>The outcome may be uncertain. Retrying reuses the same request ID; closing this page loses that recovery ID.</p>}
    {receipt && <><p role="status">{receipt.data.stage}</p><p>Grant ID: <code>{receipt.data.grantId}</code></p><p>Expires: {receipt.data.expiresAtUtc}</p></>}
    {code && <><label>One-time pairing code<textarea aria-label="One-time pairing code" readOnly value={code} autoComplete="off" spellCheck={false} /></label>
      <p>Enter the grant ID and this code only in the protected local helper. Do not put them in a URL, command, log, screenshot or recording. The code is displayed once and disappears when claimed, on error or when leaving this provider.</p>
      <button onClick={() => setCode(null)}>Hide pairing code</button></>}
    {lostCode && <p>The one-time code cannot be displayed again. A new grant requires explicit reissue for this same agent and current assignment; do not repeatedly request a new agent.</p>}
    {grant.current && receipt?.data.stage !== 'Redeemed' && <><p>Reissuing cancels the pending pairing for this agent. Verify the intended machine and selected provider first.</p><button disabled={busy} onClick={() => void issue(true)}>Reissue pairing code for this agent</button></>}
    {grant.current && !expired && (!receipt || !terminal.has(receipt.data.stage)) && <button disabled={busy} onClick={() => void check()}>Check pairing status</button>}
    {receipt?.data.stage === 'Proven' && receipt.data.verificationPhrase && <><p>Verification phrase: <strong>{receipt.data.verificationPhrase}</strong></p>
      <label><input type="checkbox" checked={matched} onChange={(event) => setMatched(event.target.checked)} />I compared this phrase with the local helper on the intended machine and verified this provider assignment.</label>
      <button disabled={busy || !matched || expired} onClick={() => void confirm()}>Confirm pairing</button>
      <p>If the phrases differ, stop and request a fresh grant explicitly. Pairing never activates automatically.</p></>}
    {receipt?.data.stage === 'Redeemed' && <p>Pairing confirmed. Agent enrollment does not establish native execution readiness.</p>}
    {error && <p role="alert">{error}</p>}
  </section>
}
