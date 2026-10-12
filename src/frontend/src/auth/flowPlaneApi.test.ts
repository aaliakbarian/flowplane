import { describe, expect, it, vi } from 'vitest'

import { getCurrentIdentity } from './flowPlaneApi'

describe('authenticated FlowPlane API calls', () => {
  it('uses the access token against the same-origin API path', async () => {
    const fetcher = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ subject: 'subject-123', name: 'FlowPlane Developer' }),
        {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        },
      ),
    )

    const identity = await getCurrentIdentity('access-token-value', fetcher)

    expect(identity).toEqual({
      subject: 'subject-123',
      name: 'FlowPlane Developer',
    })
    expect(fetcher).toHaveBeenCalledWith('/api/me', {
      headers: { Authorization: 'Bearer access-token-value' },
    })
  })

  it('does not call the API without an access token', async () => {
    const fetcher = vi.fn()

    await expect(getCurrentIdentity('', fetcher)).rejects.toThrow(
      'An access token is required',
    )
    expect(fetcher).not.toHaveBeenCalled()
  })
})
