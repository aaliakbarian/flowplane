import { describe, expect, it, vi } from 'vitest'

import { completeOidcCallback } from './callback'

describe('OIDC callback', () => {
  it('completes the stored authorization transaction and removes callback data from the URL', async () => {
    const signinRedirectCallback = vi.fn().mockResolvedValue(undefined)
    const replaceState = vi.fn()

    const handled = await completeOidcCallback(
      { signinRedirectCallback },
      { pathname: '/oidc/callback' },
      { replaceState },
    )

    expect(handled).toBe(true)
    expect(signinRedirectCallback).toHaveBeenCalledOnce()
    expect(replaceState).toHaveBeenCalledWith(null, '', '/')
  })

  it('leaves ordinary application navigation alone', async () => {
    const signinRedirectCallback = vi.fn().mockResolvedValue(undefined)
    const replaceState = vi.fn()

    const handled = await completeOidcCallback(
      { signinRedirectCallback },
      { pathname: '/' },
      { replaceState },
    )

    expect(handled).toBe(false)
    expect(signinRedirectCallback).not.toHaveBeenCalled()
    expect(replaceState).not.toHaveBeenCalled()
  })
})
