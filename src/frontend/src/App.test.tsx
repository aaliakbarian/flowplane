import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import App from './App'
import { AuthProvider, type BrowserOidcClient } from './auth/AuthProvider'

describe('App', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('offers standards-based sign-in when no OIDC user exists', async () => {
    const signinRedirect = vi.fn().mockResolvedValue(undefined)
    const client: BrowserOidcClient = {
      getUser: vi.fn().mockResolvedValue(null),
      signinRedirect,
    }
    render(
      <AuthProvider client={client}>
        <App />
      </AuthProvider>,
    )

    expect(screen.getByRole('heading', { name: 'FlowPlane' })).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Sign in' }))

    expect(signinRedirect).toHaveBeenCalledOnce()
  })

  it('returns to unauthenticated state when the stored OIDC session cannot be read', async () => {
    const client: BrowserOidcClient = {
      getUser: vi.fn().mockRejectedValue(new Error('corrupt session state')),
      signinRedirect: vi.fn().mockResolvedValue(undefined),
    }

    render(
      <AuthProvider client={client}>
        <App />
      </AuthProvider>,
    )

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('loads the protected same-origin identity for an authenticated user', async () => {
    const client: BrowserOidcClient = {
      getUser: vi.fn().mockResolvedValue({
        access_token: 'access-token-value',
        expired: false,
      }),
      signinRedirect: vi.fn().mockResolvedValue(undefined),
    }
    const fetcher = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          subject: 'subject-123',
          name: 'FlowPlane Developer',
        }),
        {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        },
      ),
    )
    vi.stubGlobal('fetch', fetcher)

    render(
      <AuthProvider client={client}>
        <App />
      </AuthProvider>,
    )

    expect(await screen.findByText('FlowPlane Developer')).toBeInTheDocument()
    expect(screen.getByText('subject-123')).toBeInTheDocument()
  })

  it('clears operator administration and identity on local sign-out', async () => {
    const removeUser = vi.fn().mockResolvedValue(undefined)
    const client: BrowserOidcClient = { getUser: vi.fn().mockResolvedValue({ access_token: 'synthetic-token', expired: false }), signinRedirect: vi.fn(), removeUser }
    vi.stubGlobal('fetch', vi.fn(async (path: string) => new Response(JSON.stringify(path === '/api/me' ? { subject: 'operator', name: 'Operator' } : [{ id: 'p1', displayName: 'PrivateProvider' }]), { status: 200 })))
    render(<AuthProvider client={client}><App /></AuthProvider>)
    await screen.findByRole('button', { name: 'PrivateProvider' })
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }))
    await screen.findByRole('button', { name: 'Sign in' })
    expect(screen.queryByText('PrivateProvider')).not.toBeInTheDocument()
    expect(screen.queryByText('Operator')).not.toBeInTheDocument()
    expect(removeUser).toHaveBeenCalledOnce()
  })
  it('returns to sign-in and clears administration after API401', async () => {
    const client: BrowserOidcClient = { getUser: vi.fn().mockResolvedValue({ access_token: 'synthetic-token', expired: false }), signinRedirect: vi.fn(), removeUser: vi.fn() }
    vi.stubGlobal('fetch', vi.fn(async (path: string) => path === '/api/me' ? new Response(JSON.stringify({ subject: 'operator', name: 'Operator' }), { status: 200 }) : new Response(null, { status: 401 })))
    render(<AuthProvider client={client}><App /></AuthProvider>)
    await screen.findByRole('button', { name: 'Sign in' })
    expect(screen.queryByRole('button', { name: 'New provider' })).not.toBeInTheDocument()
    expect(screen.queryByText('Operator')).not.toBeInTheDocument()
  })
  it('recovers from identity401 without losing non-secret refresh recovery', async () => {
    const key = 'flowplane.refresh.v1:operator:p1'
    sessionStorage.setItem(key, 'non-secret-recovery')
    const removeUser = vi.fn()
    const client: BrowserOidcClient = { getUser: vi.fn().mockResolvedValue({ access_token: 'synthetic-token', expired: false }), signinRedirect: vi.fn(), removeUser }
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 401 })))
    render(<AuthProvider client={client}><App /></AuthProvider>)
    await screen.findByRole('button', { name: 'Sign in' })
    expect(removeUser).toHaveBeenCalledOnce()
    expect(screen.queryByRole('button', { name: 'New provider' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Retry identity' })).not.toBeInTheDocument()
    expect(sessionStorage.getItem(key)).toBe('non-secret-recovery')
    sessionStorage.removeItem(key)
  })

  it.each(['network', 'server'])('retries a transient %s identity failure without reload or stale administration', async (failure) => {
    let attempts = 0
    let finish!: (response: Response) => void
    const client: BrowserOidcClient = { getUser: vi.fn().mockResolvedValue({ access_token: 'synthetic-token', expired: false }), signinRedirect: vi.fn() }
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path !== '/api/me') return new Response('[]', { status: 200 })
      if (++attempts === 1) {
        if (failure === 'network') throw new Error('private transport diagnostic')
        return new Response('private server diagnostic', { status: 503 })
      }
      return new Promise<Response>((resolve) => { finish = resolve })
    }))
    render(<AuthProvider client={client}><App /></AuthProvider>)
    fireEvent.click(await screen.findByRole('button', { name: 'Retry identity' }))
    expect(screen.queryByRole('button', { name: 'Retry identity' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New provider' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Authenticated identity' })).not.toBeInTheDocument()
    expect(document.body.textContent).not.toContain('private')
    await act(async () => { finish(new Response(JSON.stringify({ subject: 'operator', name: 'Recovered operator' }), { status: 200 })) })
    await screen.findByRole('button', { name: 'New provider' })
    expect(screen.getByText('Recovered operator')).toBeInTheDocument()
    expect(attempts).toBe(2)
    expect(client.signinRedirect).not.toHaveBeenCalled()
  })

  it('still requires operator authorization after a successful identity retry', async () => {
    let attempts = 0
    const client: BrowserOidcClient = { getUser: vi.fn().mockResolvedValue({ access_token: 'synthetic-token', expired: false }), signinRedirect: vi.fn() }
    vi.stubGlobal('fetch', vi.fn(async (path: string) => path !== '/api/me' ? new Response(null, { status: 403 }) : ++attempts === 1 ? new Response(null, { status: 503 }) : new Response(JSON.stringify({ subject: 'non-operator', name: 'Non-operator' }), { status: 200 })))
    render(<AuthProvider client={client}><App /></AuthProvider>)
    fireEvent.click(await screen.findByRole('button', { name: 'Retry identity' }))
    await screen.findByText(/not an authorized operator/)
    expect(screen.queryByRole('button', { name: 'New provider' })).not.toBeInTheDocument()
  })

  it('ignores a late identity retry response after the OIDC session expires', async () => {
    let attempts = 0
    let finish!: (response: Response) => void
    let expire!: () => void
    const client: BrowserOidcClient = {
      getUser: vi.fn().mockResolvedValue({ access_token: 'synthetic-token', expired: false }), signinRedirect: vi.fn(),
      events: { addUserLoaded: vi.fn(), removeUserLoaded: vi.fn(), addUserUnloaded: vi.fn(), removeUserUnloaded: vi.fn(), addAccessTokenExpired: (callback) => { expire = callback }, removeAccessTokenExpired: vi.fn() },
    }
    vi.stubGlobal('fetch', vi.fn(async () => ++attempts === 1 ? new Response(null, { status: 503 }) : new Promise<Response>((resolve) => { finish = resolve })))
    render(<AuthProvider client={client}><App /></AuthProvider>)
    fireEvent.click(await screen.findByRole('button', { name: 'Retry identity' }))
    await act(async () => { expire() })
    await screen.findByRole('button', { name: 'Sign in' })
    await act(async () => { finish(new Response(JSON.stringify({ subject: 'operator', name: 'Stale operator' }), { status: 200 })) })
    expect(screen.queryByText('Stale operator')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New provider' })).not.toBeInTheDocument()
  })
})
