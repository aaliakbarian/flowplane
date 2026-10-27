import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { ProviderApi } from './api'
import { DiscoveryRefresh } from './DiscoveryRefresh'
import { InventoryBrowser } from './InventoryBrowser'
import { writePending } from './refreshSession'

const receipt = (status = 'Scanning') => ({ providerInstanceId: 'p1', refreshId: '017f22e2-79b0-7cc3-98c4-dc0c0c07398f', status, acceptedAtUtc: new Date().toISOString(), expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), observations: 3, candidates: 1, scanCompleted: status === 'Published' })
const json = (body: unknown, status = 200, headers = {}) => new Response(JSON.stringify(body), { status, headers })
const item = (name: string) => ({ schemaId: 'flowplane.ssis/discovery-package.v1', metadata: { packageName: name, packageId: '9223372036854775807', default_value: 'must-not-render', unknownSecret: 'must-not-render' }, nativeIdentity: [], executableReference: null })
afterEach(() => { vi.useRealTimers(); sessionStorage.clear() })

it.each(['Accepted','Scanning','Preparing','Publishing','Published','Failed','Unavailable','Abandoned','PublicationUncertain'])('observes durable %s without resubmitting or exposing cancellation', async (phase) => {
  writePending('operator', { providerId: 'p1', key: '017f22e2-79b0-7cc3-98c4-dc0c0c07398f', etag: '"1"' })
  const fetcher = vi.fn(async () => json(receipt(phase)))
  render(<DiscoveryRefresh api={new ProviderApi('test', fetcher)} id="p1" etag={'"2"'} subject="operator" onFailure={() => {}} onInvalidate={() => {}} />)
  expect(await screen.findByText(phase)).toBeInTheDocument()
  expect(fetcher.mock.calls[0]).toBeDefined()
  expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument()
  expect(document.body.textContent).not.toContain('%')
})

it.each([['empty',200],['unpublished',409]])('distinguishes %s inventory', async (kind, status) => {
  const fetcher = vi.fn(async () => json(kind === 'empty' ? { providerInstanceId: 'p1', generationId: 'g1', items: [], nextCursor: null } : { code: 'InventoryNotPublished' }, status as number))
  render(<InventoryBrowser api={new ProviderApi('test', fetcher)} id="p1" onFailure={() => {}} />)
  fireEvent.click(screen.getByRole('button', { name: 'Load inventory' }))
  await screen.findByText(kind === 'empty' ? /No visible observations/ : /No published inventory/)
})

