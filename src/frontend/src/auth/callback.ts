interface RedirectCallbackClient {
  signinRedirectCallback(): Promise<unknown>
}

interface BrowserLocation {
  pathname: string
}

interface BrowserHistory {
  replaceState(data: unknown, unused: string, url?: string | URL | null): void
}

export async function completeOidcCallback(
  client: RedirectCallbackClient,
  location: BrowserLocation,
  history: BrowserHistory,
) {
  if (location.pathname !== '/oidc/callback') {
    return false
  }

  await client.signinRedirectCallback()
  history.replaceState(null, '', '/')
  return true
}
