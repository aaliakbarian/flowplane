import { resolve } from 'node:path'
import { expect, test, type Page } from '@playwright/test'

// Hermetic browser contract: all navigation/assets/API calls are intercepted.
// No running API, OIDC server, native provider, trust installation or private env.
test.use({ trace: 'off', screenshot: 'off', video: 'off' })
const origin = 'https://pairing.synthetic.test'
const providerId = '018f8f55-82e7-734b-ab9a-a2d355bc9f6d'
const agentId = '529a2346-c686-421c-89c1-6f1dc996e545'
const grantId = '06acae15-1f30-4443-ae9c-124b2d4874e8'
const providerVersion = '9223372036854775807'
const provider = { id: providerId, providerType: 'ssis', displayName: 'Synthetic provider', isEnabled: true, configuration: { schemaId: 'ssis.provider-instance/v1', data: { host: 'sql.synthetic.test', port: 1433, username: 'synthetic', trustServerCertificate: false } } }
const status = (stage = 'Issued', version = '1') => ({ grantId, agentId, providerInstanceId: providerId, stage, version, expiresAtUtc: new Date(Date.now() + 600000).toISOString() })

async function session(page: Page) {
  await page.addInitScript(() => {
    const issuer = 'http://127.0.0.1:8081/realms/flowplane'
    sessionStorage.setItem(`oidc.user:${issuer}:flowplane-spa`, JSON.stringify({ access_token: 'synthetic-pairing-token', token_type: 'Bearer', scope: 'openid profile', expires_at: Math.floor(Date.now() / 1000) + 3600, profile: { sub: 'synthetic-operator', iss: issuer } }))
  })
}

