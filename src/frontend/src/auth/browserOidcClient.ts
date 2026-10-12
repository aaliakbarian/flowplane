import { UserManager } from 'oidc-client-ts'

import { createOidcSettings } from './oidc'

function requiredEnvironmentValue(name: string, value: string | undefined) {
  if (value === undefined || value.length === 0) {
    throw new Error(`${name} must be configured`)
  }

  return value
}

export const browserOidcClient = new UserManager(
  createOidcSettings(
    {
      authority: requiredEnvironmentValue(
        'VITE_FLOWPLANE_OIDC_AUTHORITY',
        import.meta.env.VITE_FLOWPLANE_OIDC_AUTHORITY,
      ),
      clientId: requiredEnvironmentValue(
        'VITE_FLOWPLANE_OIDC_CLIENT_ID',
        import.meta.env.VITE_FLOWPLANE_OIDC_CLIENT_ID,
      ),
      origin: window.location.origin,
    },
    window.sessionStorage,
  ),
)
