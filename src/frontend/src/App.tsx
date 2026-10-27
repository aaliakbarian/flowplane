import { useCallback, useEffect, useState } from 'react'

import { useAuth } from './auth/AuthProvider'
import {
  getCurrentIdentity,
  IdentityRequestError,
  type FlowPlaneIdentity,
} from './auth/flowPlaneApi'
import { ProviderWorkspace } from './providers/ProviderWorkspace'
import { clearRefreshRecovery } from './providers/refreshSession'

function App() {
  const { loading, signIn, signOut, user } = useAuth()
  const [identity, setIdentity] = useState<FlowPlaneIdentity | null>(null)
  const [identityFailed, setIdentityFailed] = useState(false)
  const [identityAttempt, setIdentityAttempt] = useState(0)
  const [identityUser, setIdentityUser] = useState(user)
  const endSession = useCallback(() => {
    try { clearRefreshRecovery() } catch { /* Local logout still clears the visible workspace. */ }
    void signOut().catch(() => {})
  }, [signOut])
  const sessionExpired = useCallback(() => { void signOut().catch(() => {}) }, [signOut])

  useEffect(() => {
    let active = true
    setIdentity(null); setIdentityFailed(false); setIdentityUser(user)
    if (user === null) {
      setIdentity(null)
      return () => {
        active = false
      }
    }

    void getCurrentIdentity(user.access_token)
      .then((loadedIdentity) => {
        if (active) {
          setIdentity(loadedIdentity)
          setIdentityFailed(false)
        }
      })
      .catch((failure: unknown) => {
        if (active) {
          if (failure instanceof IdentityRequestError && failure.status === 401) sessionExpired()
          else setIdentityFailed(true)
        }
      })

    return () => {
      active = false
    }
  }, [user, sessionExpired, identityAttempt])

  return (
    <main>
      <h1>FlowPlane</h1>
      {loading ? <p>Checking your session…</p> : null}
      {!loading && user === null ? (
        <button type="button" onClick={() => void signIn()}>
          Sign in
        </button>
      ) : null}
      {user !== null && identity === null && !identityFailed ? (
        <p>Loading identity…</p>
      ) : null}
      {user !== null && identityFailed ? <section aria-label="Identity recovery">
        <p role="alert">Unable to load the authenticated identity. Retry when the service is available.</p>
        <button type="button" onClick={() => {
          setIdentity(null); setIdentityFailed(false)
          setIdentityAttempt((attempt) => attempt + 1)
        }}>Retry identity</button>
      </section> : null}
      {user !== null && identityUser === user && identity !== null ? (
        <section aria-label="Authenticated identity">
          <h2>{identity.name ?? 'Authenticated user'}</h2>
          <p>{identity.subject}</p>
          <button type="button" onClick={endSession}>Sign out</button>
          <ProviderWorkspace key={identity.subject} token={user.access_token} subject={identity.subject} onUnauthorized={sessionExpired} />
        </section>
      ) : null}
    </main>
  )
}

export default App
