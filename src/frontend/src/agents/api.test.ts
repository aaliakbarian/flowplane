import { afterEach, describe, expect, it, vi } from 'vitest'
import { AgentApi } from './api'

const agentId = '529a2346-c686-421c-89c1-6f1dc996e545'
const grantId = '06acae15-1f30-4443-ae9c-124b2d4874e8'
const providerInstanceId = '018f8f55-82e7-734b-ab9a-a2d355bc9f6d'
const data = { agentId, grantId, providerInstanceId, stage: 'Issued', version: '9223372036854775807', expiresAtUtc: '2026-11-14T10:00:00Z' }
afterEach(() => vi.unstubAllGlobals())
describe('agent API boundary', () => {
  it.each([
    [{ ...data, version: 1 }, '"1"'],
    [data, 'W/"9223372036854775807"'],
    [data, '"1"'],
    [{ ...data, stage: 'Unexpected' }, '"9223372036854775807"'],
  ])('rejects lossy versions, weak or mismatched ETags and unknown stages', async (body, etag) => {
    vi.stubGlobal('location', { protocol: 'https:' })
    const api = new AgentApi('synthetic', async () => new Response(JSON.stringify(body), { headers: { ETag: etag } }))
    await expect(api.status(agentId, grantId, new AbortController().signal)).rejects.toMatchObject({ status: 502 })
  })
  it('keeps authorization, no-store and redirect rejection on the same-origin path, and returns lossless versions', async () => {
    vi.stubGlobal('location', { protocol: 'https:' })
    const api = new AgentApi('synthetic', async (path, init) => {
      expect(path).toBe(`/api/agents/${agentId}/pairings/${grantId}`)
      expect(init).toMatchObject({ cache: 'no-store', credentials: 'same-origin', redirect: 'error', headers: { Authorization: 'Bearer synthetic' } })
      return new Response(JSON.stringify({ ...data, pairingCode: 'synthetic-unexpected-secret' }), { headers: { ETag: '"9223372036854775807"' } })
    })
    const reply = await api.status(agentId, grantId, new AbortController().signal)
    expect(reply.data.version).toBe('9223372036854775807')
    expect('pairingCode' in reply.data).toBe(false)
  })
  it('rejects plaintext before transmitting credentials', async () => {
    vi.stubGlobal('location', { protocol: 'http:' })
    const fetcher = vi.fn()
    await expect(new AgentApi('synthetic', fetcher).status(agentId, grantId, new AbortController().signal)).rejects.toMatchObject({ status: 400 })
    expect(fetcher).not.toHaveBeenCalled()
  })
  it('accepts an unclaimed status whose verification phrase is null', async () => {
    vi.stubGlobal('location', { protocol: 'https:' })
    const api = new AgentApi('synthetic', async () => new Response(JSON.stringify({ ...data, verificationPhrase: null }), { headers: { ETag: '"9223372036854775807"' } }))
    const reply = await api.status(agentId, grantId, new AbortController().signal)
    expect(reply.data.stage).toBe('Issued')
    expect(reply.data.verificationPhrase).toBeNull()
  })
})
