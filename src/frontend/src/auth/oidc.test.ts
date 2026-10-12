import { describe, expect, it } from 'vitest'

import { createOidcSettings } from './oidc'

class MemoryStorage implements Storage {
  readonly values = new Map<string, string>()

  get length() {
    return this.values.size
  }

  clear() {
    this.values.clear()
  }

  getItem(key: string) {
    return this.values.get(key) ?? null
  }

  key(index: number) {
    return [...this.values.keys()][index] ?? null
  }

  removeItem(key: string) {
    this.values.delete(key)
  }

  setItem(key: string, value: string) {
    this.values.set(key, value)
  }
}

describe('OIDC configuration', () => {
  it('uses Authorization Code with session-scoped user and transaction stores', async () => {
    const sessionStore = new MemoryStorage()

    const settings = createOidcSettings(
      {
        authority: 'http://127.0.0.1:8081/realms/flowplane',
        clientId: 'flowplane-spa',
        origin: 'http://127.0.0.1:8080',
      },
      sessionStore,
    )

    expect(settings).toMatchObject({
      authority: 'http://127.0.0.1:8081/realms/flowplane',
      client_id: 'flowplane-spa',
      redirect_uri: 'http://127.0.0.1:8080/oidc/callback',
      response_type: 'code',
      scope: 'openid profile',
      automaticSilentRenew: false,
    })
    expect(settings.scope).not.toContain('offline_access')

    await settings.userStore?.set('user-proof', 'session-user')
    await settings.stateStore?.set('state-proof', 'session-state')

    expect([...sessionStore.values.values()]).toEqual([
      'session-user',
      'session-state',
    ])
  })
})
