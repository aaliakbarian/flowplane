import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'

export interface BrowserOidcUser {
  access_token: string
  expired?: boolean
}

export interface BrowserOidcClient {
  getUser(): Promise<BrowserOidcUser | null>
  signinRedirect(): Promise<void>
  removeUser?(): Promise<void>
  signoutRedirect?(): Promise<void>
  events?: {
    addUserLoaded(callback: (user: BrowserOidcUser) => void): void
    removeUserLoaded(callback: (user: BrowserOidcUser) => void): void
    addUserUnloaded(callback: () => void): void
    removeUserUnloaded(callback: () => void): void
    addAccessTokenExpired(callback: () => void): void
    removeAccessTokenExpired(callback: () => void): void
  }
}

interface AuthContextValue {
  loading: boolean
  user: BrowserOidcUser | null
  signIn(): Promise<void>
  signOut(): Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

export interface AuthProviderProps {
  children: ReactNode
  client: BrowserOidcClient
}

export function AuthProvider({ children, client }: AuthProviderProps) {
  const [loading, setLoading] = useState(true)
  const [user, setUser] = useState<BrowserOidcUser | null>(null)
  const revision = useRef(0)

  useEffect(() => {
    let active = true
    const loaded = (value: BrowserOidcUser) => { revision.current++; setUser(value.expired === false ? value : null); setLoading(false) }
    const unloaded = () => { revision.current++; setUser(null); setLoading(false) }
    client.events?.addUserLoaded(loaded)
    client.events?.addUserUnloaded(unloaded)
    client.events?.addAccessTokenExpired(unloaded)
    const captured = revision.current

    void client
      .getUser()
      .then((loadedUser) => {
        if (active && revision.current === captured) {
          setUser(loadedUser?.expired === false ? loadedUser : null)
          setLoading(false)
        }
      })
      .catch(() => {
        if (active && revision.current === captured) {
          setUser(null)
          setLoading(false)
        }
      })

    return () => {
      active = false
      client.events?.removeUserLoaded(loaded)
      client.events?.removeUserUnloaded(unloaded)
      client.events?.removeAccessTokenExpired(unloaded)
    }
  }, [client])

  const value = useMemo<AuthContextValue>(
    () => ({
      loading,
      user,
      signIn: () => client.signinRedirect(),
      signOut: async () => {
        revision.current++; setUser(null)
        await client.removeUser?.()
        await client.signoutRedirect?.()
      },
    }),
    [client, loading, user],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const context = useContext(AuthContext)
  if (context === null) {
    throw new Error('useAuth must be used within AuthProvider')
  }

  return context
}
