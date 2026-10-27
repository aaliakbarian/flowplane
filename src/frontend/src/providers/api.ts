export interface Settings { host: string; port: number; username: string; trustServerCertificate: boolean }
export interface Configuration { providerType: 'ssis'; displayName: string; isEnabled: boolean; configuration: { schemaId: 'ssis.provider-instance/v1'; data: Settings } }
export interface Provider extends Configuration { id: string }
export interface Validation { outcome: number; checks: { code: string; status: number; message: string }[]; capabilities: { code: string; availability: number; reasonCode: string }[] }
export type Phase = 'Accepted' | 'Scanning' | 'Preparing' | 'Publishing' | 'Published' | 'Failed' | 'Unavailable' | 'Abandoned' | 'PublicationUncertain'
export interface Receipt { refreshId: string; providerInstanceId: string; status: Phase; acceptedAtUtc: string; expiresAtUtc: string; observations: number; candidates: number; scanCompleted: boolean; error?: string }
export interface Item { schemaId: string; nativeIdentity: unknown; metadata: Record<string, unknown>; executableReference: { providerInstanceId: string; providerType: string; schemaId: string; data: { folderName: string; projectName: string; packageName: string } } | null }
export interface Inventory { providerInstanceId: string; generationId: string; items: Item[]; nextCursor: string | null }
export interface Reply<T> { data: T; etag: string | null; retryAfterMs: number }
type Fetcher = typeof fetch
const codes = new Set(['ProviderVersionChanged', 'ProviderContextChanged', 'ProviderEndpointDenied', 'ProviderTlsPolicyDenied', 'ProviderUnavailable', 'ProviderConfigurationInvalid', 'CredentialProtectionUnavailable', 'DiscoverySlotOccupied', 'DiscoveryKeyConflict', 'DiscoveryInvalidKey', 'InventoryInvalidCursor', 'InventoryGenerationChanged', 'InventoryNotPublished', 'InventoryContextChanged', 'InventoryNativeChanged', 'InventoryNativeDenied', 'InventoryNativeUnavailable'])
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly retryAfterMs: number
  constructor(status: number, code = 'RequestFailed', retryAfterMs = 2000) {
    super(`Request unavailable (${status})`)
    this.status = status
    this.code = codes.has(code) ? code : 'RequestFailed'
    this.retryAfterMs = retryAfterMs
  }
}
export function safeMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Unable to complete the request. Try again safely.'
  if (error.status === 401) return 'Your session has expired. Sign in again.'
  if (error.status === 403) return 'Access denied. No cached data is displayed.'
  if (error.status === 412 || error.status === 428) return 'Configuration changed. Reload the provider before editing or starting a new refresh.'
  if (error.code === 'InventoryNotPublished') return 'No published inventory yet. Start discovery first.'
  if (error.code === 'InventoryNativeChanged' || error.code === 'InventoryContextChanged') return 'Native visibility or metadata changed. Refresh discovery before browsing again.'
  if (error.code === 'ProviderEndpointDenied') return 'The endpoint is not allowed by the backend operator policy.'
  if (error.code === 'ProviderTlsPolicyDenied') return 'Certificate bypass is allowed only in Development.'
  if (error.code === 'CredentialProtectionUnavailable') return 'Credential protection is unavailable. Contact the operator.'
  if (error.code === 'DiscoveryInvalidKey') return 'Refresh key rejected. Check browser/server clock skew; do not silently replace an uncertain request.'
  if (error.status === 409) return 'The request conflicts with current provider or discovery state.'
  if (error.status === 429) return 'Execution capacity is busy. Wait before retrying.'
  if (error.status === 400) return 'Invalid configuration or request. Check the supplied values.'
  if (error.status === 404) return 'The requested provider or refresh was not found.'
  return 'Provider or service unavailable. No cached inventory is displayed.'
}
export function retryDelay(header: string | null): number {
  if (!header) return 2000
  const delay = /^\d+$/.test(header) ? Number(header) * 1000 : Date.parse(header) - Date.now()
  return Number.isFinite(delay) ? Math.max(1000, Math.min(delay, 86_400_000)) : 2000
}
export class ProviderApi {
  private readonly token: string
  private readonly fetcher: Fetcher
  constructor(token: string, fetcher: Fetcher = fetch) { this.token = token; this.fetcher = fetcher }
  private async request<T>(path: string, method = 'GET', body?: unknown, etag?: string, key?: string, signal?: AbortSignal): Promise<Reply<T>> {
    const headers: Record<string, string> = { Authorization: `Bearer ${this.token}` }
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    if (etag !== undefined) {
      if (!/^"[1-9]\d{0,18}"$/.test(etag)) throw new ApiError(400)
      headers['If-Match'] = etag
    }
    if (key) headers['Idempotency-Key'] = key
    let response: Response
    try { response = await this.fetcher.call(globalThis, `/api/provider-instances${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal, cache: 'no-store', credentials: 'same-origin', redirect: 'error' }) }
    catch { if (signal?.aborted) throw new DOMException('Aborted', 'AbortError'); throw new ApiError(0) }
    const retryAfterMs = retryDelay(response.headers.get('Retry-After'))
    if (!response.ok) {
      let code: string | undefined
      try { const problem = await response.json() as { code?: unknown }; if (typeof problem.code === 'string' && codes.has(problem.code)) code = problem.code } catch { /* No native or untrusted text is exposed. */ }
      throw new ApiError(response.status, code, retryAfterMs)
    }
    try { return { data: response.status === 204 ? undefined as T : await response.json() as T, etag: response.headers.get('ETag'), retryAfterMs } }
    catch { throw new ApiError(502) }
  }
  list(offset = 0, signal?: AbortSignal) { return this.request<Provider[]>(`?offset=${offset}&limit=50`, 'GET', undefined, undefined, undefined, signal) }
  private async provider(reply: Promise<Reply<Provider>>) {
    const result = await reply
    if (!result.etag || !/^"[1-9]\d{0,18}"$/.test(result.etag) || result.data.providerType !== 'ssis' || result.data.configuration?.schemaId !== 'ssis.provider-instance/v1') throw new ApiError(502)
    return { ...result, etag: result.etag }
  }
  get(id: string, signal?: AbortSignal) { return this.provider(this.request<Provider>(`/${encodeURIComponent(id)}`, 'GET', undefined, undefined, undefined, signal)) }
  create(configuration: Configuration, password: string) { return this.provider(this.request<Provider>('', 'POST', { ...configuration, credential: { password } })) }
  update(id: string, etag: string, configuration: Configuration) { return this.provider(this.request<Provider>(`/${encodeURIComponent(id)}`, 'PUT', configuration, etag)) }
  credential(id: string, etag: string, password: string) { return this.request<void>(`/${encodeURIComponent(id)}/credential`, 'PUT', { password }, etag) }
  validate(id: string) { return this.request<Validation>(`/${encodeURIComponent(id)}/validation`, 'POST') }
  admit(id: string, etag: string, key: string) { return this.request<Receipt>(`/${encodeURIComponent(id)}/discovery-refreshes`, 'POST', undefined, etag, key) }
  status(id: string, key: string, signal?: AbortSignal) { return this.request<Receipt>(`/${encodeURIComponent(id)}/discovery-refreshes/${encodeURIComponent(key)}`, 'GET', undefined, undefined, undefined, signal) }
  inventory(id: string, view: string, cursor?: string, signal?: AbortSignal) {
    const query = new URLSearchParams({ limit: '50', candidatesOnly: String(view === 'candidates') })
    if (view !== 'candidates' && view !== 'all') query.set('schema', `flowplane.ssis/discovery-${view}.v1`)
    if (cursor) query.set('cursor', cursor)
    return this.request<Inventory>(`/${encodeURIComponent(id)}/inventory?${query}`, 'GET', undefined, undefined, undefined, signal)
  }
}