it('retains the same UUIDv7 and ETag after ambiguous admission and recovers without another POST after reload', async () => {
  const keys: string[] = []
  let posts = 0
  let key = ''
  const fetcher = vi.fn(async (_path: RequestInfo | URL, init: RequestInit = {}) => {
    if (init.method === 'POST') {
      posts++; key = (init.headers as Record<string,string>)['Idempotency-Key']; keys.push(key)
      if (posts === 1) throw new Error('private transport diagnostic')
      return json({ ...receipt(), refreshId: key }, 202)
    }
    return posts < 2 ? json({}, 404) : json({ ...receipt('Published'), refreshId: key })
  })
  const api = new ProviderApi('synthetic-token', fetcher)
  const props = { api, id: 'p1', etag: '"7"', subject: 'operator', onFailure: () => {}, onInvalidate: () => {} }
  const first = render(<DiscoveryRefresh {...props} />)
  fireEvent.click(screen.getByRole('button', { name: 'Start discovery' }))
  const retry = await screen.findByRole('button', { name: 'Retry admission' })
  await waitFor(() => expect(retry).not.toBeDisabled(), { timeout: 4000 })
  fireEvent.click(retry)
  await screen.findByText('Published', {}, { timeout: 4000 }) // Includes the server-directed polling delay.
  expect(keys[0]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  expect(keys[1]).toBe(keys[0])
  expect((fetcher.mock.calls[0][1]?.headers as Record<string,string>)['If-Match']).toBe('"7"')
  first.unmount()
  render(<DiscoveryRefresh {...props} etag={'"99"'} />)
  await screen.findByText('Published')
  expect(posts).toBe(2)
  expect(JSON.stringify(Object.values(sessionStorage))).not.toContain('synthetic-token')
  expect(document.body.textContent).not.toContain('private transport diagnostic')
}, 10000)

it('honours capacity Retry-After without silently creating a new admission key', async () => {
  vi.useFakeTimers()
  const fetcher = vi.fn(async (_path: RequestInfo | URL, init: RequestInit = {}) => init.method === 'POST' ? json({}, 429, { 'Retry-After': '7' }) : json({}, 404))
  await act(async () => { render(<DiscoveryRefresh api={new ProviderApi('test', fetcher)} id="p1" etag={'"1"'} subject="operator" onFailure={() => {}} onInvalidate={() => {}} />) })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Start discovery' })) })
  expect(screen.getByRole('button', { name: 'Retry admission' })).toBeDisabled()
  await act(async () => { await vi.advanceTimersByTimeAsync(7100) })
  expect(screen.getByRole('button', { name: 'Retry admission' })).not.toBeDisabled()
  expect(fetcher.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
})

it.each([['ProviderUnavailable',409],['ProviderEndpointDenied',400]])('does not discard an ambiguous admission after later %s rejection or reload', async (code, status) => {
  vi.useFakeTimers()
  let posts = 0
  const keys: string[] = []
  const fetcher = vi.fn(async (_path: RequestInfo | URL, init: RequestInit = {}) => {
    if (init.method !== 'POST') return json({}, 404)
    keys.push((init.headers as Record<string,string>)['Idempotency-Key'])
    if (++posts === 1) throw new Error('acknowledgement lost')
    return json({ code }, status as number, { 'Retry-After': '1' })
  })
  const props = { api: new ProviderApi('test', fetcher), id: 'p1', etag: '"1"', subject: 'operator', onFailure: () => {}, onInvalidate: () => {} }
  let ui = render(<DiscoveryRefresh {...props} />)
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Start discovery' })) })
  await act(async () => { await vi.advanceTimersByTimeAsync(3100) })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Retry admission' })) })
  expect(screen.queryByRole('button', { name: 'Discard rejected request' })).not.toBeInTheDocument()
  expect(keys[1]).toBe(keys[0])
  const saved = sessionStorage.getItem('flowplane.refresh.v1:operator:p1')
  ui.unmount()
  await act(async () => { ui = render(<DiscoveryRefresh {...props} />) })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Retry admission' })) })
  expect(screen.queryByRole('button', { name: 'Discard rejected request' })).not.toBeInTheDocument()
  expect(keys[2]).toBe(keys[0])
  expect(sessionStorage.getItem('flowplane.refresh.v1:operator:p1')).toBe(saved)
  ui.unmount()
})

it('honours status Retry-After across phase changes and does not poll an in-flight admission', async () => {
  vi.useFakeTimers()
  let finish!: (response: Response) => void
  let key = ''
  let gets = 0
  const fetcher = vi.fn(async (_path: RequestInfo | URL, init: RequestInit = {}) => {
    if (init.method === 'POST') {
      key = (init.headers as Record<string,string>)['Idempotency-Key']
      return new Promise<Response>((resolve) => { finish = resolve })
    }
    gets++
    return json({ ...receipt(gets === 1 ? 'Scanning' : 'Preparing'), refreshId: key }, 200, { 'Retry-After': '7' })
  })
  render(<DiscoveryRefresh api={new ProviderApi('test', fetcher)} id="p1" etag={'"1"'} subject="operator" onFailure={() => {}} onInvalidate={() => {}} />)
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Start discovery' })) })
  expect(gets).toBe(0)
  await act(async () => { finish(json({ ...receipt('Accepted'), refreshId: key }, 202, { 'Retry-After': '7' })) })
  await act(async () => { await vi.advanceTimersByTimeAsync(6999) })
  expect(gets).toBe(0)
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(screen.getByText('Scanning')).toBeInTheDocument()
  expect(gets).toBe(1)
  await act(async () => { await vi.advanceTimersByTimeAsync(6999) })
  expect(gets).toBe(1)
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(screen.getByText('Preparing')).toBeInTheDocument()
  expect(gets).toBe(2)
})

it('displays the current project active revision losslessly', async () => {
  const fetcher = vi.fn(async () => json({ providerInstanceId: 'p1', generationId: 'g1', items: [{ ...item('EntryPoint.dtsx'), metadata: { projectName: 'Fixture', activeObjectVersionLsn: '9223372036854775806' } }], nextCursor: null }))
  render(<InventoryBrowser api={new ProviderApi('test', fetcher)} id="p1" onFailure={() => {}} />)
  fireEvent.click(screen.getByRole('button', { name: 'Load inventory' }))
  expect(await screen.findByText('9223372036854775806')).toBeInTheDocument()
})

it('restarts an expired inventory cursor at page one and never renders unapproved metadata', async () => {
  let calls = 0
  const fetcher = vi.fn(async (path: RequestInfo | URL) => {
    calls++
    if (String(path).includes('cursor=')) return json({ code: 'InventoryInvalidCursor' }, 400)
    return json({ providerInstanceId: 'p1', generationId: 'g1', items: [item(calls === 1 ? 'OldPage' : 'FreshPage')], nextCursor: calls === 1 ? 'opaque-cursor' : null })
  })
  render(<InventoryBrowser api={new ProviderApi('test', fetcher)} id="p1" onFailure={() => {}} />)
  fireEvent.click(screen.getByRole('button', { name: 'Load inventory' }))
  await screen.findByText('OldPage')
  expect(screen.getByText('9223372036854775807')).toBeInTheDocument()
  expect(document.body.textContent).not.toContain('must-not-render')
  fireEvent.click(screen.getByRole('button', { name: 'Next inventory page' }))
  await screen.findByText('FreshPage')
  expect(screen.queryByText('OldPage')).not.toBeInTheDocument()
  expect(calls).toBe(3)
  expect(String(fetcher.mock.calls[0][0])).toContain('candidatesOnly=true')
})

it.each([['InventoryNativeChanged',409],['InventoryNativeDenied',403],['InventoryNativeUnavailable',503]])('clears the entire inventory after %s with no stale fallback', async (code, status) => {
  let calls = 0
  const fetcher = vi.fn(async () => ++calls === 1 ? json({ providerInstanceId: 'p1', generationId: 'g1', items: [item('ProtectedPage')], nextCursor: 'next' }) : json({ code, detail: 'private-error' }, status as number))
  render(<InventoryBrowser api={new ProviderApi('test', fetcher)} id="p1" onFailure={() => {}} />)
  fireEvent.click(screen.getByRole('button', { name: 'Load inventory' }))
  await screen.findByText('ProtectedPage')
  fireEvent.click(screen.getByRole('button', { name: 'Next inventory page' }))
  await screen.findByRole('alert')
  expect(screen.queryByText('ProtectedPage')).not.toBeInTheDocument()
  expect(document.body.textContent).not.toContain('private-error')
  expect(calls).toBe(2)
})
