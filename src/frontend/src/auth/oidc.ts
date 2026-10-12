import { WebStorageStateStore, type UserManagerSettings } from 'oidc-client-ts'

export interface OidcRuntimeConfig {
  authority: string
  clientId: string
  origin: string
}

export function createOidcSettings(
  config: OidcRuntimeConfig,
  storage: Storage,
): UserManagerSettings {
  const userStore = new WebStorageStateStore({ store: storage })
  const stateStore = new WebStorageStateStore({ store: storage })

  return {
    authority: config.authority,
    client_id: config.clientId,
    redirect_uri: `${config.origin}/oidc/callback`,
    response_type: 'code',
    scope: 'openid profile',
    automaticSilentRenew: false,
    loadUserInfo: false,
    userStore,
    stateStore,
  }
}
