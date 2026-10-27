import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ProviderWorkspace } from './ProviderWorkspace'

const provider = { id: 'p1', providerType: 'ssis', displayName: 'SSIS test', isEnabled: true, configuration: { schemaId: 'ssis.provider-instance/v1', data: { host: 'sql.example.test', port: 1433, username: 'synthetic', trustServerCertificate: false } } }
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { ETag: '"9007199254740993"' } })
const renderWorkspace = () => render(<ProviderWorkspace token="synthetic-token" subject="operator" onUnauthorized={() => {}} />)
afterEach(() => { vi.unstubAllGlobals(); sessionStorage.clear() })

describe('provider workspace', () => {
  it('does not expose administration controls while operator authorization is unresolved', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})))
    renderWorkspace()
    expect(screen.queryByRole('button', { name: 'New provider' })).not.toBeInTheDocument()
  })
  it('denies non-operators without rendering administration data', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ detail: 'private-error' }, 403)))
    renderWorkspace()
    expect(await screen.findByText(/not an authorized operator/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New provider' })).not.toBeInTheDocument()
    expect(document.body.textContent).not.toContain('private-error')
  })
  it('creates through separate credentials and clears password immediately, without validation', async () => {
    const requests: { path: string; body?: string }[] = []
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit = {}) => {
      requests.push({ path, body: init.body as string })
      if (init.method === 'POST') return json(provider, 201)
      return json(path.includes('/p1') ? provider : [])
    }))
    renderWorkspace()
    fireEvent.click(await screen.findByRole('button', { name: 'New provider' }))
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'SSIS test' } })
    fireEvent.change(screen.getByLabelText('Host'), { target: { value: 'sql.example.test' } })
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'synthetic' } })
    const password = screen.getByLabelText('Password') as HTMLInputElement
    fireEvent.change(password, { target: { value: 'synthetic-write-only' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create provider' }))
    expect(password.value).toBe('')
    await screen.findByRole('button', { name: 'Save settings' })
    const create = JSON.parse(requests.find((entry) => entry.body)?.body ?? '{}')
    expect(create.credential.password).toBe('synthetic-write-only')
    expect(create.configuration.data.password).toBeUndefined()
    expect(requests.some((entry) => entry.path.includes('/validation'))).toBe(false)
    expect(JSON.stringify(Object.values(sessionStorage))).not.toContain('synthetic-write-only')
  })
  it('uses the strong ETag and clears credential fields even on stale updates', async () => {
    const fetcher = vi.fn(async (path: string, init: RequestInit = {}) => init.method === 'PUT' ? json({}, 412) : json(path.includes('/p1') ? provider : [provider]))
    vi.stubGlobal('fetch', fetcher)
    renderWorkspace()
    fireEvent.click(await screen.findByRole('button', { name: 'SSIS test' }))
    fireEvent.change(await screen.findByLabelText('New password'), { target: { value: 'synthetic-rotation' } })
    fireEvent.click(screen.getByRole('button', { name: 'Replace password' }))
    expect((screen.getByLabelText('New password') as HTMLInputElement).value).toBe('')
    await screen.findByText(/Configuration changed/)
    const update = fetcher.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect((update?.[1]?.headers as Record<string,string>)['If-Match']).toBe('"9007199254740993"')
  })
  it('renders separate validation checks and capability Unknown, not an execution success', async () => {
    vi.stubGlobal('fetch', vi.fn(async (path: string) => path.endsWith('/validation') ? json({ outcome: 0, checks: [{ code: 'ssis.authentication.accepted', status: 0 }], capabilities: [{ code: 'discovery', availability: 2 }, { code: 'execution', availability: 0 }] }) : json(path.includes('/p1') ? provider : [provider])))
    renderWorkspace()
    fireEvent.click(await screen.findByRole('button', { name: 'SSIS test' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Validate provider' }))
    expect(await screen.findByText('discovery: Unknown')).toBeInTheDocument()
    expect(screen.getByText('execution: Available')).toBeInTheDocument()
    expect(screen.getByText(/does not prove package execution/)).toBeInTheDocument()
  })
  it('clears previous provider inventory before a delayed provider switch resolves', async () => {
    vi.stubGlobal('fetch', vi.fn(async (path: string) => path.includes('/inventory') ? json({ providerInstanceId: 'p1', generationId: 'g1', items: [{ schemaId: 'flowplane.ssis/discovery-package.v1', nativeIdentity: [], metadata: { packageName: 'EntryPoint.dtsx', packageId: '9223372036854775807' }, executableReference: null }], nextCursor: null }) : path.endsWith('/p2') ? new Promise<Response>(() => {}) : json(path.includes('/p1') ? provider : [provider, { ...provider, id: 'p2', displayName: 'Second' }])))
    renderWorkspace()
    fireEvent.click(await screen.findByRole('button', { name: 'SSIS test' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Load inventory' }))
    expect(await screen.findByText('EntryPoint.dtsx')).toBeInTheDocument()
    expect(screen.getByText('9223372036854775807')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Second' }))
    await waitFor(() => expect(screen.queryByText('EntryPoint.dtsx')).not.toBeInTheDocument())
  })
  it('does not restore a previous form after its late create response', async () => {
    let finish!: (response: Response) => void
    const second = { ...provider, id: 'p2', displayName: 'Second' }
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit = {}) => init.method === 'POST' ? new Promise<Response>((resolve) => { finish = resolve }) : json(path.endsWith('/p2') ? second : path.endsWith('/p1') ? provider : [second])))
    renderWorkspace()
    fireEvent.click(await screen.findByRole('button', { name: 'New provider' }))
    for (const [label,value] of [['Display name','SSIS test'],['Host','sql.example.test'],['Username','synthetic'],['Password','synthetic-temporary']]) fireEvent.change(screen.getByLabelText(label), { target: { value } })
    fireEvent.click(screen.getByRole('button', { name: 'Create provider' }))
    fireEvent.click(screen.getByRole('button', { name: 'Second' }))
    await screen.findByRole('heading', { name: 'Second' })
    await act(async () => { finish(json(provider, 201)) })
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'SSIS test' })).not.toBeInTheDocument())
    // Let the delayed response chain complete before asserting the selected resource.
    await waitFor(() => expect(screen.getByLabelText('Display name')).toHaveValue('Second'))
  })
})
