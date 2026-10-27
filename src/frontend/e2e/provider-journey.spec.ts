import { expect, test } from '@playwright/test'

// Synthetic OIDC session + intercepted HTTP contracts only. Never sends provider
// credentials to a development database, native provider or identity server.
const id = '018f8f55-82e7-734b-ab9a-a2d355bc9f6d'
const issuer = 'http://127.0.0.1:8081/realms/flowplane'
test.use({ trace: 'off', screenshot: 'off', video: 'off' })
test('anonymous Nginx page preserves sign-in and API proxy separation', async ({ page, request }) => {
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
  expect((await request.get('/api/me')).status()).toBe(401)
  expect((await request.get('/api/health/ready')).status()).toBe(200)
})

test('synthetic operator configures, validates, recovers discovery and fails closed while paging', async ({ page }) => {
  test.setTimeout(40000)
  const token = 'synthetic-browser-token'
  const canary = `synthetic-${crypto.randomUUID()}`
  const messages: string[] = []
  const urls: string[] = []
  page.on('console', (message) => messages.push(message.text()))
  page.on('request', (request) => urls.push(request.url()))
  await page.addInitScript(({ issuer, token }) => {
    sessionStorage.setItem(`oidc.user:${issuer}:flowplane-spa`, JSON.stringify({ access_token: token, token_type: 'Bearer', scope: 'openid profile', expires_at: Math.floor(Date.now()/1000)+3600, profile: { sub: 'synthetic-operator', iss: issuer } }))
  }, { issuer, token })
  let provider: unknown = null
  let version = 1
  let admissions = 0
  let accepted = false
  const keys: string[] = []
  let inventoryPage = 0
  await page.route('**/api/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    const respond = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', headers: { ETag: `"${version}"`, 'Retry-After': '1' }, body: JSON.stringify(body) })
    if (path === '/api/me') return respond({ subject: 'synthetic-operator', name: 'Synthetic operator' })
    if (path === '/api/provider-instances' && request.method() === 'GET') return respond(provider ? [provider] : [])
    if (path === '/api/provider-instances' && request.method() === 'POST') {
      const input = request.postDataJSON()
      expect(input.credential.password === canary).toBe(true)
      expect(input.configuration.data.password === undefined).toBe(true)
      provider = { id, providerType: input.providerType, displayName: input.displayName, isEnabled: input.isEnabled, configuration: input.configuration }
      return respond(provider, 201)
    }
    if (path.endsWith('/validation')) return respond({ outcome: 0, checks: [{ code: 'ssis.authentication.accepted', status: 0 }], capabilities: [{ code: 'discovery', availability: 0 }, { code: 'execution', availability: 2 }] })
    if (path.endsWith('/discovery-refreshes') && request.method() === 'POST') {
      expect(request.headers()['if-match']).toBe(`"${version}"`)
      keys.push(request.headers()['idempotency-key']); admissions++
      if (admissions === 1) return route.abort('failed')
      accepted = true
      return respond({ providerInstanceId: id, refreshId: keys[0], status: 'Accepted', acceptedAtUtc: new Date().toISOString(), expiresAtUtc: new Date(Date.now()+3600000).toISOString(), observations: 0, candidates: 0, scanCompleted: false }, 202)
    }
    if (path.includes('/discovery-refreshes/')) return accepted ? respond({ providerInstanceId: id, refreshId: keys[0], status: 'Published', acceptedAtUtc: new Date().toISOString(), expiresAtUtc: new Date(Date.now()+3600000).toISOString(), observations: 4, candidates: 1, scanCompleted: true }) : respond({}, 404)
    if (path.endsWith('/inventory')) {
      inventoryPage++
      if (inventoryPage === 2) return respond({ code: 'InventoryGenerationChanged' }, 409)
      if (inventoryPage === 4) return respond({ code: 'InventoryNativeUnavailable' }, 503)
      return respond({ providerInstanceId: id, generationId: inventoryPage === 1 ? 'generation1' : 'generation2', items: [{ schemaId: 'flowplane.ssis/discovery-package.v1', nativeIdentity: ['package','Folder','Project','EntryPoint.dtsx'], metadata: { folderName: 'Folder', projectName: 'Project', packageName: 'EntryPoint.dtsx', packageId: '9223372036854775807', default_value: canary }, executableReference: { providerInstanceId: id, providerType: 'ssis', schemaId: 'flowplane.ssis/native-definition-reference.v1', data: { folderName: 'Folder', projectName: 'Project', packageName: 'EntryPoint.dtsx' } } }], nextCursor: 'opaque-next' })
    }
    if (path === `/api/provider-instances/${id}` && request.method() === 'PUT') { expect(request.headers()['if-match']).toBe(`"${version}"`); version++; provider = { ...request.postDataJSON(), id }; return respond(provider) }
    if (path === `/api/provider-instances/${id}`) return respond(provider)
    return respond({}, 404)
  })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Synthetic operator' })).toBeVisible({ timeout: 5000 })
  await page.getByRole('button', { name: 'New provider' }).click()
  await page.getByLabel('Display name').fill('Synthetic SSIS')
  await page.getByLabel('Host', { exact: true }).fill('sql.example.test')
  await page.getByLabel('Username', { exact: true }).fill('synthetic-user')
  // No plaintext canary appears in Playwright fill diagnostics or screenshots.
  await page.getByLabel('Password', { exact: true }).evaluate((input, value) => { (input as HTMLInputElement).value = value }, canary)
  await page.getByRole('button', { name: 'Create provider' }).click()
  await expect(page.getByRole('button', { name: 'Save settings' })).toBeVisible()
  expect(await page.locator('input[type=password]').evaluateAll((inputs) => inputs.every((input) => (input as HTMLInputElement).value === ''))).toBe(true)
  await page.getByRole('button', { name: 'Validate provider' }).click()
  await expect(page.getByText('execution: Unknown')).toBeVisible()
  await page.getByRole('button', { name: 'Start discovery' }).click()
  await expect(page.getByRole('button', { name: 'Retry admission' })).toBeVisible()
  await page.reload()
  await page.getByRole('button', { name: 'Synthetic SSIS', exact: true }).click()
  await page.getByRole('button', { name: 'Retry admission' }).click()
  await expect(page.getByText('Published', { exact: true })).toBeVisible()
  expect(admissions).toBe(2)
  expect(keys[0] === keys[1]).toBe(true)
  expect(keys[0]).toMatch(/^[0-9a-f-]{14}7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  await page.getByRole('button', { name: 'Load inventory' }).click()
  await expect(page.getByText('9223372036854775807')).toBeVisible()
  await page.getByRole('button', { name: 'Next inventory page' }).click()
  await expect(page.getByText('Published generation: generation2')).toBeVisible()
  await page.getByRole('button', { name: 'Next inventory page' }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByText('EntryPoint.dtsx', { exact: true })).toHaveCount(0)
  const storage = await page.evaluate(() => JSON.stringify({ session: Object.values(sessionStorage), local: Object.values(localStorage) }))
  expect(storage.includes(canary)).toBe(false)
  expect((await page.locator('body').textContent())?.includes(canary)).toBe(false)
  expect(messages.some((message) => message.includes(canary) || message.includes(token))).toBe(false)
  expect(urls.some((url) => url.includes(canary) || url.includes(token) || url.includes(':5173'))).toBe(false)
})