test('synthetic HTTPS pairing recovers issue ambiguity, explicitly reissues and confirms with current phrase and ETag', async ({ page }) => {
  const canary = `synthetic-${crypto.randomUUID()}`
  const logs: string[] = [], urls: string[] = [], bodies: string[] = []
  page.on('console', (message) => logs.push(message.text()))
  page.on('request', (request) => urls.push(request.url()))
  await session(page)
  let confirmations = 0
  await page.route('**/*', async (route) => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname
    if (url.origin !== origin) return route.abort()
    const respond = (body: unknown, version = '1', code = 200) => route.fulfill({ status: code, contentType: 'application/json', headers: { ETag: `"${version}"` }, body: JSON.stringify(body) })
    if (path === '/api/me') return respond({ subject: 'synthetic-operator', name: 'Synthetic operator' })
    if (path === '/api/provider-instances') return respond([provider])
    if (path === `/api/provider-instances/${providerId}`) return respond(provider, providerVersion)
    if (path === `/api/agents/${agentId}`) return respond({ agentId, version: '4', providers: [{ providerInstanceId: providerId, providerVersion }] }, '4')
    if (path === '/api/agents/pairing-grants') {
      bodies.push(request.postData()!)
      if (bodies.length === 1) return route.abort('failed')
      if (bodies.length === 2) return respond({ ...status(), pairingCode: null })
      const body = request.postDataJSON()
      expect(body.existingAgentId).toBe(agentId)
      expect(body.expectedAgentVersion).toBe('4')
      return respond({ ...status(), pairingCode: canary })
    }
    if (path.endsWith('/confirmation')) {
      confirmations++
      expect(request.headers()['if-match']).toBe(confirmations === 1 ? '"2"' : '"3"')
      expect(request.postDataJSON()).toEqual({ verificationPhrase: 'silver maple river' })
      return confirmations === 1 ? respond({ code: 'AgentVersionChanged', detail: canary }, '2', 412) : respond(status('Redeemed', '4'), '4')
    }
    if (path.includes('/pairings/')) return respond({ ...status('Proven', confirmations ? '3' : '2'), verificationPhrase: 'silver maple river' }, confirmations ? '3' : '2')
    if (path === '/') return route.fulfill({ path: resolve(process.env.FLOWPLANE_PAIRING_DIST ?? 'dist', 'index.html') })
    if (/^\/assets\/[a-zA-Z0-9_.-]+\.(js|css)$/.test(path)) return route.fulfill({ path: resolve(process.env.FLOWPLANE_PAIRING_DIST ?? 'dist', path.slice(1)) })
    return route.abort()
  })
  await page.goto(origin)
  await page.getByRole('button', { name: 'Synthetic provider', exact: true }).click()
  await page.getByRole('button', { name: 'Request pairing code' }).click()
  await page.getByRole('button', { name: 'Retry same pairing request' }).click()
  await expect(page.getByText(/cannot be displayed again/)).toBeVisible()
  expect(bodies[0] === bodies[1]).toBe(true)
  expect(bodies.length).toBe(2)
  await page.getByRole('button', { name: 'Reissue pairing code for this agent' }).click()
  await expect(page.getByLabel('One-time pairing code')).toBeVisible()
  expect(await page.getByLabel('One-time pairing code').inputValue() === canary).toBe(true)
  expect(bodies.length).toBe(3)
  await page.getByRole('button', { name: 'Check pairing status' }).click()
  await expect(page.getByLabel('One-time pairing code')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Confirm pairing' })).toBeDisabled()
  expect(confirmations).toBe(0)
  await page.getByRole('checkbox', { name: /I compared/ }).check()
  await page.getByRole('button', { name: 'Confirm pairing' }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByText('silver maple river', { exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'Check pairing status' }).click()
  await expect(page.getByRole('button', { name: 'Confirm pairing' })).toBeDisabled()
  await page.getByRole('checkbox', { name: /I compared/ }).check()
  await page.getByRole('button', { name: 'Confirm pairing' }).click()
  await expect(page.getByText('Redeemed', { exact: true })).toBeVisible()
  const stored = await page.evaluate(() => JSON.stringify({ session: Object.values(sessionStorage), local: Object.values(localStorage) }))
  expect(stored.includes(canary)).toBe(false)
  expect((await page.locator('body').textContent())?.includes(canary)).toBe(false)
  expect(urls.some((url) => url.includes(canary))).toBe(false)
  expect(logs.some((message) => message.includes(canary) || message.includes('synthetic-pairing-token'))).toBe(false)
})

for (const outcome of ['logout', '401', '403']) test(`synthetic pairing clears the code on ${outcome}`, async ({ page }) => {
  const canary = `synthetic-${crypto.randomUUID()}`
  await session(page)
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url()), path = url.pathname
    if (url.origin !== origin) return route.abort()
    const respond = (body: unknown, code = 200) => route.fulfill({ status: code, contentType: 'application/json', headers: { ETag: path.startsWith('/api/provider') ? `"${providerVersion}"` : '"1"' }, body: JSON.stringify(body) })
    if (path === '/api/me') return respond({ subject: 'synthetic-operator', name: 'Synthetic operator' })
    if (path === '/api/provider-instances') return respond([provider])
    if (path === `/api/provider-instances/${providerId}`) return respond(provider)
    if (path === '/api/agents/pairing-grants') return respond({ ...status(), pairingCode: canary })
    if (path.includes('/pairings/')) return respond({ detail: canary }, Number(outcome))
    if (path === '/') return route.fulfill({ path: resolve(process.env.FLOWPLANE_PAIRING_DIST ?? 'dist', 'index.html') })
    if (/^\/assets\/[a-zA-Z0-9_.-]+\.(js|css)$/.test(path)) return route.fulfill({ path: resolve(process.env.FLOWPLANE_PAIRING_DIST ?? 'dist', path.slice(1)) })
    return route.abort()
  })
  await page.goto(origin)
  await page.getByRole('button', { name: 'Synthetic provider', exact: true }).click()
  await page.getByRole('button', { name: 'Request pairing code' }).click()
  await expect(page.getByLabel('One-time pairing code')).toBeVisible()
  await page.getByRole('button', { name: outcome === 'logout' ? 'Sign out' : 'Check pairing status' }).click()
  await expect(page.getByLabel('One-time pairing code')).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Agent pairing' })).toHaveCount(0)
  expect((await page.locator('body').textContent())?.includes(canary)).toBe(false)
})
