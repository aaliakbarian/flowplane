import { chownSync, writeFileSync } from 'node:fs'

import { expect, test } from '@playwright/test'

interface TokenResponse {
  access_token: string
  refresh_token?: string
  id_token: string
}

test('signs in with Authorization Code and S256 PKCE through the Nginx origin', async ({
  page,
}) => {
  test.setTimeout(60_000)
  const username = requiredEnvironmentValue('FLOWPLANE_TEST_USERNAME')
  const password = requiredEnvironmentValue('FLOWPLANE_TEST_PASSWORD')
  const evidenceFile = requiredEnvironmentValue('FLOWPLANE_TOKEN_EVIDENCE_FILE')
  const browserOrigin = 'http://127.0.0.1:8080'
  const issuerOrigin = 'http://127.0.0.1:8081'

  const observedBrowserUrls: string[] = []
  page.on('request', (request) => {
    observedBrowserUrls.push(request.url())
  })

  await page.goto('/')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.waitForURL(`${issuerOrigin}/realms/flowplane/**`)

  const authorizationUrl = new URL(page.url())
  expect(authorizationUrl.origin).toBe(issuerOrigin)
  expect(authorizationUrl.pathname).toBe(
    '/realms/flowplane/protocol/openid-connect/auth',
  )
  expect(authorizationUrl.searchParams.get('client_id')).toBe('flowplane-spa')
  expect(authorizationUrl.searchParams.get('redirect_uri')).toBe(
    `${browserOrigin}/oidc/callback`,
  )
  expect(authorizationUrl.searchParams.get('response_type')).toBe('code')
  expect(authorizationUrl.searchParams.get('code_challenge_method')).toBe('S256')
  expect(authorizationUrl.searchParams.get('code_challenge')).toMatch(
    /^[A-Za-z0-9_-]{43}$/,
  )
  expect(authorizationUrl.searchParams.get('scope')).not.toContain(
    'offline_access',
  )

  await page.locator('#username').fill(username)
  await page.locator('#password').fill(password)

  const tokenResponsePromise = page.waitForResponse(
    (response) =>
      response.url() ===
        `${issuerOrigin}/realms/flowplane/protocol/openid-connect/token` &&
      response.request().method() === 'POST',
    { timeout: 10_000 },
  )
  const identityResponsePromise = page
    .waitForResponse(
      (response) => response.url() === `${browserOrigin}/api/me`,
      { timeout: 10_000 },
    )
    .catch(() => null)
  await page.locator('#kc-login').click()
  let tokenResponse
  try {
    tokenResponse = await tokenResponsePromise
  } catch {
    const currentUrl = new URL(page.url())
    const loginError = await page
      .locator('.alert-error')
      .allTextContents()
      .then((messages) => messages.join(' ').trim())
    const pageHeading = await page
      .locator('h1')
      .allTextContents()
      .then((headings) => headings.join(' ').trim())
    throw new Error(
      `OIDC token exchange did not start; page=${currentUrl.origin}${currentUrl.pathname}; heading=${pageHeading || 'none'}; loginError=${loginError || 'none'}`,
    )
  }
  const tokenRequest = tokenResponse.request()
  const tokenRequestBody = new URLSearchParams(tokenRequest.postData() ?? '')

  expect(tokenResponse.ok()).toBe(true)
  expect(tokenRequestBody.get('grant_type')).toBe('authorization_code')
  expect(tokenRequestBody.get('code_verifier')).toMatch(/^[A-Za-z0-9_-]{43,128}$/)
  expect(tokenRequestBody.has('client_secret')).toBe(false)
  expect(tokenRequestBody.has('username')).toBe(false)
  expect(tokenRequestBody.has('password')).toBe(false)

  const tokens = (await tokenResponse.json()) as TokenResponse
  const accessClaims = decodeJwtPayload(tokens.access_token)
  const audiences = Array.isArray(accessClaims.aud)
    ? accessClaims.aud
    : [accessClaims.aud]
  expect(audiences).toContain('flowplane-api')

  const identityResponse = await identityResponsePromise
  if (identityResponse === null) {
    throw new Error('Protected identity response was not observed')
  }
  expect(identityResponse.status()).toBe(200)
  await expect(page.getByRole('heading', { name: 'FlowPlane Developer' })).toBeVisible()
  await expect(page.getByText(accessClaims.sub as string)).toBeVisible()
  await expect(page).toHaveURL(`${browserOrigin}/`)

  const sessionValues = await page.evaluate(() =>
    Object.values(window.sessionStorage),
  )
  expect(sessionValues.some((value) => value.includes(tokens.access_token))).toBe(true)
  expect(observedBrowserUrls.some((url) => url.includes(':5173'))).toBe(false)
  expect(observedBrowserUrls.some((url) => url.includes('backend:8080'))).toBe(false)

  writeFileSync(
    evidenceFile,
    [tokens.access_token, tokens.refresh_token ?? '', tokens.id_token].join('\n'),
    { mode: 0o600 },
  )
  chownSync(
    evidenceFile,
    requiredNumericEnvironmentValue('FLOWPLANE_EVIDENCE_UID'),
    requiredNumericEnvironmentValue('FLOWPLANE_EVIDENCE_GID'),
  )
})

function requiredEnvironmentValue(name: string) {
  const value = process.env[name]
  if (value === undefined || value.length === 0) {
    throw new Error(`${name} must be configured`)
  }

  return value
}

function requiredNumericEnvironmentValue(name: string) {
  const value = Number.parseInt(requiredEnvironmentValue(name), 10)
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error(`${name} must be a non-negative integer`)
  }

  return value
}

function decodeJwtPayload(token: string): Record<string, unknown> {
  const payload = token.split('.')[1]
  if (payload === undefined) {
    throw new Error('The access token is not a JWT')
  }

  return JSON.parse(Buffer.from(payload, 'base64url').toString('utf8')) as Record<
    string,
    unknown
  >
}
