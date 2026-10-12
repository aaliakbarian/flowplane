import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import App from './App'
import { AuthProvider } from './auth/AuthProvider'
import { browserOidcClient } from './auth/browserOidcClient'
import { completeOidcCallback } from './auth/callback'

const rootElement = document.getElementById('root')

if (rootElement === null) {
  throw new Error('FlowPlane root element was not found')
}

async function startApplication() {
  await completeOidcCallback(browserOidcClient, window.location, window.history)

  createRoot(rootElement!).render(
    <StrictMode>
      <AuthProvider client={browserOidcClient}>
        <App />
      </AuthProvider>
    </StrictMode>,
  )
}

void startApplication().catch(() => {
  createRoot(rootElement).render(
    <main>
      <h1>FlowPlane</h1>
      <p>Sign-in could not be completed.</p>
    </main>,
  )
})
