import {
  createContext,
  useContext,
  useEffect,
  useMemo,
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
}

interface AuthContextValue {
  loading: boolean
  user: BrowserOidcUser | null
  signIn(): Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

export interface AuthProviderProps {
  children: ReactNode
  client: BrowserOidcClient
}

export function AuthProvider({ children, client }: AuthProviderProps) {
  const [loading, setLoading] = useState(true)
  const [user, setUser] = useState<BrowserOidcUser | null>(null)

  useEffect(() => {
    let active = true

    void client
      .getUser()
      .then((loadedUser) => {
        if (active) {
          setUser(loadedUser?.expired === false ? loadedUser : null)
          setLoading(false)
        }
      })
      .catch(() => {
        if (active) {
          setUser(null)
          setLoading(false)
        }
      })

    return () => {
      active = false
    }
  }, [client])

  const value = useMemo<AuthContextValue>(
    () => ({
      loading,
      user,
      signIn: () => client.signinRedirect(),
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
