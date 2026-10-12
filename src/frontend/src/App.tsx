import { useEffect, useState } from 'react'

import { useAuth } from './auth/AuthProvider'
import {
  getCurrentIdentity,
  type FlowPlaneIdentity,
} from './auth/flowPlaneApi'

function App() {
  const { loading, signIn, user } = useAuth()
  const [identity, setIdentity] = useState<FlowPlaneIdentity | null>(null)
  const [identityFailed, setIdentityFailed] = useState(false)

  useEffect(() => {
    let active = true
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
      .catch(() => {
        if (active) {
          setIdentityFailed(true)
        }
      })

    return () => {
      active = false
    }
  }, [user])

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
      {identityFailed ? <p>Unable to load the authenticated identity.</p> : null}
      {identity !== null ? (
        <section aria-label="Authenticated identity">
          <h2>{identity.name ?? 'Authenticated user'}</h2>
          <p>{identity.subject}</p>
        </section>
      ) : null}
    </main>
  )
}

export default App
