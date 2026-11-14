import { ApiError } from '../providers/api'

export type PairingStage = 'Issued' | 'Claimed' | 'Proven' | 'Redeemed' | 'Expired' | 'Cancelled'
export interface PairingStatus { grantId: string; agentId: string; providerInstanceId: string; stage: PairingStage; expiresAtUtc: string; version: string; verificationPhrase?: string | null }
export interface PairingRequest { providerInstanceId: string; providerVersion: string; clientRequestId: string; displayName: string; existingAgentId?: string; expectedAgentVersion?: string }
export interface PairingReply { data: PairingStatus; etag: string }
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/
const version = /^[1-9]\d{0,18}$/
const stages = new Set(['Issued', 'Claimed', 'Proven', 'Redeemed', 'Expired', 'Cancelled'])

export class AgentApi {
  private readonly token: string
  private readonly fetcher: typeof fetch
  constructor(token: string, fetcher: typeof fetch = fetch) { this.token = token; this.fetcher = fetcher }
  private async request(path: string, signal: AbortSignal, body?: unknown, etag?: string) {
    if (location.protocol !== 'https:') throw new ApiError(400)
    const headers: Record<string, string> = { Authorization: `Bearer ${this.token}` }
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    if (etag !== undefined) {
      if (!/^"[1-9]\d{0,18}"$/.test(etag)) throw new ApiError(400)
      headers['If-Match'] = etag
    }
    const bounded = AbortSignal.any([signal, AbortSignal.timeout(10000)])
    let response: Response
    try { response = await this.fetcher.call(globalThis, `/api/agents${path}`, { method: body === undefined ? 'GET' : 'POST', headers, body: body === undefined ? undefined : JSON.stringify(body), signal: bounded, cache: 'no-store', credentials: 'same-origin', redirect: 'error' }) }
    catch { throw new ApiError(0) }
    // Never retain or display untrusted error bodies, including framework details.
    if (!response.ok) throw new ApiError(response.status)
    try {
      return { data: await response.json() as unknown, etag: response.headers.get('ETag') }
    } catch { throw new ApiError(502) }
  }
  private pairing(reply: { data: unknown; etag: string | null }) {
    const data = reply.data as PairingStatus & { pairingCode?: unknown }
    if (!data || !uuid.test(data.grantId) || !uuid.test(data.agentId) || !uuid.test(data.providerInstanceId) || !stages.has(data.stage) || typeof data.version !== 'string' || !version.test(data.version) || reply.etag !== `"${data.version}"` || !Number.isFinite(Date.parse(data.expiresAtUtc)) || data.verificationPhrase != null && (typeof data.verificationPhrase !== 'string' || !/^[^\x00-\x1f\x7f]{1,256}$/.test(data.verificationPhrase))) throw new ApiError(502)
    return { data, etag: reply.etag }
  }
  async issue(body: PairingRequest, signal: AbortSignal) {
    if (!uuid.test(body.providerInstanceId) || !uuid.test(body.clientRequestId) || !version.test(body.providerVersion)) throw new ApiError(400)
    const reply = this.pairing(await this.request('/pairing-grants', signal, body))
    const code = reply.data.pairingCode
    if (reply.data.providerInstanceId !== body.providerInstanceId || !(code === null || typeof code === 'string' && /^[^\x00-\x20\x7f]{1,512}$/.test(code))) throw new ApiError(502)
    // Construct the safe status explicitly: a secret-bearing response never enters status state.
    return { ...this.safe(reply), pairingCode: code }
  }
  private safe(reply: { data: PairingStatus; etag: string }) {
    const { grantId, agentId, providerInstanceId, stage, expiresAtUtc, version, verificationPhrase } = reply.data
    return { data: { grantId, agentId, providerInstanceId, stage, expiresAtUtc, version, verificationPhrase }, etag: reply.etag }
  }
  async status(agentId: string, grantId: string, signal: AbortSignal) {
    return this.safe(this.pairing(await this.request(`/${encodeURIComponent(agentId)}/pairings/${encodeURIComponent(grantId)}`, signal)))
  }
  async confirm(status: PairingReply, signal: AbortSignal) {
    return this.safe(this.pairing(await this.request(`/${encodeURIComponent(status.data.agentId)}/pairings/${encodeURIComponent(status.data.grantId)}/confirmation`, signal, { verificationPhrase: status.data.verificationPhrase }, status.etag)))
  }
  async replacementVersion(agentId: string, providerId: string, providerVersion: string, signal: AbortSignal) {
    const reply = await this.request(`/${encodeURIComponent(agentId)}`, signal)
    const data = reply.data as { agentId: string; version: string; providers: { providerInstanceId: string; providerVersion: string }[] }
    if (!data || data.agentId !== agentId || typeof data.version !== 'string' || !version.test(data.version) || reply.etag !== `"${data.version}"` || !Array.isArray(data.providers) || !data.providers.some((item) => item.providerInstanceId === providerId && item.providerVersion === providerVersion)) throw new ApiError(412)
    return data.version
  }
}

export function pairingMessage(error: unknown) {
  if (error instanceof ApiError) {
    if (error.status === 401) return 'Your session has expired. Sign in again.'
    if (error.status === 403) return 'Access denied. Pairing details have been cleared.'
    if (error.status === 412 || error.status === 428) return 'Pairing or provider state changed. Check pairing status and reload the provider before proceeding.'
    if (error.status === 409) return 'Pairing conflicts with current agent or provider state. Review the assignment before proceeding.'
    if (error.status === 400) return 'Pairing requires trusted HTTPS and a valid request.'
  }
  return 'Pairing request unavailable. Retry the same request if its outcome is uncertain.'
}
