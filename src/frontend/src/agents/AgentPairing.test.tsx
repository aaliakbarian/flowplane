import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AgentPairing } from './AgentPairing'
import { AgentApi } from './api'

const providerId = '018f8f55-82e7-734b-ab9a-a2d355bc9f6d'
const agentId = '529a2346-c686-421c-89c1-6f1dc996e545'
const grantId = '06acae15-1f30-4443-ae9c-124b2d4874e8'
const canary = 'synthetic-pairing-secret-only'
const receipt = (stage = 'Issued', version = '1') => ({ providerInstanceId: providerId, agentId, grantId, stage, version, expiresAtUtc: new Date(Date.now() + 600000).toISOString() })
const json = (data: unknown, status = 200, etag = '"1"') => new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json', ETag: etag } })
function mount(fetcher: typeof fetch) {
  vi.stubGlobal('location', { protocol: 'https:' })
  return render(<AgentPairing api={new AgentApi('synthetic-token', fetcher)} providerId={providerId} providerVersion="9223372036854775807" displayName="Synthetic provider" onFailure={() => {}} />)
}
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers() })

describe('operator pairing', () => {
  it('retries an ambiguous issue with the identical UUID and body, and never recovers a one-time code from a replay', async () => {
    const bodies: string[] = []
    mount((async (path, init) => {
      expect(String(path)).toBe('/api/agents/pairing-grants')
      bodies.push(String(init?.body))
      if (bodies.length === 1) throw new TypeError('synthetic offline')
      return json({ ...receipt(), pairingCode: null })
    }) as typeof fetch)
    fireEvent.click(screen.getByRole('button', { name: 'Request pairing code' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Retry same pairing request' }))
    await screen.findByText(/cannot be displayed again/)
    expect(bodies.length).toBe(2)
    expect(bodies[0] === bodies[1]).toBe(true)
    expect(JSON.parse(bodies[0])).toMatchObject({ providerInstanceId: providerId, providerVersion: '9223372036854775807' })
    expect(JSON.parse(bodies[0]).clientRequestId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(screen.queryByLabelText('One-time pairing code')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Request pairing code' })).not.toBeInTheDocument()
  })

  it('shows the secret transiently, clears it after claim, and requires an explicit phrase match with the latest ETag', async () => {
    let confirmation: RequestInit | undefined
    mount((async (path, init) => {
      if (String(path).endsWith('/confirmation')) { confirmation = init; return json(receipt('Redeemed', '3'), 200, '"3"') }
      if (init?.method === 'POST') return json({ ...receipt(), pairingCode: canary })
      return json({ ...receipt('Proven', '2'), verificationPhrase: 'silver maple river' }, 200, '"2"')
    }) as typeof fetch)
    fireEvent.click(screen.getByRole('button', { name: 'Request pairing code' }))
    await screen.findByLabelText('One-time pairing code')
    expect(JSON.stringify({ ...localStorage, ...sessionStorage }).includes(canary)).toBe(false)
    expect(location.href?.includes(canary) ?? false).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Check pairing status' }))
    await screen.findByText('silver maple river')
    expect(screen.queryByLabelText('One-time pairing code')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reissue pairing code for this agent' })).toBeEnabled()
    const confirm = screen.getByRole('button', { name: 'Confirm pairing' })
    expect(confirm).toBeDisabled()
    fireEvent.click(screen.getByRole('checkbox', { name: /I compared/ }))
    fireEvent.click(confirm)
    await screen.findByText('Redeemed')
    expect(confirmation?.headers).toMatchObject({ 'If-Match': '"2"' })
    expect(JSON.parse(String(confirmation?.body))).toEqual({ verificationPhrase: 'silver maple river' })
  })

  it.each([401, 403, 412])('clears the code and confirmation data on status failure %s', async (status) => {
    const onFailure = vi.fn()
    vi.stubGlobal('location', { protocol: 'https:' })
    const fetcher = (async (_path, init) => init?.method === 'POST' ? json({ ...receipt(), pairingCode: canary }) : json({ code: 'untrusted-secret-text' }, status)) as typeof fetch
    render(<AgentPairing api={new AgentApi('synthetic-token', fetcher)} providerId={providerId} providerVersion="1" displayName="Synthetic provider" onFailure={onFailure} />)
    fireEvent.click(screen.getByRole('button', { name: 'Request pairing code' }))
    await screen.findByLabelText('One-time pairing code')
    fireEvent.click(screen.getByRole('button', { name: 'Check pairing status' }))
    await screen.findByRole('alert')
    expect(screen.queryByLabelText('One-time pairing code')).not.toBeInTheDocument()
    expect(screen.queryByText('Issued')).not.toBeInTheDocument()
    expect(screen.queryByText('untrusted-secret-text')).not.toBeInTheDocument()
    expect(onFailure).toHaveBeenCalled()
  })

  it('aborts requests on unmount and ignores late issue responses', async () => {
    let resolve!: (response: Response) => void
    let signal: AbortSignal | undefined | null
    const view = mount((async (_path, init) => { signal = init?.signal; return new Promise<Response>((finish) => { resolve = finish }) }) as typeof fetch)
    fireEvent.click(screen.getByRole('button', { name: 'Request pairing code' }))
    view.unmount()
    expect(signal?.aborted).toBe(true)
    await act(async () => resolve(json({ ...receipt(), pairingCode: canary })))
    expect(document.body.textContent?.includes(canary)).toBe(false)
  })

  it('stops polling and clears the code on local expiry', async () => {
    const fetcher = vi.fn(async (_path, init) => init?.method === 'POST' ? json({ ...receipt(), expiresAtUtc: new Date(Date.now() + 1000).toISOString(), pairingCode: canary }) : json(receipt('Expired', '2'), 200, '"2"'))
    mount(fetcher as typeof fetch)
    vi.useFakeTimers()
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Request pairing code' })))
    expect(screen.getByLabelText('One-time pairing code')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(6000) })
    expect(screen.queryByLabelText('One-time pairing code')).not.toBeInTheDocument()
    const count = fetcher.mock.calls.length
    await act(async () => { await vi.advanceTimersByTimeAsync(600000) })
    expect(fetcher.mock.calls.length).toBe(count)
  })

  it('only reissues explicitly against the same agent and current version after a lost code', async () => {
    const bodies: Record<string, string>[] = []
    mount((async (path, init) => {
      if (String(path) === `/api/agents/${agentId}`) return json({ agentId, version: '7', providers: [{ providerInstanceId: providerId, providerVersion: '9223372036854775807' }] }, 200, '"7"')
      bodies.push(JSON.parse(String(init?.body)))
      return json({ ...receipt(), pairingCode: bodies.length === 1 ? null : canary })
    }) as typeof fetch)
    fireEvent.click(screen.getByRole('button', { name: 'Request pairing code' }))
    await screen.findByText(/cannot be displayed again/)
    expect(bodies.length).toBe(1)
    fireEvent.click(screen.getByRole('button', { name: 'Reissue pairing code for this agent' }))
    await screen.findByLabelText('One-time pairing code')
    expect(bodies.length).toBe(2)
    expect(bodies[1]).toMatchObject({ existingAgentId: agentId, expectedAgentVersion: '7', providerInstanceId: providerId })
    expect(bodies[1].clientRequestId !== bodies[0].clientRequestId).toBe(true)
  })

  it('does not submit pairing over HTTP', async () => {
    vi.stubGlobal('location', { protocol: 'http:' })
    const fetcher = vi.fn()
    render(<AgentPairing api={new AgentApi('synthetic-token', fetcher)} providerId={providerId} providerVersion="1" displayName="Synthetic provider" onFailure={() => {}} />)
    expect(screen.getByRole('button', { name: 'Request pairing code' })).toBeDisabled()
    expect(screen.getByText(/trusted HTTPS/)).toBeInTheDocument()
    expect(fetcher).not.toHaveBeenCalled()
  })
})
