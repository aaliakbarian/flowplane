import { describe, expect, it, vi } from 'vitest'
import { ProviderApi, ApiError } from './api'
import { uuidv7 } from './uuidv7'

describe('operator API boundary', () => {
  it('does not call native browser fetch with the API object as its receiver', async () => {
    const fetcher = function(this: unknown) {
      if (this !== globalThis) throw new TypeError('Illegal invocation')
      return Promise.resolve(new Response('[]', { status: 200 }))
    }
    const result = await new ProviderApi('test', fetcher).list()
    expect(result.data).toEqual([])
  })
  it('uses same-origin bearer requests, strong ETags and separate write-only credentials', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 204, headers: { ETag: '"9007199254740993"' } }))
    const api = new ProviderApi('synthetic-token', fetcher)
    await api.credential('provider', '"9007199254740992"', 'synthetic-password')
    const [path, request] = fetcher.mock.calls[0]
    expect(path).toBe('/api/provider-instances/provider/credential')
    expect(request.headers['If-Match']).toBe('"9007199254740992"')
    expect(request.headers.Authorization).toBe('Bearer synthetic-token')
    expect(request.body).toBe('{"password":"synthetic-password"}')
    expect(request.cache).toBe('no-store')
    expect(request.redirect).toBe('error')
  })

  it('never echoes raw Problem Details or network errors', async () => {
    const api = new ProviderApi('test', vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'untrusted-secret', detail: 'sensitive-value' }), { status: 403 })))
    const error = await api.list().catch((value: unknown) => value)
    expect(error).toBeInstanceOf(ApiError)
    expect(String(error)).not.toContain('sensitive-value')
    expect(String(error)).not.toContain('untrusted-secret')
  })

  it('sends empty admission with the original key and ETag and honours Retry-After', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response('{}', { status: 202, headers: { 'Retry-After': '7' } }))
    const api = new ProviderApi('test', fetcher)
    const response = await api.admit('provider', '"12"', '017f22e2-79b0-7cc3-98c4-dc0c0c07398f')
    expect(fetcher.mock.calls[0][1].body).toBeUndefined()
    expect(fetcher.mock.calls[0][1].headers['Idempotency-Key']).toBe('017f22e2-79b0-7cc3-98c4-dc0c0c07398f')
    expect(response.retryAfterMs).toBe(7000)
  })
})

describe('browser UUIDv7', () => {
  it('matches the independent RFC9562 Appendix A.6 vector', () => {
    const random = vi.spyOn(crypto, 'getRandomValues').mockImplementation((array) => {
      const bytes = array as Uint8Array
      bytes.set([0,0,0,0,0,0,0x0c,0xc3,0x18,0xc4,0xdc,0x0c,0x0c,0x07,0x39,0x8f])
      return array
    })
    expect(uuidv7(1645557742000)).toBe('017f22e2-79b0-7cc3-98c4-dc0c0c07398f')
    random.mockRestore()
  })
  it.each([-1, 1.5, Number.NaN, 281474976710656])('rejects invalid timestamp %s', (time) => expect(() => uuidv7(time)).toThrow())
  it('uses Web Crypto with canonical version7/variant and independent random values', () => {
    const first = uuidv7(1234567890000)
    expect(first).toMatch(/^011f71fb-0450-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(uuidv7(1234567890000)).not.toBe(first)
  })
})
