import { fireEvent, render, screen } from '@testing-library/react'
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
})
